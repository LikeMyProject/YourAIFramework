using System.Collections.Generic;

namespace YourAI.Core.Contracts
{
    /// <summary>
    /// How much damage a tool can do. The framework uses this to decide how much
    /// confirmation a call needs before it reaches the game.
    ///
    /// This exists because a language model will occasionally propose something
    /// destructive with total confidence. The model's job is to say what it wants
    /// to do; classifying the blast radius is the deterministic side's job.
    /// </summary>
    public enum ToolSideEffect
    {
        /// <summary>Reads state, changes nothing. Safe to auto-execute.</summary>
        ReadOnly = 0,

        /// <summary>Changes state but can be undone. May auto-execute with logging.</summary>
        Reversible = 1,

        /// <summary>Cannot be undone. Requires explicit host approval.</summary>
        Irreversible = 2,
    }

    /// <summary>A model's request to run a tool, as parsed from its output.</summary>
    public sealed class ToolInvocation
    {
        public string ToolName;

        /// <summary>Raw JSON object of arguments, exactly as the model emitted it.</summary>
        public string ArgumentsJson;

        /// <summary>Vendor-assigned call id, echoed back with the result.</summary>
        public string CallId;

        public override string ToString()
        {
            return ToolName + "(" + ArgumentsJson + ")";
        }
    }

    /// <summary>What a tool hands back to the model.</summary>
    public sealed class ToolResult
    {
        public bool Success;

        /// <summary>Text fed back into the conversation. Keep it short and factual.</summary>
        public string Content;

        public string Error;

        public static ToolResult Ok(string content)
        {
            return new ToolResult { Success = true, Content = content };
        }

        public static ToolResult Fail(string error)
        {
            return new ToolResult { Success = false, Error = error, Content = "error: " + error };
        }
    }

    /// <summary>
    /// Something the model is allowed to ask for. Registered by name and described
    /// to the model through <see cref="ParametersSchemaJson"/>.
    ///
    /// Implementations must not throw from <see cref="Invoke"/>: a tool that blows
    /// up mid-conversation should report failure, not unwind the agent loop.
    /// </summary>
    public interface ITool
    {
        string Name { get; }
        string Description { get; }

        /// <summary>JSON Schema for the arguments object, e.g. {"type":"object",...}.</summary>
        string ParametersSchemaJson { get; }

        ToolSideEffect SideEffect { get; }

        ToolResult Invoke(ToolInvocation invocation);
    }

    /// <summary>A registry lookup performed at call time, so tools can be swapped live.</summary>
    public interface IToolRegistry
    {
        bool TryGet(string name, out ITool tool);
        IReadOnlyList<ITool> All { get; }
        void Register(ITool tool);
        bool Unregister(string name);
    }
}
