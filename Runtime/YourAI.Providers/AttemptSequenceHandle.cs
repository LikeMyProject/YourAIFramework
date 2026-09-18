using System;
using System.Diagnostics;
using YourAI.Core.Contracts;

namespace YourAI.Providers
{
    /// <summary>
    /// Base for handles that run a sequence of attempts and hand the caller whichever
    /// one works: a retry after a 429, a failover to a second vendor, a wait for a
    /// token before the first byte goes out.
    ///
    /// The three shipped policies in this assembly look unrelated until you notice
    /// they all answer the same two questions: may the next attempt start yet, and
    /// given how this one ended, is there a next one. Everything else is bookkeeping,
    /// and doing that bookkeeping three times is how three implementations end up
    /// disagreeing about cancellation and about events.
    ///
    /// One rule is not policy and is enforced here rather than by the subclasses:
    ///
    ///   <b>Once any text has reached the caller, the attempt can no longer be
    ///   replaced.</b>
    ///
    /// Swap attempts after a partial answer and the player watches the same sentence
    /// begin twice. That is worse than the failure it was hiding, so a delivered
    /// prefix ends the sequence and the failure is reported as-is, partial text and
    /// all. Subclasses get told through <see cref="Delivered"/> but cannot opt out,
    /// because there is no situation in which starting over is what the user wanted.
    ///
    /// Nothing here sleeps. A waiting sequence returns true from <c>Pump</c>, the
    /// host calls it again next frame, and <see cref="WaitRemainingSeconds"/> is
    /// there for a UI that wants to say "retrying in 2s". A blocked thread during a
    /// backoff is a frozen game, and no amount of retry correctness buys that back.
    /// </summary>
    public abstract class AttemptSequenceHandle : ILlmStreamHandle
    {
        private readonly Stopwatch _clock = new Stopwatch();

        private ILlmStreamHandle _attempt;
        private LlmStreamResult _attemptResult;
        private LlmStreamState _state = LlmStreamState.Running;
        private string _content = string.Empty;
        private string _reasoning = string.Empty;
        private string _error;
        private double _lastAttemptEndedSeconds;
        private bool _dispatched;
        private bool _cancelled;

        protected AttemptSequenceHandle()
        {
            _clock.Start();
        }

        // ----------------------------------------------------------------- surface

        public LlmStreamState State
        {
            get { return _state; }
        }

        public string Content
        {
            get { return _content; }
        }

        public string Reasoning
        {
            get { return _reasoning; }
        }

        public string Error
        {
            get { return _error; }
        }

        public event Action<string> ContentDelta;
        public event Action<string> ReasoningDelta;
        public event Action<LlmStreamResult> Completed;

        /// <summary>How many attempts have been started so far. Zero before the first Pump.</summary>
        public int AttemptCount { get; private set; }

        /// <summary>True once any content or reasoning text has been handed to the caller.</summary>
        public bool Delivered { get; private set; }

        /// <summary>Seconds the next attempt is still waiting for. Zero when not waiting.</summary>
        public double WaitRemainingSeconds { get; private set; }

        /// <summary>
        /// The result of the most recent attempt, whether or not it was the last one.
        /// Read it to explain a failure that a retry went on to mask.
        /// </summary>
        public LlmStreamResult LastAttemptResult { get; private set; }

        /// <summary>
        /// Seconds since this sequence started, from a monotonic clock.
        ///
        /// Subclasses get this so that their own timing shares one epoch with the base
        /// and with each other. Two components each starting their own stopwatch is
        /// how a "wait 2s" setting becomes "wait between 0 and 2s".
        /// </summary>
        protected double NowSeconds
        {
            get { return _clock.Elapsed.TotalSeconds; }
        }

        /// <summary>When the previous attempt reached its terminal state.</summary>
        protected double LastAttemptEndedSeconds
        {
            get { return _lastAttemptEndedSeconds; }
        }

        // ------------------------------------------------------- subclass contract

