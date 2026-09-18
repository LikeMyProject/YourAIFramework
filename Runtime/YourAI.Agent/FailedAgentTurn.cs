using System;
using YourAI.Core.Contracts;

namespace YourAI.Agent
{
    /// <summary>
    /// A turn that has already failed, used wherever a caller needs an
    /// <see cref="IAgentTurn"/> and there is nothing to run: no provider configured,
    /// no agent assigned, no network.
    ///
    /// It exists for the same reason FailedStreamHandle does. The framework's promise
    /// is that business code never null-checks the AI layer, so a host that writes
    /// "ask the blacksmith and show what he says" should not need a branch for "there
    /// is no blacksmith configured". It gets a result that says so, in exactly the
    /// shape every other result arrives in.
    /// </summary>
    public sealed class FailedAgentTurn : IAgentTurn
    {
        private readonly AgentTurnResult _result;

        public FailedAgentTurn(string error)
        {
            _result = new AgentTurnResult
            {
                Success = false,
                Error = string.IsNullOrEmpty(error) ? "agent unavailable" : error,
            };
        }

        public AgentTurnState State
        {
            get { return AgentTurnState.Faulted; }
        }

        public string Text
        {
            get { return string.Empty; }
        }

        public bool IsDone
        {
            get { return true; }
        }

        public AgentTurnResult Result
        {
            get { return _result; }
        }

        // Explicit empty accessors rather than field-like events. This turn never
        // raises anything, and saying that in the declaration is both clearer and
        // quieter than leaving fields the compiler reports as unused.
        public event Action<string> Delta
        {
            add { }
            remove { }
        }

        public event Action<ToolInvocation, ToolResult> ToolFinished
        {
            add { }
            remove { }
        }

        public bool Pump()
        {
            return false;
        }

        public void Cancel()
        {
        }
    }
}
