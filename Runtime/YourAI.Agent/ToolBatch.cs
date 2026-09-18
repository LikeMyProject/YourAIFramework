using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Agent
{
    /// <summary>
    /// Runs the tool calls of one round, advancing several of them at a time.
    ///
    /// The core has no threads, so this is not parallelism in the usual sense. It is a
    /// scheduler: each call becomes a <see cref="IToolJob"/>, and every pump advances
    /// each in-flight job by one slice. Three slow calls therefore finish in roughly
    /// the time of one instead of the sum of all three, and no call ever blocks a frame.
    ///
    /// Two orderings live in here and they are deliberately different:
    ///
    ///   * Results are stored in *call order*. The provider requires that a round's
    ///     tool messages appear in the same order as the tool_calls it sent; a vendor
    ///     that sees them shuffled will reject the round or, worse, silently pair the
    ///     wrong result with the wrong call, and the model will act on a lie.
    ///
    ///   * The finished callback fires in *completion order*. That is the only order
    ///     in which a UI can honestly report progress -- a fast lookup should not wait
    ///     behind a slow one to be announced.
    ///
    /// Conflating the two is the bug this class exists to prevent, so the array the
    /// agent reads back is indexed by call position and the event is not.
    ///
    /// Bounded concurrency: when <see cref="MaxConcurrent"/> is positive, no more than
    /// that many jobs run at once. A model can emit ten calls in one round; a game that
    /// opened ten pathfinding queries at once would deserve what it got.
    /// </summary>
    internal sealed class ToolBatch
    {
        private sealed class Slot
        {
            public ToolInvocation Call;
            public ITool Tool;
            public IToolJob Job;
            public ToolResult Result;
            public bool Terminal;
            public bool Started;
        }

        private readonly List<ToolInvocation> _calls;
        private readonly List<Slot> _slots;
        private readonly List<int> _ready = new List<int>(4);
        private readonly List<int> _running = new List<int>(4);
        private readonly HashSet<int> _advancedThisPump = new HashSet<int>();
        private readonly List<int> _notify = new List<int>(4);
        private readonly Action<ToolInvocation, ToolResult> _onFinished;

        private readonly int _maxConcurrent;
        private bool _done;

        public ToolBatch(
            List<ToolInvocation> calls,
            IToolRegistry registry,
            Func<ToolInvocation, ITool, bool> approval,
            int maxConcurrent,
            Action<ToolInvocation, ToolResult> onFinished)
        {
            _calls = calls ?? new List<ToolInvocation>();
            _onFinished = onFinished;
            _maxConcurrent = maxConcurrent < 0 ? 0 : maxConcurrent;

            int count = _calls.Count;
            _slots = new List<Slot>(count);

            for (int i = 0; i < count; i++)
            {
                ToolInvocation call = _calls[i];
                Slot slot = new Slot { Call = call };

                // Rejection happens up front, before anything starts. Deciding it here
                // rather than mid-flight means a refused call cannot hold a concurrency
                // slot, and the model gets the same wording it would have got from the
                // fully serial path.
                string refusal = Refuse(call, registry, approval, out slot.Tool);
                if (refusal != null)
                {
                    slot.Result = ToolResult.Fail(refusal);
                    slot.Terminal = true;
                }
                else
                {
                    _ready.Add(i);
                }

                _slots.Add(slot);
            }
        }

        /// <summary>The calls, in the order the model emitted them.</summary>
        public IReadOnlyList<ToolInvocation> Calls
        {
            get { return _calls; }
        }

        /// <summary>How many calls this round contained.</summary>
        public int Count
        {
            get { return _slots.Count; }
        }

        /// <summary>Upper bound on jobs in flight at once; 0 means unbounded.</summary>
        public int MaxConcurrent
        {
            get { return _maxConcurrent; }
        }

        /// <summary>Highest number of jobs that were ever in flight together. For tests and telemetry.</summary>
        public int PeakConcurrent { get; private set; }

        public int StartedCount { get; private set; }

        public int FinishedCount { get; private set; }

        public bool IsDone
        {
            get { return _done; }
        }

        /// <summary>
        /// Results indexed by call position -- <c>Results[i]</c> belongs to <c>Calls[i]</c>,
        /// never to whichever call happened to finish i-th. Null for calls that have not
        /// settled yet.
        /// </summary>
        public ToolResult[] Results
        {
            get
            {
                ToolResult[] results = new ToolResult[_slots.Count];
                for (int i = 0; i < _slots.Count; i++)
                {
                    results[i] = _slots[i].Result;
                }
                return results;
            }
        }

        /// <summary>
        /// Advances the round. Returns true while work remains, so it reads like every
        /// other pump in the framework.
        /// </summary>
        public bool Pump()
        {
            if (_done)
            {
                return false;
            }

            _advancedThisPump.Clear();

            // The loop is not about doing more work per frame for its own sake; it is
            // about not idling. A synchronous tool finishes inside StartReady, which
            // frees its slot immediately, so the next call should start in this same
            // pump rather than waiting for the next frame. Without this, a round of
            // five synchronous tools would take five frames to run -- a visible stall
            // where the old serial code had none.
            while (true)
            {
                bool started = StartReady();
                bool advanced = AdvanceRunning();
                FlushFinished();

                if (AllTerminal())
                {
                    _done = true;
                    break;
                }

                if (!started && !advanced)
                {
                    break;
                }
            }

            return !_done;
        }

        public void Cancel()
        {
            if (_done)
            {
                return;
            }

            for (int i = 0; i < _slots.Count; i++)
            {
                Slot slot = _slots[i];
                if (slot.Terminal)
                {
                    continue;
                }

                try
                {
                    if (slot.Job != null)
                    {
                        slot.Job.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    slot.Result = ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
                }

                if (slot.Result == null)
                {
                    slot.Result = ToolResult.Fail("cancelled");
                }

                slot.Terminal = true;
                FinishedCount++;
                _notify.Add(i);
            }

            _running.Clear();
            _ready.Clear();
            FlushFinished();
            _done = true;
        }

        // ------------------------------------------------------------------ starting

        private bool StartReady()
        {
            bool any = false;

            while (_ready.Count > 0)
            {
                if (_maxConcurrent > 0 && _running.Count >= _maxConcurrent)
                {
                    break;
                }

                int index = _ready[0];
                _ready.RemoveAt(0);
                Start(index);
                any = true;
            }

            return any;
        }

        private void Start(int index)
        {
            Slot slot = _slots[index];
            slot.Started = true;
            StartedCount++;

            IDeferredTool deferred = slot.Tool as IDeferredTool;

            if (deferred == null)
            {
                // A plain ITool: run it now. Its result is final, so the slot never
                // enters the running set. This is the path every existing tool takes,
                // which is why adding the scheduler changed no observable behaviour.
                try
                {
                    slot.Result = slot.Tool.Invoke(slot.Call) ?? ToolResult.Fail("tool '" + slot.Tool.Name + "' returned no result");
                }
                catch (Exception ex)
                {
                    // ITool documents that implementations must not throw, but a host
                    // will eventually write one that does.
                    slot.Result = ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
                }

                Finish(index);
                return;
            }

            try
            {
                slot.Job = deferred.BeginInvoke(slot.Call);
            }
            catch (Exception ex)
            {
                slot.Result = ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
                Finish(index);
                return;
            }

            if (slot.Job == null)
            {
                // BeginInvoke returning null means "I have nothing to give you". The
                // model still needs an answer, so it gets a failure rather than a hang.
                slot.Result = ToolResult.Fail("deferred tool '" + slot.Tool.Name + "' returned no job");
                Finish(index);
                return;
            }

            _running.Add(index);
            if (_running.Count > PeakConcurrent)
            {
                // Recorded here rather than computed on demand because the peak is the
                // one number that cannot be recovered after the fact: by the time anyone
                // asks, the batch has drained and every job looks sequential.
                PeakConcurrent = _running.Count;
            }
        }

        // ----------------------------------------------------------------- advancing

        private bool AdvanceRunning()
        {
            bool any = false;

            // Backwards so removals do not skip entries.
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                int index = _running[i];

                // At most one slice per job per pump. Without this a job would run two
                // slices in a single frame (once here, once in the next loop pass),
                // making "how many frames until this finishes" depend on the batch size
                // -- and every timing assertion in the suite would become a lie.
                if (_advancedThisPump.Contains(index))
                {
                    continue;
                }
                _advancedThisPump.Add(index);

                Slot slot = _slots[index];

                bool going;
                try
                {
                    going = slot.Job.Pump();
                }
                catch (Exception ex)
                {
                    slot.Result = ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
                    _running.RemoveAt(i);
                    Finish(index);
                    any = true;
                    continue;
                }

                ToolJobState state = slot.Job.State;
                bool terminal = state == ToolJobState.Completed
                    || state == ToolJobState.Faulted
                    || state == ToolJobState.Cancelled;

                if (!going || terminal)
                {
                    slot.Result = slot.Job.Result ?? ToolResult.Fail("tool job ended without a result");
                    _running.RemoveAt(i);
                    Finish(index);
                    any = true;
                }
                else
                {
                    any = true;
                }
            }

            return any;
        }

        private void Finish(int index)
        {
            Slot slot = _slots[index];
            slot.Terminal = true;
            FinishedCount++;
            _notify.Add(index);
        }

        /// <summary>Fires callbacks in completion order, after the state is already final.</summary>
        private void FlushFinished()
        {
            if (_notify.Count == 0)
            {
                return;
            }

            Action<ToolInvocation, ToolResult> handler = _onFinished;
            for (int i = 0; i < _notify.Count; i++)
            {
                Slot slot = _slots[_notify[i]];
                if (handler == null)
                {
                    continue;
                }

                try
                {
                    handler(slot.Call, slot.Result);
                }
                catch (Exception)
                {
                    // A subscriber that throws is a host bug. Letting it unwind would
                    // abandon the remaining notifications and leave the turn mid-round.
                }
            }

            _notify.Clear();
        }

        private bool AllTerminal()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].Terminal)
                {
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------- rejection rules

        /// <summary>
        /// Returns a refusal message, or null when the call may run. Wording matches the
        /// serial path's, so a host parsing these strings sees no change.
        /// </summary>
        private static string Refuse(
            ToolInvocation call,
            IToolRegistry registry,
            Func<ToolInvocation, ITool, bool> approval,
            out ITool tool)
        {
            tool = null;

            if (call == null || string.IsNullOrEmpty(call.ToolName))
            {
                return "model emitted a tool call with no name";
            }

            if (registry == null || !registry.TryGet(call.ToolName, out tool) || tool == null)
            {
                return "no tool named '" + call.ToolName + "' is registered";
            }

            if (tool.SideEffect == ToolSideEffect.Irreversible && !IsApproved(approval, call, tool))
            {
                return "'" + tool.Name + "' cannot be undone, and no approval is configured for this agent";
            }

            return null;
        }

        private static bool IsApproved(Func<ToolInvocation, ITool, bool> approval, ToolInvocation call, ITool tool)
        {
            if (approval == null)
            {
                return false;
            }

            try
            {
                return approval(call, tool);
            }
            catch (Exception)
            {
                // An approval callback that throws has not approved anything.
                return false;
            }
        }
    }
}
