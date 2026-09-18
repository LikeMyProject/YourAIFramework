using System;
using System.Collections.Generic;
using YourFramework.Core;

namespace YourFramework.HotUpdate
{
    /// <summary>One orchestration stage's progress.</summary>
    public enum HotUpdateStepState
    {
        /// <summary>Begin succeeded, Pump is advancing it.</summary>
        Running,
        /// <summary>Stage finished successfully.</summary>
        Done,
        /// <summary>Stage failed; the reason is authoritative for the UI.</summary>
        Failed
    }

    /// <summary>Bag the host fills before Run: URLs, versions, user data — steps read what they need.</summary>
    public sealed class HotUpdateContext
    {
        private readonly Dictionary<string, object> _values =
            new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>Sets (or replaces) a value.</summary>
        public void Set(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("HotUpdateContext.Set: key must not be empty.", "key");
            }

            _values[key] = value;
        }

        /// <summary>Reads a value, or null when absent (missing context is a step's business).</summary>
        public object Get(string key)
        {
            object value;
            return key != null && _values.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>Typed read: null when absent or of the wrong type.</summary>
        public T Get<T>(string key) where T : class
        {
            return Get(key) as T;
        }
    }

    /// <summary>
    /// One hot-update stage (check version / download patches / apply / reload
    /// assembly). Contract: polled — Begin once, then Pump every frame until it
    /// returns Done or Failed(reason). Begin/Pump must not throw to the flow:
    /// throwing is treated as Failed with the exception's reason.
    /// </summary>
    public interface IHotUpdateStep
    {
        /// <summary>Stage name (shown in progress UI and diagnostics).</summary>
        string Name { get; }

        /// <summary>Called once when the stage starts.</summary>
        void Begin(HotUpdateContext context);

        /// <summary>Advances one frame; returns the stage's progress.</summary>
        HotUpdateStepState Pump();
    }

    /// <summary>
    /// Optional side-channel for a step that knows *why* it failed.
    ///
    /// A step returning <see cref="HotUpdateStepState.Failed"/> can only say "I failed";
    /// without this the flow would report the generic "step 'X' failed" and the real
    /// reason (which file was truncated, which entry point threw) would be lost at exactly
    /// the moment a player needs it. Steps that implement this get their reason forwarded
    /// verbatim into <see cref="IHotUpdateListener.OnFailed"/>.
    /// </summary>
    public interface IHotUpdateStepFailure
    {
        /// <summary>The authoritative failure reason; a null/empty value falls back to the generic wording.</summary>
        string FailureReason { get; }
    }

    /// <summary>Game-facing progress events (all on the pump thread, in order).</summary>
    public interface IHotUpdateListener
    {
        /// <summary>A stage started.</summary>
        void OnStageStarted(string stageName, int stageIndex, int stageCount);

        /// <summary>A stage finished successfully.</summary>
        void OnStageDone(string stageName);

        /// <summary>The whole flow failed at a stage, with the reason. Terminal.</summary>
        void OnFailed(string stageName, string reason);

        /// <summary>All stages done. Terminal.</summary>
        void OnSucceeded();
    }

    /// <summary>
    /// 热更编排：版本检查 → 补丁下载 → 应用 → 程序集重载，一串可替换的阶段。
    ///
    /// 可插拔的落点：版本检查、补丁下载、程序集重载都是 IHotUpdateStep 的实现
    /// （框架自带自家实现的步骤，宿主也可以插自己的），本类只做编排 —— 顺序、
    /// 进度事件、失败截停、终态语义。纯 C#，mock 步骤离线全测。
    ///
    /// 失败也是值：任何阶段 Failed（或 Begin/Pump 抛异常）→ OnFailed(阶段名, 原因)
    /// → 终态，UI 有话可说；Run 前须 Reset。Pump 在 Idle/终态是安全空转。
    /// </summary>
    public sealed class HotUpdateFlow : IModule
    {
        private readonly IHotUpdateListener _listener;
        private readonly HotUpdateContext _context = new HotUpdateContext();
        private readonly List<IHotUpdateStep> _steps = new List<IHotUpdateStep>();
        private int _current;
        private bool _running;
        private bool _finished;

        /// <summary>Builds the flow over a listener.</summary>
        public HotUpdateFlow(IHotUpdateListener listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException("listener");
            }

            _listener = listener;
        }

        /// <summary>The shared context; fill it before Run.</summary>
        public HotUpdateContext Context { get { return _context; } }

