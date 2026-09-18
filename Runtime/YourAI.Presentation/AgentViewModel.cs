using System;
using YourAI.Agent;
using YourAI.Core.Contracts;

namespace YourAI.Presentation
{
    /// <summary>
    /// The reactive half of the MVVM pair: a poll-driven view model over one
    /// <see cref="AiAgent"/>'s current turn.
    ///
    /// Why this exists. The agent layer already does everything a dialogue UI needs
    /// -- streaming text, turn phases, tool activity, failure values -- but it
    /// exposes them as state to pump, and a UI wants "tell me when something
    /// changed". Both are true at once and this class is the translation:
    ///
    ///     var vm = new AgentViewModel(agent);
    ///     vm.TextChanged += () => label.text = vm.Text;
    ///     vm.Send("多少钱？");
    ///
    ///     // in Update(): advances the turn, then raises each event at most once
    ///     // per actual change, on the thread that called Pump.
    ///     vm.Pump();
    ///
    /// The contract, in order of importance:
    ///
    /// 1. Events fire only on change. A UI that re-renders on every event does no
    ///    more work than it must, and a profiler sampling a quiet frame sees no
    ///    view-model cost at all.
    /// 2. Nothing blocks and nothing throws. Unavailability, refusal and failure
    ///    are return values and properties, exactly like the layers below.
    /// 3. One Pump drives everything. The view model advances the turn itself and
    ///    flushes notifications after it, so ordering is deterministic and a host
    ///    never pumps two objects in the wrong order. The host must not pump the
    ///    underlying turn separately while it is tracked here.
    ///
    /// Rebinding. Calling <see cref="Send"/> or <see cref="Track"/> while a turn is
    /// running is refused (busy), and starting a new turn clears the displayed
    /// text, error and tool name first -- a stale answer from the previous
    /// question must never sit in the input's shadow.
    /// </summary>
    public sealed class AgentViewModel
    {
        private readonly AiAgent _agent;
        private readonly string _actorId;

        private IAgentTurn _turn;

        // Cached view state. These five fields are the whole model; events are the
        // notification of the diff between the cached values and the turn's.
        private AgentTurnState _state = AgentTurnState.Idle;
        private string _text = string.Empty;
        private string _error = string.Empty;
        private string _lastTool = string.Empty;
        private bool _busy;

        /// <summary>Streaming text so far; final once the turn is done. Never null.</summary>
        public string Text { get { return _text; } }

        /// <summary>Where the tracked turn is. Idle when nothing is tracked.</summary>
        public AgentTurnState State { get { return _state; } }

        /// <summary>
        /// Why the last turn failed, empty while a turn runs and after a success.
        /// Never null; a value, not an exception.
        /// </summary>
        public string Error { get { return _error; } }

        /// <summary>Name of the most recently finished tool call, empty when none.</summary>
        public string LastTool { get { return _lastTool; } }

        /// <summary>True while a tracked turn has not reached a terminal state.</summary>
        public bool IsBusy { get { return _busy; } }

        /// <summary>False when busy or when no agent was given; Send refuses then.</summary>
        public bool CanSend { get { return !_busy && _agent != null; } }

        /// <summary>Raised when <see cref="Text"/> changed, at most once per Pump.</summary>
        public event Action TextChanged;

        /// <summary>Raised when <see cref="State"/> changed, at most once per Pump.</summary>
        public event Action StateChanged;

        /// <summary>Raised when <see cref="Error"/> changed, at most once per Pump.</summary>
        public event Action ErrorChanged;

        /// <summary>Raised when <see cref="LastTool"/> changed, at most once per Pump.</summary>
        public event Action ToolChanged;

        /// <summary>Raised when <see cref="IsBusy"/> changed, at most once per Pump.</summary>
        public event Action BusyChanged;

        /// <summary>Wraps an agent. Null is tolerated: the view model then reports
        /// CanSend=false forever, matching the framework's no-key stance.</summary>
        public AgentViewModel(AiAgent agent)
            : this(agent, null)
        {
        }

        /// <param name="actorId">Memory scope for turns started by Send. Defaults to the agent's name.</param>
        public AgentViewModel(AiAgent agent, string actorId)
        {
            _agent = agent;
            _actorId = !string.IsNullOrEmpty(actorId)
                ? actorId
                : (agent != null ? agent.Name : string.Empty);
        }

