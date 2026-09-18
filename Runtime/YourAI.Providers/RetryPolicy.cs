using System;
using YourAI.Core.Contracts;

namespace YourAI.Providers
{
    /// <summary>
    /// When to try again, how long to wait, and which failures are worth answering.
    ///
    /// Separated from <see cref="RetryProvider"/> because it is the part an author
    /// actually edits. Endpoints differ: a local llama.cpp never rate limits, a
    /// paid vendor does, and a hobby project behind a flaky hotspot cares about
    /// transport failures in a way a data-centre deployment does not. Keeping the
    /// numbers in a plain object means changing them is not a source change to a
    /// hand written state machine.
    /// </summary>
    public sealed class RetryPolicy
    {
        /// <summary>
        /// Total tries, not extra tries. Three means one original plus two retries,
        /// which is the wording people expect from a "retry 3 times" setting.
        /// </summary>
        public int MaxAttempts = 3;

        /// <summary>Wait before the second attempt. The first attempt never waits.</summary>
        public double InitialDelaySeconds = 0.5;

        /// <summary>
        /// Ceiling for the exponential growth, before jitter. Without one, attempt ten
        /// would wait over four minutes and the player would have closed the game.
        /// </summary>
        public double MaxDelaySeconds = 8.0;

        /// <summary>Growth per attempt. Two doubles the wait each time.</summary>
        public double BackoffFactor = 2.0;

        /// <summary>
        /// Fraction by which a delay is randomly stretched either side of nominal.
        ///
        /// Jitter is not decoration. Every client of a rate limited endpoint that
        /// backs off by the same amount returns at the same instant, and the retry
        /// storm is the reason the endpoint rate limited in the first place. At 0.25
        /// a nominal one second wait becomes somewhere in 0.75 to 1.25 seconds.
        /// </summary>
        public double JitterFraction = 0.25;

        /// <summary>
        /// Whether a failure with no HTTP status is retried. Those are transport level:
        /// DNS, timeout, connection reset. Setting this false is right when status 0
        /// means "misconfigured base URL" in your deployment and retrying it just
        /// delays the error message.
        /// </summary>
        public bool RetryOnTransportFailure = true;

        /// <summary>
        /// Full override of the decision. When set, it replaces the built-in rules
        /// entirely, including the success case, so a caller can retry an answer it
        /// does not like as well as one that failed.
        /// </summary>
        public Func<LlmStreamResult, bool> ShouldRetry;

        /// <summary>
        /// The delay before <paramref name="attemptIndex"/> (index 0 is the first
        /// attempt and never waits), given a jitter sample in 0..1.
        ///
        /// The sample is a parameter so that the schedule is reproducible under test.
        /// A policy that drew its own random numbers could only be tested by
        /// statistics, and a flaky retry schedule is worse than a fixed one.
        /// </summary>
        public double DelayForAttempt(int attemptIndex, double jitterSample)
        {
            if (attemptIndex <= 0)
            {
                return 0;
            }

            double delay = InitialDelaySeconds;
            for (int i = 1; i < attemptIndex; i++)
            {
                delay *= BackoffFactor;
                if (delay >= MaxDelaySeconds)
                {
                    delay = MaxDelaySeconds;
                    break;
                }
            }
            if (delay > MaxDelaySeconds)
            {
                delay = MaxDelaySeconds;
            }
            if (delay < 0)
            {
                delay = 0;
            }

            double jitter = JitterFraction;
            if (jitter <= 0 || delay <= 0)
            {
                return delay;
            }
            if (jitter > 1)
            {
                jitter = 1;
            }
            if (jitterSample < 0)
            {
                jitterSample = 0;
            }
            else if (jitterSample > 1)
            {
                jitterSample = 1;
            }

            // Samples of exactly 0.5 reproduce the nominal delay, which is what makes
            // the schedule assertion in the test suite read like the spec.
            return delay * (1.0 - jitter + 2.0 * jitter * jitterSample);
        }

        /// <summary>
        /// Whether a result is worth another attempt. Deliberately not consulted when
        /// text has already reached the caller: see <see cref="AttemptSequenceHandle"/>.
        /// </summary>
        public bool IsRetryable(LlmStreamResult result)
        {
            if (ShouldRetry != null)
            {
                return ShouldRetry(result);
            }
            if (result == null)
            {
                return false;
            }

            if (result.Success)
            {
                // A stream that ended without the vendor's sentinel and produced no
                // text is a failed request wearing a success flag, typically a proxy
                // that closed the connection after the headers. Retrying is free
                // because the base class has already established that nothing was
                // shown to anybody.
                return result.Truncated && string.IsNullOrEmpty(result.Content);
            }

            if (result.HttpStatus == 0)
            {
                return RetryOnTransportFailure;
            }

            return IsRetryableStatus(result.HttpStatus);
        }

        /// <summary>
        /// The statuses that mean "later", as distinct from "differently".
        ///
        /// 429 is the obvious one. 408 and 409 and 425 are the transient client-side
        /// trio. 5xx are server-side and mostly transient. Everything else, and in
        /// particular 400, 401, 403, 404 and 422, is a statement about the request or
        /// the credentials, and repeating an identical request is guaranteed to
        /// produce an identical answer while doubling the bill.
        /// </summary>
        public static bool IsRetryableStatus(long status)
        {
            return status == 408
                || status == 409
                || status == 425
                || status == 429
                || (status >= 500 && status <= 599);
        }
    }
}
