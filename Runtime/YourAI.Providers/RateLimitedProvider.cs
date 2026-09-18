using System;
using YourAI.Core.Contracts;

namespace YourAI.Providers
{
    /// <summary>
    /// Delays the start of a request until <see cref="PollingRateLimiter"/> allows it.
    ///
    /// This is client-side politeness, not error handling. A 429 answered by a retry
    /// is the endpoint telling you that you are already too loud; the only way to
    /// stop being too loud is to send fewer requests, which means deciding before the
    /// request goes out rather than after it comes back. That distinction is why this
    /// is a separate decorator from <see cref="RetryProvider"/> and not a field on it.
    ///
    /// It is also the seam that keeps a game from being banned. A companion NPC that
    /// answers every step the player takes, in a scene the player is mashing through,
    /// is a request per frame, and no vendor's free tier survives that.
    ///
    /// Waiting is expressed as "still running" rather than as a sleep, so the host's
    /// frame keeps rendering and the player keeps playing while the limiter counts
    /// down. See <see cref="PollingRateLimiter"/> for why the limiter cannot simply
    /// await anything.
    /// </summary>
    public sealed class RateLimitedProvider : ILlmProvider
    {
        private readonly ILlmProvider _inner;
        private readonly PollingRateLimiter _limiter;

        public RateLimitedProvider(ILlmProvider inner, PollingRateLimiter limiter)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }
            if (limiter == null)
            {
                throw new ArgumentNullException("limiter");
            }
            _inner = inner;
            _limiter = limiter;
        }

        public ILlmProvider Inner
        {
            get { return _inner; }
        }

        public PollingRateLimiter Limiter
        {
            get { return _limiter; }
        }

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
                // The limiter exists to space out real traffic. A provider that cannot
                // serve is not traffic, and making the caller wait to be told about a
                // missing key would be a strange thing to do.
                return _inner.StartStream(request);
            }

            return new RateLimitedHandle(_inner, request, _limiter);
        }

        /// <summary>
        /// One attempt, gated. Reuses the sequence machinery so that the waiting,
        /// cancellation and event-forwarding rules are the same ones the retry and
        /// router handles obey.
        /// </summary>
        private sealed class RateLimitedHandle : AttemptSequenceHandle
        {
            private readonly ILlmProvider _inner;
            private readonly LlmRequest _request;
            private readonly PollingRateLimiter _limiter;

            public RateLimitedHandle(ILlmProvider inner, LlmRequest request, PollingRateLimiter limiter)
            {
                _inner = inner;
                _request = request;
                _limiter = limiter;
            }

            protected override AttemptDecision DecideAttempt(int attemptIndex, double nowSeconds, out double waitSeconds)
            {
                waitSeconds = 0;
                if (attemptIndex > 0)
                {
                    // Single attempt by construction. A retry wrapper adds attempts
                    // underneath or above this one; this handle only decides when the
                    // request it was handed may leave.
                    return AttemptDecision.Stop;
                }

                // The limiter keeps its own clock, so the handle's own NowSeconds is
                // deliberately not consulted here. One bucket is shared by every
                // request, and every request has its own stopwatch; mixing the two
                // would make the bucket's refill depend on which request asked.
                double delay = _limiter.DelayUntilNextToken();
                if (delay <= 0)
                {
                    return AttemptDecision.Start;
                }

                waitSeconds = delay;
                return AttemptDecision.Wait;
            }

            protected override ILlmStreamHandle StartAttempt(int attemptIndex)
            {
                // Charged here rather than in DecideAttempt so that a poll which only
                // asks the question and is never pumped again does not spend a token on
                // a request that never happened. The result is ignored on purpose: the
                // wait above already established that a token is due, and refusing to
                // send over a fraction of a token's difference would be worse than one
                // request landing slightly early.
                _limiter.TryAcquire();
                return _inner.StartStream(_request);
            }

            protected override bool ShouldContinue(int attemptIndex, LlmStreamResult result)
            {
                return false;
            }
        }
    }
}