        /// <summary>
        /// What the sequence should do about <c>attemptIndex</c>: start it, wait, or
        /// give up.
        ///
        /// <see cref="AttemptDecision.Stop"/> exists so that "cannot start yet" and
        /// "will never start" are different answers. With a two-valued contract a
        /// subclass that can enumerate its attempts has no way to say it is finished,
        /// and the base would sit in the waiting state forever, which in a host means
        /// a frame that never settles.
        /// </summary>
        protected enum AttemptDecision
        {
            Start = 0,

            /// <summary>Not yet. Asked again on a later Pump; nothing is consumed.</summary>
            Wait = 1,

            /// <summary>There is no such attempt. The sequence ends, reporting the last failure.</summary>
            Stop = 2,
        }

        /// <summary>
        /// Decides what to do about attempt <paramref name="attemptIndex"/>, and when
        /// the answer is <see cref="AttemptDecision.Wait"/>, roughly how long the
        /// caller is waiting for.
        ///
        /// Implementations should not sleep and should not assume they are asked at
        /// any particular cadence.
        /// </summary>
        protected abstract AttemptDecision DecideAttempt(int attemptIndex, double nowSeconds, out double waitSeconds);

        /// <summary>
        /// Builds attempt <paramref name="attemptIndex"/>. Must not return null, and
        /// must not throw; an inner provider that cannot serve returns a faulted
        /// handle, which is a normal outcome the sequence can act on.
        /// </summary>
        protected abstract ILlmStreamHandle StartAttempt(int attemptIndex);

        /// <summary>
        /// Whether another attempt should follow the one that just ended. Only asked
        /// after a non-success, and only while <see cref="Delivered"/> is false.
        /// </summary>
        protected abstract bool ShouldContinue(int attemptIndex, LlmStreamResult result);

        // ------------------------------------------------------------------- pump

        public bool Pump()
        {
            if (_cancelled || _dispatched)
            {
                return false;
            }

            if (_attempt == null)
            {
                double waitSeconds;
                AttemptDecision decision = DecideAttempt(AttemptCount, NowSeconds, out waitSeconds);

                if (decision == AttemptDecision.Wait)
                {
                    WaitRemainingSeconds = waitSeconds > 0 ? waitSeconds : 0;
                    return true;
                }

                if (decision == AttemptDecision.Stop)
                {
                    WaitRemainingSeconds = 0;
                    LlmStreamResult last = LastAttemptResult;
                    Finish(last != null
                        ? last
                        : Failed("no attempt could be started", 0));
                    return false;
                }

                WaitRemainingSeconds = 0;

                int index = AttemptCount;
                try
                {
                    _attempt = StartAttempt(index);
                }
                catch (Exception ex)
                {
                    // Pump is called from the host's update loop. An exception escaping
                    // it would abort a frame, so a misbehaving subclass or inner
                    // provider is converted into a failed attempt like any other.
                    _attempt = null;
                    Finish(Failed("attempt " + index + " threw " + ex.GetType().Name + ": " + ex.Message, 0));
                    return false;
                }

                if (_attempt == null)
                {
                    // A subclass that returns nothing would otherwise spin forever.
                    Finish(Failed("attempt " + index + " produced no handle", 0));
                    return false;
                }

                AttemptCount = index + 1;
                _attemptResult = null;
                Subscribe(_attempt);
            }

            ILlmStreamHandle attempt = _attempt;
            bool active = attempt.Pump();

            if (_attemptResult == null && attempt.State == LlmStreamState.Running)
            {
                if (active)
                {
                    return true;
                }

                // The attempt went quiet without reaching a terminal state. That is a
                // bug in the inner handle, but leaving the sequence running would hang
                // the host's update loop, so it is converted into an ordinary failure
                // of this attempt and the policy gets to decide what happens next.
                Settle(Failed("attempt ended without completing or failing", 0));
                return !_dispatched;
            }

            LlmStreamResult result = _attemptResult;
            if (result == null)
            {
                result = attempt.State == LlmStreamState.Cancelled
                    ? new LlmStreamResult { Success = false, Error = "attempt was cancelled" }
                    : Failed("attempt ended without a result", 0);
            }

            Settle(result);
            return !_dispatched;
        }