        // ------------------------------------------------------------------ driving

        /// <summary>
        /// Starts a turn from <paramref name="userMessage"/>. Returns false when
        /// busy or when no agent is wired -- refused, not thrown, and nothing else
        /// changes.
        /// </summary>
        public bool Send(string userMessage)
        {
            if (_busy || _agent == null)
            {
                return false;
            }

            Track(_agent.BeginTurn(_actorId, userMessage, null));
            return true;
        }

        /// <summary>
        /// Tracks an externally started turn, which is how a host that begins turns
        /// itself (a relay, a cutscene script) still gets the reactive surface. Any
        /// previously tracked turn is abandoned silently.
        /// </summary>
        public void Track(IAgentTurn turn)
        {
            _turn = turn;

            if (_turn != null)
            {
                _turn.ToolFinished += OnToolFinished;
            }

            // A new stage must not show the old play. Clear first, then take the
            // turn's opening state, so a busy turn reads busy from the first frame.
            SetText(string.Empty);
            SetError(string.Empty);
            SetTool(string.Empty);
            SetState(turn != null ? turn.State : AgentTurnState.Idle);
            SetBusy(turn != null && !IsTerminal(_state));
        }

        /// <summary>
        /// Advances the tracked turn and flushes change notifications. Returns true
        /// while the turn has more work, false when idle or finished -- the same
        /// rhythm as everything else in the framework.
        /// </summary>
        public bool Pump()
        {
            if (_turn == null)
            {
                return false;
            }

            bool more = _turn.Pump();
            Flush();
            return more;
        }

        /// <summary>
        /// Abandons the tracked turn. No error is fabricated: the caller asked for
        /// the stop, so "cancelled" is not a failure.
        /// </summary>
        public void Cancel()
        {
            if (_turn == null || _turn.IsDone)
            {
                return;
            }

            _turn.Cancel();
            Flush();
        }

        // ---------------------------------------------------------------- internals

        private void Flush()
        {
            SetState(_turn.State);
            SetText(Normalise(_turn.Text));

            if (_turn.IsDone)
            {
                AgentTurnResult result = _turn.Result;
                SetError(result != null && !result.Success
                    ? (string.IsNullOrEmpty(result.Error) ? "the turn failed without a reason" : result.Error)
                    : string.Empty);
            }

            // Terminal means not busy, whichever way the turn got there.
            SetBusy(!_turn.IsDone);
        }

        private void OnToolFinished(ToolInvocation call, ToolResult result)
        {
            SetTool(call != null && !string.IsNullOrEmpty(call.ToolName) ? call.ToolName : string.Empty);
        }

        private static bool IsTerminal(AgentTurnState state)
        {
            return state == AgentTurnState.Completed
                || state == AgentTurnState.Faulted
                || state == AgentTurnState.Cancelled;
        }

        private static string Normalise(string value)
        {
            return value != null ? value : string.Empty;
        }

        // Every setter is a diff. The event fires only when the value actually
        // moved, which is what keeps a quiet frame free of view-model work.

        private void SetText(string value)
        {
            if (_text == value)
            {
                return;
            }
            _text = value;
            Action handler = TextChanged;
            if (handler != null)
            {
                handler();
            }
        }

        private void SetState(AgentTurnState value)
        {
            if (_state == value)
            {
                return;
            }
            _state = value;
            Action handler = StateChanged;
            if (handler != null)
            {
                handler();
            }
        }

        private void SetError(string value)
        {
            if (_error == value)
            {
                return;
            }
            _error = value;
            Action handler = ErrorChanged;
            if (handler != null)
            {
                handler();
            }
        }

        private void SetTool(string value)
        {
            if (_lastTool == value)
            {
                return;
            }
            _lastTool = value;
            Action handler = ToolChanged;
            if (handler != null)
            {
                handler();
            }
        }

        private void SetBusy(bool value)
        {
            if (_busy == value)
            {
                return;
            }
            _busy = value;
            Action handler = BusyChanged;
            if (handler != null)
            {
                handler();
            }
        }
    }
}
