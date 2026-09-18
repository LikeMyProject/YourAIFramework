using System;
using YourAI.Core.Contracts;

namespace YourAI.Providers
{
    /// <summary>
    /// Wraps a provider with retry, exponential backoff and jitter.
    ///
    /// A decorator rather than a flag inside <see cref="OpenAiCompatibleProvider"/>
    /// for two reasons. First, retry is a policy, and policy belongs to the
    /// deployment: the same provider object should be able to run bare in a test and
    /// wrapped in production. Second, it composes with the other policies in this
    /// assembly in whatever order the deployment needs, and each of them then means
    /// exactly one thing.
    ///
    /// The composition order matters and is worth stating. Rate limiting goes
    /// <i>inside</i> retry:
    ///
    ///     new RetryProvider(new RateLimitedProvider(openai))
    ///
    /// so that every retry passes through the limiter too. The other order would
    /// throttle the first attempt of each request and let a burst of retries through
    /// unthrottled, which is precisely the burst that caused the 429.
    ///
    /// Name and availability are forwarded rather than invented. A wrapper that
    /// renames its inner provider breaks <c>IAiService.Resolve("deepseek")</c> for
    /// anyone who wrapped it, and there is no reading of "available" that a retry
    /// wrapper could improve on: it can repeat a request, not conjure credentials.
    /// </summary>
    public sealed class RetryProvider : ILlmProvider
    {
        private readonly ILlmProvider _inner;
        private readonly RetryPolicy _policy;
        private readonly Random _random = new Random();

        public RetryProvider(ILlmProvider inner)
            : this(inner, null)
        {
        }

        public RetryProvider(ILlmProvider inner, RetryPolicy policy)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }
            _inner = inner;
            _policy = policy ?? new RetryPolicy();
        }

        public ILlmProvider Inner
        {
            get { return _inner; }
        }

        public RetryPolicy Policy
        {
            get { return _policy; }
        }

        /// <summary>
        /// Source of jitter samples in 0..1. Replaceable so a test can pin the delay
        /// schedule instead of asserting on a distribution.
        /// </summary>
        public Func<double> JitterSource { get; set; }

        public string Name
        {
            get { return _inner.Name; }
        }

        public bool IsAvailable
        {
            get { return _inner.IsAvailable; }
        }

        public string UnavailableReason
        {
            get { return _inner.UnavailableReason; }
        }

        public ILlmStreamHandle StartStream(LlmRequest request)
        {
            if (!_inner.IsAvailable)
            {
                // Nothing to retry. A missing key fails identically three times and
                // merely delays the explanation by the backoff schedule, so the plain
                // faulted handle goes straight back.
                return _inner.StartStream(request);
            }

            return new RetryHandle(_inner, request, _policy, JitterSource ?? DefaultJitter);
        }

        private double DefaultJitter()
        {
            return _random.NextDouble();
        }

        private sealed class RetryHandle : AttemptSequenceHandle
        {
            private readonly ILlmProvider _inner;
            private readonly LlmRequest _request;
            private readonly RetryPolicy _policy;
            private readonly Func<double> _jitter;

            public RetryHandle(ILlmProvider inner, LlmRequest request, RetryPolicy policy, Func<double> jitter)
            {
                _inner = inner;
                _request = request;
                _policy = policy;
                _jitter = jitter;
            }

            protected override AttemptDecision DecideAttempt(int attemptIndex, double nowSeconds, out double waitSeconds)
            {
                waitSeconds = 0;

                if (attemptIndex <= 0)
                {
                    return AttemptDecision.Start;
                }
                if (attemptIndex >= _policy.MaxAttempts)
                {
                    // ShouldContinue already declined, so this is the belt to its
                    // braces: a policy edited to fewer attempts mid-flight must not be
                    // able to park the sequence on an attempt nobody will allow.
                    return AttemptDecision.Stop;
                }

                double delay = _policy.DelayForAttempt(attemptIndex, Sample());
                double elapsed = nowSeconds - LastAttemptEndedSeconds;
                if (elapsed >= delay)
                {
                    return AttemptDecision.Start;
                }

                waitSeconds = delay - elapsed;
                return AttemptDecision.Wait;
            }

            protected override ILlmStreamHandle StartAttempt(int attemptIndex)
            {
                return _inner.StartStream(_request);
            }

            protected override bool ShouldContinue(int attemptIndex, LlmStreamResult result)
            {
                return attemptIndex + 1 < _policy.MaxAttempts && _policy.IsRetryable(result);
            }

            private double Sample()
            {
                double value = _jitter != null ? _jitter() : 0.5;
                if (value < 0)
                {
                    return 0;
                }
                return value > 1 ? 1 : value;
            }
        }
    }
}