        public void Cancel()
        {
            if (_dispatched)
            {
                return;
            }

            _cancelled = true;
            _dispatched = true;

            if (_attempt != null)
            {
                Unsubscribe(_attempt);
                _attempt.Cancel();
                _attempt = null;
            }

            // Silence rather than a failure event. The caller has said it no longer
            // wants the answer, so reporting that it did not get one is noise.
            _state = LlmStreamState.Cancelled;
        }

        // -------------------------------------------------------------- internals

        /// <summary>
        /// Resolves one terminal attempt: either the whole sequence finishes, or the
        /// next attempt is scheduled.
        /// </summary>
        private void Settle(LlmStreamResult result)
        {
            ILlmStreamHandle attempt = _attempt;
            Unsubscribe(attempt);
            _attempt = null;
            _attemptResult = null;

            if (result.Success)
            {
                _content = result.Content ?? _content;
                _reasoning = result.Reasoning ?? _reasoning;
            }
            else
            {
                _content = attempt.Content ?? _content;
                _reasoning = attempt.Reasoning ?? _reasoning;
            }

            _lastAttemptEndedSeconds = NowSeconds;
            LastAttemptResult = result;

            if (result.Success)
            {
                Finish(result);
                return;
            }

            // The rule from the class comment: a delivered prefix is final.
            if (!Delivered && ShouldContinue(AttemptCount - 1, result))
            {
                return;
            }

            Finish(result);
        }

        private void Finish(LlmStreamResult result)
        {
            _dispatched = true;

            if (result.Success)
            {
                _state = LlmStreamState.Completed;
                _error = null;
                result.Content = _content;
                result.Reasoning = _reasoning;
            }
            else
            {
                _state = LlmStreamState.Faulted;
                _error = string.IsNullOrEmpty(result.Error) ? "request failed" : result.Error;

                // Report the text that was actually shown, not an empty string. A
                // mid-stream failure after two sentences is a different situation from
                // one that never started, and the caller cannot tell them apart if both
                // arrive as "no content".
                result.Content = _content;
                result.Reasoning = _reasoning;
            }

            // Total wall time across every attempt, backoff included. The winning
            // attempt's own ElapsedMs would under-report exactly the case this class
            // exists for.
            result.ElapsedMs = _clock.Elapsed.TotalMilliseconds;

            Action<LlmStreamResult> handler = Completed;
            if (handler != null)
            {
                handler(result);
            }
        }

        private void Subscribe(ILlmStreamHandle attempt)
        {
            attempt.ContentDelta += OnContentDelta;
            attempt.ReasoningDelta += OnReasoningDelta;
            attempt.Completed += OnAttemptCompleted;
        }

        private void Unsubscribe(ILlmStreamHandle attempt)
        {
            if (attempt == null)
            {
                return;
            }
            attempt.ContentDelta -= OnContentDelta;
            attempt.ReasoningDelta -= OnReasoningDelta;
            attempt.Completed -= OnAttemptCompleted;
        }

        private void OnContentDelta(string delta)
        {
            if (_dispatched || string.IsNullOrEmpty(delta))
            {
                return;
            }

            Delivered = true;
            _content = _content + delta;

            Action<string> handler = ContentDelta;
            if (handler != null)
            {
                handler(delta);
            }
        }

        private void OnReasoningDelta(string delta)
        {
            if (_dispatched || string.IsNullOrEmpty(delta))
            {
                return;
            }

            // Reasoning counts as delivered. It is text the player may already be
            // reading, and the same duplication argument applies to it.
            Delivered = true;
            _reasoning = _reasoning + delta;

            Action<string> handler = ReasoningDelta;
            if (handler != null)
            {
                handler(delta);
            }
        }

        private void OnAttemptCompleted(LlmStreamResult result)
        {
            // Recorded, not acted on. The decision belongs at the end of Pump, where
            // the handle's own state is consistent and the caller's handler sees a
            // settled object rather than one mid-transition.
            _attemptResult = result;
        }

        private static LlmStreamResult Failed(string error, long httpStatus)
        {
            return new LlmStreamResult
            {
                Success = false,
                Error = error,
                HttpStatus = httpStatus,
                Content = string.Empty,
                Reasoning = string.Empty,
            };
        }
    }
}
