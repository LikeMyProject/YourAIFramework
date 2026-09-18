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
    /// <summary>Connection settings for the Gemini generateContent API.</summary>
    public sealed class GeminiOptions
    {
        /// <summary>
        /// Sent as the x-goog-api-key header rather than the key= query parameter the
        /// documentation also accepts. A key in a URL ends up in access logs and in
        /// whatever a proxy decides to record; a key in a header does not.
        /// </summary>
        public string ApiKey;

        /// <summary>Scheme and host, no trailing slash.</summary>
        public string BaseUrl = "https://generativelanguage.googleapis.com";

        /// <summary>API revision segment. The streaming endpoint lives under v1beta.</summary>
        public string ApiVersion = "v1beta";

        public string DefaultModel = "gemini-1.5-flash";

        public int TimeoutSeconds = 90;

        public Dictionary<string, string> ExtraHeaders;

        public GeminiOptions Clone()
        {
            GeminiOptions copy = new GeminiOptions
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                ApiVersion = ApiVersion,
                DefaultModel = DefaultModel,
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
    /// A provider for the Gemini generateContent API.
    ///
    /// This is the furthest from the OpenAI shape of the three, and the differences are
    /// structural rather than cosmetic:
    ///
    ///   * the model name is part of the URL path, not a body field;
    ///   * the assistant role is called "model";
    ///   * the system prompt is a systemInstruction object;
    ///   * sampling knobs live inside generationConfig;
    ///   * a tool result is a functionResponse part, and it is matched to its call by
    ///     *name* -- there is no call id anywhere in the protocol.
    ///
    /// That last one matters more than it looks. The framework pairs results with calls
    /// by id, so this provider synthesises one. The ordinal is stable within a round,
    /// which is all the agent loop needs.
    /// </summary>
    public sealed class GeminiProvider : ILlmProvider
    {
        private readonly string _name;
        private readonly IHttpTransport _transport;
        private readonly GeminiOptions _options;

        public GeminiProvider(string name, IHttpTransport transport, GeminiOptions options)
        {
            _name = string.IsNullOrEmpty(name) ? "gemini" : name;
            _transport = transport;
            _options = options ?? new GeminiOptions();
        }

        public static GeminiProvider Google(IHttpTransport transport, string apiKey)
        {
            return new GeminiProvider("gemini", transport, new GeminiOptions { ApiKey = apiKey });
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

        public GeminiOptions Options
        {
            get { return _options; }
        }

        public ILlmStreamHandle StartStream(LlmRequest request)
        {
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
                Url = BuildUrl(request),
                Method = "POST",
                Body = BuildRequestBody(request),
                TimeoutSeconds = _options.TimeoutSeconds,
                StreamResponse = true,
            };
            spec.SetHeader("Content-Type", "application/json");
            spec.SetHeader("Accept", "text/event-stream");
            spec.SetHeader("x-goog-api-key", _options.ApiKey);

            if (_options.ExtraHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in _options.ExtraHeaders)
                {
                    spec.SetHeader(header.Key, header.Value);
                }
            }

            GeminiStreamHandle handle = new GeminiStreamHandle(_name);
            IHttpExchange exchange = _transport.Begin(spec, handle);
            handle.Attach(exchange);
            return handle;
        }

        private string BuildUrl(LlmRequest request)
        {
            string baseUrl = _options.BaseUrl ?? string.Empty;
            while (baseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
            }

            string version = string.IsNullOrEmpty(_options.ApiVersion) ? "v1beta" : _options.ApiVersion.Trim('/');
            string model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;

            // The model lives in the path here. Uri.EscapeDataString keeps a name like
            // "models/gemini-1.5-pro" from being read as a deeper path segment.
            return baseUrl + "/" + version + "/models/" + Uri.EscapeDataString(model)
                 + ":streamGenerateContent?alt=sse";
        }

        internal string BuildRequestBody(LlmRequest request)
        {
            StringBuilder sb = new StringBuilder(768);
            sb.Append('{');

            sb.Append("\"contents\":[");
            int written = 0;
            for (int i = 0; i < request.Messages.Count; i++)
            {
                LlmMessage message = request.Messages[i];
                if (message == null || message.Role == LlmRole.System)
                {
                    // System content becomes systemInstruction below.
                    continue;
                }

                if (written > 0)
                {
                    sb.Append(',');
                }
                AppendContent(sb, message);
                written++;
            }
            sb.Append(']');

            string system = CollectSystem(request);
            if (!string.IsNullOrEmpty(system))
            {
                sb.Append(",\"systemInstruction\":{\"parts\":[{\"text\":\"")
                  .Append(JsonParser.Escape(system))
                  .Append("\"}]}");
            }

            AppendGenerationConfig(sb, request);
            AppendTools(sb, request);

            sb.Append('}');
            return sb.ToString();
        }

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

        private static void AppendContent(StringBuilder sb, LlmMessage message)
        {
            sb.Append('{');

            if (message.Role == LlmRole.Tool)
            {
                // A function result is a user-role turn carrying a functionResponse
                // part, and it is matched to its call by function name -- which is why
                // the message must carry one.
                sb.Append("\"role\":\"user\",\"parts\":[{\"functionResponse\":{\"name\":\"")
                  .Append(JsonParser.Escape(message.Name ?? string.Empty))
                  .Append("\",\"response\":{\"content\":\"")
                  .Append(JsonParser.Escape(message.Content ?? string.Empty))
                  .Append("\"}}}]}");
                return;
            }

            // "model", not "assistant".
            sb.Append("\"role\":\"")
              .Append(message.Role == LlmRole.Assistant ? "model" : "user")
              .Append("\",\"parts\":[");

            int written = 0;

            if (!string.IsNullOrEmpty(message.Content))
            {
                sb.Append("{\"text\":\"").Append(JsonParser.Escape(message.Content)).Append("\"}");
                written++;
            }

            if (message.ToolCalls != null)
            {
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

                    sb.Append("{\"functionCall\":{\"name\":\"")
                      .Append(JsonParser.Escape(call.ToolName))
                      .Append("\",\"args\":")
                      .Append(InputObject(call.ArgumentsJson))
                      .Append("}}");
                    written++;
                }
            }

            if (written == 0)
            {
                // An empty parts array is rejected, so say something rather than nothing.
                sb.Append("{\"text\":\"\"}");
            }

            sb.Append("]}");
        }

        /// <summary>Args are an object here, not JSON-in-a-string as elsewhere.</summary>
        private static string InputObject(string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson))
            {
                return "{}";
            }

            JsonValue value = JsonParser.Parse(argumentsJson);
            return value != null && value.IsObject ? argumentsJson : "{}";
        }

        private static void AppendGenerationConfig(StringBuilder sb, LlmRequest request)
        {
            bool hasTemperature = request.Temperature > 0;
            bool hasTopP = request.TopP > 0 && Math.Abs(request.TopP - 1.0) > 0.0001;
            bool hasMax = request.MaxTokens > 0;
            bool hasStop = request.Stop != null && request.Stop.Count > 0;

            if (!hasTemperature && !hasTopP && !hasMax && !hasStop)
            {
                return;
            }

            sb.Append(",\"generationConfig\":{");
            int written = 0;

            if (hasTemperature)
            {
                sb.Append("\"temperature\":").Append(Number(request.Temperature));
                written++;
            }
            if (hasMax)
            {
                if (written > 0)
                {
                    sb.Append(',');
                }
                // maxOutputTokens, not max_tokens.
                sb.Append("\"maxOutputTokens\":").Append(request.MaxTokens.ToString(CultureInfo.InvariantCulture));
                written++;
            }
            if (hasTopP)
            {
                if (written > 0)
                {
                    sb.Append(',');
                }
                sb.Append("\"topP\":").Append(Number(request.TopP));
                written++;
            }
            if (hasStop)
            {
                if (written > 0)
                {
                    sb.Append(',');
                }
                sb.Append("\"stopSequences\":[");
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

            sb.Append('}');
        }

        private static void AppendTools(StringBuilder sb, LlmRequest request)
        {
            if (request.Tools == null || request.Tools.Count == 0)
            {
                return;
            }

            StringBuilder declarations = new StringBuilder(256);
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
                    declarations.Append(',');
                }

                declarations.Append("{\"name\":\"").Append(JsonParser.Escape(tool.Name)).Append('"');

                if (!string.IsNullOrEmpty(tool.Description))
                {
                    declarations.Append(",\"description\":\"").Append(JsonParser.Escape(tool.Description)).Append('"');
                }

                // "parameters", which is also what OpenAI calls it -- the one place
                // this dialect agrees with that one.
                declarations.Append(",\"parameters\":")
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

            sb.Append(",\"tools\":[{\"functionDeclarations\":[").Append(declarations).Append("]}]");

            if (!string.IsNullOrEmpty(request.ToolChoice))
            {
                // AUTO / ANY / NONE, uppercased: this API does not accept the lowercase
                // words OpenAI uses, and it does not tell you that clearly when you send one.
                string mode = request.ToolChoice.Trim().ToUpperInvariant();
                sb.Append(",\"toolConfig\":{\"functionCallingConfig\":{\"mode\":\"")
                  .Append(JsonParser.Escape(mode))
                  .Append("\"}}");
            }
        }

        private static string Number(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Reads the generateContent stream.
    ///
    /// The shape is much flatter than the other two dialects: each event carries the
    /// candidate's parts, and the only state worth carrying across events is the tool
    /// call ordinal -- because this protocol has no call id, one has to be invented,
    /// and it has to be stable so the agent loop can pair results with calls.
    ///
    /// Text may arrive split across events, so parts are appended rather than replaced.
    /// </summary>
    internal sealed class GeminiStreamHandle : SseStreamHandleBase
    {
        public GeminiStreamHandle(string providerName) : base(providerName)
        {
        }

        protected override bool ParseEvent(string data)
        {
            JsonValue root = Parse(data);
            if (root == null || !root.IsObject)
            {
                return false;
            }

            JsonValue error = At(root, "error");
            if (error != null && !error.IsNull)
            {
                string message = TextAt(root, "error.message");
                string status = TextAt(root, "error.status");
                string detail = string.IsNullOrEmpty(message) ? data : message;
                if (!string.IsNullOrEmpty(status))
                {
                    detail = status + ": " + detail;
                }
                FailFromStream("gemini stream error: " + detail);
                return false;
            }

            JsonValue usage = At(root, "usageMetadata");
            if (usage != null && !usage.IsNull)
            {
                PromptTokens = IntAt(root, "usageMetadata.promptTokenCount", PromptTokens);
                CompletionTokens = IntAt(root, "usageMetadata.candidatesTokenCount", CompletionTokens);
            }

            JsonValue candidates = At(root, "candidates");
            if (candidates == null || !candidates.IsArray || candidates.Count == 0)
            {
                // A usage-only tail event. Nothing to read, nothing wrong.
                return false;
            }

            JsonValue candidate = candidates[0];
            if (candidate == null)
            {
                return false;
            }

            ReadParts(candidate);

            string finish = TextAt(candidate, "finishReason");
            if (!string.IsNullOrEmpty(finish))
            {
                // Any non-empty reason ends the turn, including a blocking one such as
                // SAFETY. The text already emitted stands; the caller can see the reason
                // through the content it received, and a blocked answer is an empty one.
                SawTerminalEvent = true;
                return true;
            }

            return false;
        }

        private void ReadParts(JsonValue candidate)
        {
            JsonValue parts = At(candidate, "content.parts");
            if (parts == null || !parts.IsArray)
            {
                return;
            }

            for (int i = 0; i < parts.Count; i++)
            {
                JsonValue part = parts[i];
                if (part == null)
                {
                    continue;
                }

                string text = TextAt(part, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    JsonValue thought = At(part, "thought");
                    if (thought != null && thought.AsBool(false))
                    {
                        EmitReasoning(text);
                    }
                    else
                    {
                        EmitDelta(text);
                    }
                }

                JsonValue call = At(part, "functionCall");
                if (call != null && !call.IsNull)
                {
                    AddCall(call);
                }
            }
        }

        private void AddCall(JsonValue call)
        {
            string name = TextAt(call, "name");
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            JsonValue args = At(call, "args");

            ToolCalls.Add(new ToolInvocation
            {
                // No id exists in this protocol. The ordinal is stable within a round,
                // which is everything the agent loop needs to pair a result with a call.
                CallId = "gemini_"
                    + ToolCalls.Count.ToString(CultureInfo.InvariantCulture),
                ToolName = name,
                // ToJson, not ToString: this dialect hands back a real object, so the
                // framework's JSON-string form has to be rebuilt from it. ToString would
                // have produced "{1 members}" and the tool would have received that.
                ArgumentsJson = args != null && !args.IsNull ? args.ToJson() : "{}",
            });
        }
    }
}