        /// <summary>Whether a run is in progress.</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>Whether the last run finished (success or failure).</summary>
        public bool IsFinished { get { return _finished; } }

        /// <summary>Index of the current stage (or the last one touched).</summary>
        public int CurrentStageIndex { get { return _current; } }

        /// <summary>
        /// Starts a run over the given stages (executed in list order). Idle or
        /// finished states only; re-running a live flow is a wiring bug.
        /// </summary>
        public void Run(IList<IHotUpdateStep> steps)
        {
            if (steps == null || steps.Count == 0)
            {
                throw new ArgumentException("HotUpdateFlow.Run: steps must not be empty.", "steps");
            }

            if (_running || _finished)
            {
                throw new InvalidOperationException(
                    "HotUpdateFlow.Run: the flow is live or terminal. Reset() between runs.");
            }

            _steps.Clear();
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] == null)
                {
                    throw new ArgumentException("HotUpdateFlow.Run: steps contains a null entry.", "steps");
                }

                _steps.Add(steps[i]);
            }

            _current = -1;
            _finished = false;
            _running = true;
            Advance();
        }

        /// <summary>Clears terminal state so the flow can run again (context survives).</summary>
        public void Reset()
        {
            if (_running)
            {
                throw new InvalidOperationException(
                    "HotUpdateFlow.Reset: a run is in progress; let it finish or it is a wiring bug.");
            }

            _steps.Clear();
            _current = -1;
            _finished = false;
        }

        /// <summary>Advances the current stage one frame. Safe to call when idle/terminal.</summary>
        public void Pump()
        {
            if (!_running || _finished)
            {
                return;
            }

            IHotUpdateStep step = _steps[_current];
            HotUpdateStepState state;
            try
            {
                state = step.Pump();
            }
            catch (Exception ex)
            {
                Fail(step.Name, "step '" + step.Name + "' threw: "
                    + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (state == HotUpdateStepState.Done)
            {
                _listener.OnStageDone(step.Name);
                Advance();
            }
            else if (state == HotUpdateStepState.Failed)
            {
                Fail(step.Name, ReasonOf(step));
            }
            // Running: keep waiting.
        }

        /// <summary>
        /// A failed step's own reason when it volunteers one (see
        /// <see cref="IHotUpdateStepFailure"/>), otherwise the generic wording. The stage
        /// name is prefixed either way so the message stays self-describing in a log line.
        /// </summary>
        private static string ReasonOf(IHotUpdateStep step)
        {
            IHotUpdateStepFailure informative = step as IHotUpdateStepFailure;
            string reason = informative == null ? null : informative.FailureReason;
            if (string.IsNullOrEmpty(reason))
            {
                return "step '" + step.Name + "' failed";
            }

            return "step '" + step.Name + "' failed: " + reason;
        }

        /// <summary>Module plumbing.</summary>
        public string Name { get { return "HotUpdate"; } }

        /// <summary>Module plumbing: runs late, after assets/net exist.</summary>
        public int InitOrder { get { return 60; } }

        /// <summary>Module plumbing. Nothing to fetch.</summary>
        public void Init(ModuleCenter host)
        {
        }

        /// <summary>Module plumbing.</summary>
        public void Pump(float deltaSeconds)
        {
            Pump(); // hot-update flow is event-driven; delta is irrelevant to it
        }

        /// <summary>Module plumbing: a flow interrupted by shutdown is a failure with a reason.</summary>
        public void Shutdown()
        {
            if (_running && !_finished)
            {
                _running = false;
                _finished = true;
                IHotUpdateStep step = _current >= 0 && _current < _steps.Count ? _steps[_current] : null;
                _listener.OnFailed(step != null ? step.Name : "?", "flow interrupted by shutdown");
            }
        }

        private void Advance()
        {
            _current++;
            if (_current >= _steps.Count)
            {
                _running = false;
                _finished = true;
                _listener.OnSucceeded();
                return;
            }

            IHotUpdateStep step = _steps[_current];
            try
            {
                step.Begin(_context);
            }
            catch (Exception ex)
            {
                Fail(step.Name, "step '" + step.Name + "' begin threw: "
                    + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            _listener.OnStageStarted(step.Name, _current, _steps.Count);
        }

        private void Fail(string stageName, string reason)
        {
            _running = false;
            _finished = true;
            _listener.OnFailed(stageName, reason);
        }
    }
}
