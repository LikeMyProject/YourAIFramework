using System;
using System.Collections.Generic;
using System.Text;
using YourAI.Core.Ai;
using YourAI.Core.Contracts;

namespace YourAI.Providers
{
    /// <summary>
    /// Tries providers in order until one answers: the failover half of resilience.
    ///
    /// <see cref="RetryProvider"/> handles "the same endpoint, later". This handles
    /// "a different endpoint, now", and the two are genuinely different answers to
    /// different failures. A 429 wants patience. A 503 from a vendor having a bad
    /// afternoon, or a base URL that has stopped resolving, wants somebody else.
    /// Losing the second case to a retry schedule means the player waits eight
    /// seconds to be told what was knowable in one.
    ///
    /// Ordering, not balancing. Candidates are tried top to bottom and the first one
    /// that produces text wins, which makes priority explicit and reproducible: a
    /// cheap model backs up an expensive one, a local model backs up both, and a
    /// log line can say exactly which one served the request. Round-robin and
    /// latency-weighted selection are the natural next step and are deliberately not
    /// guessed at here, because the right policy depends on the pricing and rate
    /// limits of vendors this framework does not know about.
    ///
    /// Candidates that report themselves unavailable are skipped before the request
    /// is attempted, so an unconfigured vendor costs a list scan rather than a failed
    /// round trip. The snapshot is taken when the request starts, which means a key
    /// installed mid-flight is picked up by the next request and not by the current
    /// one.
    ///
    /// Composition is intended to be per candidate:
    ///
    ///     var router = new ProviderRouter();
    ///     router.Add(new RetryProvider(new RateLimitedProvider(deepseek)));
    ///     router.Add(new RetryProvider(new RateLimitedProvider(openai)));
    ///
    /// Each vendor then gets its own retry budget and its own rate limit, which is
    /// what the vendors' independent quotas require. One shared retry budget across
    /// two vendors would spend the second vendor's allowance on the first vendor's
    /// rate limiting.
    /// </summary>
    public sealed class ProviderRouter : ILlmProvider
    {
        private readonly List<ILlmProvider> _candidates = new List<ILlmProvider>(4);

        public ProviderRouter()
            : this(null)
        {
        }

        public ProviderRouter(string name)
        {
            Name = string.IsNullOrEmpty(name) ? "router" : name;
        }

        /// <summary>Identifier used when this router is registered as a provider.</summary>
        public string Name { get; set; }

        /// <summary>
        /// Name of the candidate the most recent request was handed to. On a success it
        /// names the winner; on a total failure it names the last one tried.
        ///
        /// Exposed because a silent failover is a slow leak on the backup vendor's
        /// bill. Worth a line in a log whenever it is not the primary.
        /// </summary>
        public string LastAttemptedProvider { get; private set; }

        /// <summary>Candidates in priority order, primary first.</summary>
        public IReadOnlyList<ILlmProvider> Candidates
        {
            get { return _candidates; }
        }

        public ProviderRouter Add(ILlmProvider provider)
        {
            if (provider != null)
            {
                _candidates.Add(provider);
            }
            return this;
        }

        public ProviderRouter AddRange(IEnumerable<ILlmProvider> providers)
        {
            if (providers == null)
            {
                return this;
            }
            foreach (ILlmProvider provider in providers)
            {
                Add(provider);
            }
            return this;
        }

        public void Clear()
        {
            _candidates.Clear();
        }

        /// <summary>True when at least one candidate could serve right now.</summary>
        public bool IsAvailable
        {
            get
            {
                for (int i = 0; i < _candidates.Count; i++)
                {
                    if (_candidates[i].IsAvailable)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Why nothing can serve, naming every candidate. A router that reported only
        /// the first candidate's problem would hide the second misconfiguration behind
        /// the first, and the usual cause of a dead router is that all of them are
        /// wrong at once.
        /// </summary>
        public string UnavailableReason
        {
            get
            {
                if (_candidates.Count == 0)
                {
                    return "No providers registered with router '" + Name + "'.";
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("No provider in router '").Append(Name).Append("' can serve.");
                for (int i = 0; i < _candidates.Count; i++)
                {
                    ILlmProvider candidate = _candidates[i];
                    sb.Append(" [").Append(i).Append("] ").Append(candidate.Name).Append(": ");
                    sb.Append(candidate.IsAvailable ? "available" : candidate.UnavailableReason);
                    sb.Append(';');
                }
                return sb.ToString();
            }
        }

        public ILlmStreamHandle StartStream(LlmRequest request)
        {
            List<ILlmProvider> usable = new List<ILlmProvider>(_candidates.Count);
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].IsAvailable)
                {
                    usable.Add(_candidates[i]);
                }
            }

            if (usable.Count == 0)
            {
                // The router's whole contract is that it never returns null and never
                // throws, so exhaustion is a faulted handle carrying every candidate's
                // reason, exactly as a single unusable provider would.
                return new FailedStreamHandle(UnavailableReason);
            }

            return new RouterHandle(usable, request, NoteAttempt);
        }

        private void NoteAttempt(string providerName)
        {
            LastAttemptedProvider = providerName;
        }

        private sealed class RouterHandle : AttemptSequenceHandle
        {
            private readonly List<ILlmProvider> _usable;
            private readonly LlmRequest _request;
            private readonly Action<string> _note;

            public RouterHandle(List<ILlmProvider> usable, LlmRequest request, Action<string> note)
            {
                _usable = usable;
                _request = request;
                _note = note;
            }

            protected override AttemptDecision DecideAttempt(int attemptIndex, double nowSeconds, out double waitSeconds)
            {
                waitSeconds = 0;
                // No waiting phase. Failover is immediate by design: the reason to
                // switch is that waiting would not help.
                return attemptIndex < _usable.Count ? AttemptDecision.Start : AttemptDecision.Stop;
            }

            protected override ILlmStreamHandle StartAttempt(int attemptIndex)
            {
                ILlmProvider candidate = _usable[attemptIndex];
                if (_note != null)
                {
                    _note(candidate.Name);
                }
                // ProvidersTried is not exposed separately: the base's AttemptCount is
                // exactly that number, and two names for one value is how they drift.
                return candidate.StartStream(_request);
            }

            protected override bool ShouldContinue(int attemptIndex, LlmStreamResult result)
            {
                // Every failure advances, including a 401 or a 404. Those are exactly
                // the cases a router exists for: the primary's credentials expired, its
                // model was retired, its base URL was taken down. Asking whether the
                // failure was "retryable" would be the wrong question, because the next
                // attempt is a different endpoint.
                return attemptIndex + 1 < _usable.Count;
            }
        }
    }
}
