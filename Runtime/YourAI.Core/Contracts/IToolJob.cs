using System;

namespace YourAI.Core.Contracts
{
    /// <summary>
    /// Where a deferred tool call has got to.
    ///
    /// Four terminal-ish states rather than a bool, for the same reason
    /// AttemptDecision is an enum: "finished" is not the only way a job can stop,
    /// and a host that cannot distinguish "completed" from "cancelled" will report
    /// a cancelled job as a successful one.
    /// </summary>
    public enum ToolJobState
    {
        /// <summary>Handed out but not yet advanced.</summary>
        Pending = 0,

        /// <summary>In progress; more <c>Pump()</c> calls are expected.</summary>
        Running = 1,

        /// <summary>Finished with a usable result.</summary>
        Completed = 2,

        /// <summary>Stopped because of an error. <c>Result</c> carries the reason.</summary>
        Faulted = 3,

        /// <summary>Stopped because the turn was cancelled.</summary>
        Cancelled = 4,
    }

    /// <summary>
    /// A tool call that spans more than one frame.
    ///
    /// The core has no Task, no coroutine and no thread, so "parallel tools" cannot
    /// mean "run them at once". What it means here is that several tool calls may be
    /// *in flight simultaneously* and advanced a slice at a time -- pathfinding,
    /// asset loading, a vendor HTTP call -- each making progress on every pump, none
    /// of them blocking the frame. That is the property a game actually needs.
    ///
    /// Implementations must not throw from <see cref="Pump"/>; report failure through
    /// <see cref="State"/> and <see cref="Result"/> instead. The scheduler wraps calls
    /// defensively anyway, but a job that reports its own failure keeps its stack trace.
    /// </summary>
    public interface IToolJob
    {
        ToolJobState State { get; }

        /// <summary>
        /// The outcome. Null while the job is still running; non-null in every terminal
        /// state, including <see cref="ToolJobState.Cancelled"/>, so a caller never has
        /// to null-check its way to an explanation.
        /// </summary>
        ToolResult Result { get; }

        /// <summary>
        /// Advances one slice. Returns true while more work remains, matching
        /// <c>ILlmStreamHandle.Pump()</c> so the two read the same way inside a turn.
        /// </summary>
        bool Pump();

        /// <summary>Asks the job to stop. Must be safe to call after it has finished.</summary>
        void Cancel();
    }

    /// <summary>
    /// A tool that hands back a job instead of blocking until it is done.
    ///
    /// Implement this instead of <see cref="ITool"/> when the work is naturally long:
    /// the agent loop will start the job, keep pumping it alongside any other calls
    /// the model made in the same round, and feed the result back when it lands.
    ///
    /// A tool that implements only <see cref="ITool"/> is unaffected and still runs
    /// synchronously -- the scheduler treats "returned a value" as "a job that was
    /// already finished", so the two kinds mix freely in one round.
    /// </summary>
    public interface IDeferredTool : ITool
    {
        /// <summary>
        /// Starts the work. Must not block and must not run the work to completion
        /// inline -- that would defeat the point of being deferred. Returning null is
        /// treated as failure and reported to the model.
        /// </summary>
        IToolJob BeginInvoke(ToolInvocation invocation);
    }

    /// <summary>
    /// Convenience base for deferred tools.
    ///
    /// Subclasses implement <see cref="Advance"/> and call <see cref="Complete"/> or
    /// <see cref="Fault"/>. The base owns the state machine, the exception boundary and
    /// the terminal-state guarantees, so a subclass cannot accidentally leave a job
    /// that is neither running nor finished -- a stall that would hang the whole turn.
    /// </summary>
    public abstract class ToolJobBase : IToolJob
    {
        protected ToolJobBase(ToolInvocation invocation)
        {
            Invocation = invocation;
            State = ToolJobState.Pending;
        }

        /// <summary>The call this job is fulfilling. Available for diagnostics.</summary>
        public ToolInvocation Invocation { get; private set; }

        public ToolJobState State { get; private set; }

        public ToolResult Result { get; private set; }

        public bool IsTerminal
        {
            get
            {
                ToolJobState state = State;
                return state == ToolJobState.Completed
                    || state == ToolJobState.Faulted
                    || state == ToolJobState.Cancelled;
            }
        }

        public bool Pump()
        {
            if (IsTerminal)
            {
                return false;
            }

            if (State == ToolJobState.Pending)
            {
                State = ToolJobState.Running;
            }

            bool going;
            try
            {
                going = Advance();
            }
            catch (Exception ex)
            {
                // A subclass that throws has not finished, but it must not be left
                // running either: an exception escaping here would unwind the turn and
                // lose the conversation, and returning "still running" would stall it
                // forever. Fault is the only safe answer.
                Fault(ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            if (!going && !IsTerminal)
            {
                // Advance said it was done but never reported an outcome. That is a
                // broken job, not a successful one: reporting success would tell the
                // model that a tool it never heard from had worked, and the model would
                // act on it. Fault keeps the host and the model seeing the same thing.
                Fault("tool job stopped without reporting a result");
            }

            return !IsTerminal;
        }

        public void Cancel()
        {
            if (IsTerminal)
            {
                return;
            }

            try
            {
                OnCancel();
            }
            catch (Exception)
            {
                // Cancellation is best-effort. A throwing OnCancel must not prevent the
                // job from reaching a terminal state, or the turn would never finish.
            }

            Result = ToolResult.Fail("cancelled");
            State = ToolJobState.Cancelled;
        }

        /// <summary>
        /// One slice of work. Return true while more remains; return false when done,
        /// having called <see cref="Complete"/> or <see cref="Fault"/>.
        /// </summary>
        protected abstract bool Advance();

        protected void Complete(ToolResult result)
        {
            if (IsTerminal)
            {
                return;
            }
            Result = result ?? ToolResult.Ok("(tool produced no result)");
            State = ToolJobState.Completed;
        }

        protected void Complete(string content)
        {
            Complete(ToolResult.Ok(content));
        }

        protected void Fault(string error)
        {
            if (IsTerminal)
            {
                return;
            }
            Result = ToolResult.Fail(string.IsNullOrEmpty(error) ? "tool job failed" : error);
            State = ToolJobState.Faulted;
        }

        /// <summary>Hook for releasing resources. Called at most once, never after terminal.</summary>
        protected virtual void OnCancel()
        {
        }
    }
}
