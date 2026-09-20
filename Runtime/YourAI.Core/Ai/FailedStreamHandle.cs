using System;
using YourAI.Core.Contracts;

namespace YourAI.Core.Ai
{
    /// <summary>
    /// A handle that is already doomed but refuses to say so until pumped.
    ///
    /// The delay is the entire point. Every handle in this framework raises its
    /// events from inside <c>Pump</c>, so a caller's handler always runs on the
    /// thread and at the moment the caller chose. Firing <c>Completed</c> from the
    /// constructor would break that guarantee for exactly the handles that are most
    /// likely to be created in bulk, and would surprise anyone whose handler touches
    /// engine state.
    ///
    /// State reads as Running until the first Pump, then Faulted. If cancelled first,
    /// it reads as Cancelled and never raises anything -- the caller said they no
    /// longer care, so reporting a failure at them would be noise.
    /// </summary>
    public sealed class FailedStreamHandle : ILlmStreamHandle
    {
        private readonly LlmStreamResult _result;
        private bool _dispatched;
        private bool _cancelled;

        public FailedStreamHandle(string error)
            : this(error, 0)
        {
        }

        public FailedStreamHandle(string error, long httpStatus)
        {
            _result = new LlmStreamResult
            {
                Success = false,
                Error = error,
                HttpStatus = httpStatus,
                Content = string.Empty,
                Reasoning = string.Empty,
            };
        }

        public LlmStreamState State
        {
            get
            {
                if (_cancelled)
                {
                    return LlmStreamState.Cancelled;
                }
                return _dispatched ? LlmStreamState.Faulted : LlmStreamState.Running;
            }
        }

        public string Content { get { return string.Empty; } }
        public string Reasoning { get { return string.Empty; } }
        public string Error { get { return _result.Error; } }

        // A handle that never produces text has nothing to report on these two.
        // Silenced rather than left to trip CS0067 on every build.
#pragma warning disable 0067
        public event Action<string> ContentDelta;
        public event Action<string> ReasoningDelta;
#pragma warning restore 0067

        public event Action<LlmStreamResult> Completed;

        public bool Pump()
        {
            if (_cancelled || _dispatched)
            {
                return false;
            }
            _dispatched = true;

            Action<LlmStreamResult> handler = Completed;
            if (handler != null)
            {
                handler(_result);
            }
            return false;
        }

        public void Cancel()
        {
            _cancelled = true;
            _dispatched = true;
        }
    }
}
