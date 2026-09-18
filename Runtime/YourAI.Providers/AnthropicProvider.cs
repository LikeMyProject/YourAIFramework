using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using YourAI.Core.Ai;
using YourAI.Core.Contracts;
using YourAI.Core.Json;
using YourAI.Core.Net;

namespace YourAI.Providers
{
    /// <summary>Connection settings for the Anthropic Messages API.</summary>
    public sealed class AnthropicOptions
    {
        /// <summary>Sent as the x-api-key header, not as a bearer token.</summary>
        public string ApiKey;

        /// <summary>Scheme and host, no trailing slash.</summary>
        public string BaseUrl = "https://api.anthropic.com";

        public string MessagesPath = "/v1/messages";

        public string DefaultModel = "claude-3-5-sonnet-20241022";

        /// <summary>
        /// Required by this API and pinned to a date rather than a number. The version
        /// is what the vendor considers stable; moving it changes request semantics.
        /// </summary>
        public string ApiVersion = "2023-06-01";

        /// <summary>
        /// Used when the request sets no MaxTokens. This API requires the field, so
        /// leaving it to the vendor is not an option the way it is elsewhere.
        /// </summary>
        public int DefaultMaxTokens = 1024;

        public int TimeoutSeconds = 90;

        public Dictionary<string, string> ExtraHeaders;

