using System;
using System.Collections.Generic;

namespace YourAI.Core.Contracts
{
    /// <summary>Who produced a message in the conversation.</summary>
    public enum LlmRole
    {
        System = 0,
        User = 1,
        Assistant = 2,
        Tool = 3,
    }

    /// <summary>One turn of conversation, in the shape every provider expects.</summary>
    public sealed class LlmMessage
    {
        public LlmRole Role;
        public string Content;

        /// <summary>Tool name, when Role is Tool.</summary>
        public string Name;

        /// <summary>Correlates a tool result with the assistant's tool call.</summary>
        public string ToolCallId;

        /// <summary>
        /// Tool calls this assistant message made, when replaying a tool round back to
        /// the model.
        ///
        /// A tool round only makes sense to the model if both halves are present: the
        /// assistant turn that asked, and the tool messages that answered. Sending the
        /// answers without the request reads as the model having spoken to itself, and
        /// vendors reject it outright or answer nonsense.
        /// </summary>
        public List<ToolInvocation> ToolCalls;

        public LlmMessage() { }

        public LlmMessage(LlmRole role, string content)
        {
            Role = role;
            Content = content;
        }

        public static LlmMessage System(string content) { return new LlmMessage(LlmRole.System, content); }
        public static LlmMessage User(string content) { return new LlmMessage(LlmRole.User, content); }
        public static LlmMessage Assistant(string content) { return new LlmMessage(LlmRole.Assistant, content); }

        /// <summary>A tool result, in the form the model expects to read it back.</summary>
        public static LlmMessage Tool(string toolCallId, string name, string content)
        {
            return new LlmMessage
            {
                Role = LlmRole.Tool,
                ToolCallId = toolCallId,
                Name = name,
                Content = content,
            };
        }
    }

    /// <summary>
    /// A tool as advertised to the model: name, description, JSON Schema.
    ///
    /// This is deliberately not <see cref="ITool"/>. What the model sees and what the
    /// game executes are two different contracts that happen to share a shape today;
    /// conflating them would mean a change to tool execution could change the prompt
    /// the model is shown.
    /// </summary>
    public sealed class ToolDefinition
    {
        public string Name;
        public string Description;
        public string ParametersSchemaJson;

        public static ToolDefinition From(ITool tool)
        {
            if (tool == null)
            {
                return null;
            }
            return new ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                ParametersSchemaJson = tool.ParametersSchemaJson,
            };
        }
    }

    /// <summary>
    /// A single inference request, expressed independently of any vendor's wire format.
    ///
    /// Temperature is a double rather than a float on purpose. A float 0.7 rounds to
    /// 0.699999988079071 and a naive serializer will happily send that to the API,
    /// which looks like a bug to whoever reads the request log.
    /// </summary>
    public sealed class LlmRequest
    {
        public string Model;
        public List<LlmMessage> Messages = new List<LlmMessage>();
        public double Temperature = 0.7;
        public double TopP = 1.0;
        public int MaxTokens;
        public bool Stream = true;

        /// <summary>Vendor hint such as "json_object"; null means free-form text.</summary>
        public string ResponseFormat;

        public List<string> Stop;

        /// <summary>Tools offered for this request. Null or empty offers none.</summary>
        public List<ToolDefinition> Tools;

        /// <summary>"auto" or "none". Null lets the vendor decide, which is "auto".</summary>
        public string ToolChoice;

        /// <summary>Free-form passthrough for vendor-specific knobs. Never required.</summary>
        public Dictionary<string, string> Extra;

        public LlmRequest Clone()
        {
            LlmRequest copy = new LlmRequest
            {
                Model = Model,
                Temperature = Temperature,
                TopP = TopP,
                MaxTokens = MaxTokens,
                Stream = Stream,
                ResponseFormat = ResponseFormat,
                ToolChoice = ToolChoice,
            };
            if (Messages != null)
            {
                copy.Messages = new List<LlmMessage>(Messages);
            }
            if (Stop != null)
            {
                copy.Stop = new List<string>(Stop);
            }
            if (Tools != null)
            {
                copy.Tools = new List<ToolDefinition>(Tools);
            }
            if (Extra != null)
            {
                copy.Extra = new Dictionary<string, string>(Extra);
            }
            return copy;
        }
    }

    /// <summary>Lifecycle of one streaming request.</summary>
    public enum LlmStreamState
    {
        Idle = 0,
        Running = 1,
        Completed = 2,
        Faulted = 3,
        Cancelled = 4,
    }

    /// <summary>Terminal outcome of a stream. Handed to <c>ILlmStreamHandle.Completed</c>.</summary>
    public sealed class LlmStreamResult
    {
        public bool Success;
        public string Content;
        public string Reasoning;
        public string Error;

        /// <summary>HTTP status when the failure came from the transport, otherwise 0.</summary>
        public long HttpStatus;

        public int PromptTokens;
        public int CompletionTokens;

        public double ElapsedMs;
        public double FirstTokenMs = -1;

        /// <summary>
        /// True when the stream ended without a vendor "[DONE]" sentinel. The text may
        /// still be usable, but the caller should treat it as possibly truncated.
        /// </summary>
        public bool Truncated;

        /// <summary>
        /// Tool calls the model emitted, in the order it emitted them, or null when it
        /// emitted none.
        ///
        /// A response may carry both text and tool calls: models routinely say "let me
        /// check" and call a tool in the same turn. Treating them as mutually
        /// exclusive loses the spoken half of the reply.
        /// </summary>
        public List<ToolInvocation> ToolCalls;

        public bool HasToolCalls
        {
            get { return ToolCalls != null && ToolCalls.Count > 0; }
        }

        public override string ToString()
        {
            if (Success)
            {
                return "ok content=" + (Content != null ? Content.Length : 0)
                     + " tokens=" + PromptTokens + "/" + CompletionTokens
                     + " ttft=" + FirstTokenMs.ToString("F0") + "ms";
            }
            return "failed(" + HttpStatus + ") " + Error;
        }
    }
}
