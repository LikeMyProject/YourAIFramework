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
    /// <summary>Connection settings for an OpenAI-shaped endpoint.</summary>
    public sealed class OpenAiCompatibleOptions
    {
        /// <summary>
        /// Sent as a bearer token. This is the client half of the framework's "keys
        /// never ship in the build" rule -- in practice the value here is issued by
        /// the project's own gateway, not by the model vendor.
        /// </summary>
        public string ApiKey;

        /// <summary>Scheme and host, no trailing slash. e.g. https://api.deepseek.com</summary>
        public string BaseUrl = "https://api.deepseek.com";

        /// <summary>Path appended to the base URL.</summary>
        public string ChatPath = "/chat/completions";

        public string DefaultModel = "deepseek-chat";

        public int TimeoutSeconds = 90;

        /// <summary>
        /// Asks the endpoint to attach token counts to the stream. Not every
        /// OpenAI-compatible server supports it, so it can be turned off.
        /// </summary>
        public bool IncludeUsage = true;

        /// <summary>
        /// Headers added verbatim to every request. Vendors outside the strict OpenAI
        /// shape need their own identifiers here, and tests use it to steer a mock
        /// server into producing specific failure modes.
        /// </summary>
        public Dictionary<string, string> ExtraHeaders;

        public OpenAiCompatibleOptions Clone()
        {
            OpenAiCompatibleOptions copy = new OpenAiCompatibleOptions
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                ChatPath = ChatPath,
                DefaultModel = DefaultModel,
                TimeoutSeconds = TimeoutSeconds,
                IncludeUsage = IncludeUsage,
            };
            if (ExtraHeaders != null)
            {
                copy.ExtraHeaders = new Dictionary<string, string>(ExtraHeaders);
            }
            return copy;
        }
    }

    /// <summary>
    /// A provider for the OpenAI chat-completions shape, which is what most vendors
    /// now speak: DeepSeek, OpenAI itself, Moonshot, Zhipu, and the local servers
    /// people run for testing.
    ///
    /// It is written against <see cref="IHttpTransport"/> rather than a concrete
    /// HTTP library, which is the reason this class can be exercised end to end by a
    /// console program against a local mock. A provider that called UnityWebRequest
    /// directly could only ever be tested inside a running editor.
    /// </summary>
    public sealed class OpenAiCompatibleProvider : ILlmProvider
    {
        private readonly string _name;
        private readonly IHttpTransport _transport;
        private readonly OpenAiCompatibleOptions _options;

        public OpenAiCompatibleProvider(string name, IHttpTransport transport, OpenAiCompatibleOptions options)
        {
            _name = string.IsNullOrEmpty(name) ? "openai-compatible" : name;
            _transport = transport;
            _options = options ?? new OpenAiCompatibleOptions();
        }

        /// <summary>DeepSeek, whose endpoint is the reference target for this framework.</summary>
        public static OpenAiCompatibleProvider DeepSeek(IHttpTransport transport, string apiKey)
        {
            return new OpenAiCompatibleProvider("deepseek", transport, new OpenAiCompatibleOptions
            {
                ApiKey = apiKey,
                BaseUrl = "https://api.deepseek.com",
                ChatPath = "/chat/completions",
                DefaultModel = "deepseek-chat",
            });
        }

        public static OpenAiCompatibleProvider OpenAi(IHttpTransport transport, string apiKey)
        {
            return new OpenAiCompatibleProvider("openai", transport, new OpenAiCompatibleOptions
            {
                ApiKey = apiKey,
                BaseUrl = "https://api.openai.com",
                ChatPath = "/v1/chat/completions",
                DefaultModel = "gpt-4o-mini",
            });
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

        public OpenAiCompatibleOptions Options
        {
            get { return _options; }
        }

        public ILlmStreamHandle StartStream(LlmRequest request)
        {
            // Unavailable and malformed are both ordinary conditions, so both come
            // back as a faulted handle rather than as an exception.
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
            spec.SetHeader("Authorization", "Bearer " + _options.ApiKey);

            if (_options.ExtraHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in _options.ExtraHeaders)
                {
                    spec.SetHeader(header.Key, header.Value);
                }
            }

            OpenAiStreamHandle handle = new OpenAiStreamHandle(_name);
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

            string path = _options.ChatPath ?? string.Empty;
            if (path.Length > 0 && path[0] != '/')
            {
                path = "/" + path;
            }

            return baseUrl + path;
        }

        /// <summary>
        /// Hand-rolled rather than reflection-driven, because the request shape is
        /// fixed and known, and because the core assembly deliberately carries no
        /// serialisation dependency.
        /// </summary>
        internal string BuildRequestBody(LlmRequest request)
        {
            StringBuilder sb = new StringBuilder(768);
            sb.Append('{');

            string model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
            sb.Append("\"model\":\"").Append(JsonParser.Escape(model)).Append('"');

            sb.Append(",\"messages\":[");
            for (int i = 0; i < request.Messages.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                AppendMessage(sb, request.Messages[i]);
            }
            sb.Append(']');

            sb.Append(",\"stream\":").Append(request.Stream ? "true" : "false");

            if (request.Stream && _options.IncludeUsage)
            {
                sb.Append(",\"stream_options\":{\"include_usage\":true}");
            }

            AppendTools(sb, request);

            sb.Append(",\"temperature\":").Append(Number(request.Temperature));

            if (request.TopP > 0 && Math.Abs(request.TopP - 1.0) > 0.0001)
            {
                sb.Append(",\"top_p\":").Append(Number(request.TopP));
            }
            if (request.MaxTokens > 0)
            {
                sb.Append(",\"max_tokens\":").Append(request.MaxTokens.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(request.ResponseFormat))
            {
                sb.Append(",\"response_format\":{\"type\":\"")
                  .Append(JsonParser.Escape(request.ResponseFormat))
                  .Append("\"}");
            }
            if (request.Stop != null && request.Stop.Count > 0)
            {
                sb.Append(",\"stop\":[");
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
            return sb.ToString();
        }

        /// <summary>
        /// Serialises one message.
        ///
        /// Extracted because tool rounds make the shape conditional rather than
        /// uniform: an assistant turn that requested tools carries its text and the
        /// calls together, and a tool turn carries a correlation id instead of a name.
        /// </summary>
        private static void AppendMessage(StringBuilder sb, LlmMessage m)
        {
            if (m == null)
            {
                // Keep the array well formed; a null message is a host bug that
                // should not produce an unparseable request.
                sb.Append("{\"role\":\"user\",\"content\":\"\"}");
                return;
            }

            sb.Append('{');
            sb.Append("\"role\":\"").Append(RoleName(m.Role)).Append('"');

            if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                // The content key must be present even when empty: an assistant turn
                // that carries tool_calls and omits content is rejected outright.
                sb.Append(",\"content\":");
                if (string.IsNullOrEmpty(m.Content))
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append('"').Append(JsonParser.Escape(m.Content)).Append('"');
                }

                sb.Append(",\"tool_calls\":[");
                int written = 0;
                for (int i = 0; i < m.ToolCalls.Count; i++)
                {
                    ToolInvocation call = m.ToolCalls[i];
                    if (call == null || string.IsNullOrEmpty(call.ToolName))
                    {
                        continue;
                    }
                    if (written > 0)
                    {
                        sb.Append(',');
                    }

                    // arguments is a JSON *string* containing JSON. Escaping it is the
                    // whole point; embedding it raw produces a request the vendor
                    // rejects or, worse, silently misreads.
                    sb.Append("{\"id\":\"").Append(JsonParser.Escape(call.CallId ?? string.Empty))
                      .Append("\",\"type\":\"function\",\"function\":{\"name\":\"")
                      .Append(JsonParser.Escape(call.ToolName))
                      .Append("\",\"arguments\":\"")
                      .Append(JsonParser.Escape(call.ArgumentsJson ?? "{}"))
                      .Append("\"}}");
                    written++;
                }
                sb.Append(']');
            }
            else
            {
                sb.Append(",\"content\":\"").Append(JsonParser.Escape(m.Content ?? string.Empty)).Append('"');

                if (!string.IsNullOrEmpty(m.Name))
                {
                    sb.Append(",\"name\":\"").Append(JsonParser.Escape(m.Name)).Append('"');
                }
            }

            if (!string.IsNullOrEmpty(m.ToolCallId))
            {
                sb.Append(",\"tool_call_id\":\"").Append(JsonParser.Escape(m.ToolCallId)).Append('"');
            }

            sb.Append('}');
        }

        /// <summary>
        /// Emits the tools array, or nothing at all when no usable tool was offered.
        ///
        /// An empty tools array is not equivalent to omitting the key: vendors differ
        /// on whether it means "no tools" or "here are zero tools", and one of those
        /// readings invites the model to invent a call.
        /// </summary>
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

                body.Append("{\"type\":\"function\",\"function\":{\"name\":\"")
                    .Append(JsonParser.Escape(tool.Name)).Append('"');

                if (!string.IsNullOrEmpty(tool.Description))
                {
                    body.Append(",\"description\":\"").Append(JsonParser.Escape(tool.Description)).Append('"');
                }

                // The schema is embedded verbatim: it is already JSON, and wrapping it
                // in a string is exactly what vendors reject.
                body.Append(",\"parameters\":")
                    .Append(string.IsNullOrEmpty(tool.ParametersSchemaJson)
                        ? "{\"type\":\"object\",\"properties\":{}}"
                        : tool.ParametersSchemaJson)
                    .Append("}}");

                written++;
            }

            if (written == 0)
            {
                return;
            }

            sb.Append(",\"tools\":[").Append(body).Append(']');

            if (!string.IsNullOrEmpty(request.ToolChoice))
            {
                sb.Append(",\"tool_choice\":\"").Append(JsonParser.Escape(request.ToolChoice)).Append('"');
            }
        }

        private static string RoleName(LlmRole role)
        {
            switch (role)
            {
                case LlmRole.System: return "system";
                case LlmRole.Assistant: return "assistant";
                case LlmRole.Tool: return "tool";
                default: return "user";
            }
        }

        /// <summary>
        /// Formatted invariant and trimmed of trailing zeros. A plain ToString on a
        /// double under a European locale emits "0,7", which the API rejects.
        /// </summary>
        private static string Number(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