        public AnthropicOptions Clone()
        {
            AnthropicOptions copy = new AnthropicOptions
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                MessagesPath = MessagesPath,
                DefaultModel = DefaultModel,
                ApiVersion = ApiVersion,
                DefaultMaxTokens = DefaultMaxTokens,
                TimeoutSeconds = TimeoutSeconds,
            };
            if (ExtraHeaders != null)
            {
                copy.ExtraHeaders = new Dictionary<string, string>(ExtraHeaders);
            }
            return copy;
        }
    }

    /// <summary>
    /// A provider for the Anthropic Messages API.
    ///
    /// Same shape as the OpenAI-compatible provider on the outside -- an
    /// <see cref="ILlmProvider"/> that returns a pollable handle -- but the wire format
    /// differs in four ways that each break a request if carried over from OpenAI:
    ///
    ///   * the system prompt is a top-level field, not a message with role "system";
    ///   * max_tokens is mandatory;
    ///   * a tool result is a user turn carrying a tool_result block, not a "tool" role;
    ///   * tool arguments are a JSON object, not a string containing JSON.
    ///
    /// The streaming side differs too: events are named, and text arrives as
    /// content_block_delta frames keyed by block index. All of that lives in
    /// <see cref="AnthropicStreamHandle"/>; everything protocol-independent is in the
    /// shared base.
    /// </summary>
    public sealed class AnthropicProvider : ILlmProvider
    {
        private readonly string _name;
        private readonly IHttpTransport _transport;
        private readonly AnthropicOptions _options;

        public AnthropicProvider(string name, IHttpTransport transport, AnthropicOptions options)
        {
            _name = string.IsNullOrEmpty(name) ? "anthropic" : name;
            _transport = transport;
            _options = options ?? new AnthropicOptions();
        }

        public static AnthropicProvider Claude(IHttpTransport transport, string apiKey)
        {
            return new AnthropicProvider("anthropic", transport, new AnthropicOptions { ApiKey = apiKey });
        }

        public string Name
        {
            get { return _name; }
        }

        public bool IsAvailable
        {
            get { return !string.IsNullOrEmpty(_options.ApiKey); }
        }

        public string UnavailableReason
        {
            get
            {
                if (string.IsNullOrEmpty(_options.ApiKey))
                {
                    return "No API key is set for provider '" + _name + "'.";
                }
                return string.Empty;
            }
        }

        public AnthropicOptions Options
        {
            get { return _options; }
        }

        public ILlmStreamHandle StartStream(LlmRequest request)
        {
            // Unavailable and malformed are ordinary conditions, so both come back as a
            // faulted handle rather than as an exception.
            if (!IsAvailable)
            {
                return new FailedStreamHandle(UnavailableReason);
            }
            if (_transport == null)
            {
                return new FailedStreamHandle("Provider '" + _name + "' has no transport wired up.");
            }
            if (request == null)
            {
                return new FailedStreamHandle("request is null");
            }
            if (request.Messages == null || request.Messages.Count == 0)
            {
                return new FailedStreamHandle("request carries no messages");
            }

            HttpRequestSpec spec = new HttpRequestSpec
            {
                Url = BuildUrl(),
                Method = "POST",
                Body = BuildRequestBody(request),
                TimeoutSeconds = _options.TimeoutSeconds,
                StreamResponse = true,
            };
            spec.SetHeader("Content-Type", "application/json");
            spec.SetHeader("Accept", "text/event-stream");
            spec.SetHeader("x-api-key", _options.ApiKey);
            spec.SetHeader("anthropic-version", _options.ApiVersion);

            if (_options.ExtraHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in _options.ExtraHeaders)
                {
                    spec.SetHeader(header.Key, header.Value);
                }
            }

            AnthropicStreamHandle handle = new AnthropicStreamHandle(_name);
            IHttpExchange exchange = _transport.Begin(spec, handle);
            handle.Attach(exchange);
            return handle;
        }

        private string BuildUrl()
        {
            string baseUrl = _options.BaseUrl ?? string.Empty;
            while (baseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
            }

            string path = _options.MessagesPath ?? string.Empty;
            if (path.Length > 0 && path[0] != '/')
            {
                path = "/" + path;
            }

            return baseUrl + path;
        }

        internal string BuildRequestBody(LlmRequest request)
        {
            StringBuilder sb = new StringBuilder(768);
            sb.Append('{');

            string model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
            sb.Append("\"model\":\"").Append(JsonParser.Escape(model)).Append('"');

            // Mandatory here. Falling back to the configured default rather than
            // omitting the key is the difference between a working request and a 400.
            int maxTokens = request.MaxTokens > 0 ? request.MaxTokens : _options.DefaultMaxTokens;
            sb.Append(",\"max_tokens\":").Append(maxTokens.ToString(CultureInfo.InvariantCulture));

            string system = CollectSystem(request);
            if (!string.IsNullOrEmpty(system))
            {
                sb.Append(",\"system\":\"").Append(JsonParser.Escape(system)).Append('"');
            }

            sb.Append(",\"messages\":[");
            int written = 0;
            for (int i = 0; i < request.Messages.Count; i++)
            {
                LlmMessage message = request.Messages[i];
                if (message == null || message.Role == LlmRole.System)
                {
                    // System content moved to the top-level field above. Leaving it in
                    // the array as well would duplicate the persona in the prompt.
                    continue;
                }

                if (written > 0)
                {
                    sb.Append(',');
                }
                AppendMessage(sb, message);
                written++;
            }
            sb.Append(']');

            sb.Append(",\"stream\":").Append(request.Stream ? "true" : "false");

            // This API accepts 0..1. A host that tuned Temperature for an OpenAI-range
            // endpoint and then switched vendors will be asking for values that are
            // rejected, so the value is passed through and the vendor decides.
            if (request.Temperature > 0)
            {
                sb.Append(",\"temperature\":").Append(Number(request.Temperature));
            }
            if (request.TopP > 0 && Math.Abs(request.TopP - 1.0) > 0.0001)
            {
                sb.Append(",\"top_p\":").Append(Number(request.TopP));
            }
            if (request.Stop != null && request.Stop.Count > 0)
            {
                sb.Append(",\"stop_sequences\":[");
                for (int i = 0; i < request.Stop.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append('"').Append(JsonParser.Escape(request.Stop[i] ?? string.Empty)).Append('"');
                }
                sb.Append(']');
            }

            AppendTools(sb, request);

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Merges every system message into the single top-level string.</summary>
        private static string CollectSystem(LlmRequest request)
        {
            StringBuilder sb = new StringBuilder(256);
            for (int i = 0; i < request.Messages.Count; i++)
            {
                LlmMessage message = request.Messages[i];
                if (message == null || message.Role != LlmRole.System || string.IsNullOrEmpty(message.Content))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append("\n\n");
                }
                sb.Append(message.Content);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static void AppendMessage(StringBuilder sb, LlmMessage message)
        {
            sb.Append('{');

            if (message.Role == LlmRole.Tool)
            {
                // Not a role of its own in this API: it is a user turn whose content is
                // a tool_result block. Sending role:"tool" is rejected outright.
                sb.Append("\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"")
                  .Append(JsonParser.Escape(message.ToolCallId ?? string.Empty))
                  .Append("\",\"content\":\"")
                  .Append(JsonParser.Escape(message.Content ?? string.Empty))
                  .Append("\"}]}");
                return;
            }

            // There is no "assistant" role problem here, but there is no "system" one
            // either: anything that is not assistant is user.
            sb.Append("\"role\":\"").Append(message.Role == LlmRole.Assistant ? "assistant" : "user").Append('"');

            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                sb.Append(",\"content\":[");
                int written = 0;

                // Text first, then the calls. The order matters: a model shown its own
                // tool_use before its sentence reads the sentence as an afterthought.
                if (!string.IsNullOrEmpty(message.Content))
                {
                    sb.Append("{\"type\":\"text\",\"text\":\"")
                      .Append(JsonParser.Escape(message.Content))
                      .Append("\"}");
                    written++;
                }

                for (int i = 0; i < message.ToolCalls.Count; i++)
                {
                    ToolInvocation call = message.ToolCalls[i];
                    if (call == null || string.IsNullOrEmpty(call.ToolName))
                    {
                        continue;
                    }

                    if (written > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append("{\"type\":\"tool_use\",\"id\":\"")
                      .Append(JsonParser.Escape(call.CallId ?? string.Empty))
                      .Append("\",\"name\":\"")
                      .Append(JsonParser.Escape(call.ToolName))
                      .Append("\",\"input\":")
                      .Append(InputObject(call.ArgumentsJson))
                      .Append('}');
                    written++;
                }

                if (written == 0)
                {
                    // An empty content array is rejected, so a message that carried
                    // nothing usable still has to say something.
                    sb.Append("{\"type\":\"text\",\"text\":\"\"}");
                }

                sb.Append(']');
            }
            else
            {
                sb.Append(",\"content\":\"").Append(JsonParser.Escape(message.Content ?? string.Empty)).Append('"');
            }

            sb.Append('}');
        }

        /// <summary>
        /// The model's arguments arrive as JSON-in-a-string everywhere, but this API
        /// wants the object itself. Re-emitting a string would double-encode it.
        ///
        /// If the fragment does not parse it is replaced with an empty object rather
        /// than passed through: a rejected request costs the whole turn, and a tool
        /// that receives {} can usually say what it was missing.
        /// </summary>
        private static string InputObject(string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson))
            {
                return "{}";
            }

            JsonValue value = JsonParser.Parse(argumentsJson);
            if (value != null && value.IsObject)
            {
                return argumentsJson;
            }

            return "{}";
        }

        private static void AppendTools(StringBuilder sb, LlmRequest request)
        {
            if (request.Tools == null || request.Tools.Count == 0)
            {
                return;
            }

            StringBuilder body = new StringBuilder(256);
            int written = 0;

            for (int i = 0; i < request.Tools.Count; i++)
            {
                ToolDefinition tool = request.Tools[i];
                if (tool == null || string.IsNullOrEmpty(tool.Name))
                {
                    continue;
                }

                if (written > 0)
                {
                    body.Append(',');
                }

                body.Append("{\"name\":\"").Append(JsonParser.Escape(tool.Name)).Append('"');

                if (!string.IsNullOrEmpty(tool.Description))
                {
                    body.Append(",\"description\":\"").Append(JsonParser.Escape(tool.Description)).Append('"');
                }

                // input_schema, not parameters. The name is different and the shape is
                // the same, which is precisely the kind of difference that survives a
                // copy-paste and fails at runtime.
                body.Append(",\"input_schema\":")
                    .Append(string.IsNullOrEmpty(tool.ParametersSchemaJson)
                        ? "{\"type\":\"object\",\"properties\":{}}"
                        : tool.ParametersSchemaJson)
                    .Append('}');

                written++;
            }

            if (written == 0)
            {
                return;
            }

            sb.Append(",\"tools\":[").Append(body).Append(']');

            if (!string.IsNullOrEmpty(request.ToolChoice))
            {
                // This API takes an object with a type, not the bare string OpenAI takes.
                sb.Append(",\"tool_choice\":{\"type\":\"")
                  .Append(JsonParser.Escape(request.ToolChoice))
                  .Append("\"}");
            }
        }

        private static string Number(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Reads the Messages API stream.
    ///
    /// Text does not arrive as a self-contained event the way it does elsewhere: it
    /// arrives as fragments addressed to a numbered content block, and a block's kind
    /// (text, thinking, tool_use) is only announced at its start. So this keeps a small
    /// map of open blocks, and a tool call is only complete once its block stops.
    ///
    /// Tool arguments arrive as string fragments that are not valid JSON until the last
    /// one lands, which is why they are accumulated and parsed at the close rather than
    /// read per event.
    /// </summary>
    internal sealed class AnthropicStreamHandle : SseStreamHandleBase
    {
        private sealed class Block
        {
            public string Kind;
            public string Id;
            public string Name;
            public StringBuilder Arguments;
        }

        private readonly Dictionary<int, Block> _blocks = new Dictionary<int, Block>(4);

        public AnthropicStreamHandle(string providerName) : base(providerName)
        {
        }

        protected override bool ParseEvent(string data)
        {
            JsonValue root = Parse(data);
            if (root == null || !root.IsObject)
            {
                return false;
            }

            string type = TextAt(root, "type");
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            switch (type)
            {
                case "message_start":
                    PromptTokens = IntAt(root, "message.usage.input_tokens", PromptTokens);
                    return false;

                case "content_block_start":
                    BeginBlock(root);
                    return false;

                case "content_block_delta":
                    AppendBlockDelta(root);
                    return false;

                case "content_block_stop":
                    CloseBlock(root);
                    return false;

                case "message_delta":
                    CompletionTokens = IntAt(root, "usage.output_tokens", CompletionTokens);
                    return false;

                case "message_stop":
                    SawTerminalEvent = true;
                    return true;

                case "error":
                    // The vendor's way of failing a request that already returned 200.
                    string message = TextAt(root, "error.message");
                    string kind = TextAt(root, "error.type");
                    string detail = string.IsNullOrEmpty(message) ? data : message;
                    if (!string.IsNullOrEmpty(kind))
                    {
                        detail = kind + ": " + detail;
                    }
                    FailFromStream("anthropic stream error: " + detail);
                    return false;

                default:
                    // ping and any future event type: not our business, and not an error.
                    return false;
            }
        }

        private void BeginBlock(JsonValue root)
        {
            int index = IntAt(root, "index", -1);
            JsonValue block = At(root, "content_block");
            if (index < 0 || block == null)
            {
                return;
            }

            string kind = TextAt(block, "type");
            Block entry = new Block { Kind = kind };

            // A text or thinking block carries its opening characters inline, so they
            // have to be consumed here or the answer loses its first word.
            if (kind == "text")
            {
                EmitDelta(TextAt(block, "text"));
            }
            else if (kind == "thinking")
            {
                EmitReasoning(TextAt(block, "thinking"));
            }
            else if (kind == "tool_use")
            {
                entry.Id = TextAt(block, "id");
                entry.Name = TextAt(block, "name");
                entry.Arguments = new StringBuilder(64);
            }

            _blocks[index] = entry;
        }

        private void AppendBlockDelta(JsonValue root)
        {
            int index = IntAt(root, "index", -1);
            JsonValue delta = At(root, "delta");
            if (index < 0 || delta == null)
            {
                return;
            }

            string kind = TextAt(delta, "type");

            if (kind == "text_delta")
            {
                EmitDelta(TextAt(delta, "text"));
                return;
            }

            if (kind == "thinking_delta")
            {
                EmitReasoning(TextAt(delta, "thinking"));
                return;
            }

            if (kind == "input_json_delta")
            {
                Block block;
                if (_blocks.TryGetValue(index, out block) && block.Arguments != null)
                {
                    // A fragment of the JSON string, not JSON itself. Joining them and
                    // parsing at close is the only correct way to read this.
                    block.Arguments.Append(TextAt(delta, "partial_json"));
                }
            }
        }

        private void CloseBlock(JsonValue root)
        {
            int index = IntAt(root, "index", -1);
            if (index < 0)
            {
                return;
            }

            Block block;
            if (!_blocks.TryGetValue(index, out block))
            {
                return;
            }
            _blocks.Remove(index);

            if (block.Kind != "tool_use" || string.IsNullOrEmpty(block.Name))
            {
                return;
            }

            string arguments = block.Arguments != null ? block.Arguments.ToString() : null;
            if (string.IsNullOrEmpty(arguments))
            {
                // A tool with no arguments is legal, and the framework represents that
                // as an empty object rather than an empty string.
                arguments = "{}";
            }

            ToolCalls.Add(new ToolInvocation
            {
                CallId = block.Id,
                ToolName = block.Name,
                ArgumentsJson = arguments,
            });
        }

        protected override void OnStreamEnd()
        {
            // A stream that ended without closing its last block still told us enough
            // to answer; salvage the tool call rather than dropping it.
            if (_blocks.Count == 0)
            {
                return;
            }

            List<int> indices = new List<int>(_blocks.Keys);
            for (int i = 0; i < indices.Count; i++)
            {
                int index = indices[i];
                Block block;
                if (!_blocks.TryGetValue(index, out block))
                {
                    continue;
                }

                if (block.Kind != "tool_use" || string.IsNullOrEmpty(block.Name))
                {
                    continue;
                }

                string arguments = block.Arguments != null && block.Arguments.Length > 0
                    ? block.Arguments.ToString()
                    : "{}";

                ToolCalls.Add(new ToolInvocation
                {
                    CallId = block.Id,
                    ToolName = block.Name,
                    ArgumentsJson = arguments,
                });
            }

            _blocks.Clear();
        }
    }
}
