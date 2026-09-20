using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Agent
{
    /// <summary>
    /// Where a turn is in its lifecycle. Exposed because hosts want to react to
    /// stages, not only to the outcome: "thinking" versus "running tools" is the
    /// difference between a spinner and a line of dialogue.
    /// </summary>
    public enum AgentTurnState
    {
        Idle = 0,

        /// <summary>Assembling context and history into a request.</summary>
        Building = 1,

        /// <summary>Awaiting model output.</summary>
        Streaming = 2,

        /// <summary>The model asked for tools; they are running now.</summary>
        RunningTools = 3,

        /// <summary>Running output validators.</summary>
        Validating = 4,

        Completed = 5,
        Faulted = 6,
        Cancelled = 7,
    }

    /// <summary>Everything a finished turn produced, in one object.</summary>
    public sealed class AgentTurnResult
    {
        public bool Success;

        /// <summary>The text after validators ran. This is what the game should act on.</summary>
        public string Text;

        /// <summary>The model's output before validators touched it. Kept for diagnosis.</summary>
        public string RawText;

        public string Error;

        /// <summary>HTTP status when the failure came from the transport, otherwise 0.</summary>
        public long HttpStatus;

        /// <summary>How many extra request rounds were spent on tool calls.</summary>
        public int ToolRounds;

        /// <summary>How many individual tool calls were executed.</summary>
        public int ToolCallsMade;

        /// <summary>Every call the model made, in order. Useful for logs and for tests.</summary>
        public List<ToolInvocation> ToolLog;

        /// <summary>Estimated tokens of assembled context, for budgeting.</summary>
        public int ContextTokens;

        /// <summary>Context sections the budget forced out this turn.</summary>
        public int ContextSectionsDropped;

        public double ElapsedMs;

        public LlmStreamResult LastStream;

        public override string ToString()
        {
            if (!Success)
            {
                return "failed(" + HttpStatus + ") " + Error;
            }
            return "ok chars=" + (Text != null ? Text.Length : 0)
                 + " tools=" + ToolCallsMade
                 + " rounds=" + ToolRounds
                 + " ms=" + ElapsedMs.ToString("F0");
        }
    }

    /// <summary>
    /// A single exchange with an agent, driven by <see cref="Pump"/>.
    ///
    /// Nothing here is asynchronous in the language sense. The kernel has no
    /// Awaitable and no frames, so a turn is a small state machine advanced by the
    /// host: <see cref="Pump"/> returns true while there is more to do, and every
    /// callback fires on the thread that called it. That is what lets a Unity host
    /// put this in Update, a coroutine, or an editor tick without the agent knowing
    /// which.
    /// </summary>
    public interface IAgentTurn
    {
        AgentTurnState State { get; }

        /// <summary>Text so far. Grows as deltas arrive; final after validation.</summary>
        string Text { get; }

        /// <summary>True once the turn has reached a terminal state.</summary>
        bool IsDone { get; }

        /// <summary>Non-null only once the turn is done.</summary>
        AgentTurnResult Result { get; }

        /// <summary>Incremental text, on the Pump thread.</summary>
        event Action<string> Delta;

        /// <summary>
        /// Fired after each tool call completes, before the next round starts. Lets a
        /// host show what the character just did without polling.
        /// </summary>
        event Action<ToolInvocation, ToolResult> ToolFinished;

        /// <summary>Advances the turn. Returns true while more work remains.</summary>
        bool Pump();

        /// <summary>
        /// Abandons the turn. No completion or failure event follows, because the
        /// caller has already said it stopped caring.
        /// </summary>
        void Cancel();
    }
}
