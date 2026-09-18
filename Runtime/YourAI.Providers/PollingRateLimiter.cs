using System;
using System.Diagnostics;

namespace YourAI.Providers
{
    /// <summary>
    /// A token bucket that is asked about the future instead of waiting for it.
    ///
    /// The whole reason this is not a normal rate limiter. A conventional limiter
    /// exposes <c>await WaitAsync()</c>, which needs a scheduler and a stack to
    /// suspend on. The kernel has neither: no Task, no Awaitable, no coroutine, by
    /// design, so that Core stays free of UnityEngine and the whole stack stays
    /// drivable from a single Pump call. So this type answers two questions and
    /// nothing else:
    ///
    ///   * <see cref="TryAcquire"/>: may I go now, and if so, charge me.
    ///   * <see cref="DelayUntilNextToken"/>: if not, how long until I may.
    ///
    /// The caller does the waiting, which in practice means a handle returns from
    /// Pump with "still running" and tries again next frame. Waiting therefore costs
    /// frames rather than a blocked thread, which is the correct trade in a game:
    /// a blocked thread during a 429 backoff is a frozen player.
    ///
    /// The clock is owned, not passed in. An earlier version took the current time as
    /// a parameter, which read as more testable and turned out to be a bug: a limiter
    /// is one object shared by every request, while each request's handle has its own
    /// Stopwatch, so the second request would hand the limiter a timestamp that had
    /// gone backwards relative to the first and the bucket would refuse to refill for
    /// as long as the app lived. One bucket, one clock. Tests inject a substitute
    /// through the constructor instead.
    /// </summary>
    public sealed class PollingRateLimiter
    {
        private static readonly Stopwatch SharedClock = CreateSharedClock();

        private readonly double _capacity;
        private readonly double _tokensPerSecond;
        private readonly Func<double> _clock;

        private double _tokens;
        private double _lastRefillSeconds;
        private bool _started;

        /// <param name="tokensPerSecond">
        /// Sustained refill rate. Zero or less disables the limiter entirely, which is
        /// the sane reading of "I did not ask to be throttled".
        /// </param>
        /// <param name="burst">
        /// Bucket size: how many requests may leave back to back before the sustained
        /// rate applies. Zero means "no burst", i.e. a capacity of one.
        /// </param>
        /// <param name="clock">
        /// Monotonic seconds, shared by every holder of this limiter. Defaults to a
        /// process-wide stopped-watch, because anything derived from wall clock time
        /// would mint a bucketful of tokens whenever the system clock jumped backwards.
        /// </param>
        public PollingRateLimiter(double tokensPerSecond, double burst, Func<double> clock)
        {
            _tokensPerSecond = tokensPerSecond > 0 ? tokensPerSecond : 0;
            _capacity = burst > 0 ? burst : 1;
            _clock = clock ?? SharedSeconds;
            _tokens = _capacity;
            _started = false;
        }

        public PollingRateLimiter(double tokensPerSecond, double burst)
            : this(tokensPerSecond, burst, null)
        {
        }

        /// <summary>Requests that must go through one limiter per second, sustained.</summary>
        public static PollingRateLimiter PerSecond(double requestsPerSecond)
        {
            return new PollingRateLimiter(requestsPerSecond, requestsPerSecond, null);
        }

        /// <summary>Bucket size. Never below one, so a limiter always permits a first call.</summary>
        public double Capacity
        {
            get { return _capacity; }
        }

        public double TokensPerSecond
        {
            get { return _tokensPerSecond; }
        }

        /// <summary>True when this limiter can never delay anything.</summary>
        public bool IsDisabled
        {
            get { return _tokensPerSecond <= 0; }
        }

        /// <summary>Current reading of the clock this limiter refills against.</summary>
        public double Now
        {
            get { return _clock(); }
        }

        /// <summary>Tokens currently in the bucket, after refilling.</summary>
        public double AvailableTokens()
        {
            Refill(_clock());
            return _tokens;
        }

        /// <summary>
        /// Consumes one token if the bucket has one. Returns false with nothing spent
        /// when it does not, so a refused attempt costs nothing and can simply be
        /// retried later.
        /// </summary>
        public bool TryAcquire()
        {
            if (IsDisabled)
            {
                return true;
            }

            Refill(_clock());
            if (_tokens < 1.0)
            {
                return false;
            }

            _tokens -= 1.0;
            return true;
        }

        /// <summary>
        /// Zero when a token is ready now, otherwise how many seconds until one is.
        /// The caller is expected to ask this every frame rather than to sleep.
        /// </summary>
        public double DelayUntilNextToken()
        {
            if (IsDisabled)
            {
                return 0;
            }

            Refill(_clock());
            if (_tokens >= 1.0)
            {
                return 0;
            }

            double missing = 1.0 - _tokens;
            return missing / _tokensPerSecond;
        }

        /// <summary>
        /// Empties or fills the bucket and re-anchors the refill clock. Intended for
        /// tests and for a host that wants a known-clean state after a scene load.
        /// </summary>
        public void Reset(bool empty)
        {
            _lastRefillSeconds = _clock();
            _started = true;
            _tokens = empty ? 0 : _capacity;
        }

        private void Refill(double nowSeconds)
        {
            if (!_started)
            {
                // First observation establishes the epoch. Without this, a bucket built
                // and first used a minute later would refill for that whole minute and
                // the burst cap would be meaningless.
                _started = true;
                _lastRefillSeconds = nowSeconds;
                return;
            }

            double elapsed = nowSeconds - _lastRefillSeconds;
            if (elapsed <= 0)
            {
                // A caller that goes backwards (a re-started stopwatch, a wrap) must not
                // be able to mint tokens. Treat it as no time passing.
                return;
            }

            _lastRefillSeconds = nowSeconds;
            _tokens += elapsed * _tokensPerSecond;
            if (_tokens > _capacity)
            {
                _tokens = _capacity;
            }
        }

        private static Stopwatch CreateSharedClock()
        {
            Stopwatch clock = new Stopwatch();
            clock.Start();
            return clock;
        }

        private static double SharedSeconds()
        {
            return SharedClock.Elapsed.TotalSeconds;
        }
    }
}
