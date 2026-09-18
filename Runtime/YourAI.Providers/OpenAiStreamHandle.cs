using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using YourAI.Core.Contracts;
using YourAI.Core.Json;
using YourAI.Core.Net;
using YourAI.Core.Protocol;

namespace YourAI.Providers
{
    /// <summary>
    /// Drives one chat-completions request, from request bytes to a finished result.
    ///
    /// Two interfaces, one object. <see cref="IHttpSink"/> is how bytes arrive;
    /// <see cref="ILlmStreamHandle"/> is how the application watches. The transport
    /// guarantees sink callbacks happen on the pumping thread, so everything in here
    /// is effectively single threaded and needs no locks.
    ///
    /// Failure policy: a request that cannot produce a usable answer reports it
    /// through the <c>Completed</c> event with <c>Success == false</c>. Nothing
    /// throws. A 429, a malformed chunk, a dropped connection, and a server-side
    /// error object are all just different sentences in the same field.
    /// </summary>
    internal sealed class OpenAiStreamHandle : ILlmStreamHandle, IHttpSink
    {
        /// <summary>Cap on retained non-stream body text, so a huge HTML error page cannot bloat a handle.</summary>
        private const int MaxBodyTextChars = 8192;

        private const int MaxErrorClip = 200;

        private readonly string _providerName;
        private readonly SseEventBuffer _sse = new SseEventBuffer();
        private readonly StringBuilder _content = new StringBuilder(1024);
        private readonly StringBuilder _reasoning = new StringBuilder(256);
        private readonly StringBuilder _bodyText = new StringBuilder(256);
        private readonly List<ToolCallBuilder> _toolCalls = new List<ToolCallBuilder>(2);
        private readonly Stopwatch _clock = new Stopwatch();

        private IHttpExchange _exchange;
        private LlmStreamState _state = LlmStreamState.Running;
        private bool _completedRaised;
        private bool _responseIsSse = true;
        private bool _serverError;
        private long _status;
        private string _error;
        private double _firstTokenMs = -1;
        private int _promptTokens;
        private int _completionTokens;

        public OpenAiStreamHandle(string providerName)
        {
            _providerName = providerName;
            _sse.EventReceived += OnSseEvent;
            _clock.Start();
        }

        // ------------------------------------------------------------ handle surface

        public LlmStreamState State
        {
            get { return _state; }
        }

        public string Content
        {
            get { return _content.ToString(); }
        }

        public string Reasoning
        {
            get { return _reasoning.ToString(); }
        }

        public string Error
        {
            get { return _error; }
        }

        public event Action<string> ContentDelta;
        public event Action<string> ReasoningDelta;
        public event Action<LlmStreamResult> Completed;

        /// <summary>Wires the exchange after the transport has created it.</summary>
        public void Attach(IHttpExchange exchange)
        {
            _exchange = exchange;
            if (exchange == null)
            {
                Fail("transport returned no exchange", 0);
            }
        }

        public bool Pump()
        {
            if (_state != LlmStreamState.Running)
            {
                return false;
            }

            IHttpExchange exchange = _exchange;
            if (exchange == null)
            {
                return false;
            }

            bool active = exchange.Pump();

            // A sink callback during Pump may already have decided the outcome.
            if (_state != LlmStreamState.Running)
            {
                return false;
            }

            if (!active)
            {
                // The exchange went quiet without reporting either outcome. That is a
                // transport bug, but it must not leave the caller pumping forever.
                _sse.Complete();
                _clock.Stop();

                if (_sse.EventCount > 0)
                {
                    Succeed();
                }
                else
                {
                    Fail("transport ended without completing or reporting a failure", _status);
                }
                return false;
            }

            return true;
        }

        public void Cancel()
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _state = LlmStreamState.Cancelled;
            _clock.Stop();

            if (_exchange != null)
            {
                _exchange.Cancel();
            }

            // No Completed event. The caller has said it stopped caring, and firing a
            // failure at them afterwards would be noise rather than information.
        }

        // --------------------------------------------------------------- sink surface

        public void OnResponseStarted(long statusCode, string contentType)
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _status = statusCode;
            _responseIsSse = string.IsNullOrEmpty(contentType)
                || contentType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void OnBodyChunk(byte[] data, int offset, int count)
        {
            if (_state != LlmStreamState.Running || count <= 0)
            {
                return;
            }

            if (_responseIsSse)
            {
                _sse.Feed(data, offset, count);
                return;
            }

            // Non-stream response: keep a bounded copy so the error can be reported.
            // Decoded leniently, because throwing a decode error while trying to
            // report a different failure would be a poor trade.
            if (_bodyText.Length < MaxBodyTextChars)
            {
                string piece = Encoding.UTF8.GetString(data, offset, count);
                int room = MaxBodyTextChars - _bodyText.Length;
                _bodyText.Append(piece.Length <= room ? piece : piece.Substring(0, room));
            }
        }

        public void OnCompleted()
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _sse.Complete();
            _clock.Stop();

            bool httpOk = _status == 0 || (_status >= 200 && _status < 300);
            if (!httpOk)
            {
                Fail(DescribeHttpFailure(), _status);
                return;
            }

            if (_serverError)
            {
                Fail(_error, _status);
                return;
            }

            if (!_responseIsSse && _sse.EventCount == 0)
            {
                // A 200 that is not a stream: either a JSON error body or a server
                // that ignored stream:true. Either way there is no answer here.
                Fail(DescribeNonStreamBody(), _status);
                return;
            }

            if (_sse.EventCount == 0 && _sse.RawBytes > 0)
            {
                Fail("response contained " + _sse.RawBytes + " bytes but no SSE events", _status);
                return;
            }

            Succeed();
        }

        public void OnFailed(string error, long statusCode)
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _clock.Stop();
            Fail(string.IsNullOrEmpty(error) ? "transport failure" : error, statusCode);
        }

        // -------------------------------------------------------------- outcome paths

        private void Succeed()
        {
            _state = LlmStreamState.Completed;
            RaiseCompleted(true);
        }

        private void Fail(string error, long statusCode)
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _error = string.IsNullOrEmpty(error) ? "unknown failure" : error;
            if (statusCode != 0)
            {
                _status = statusCode;
            }

            _state = LlmStreamState.Faulted;
            RaiseCompleted(false);
        }

        private void RaiseCompleted(bool success)
        {
            if (_completedRaised)
            {
                return;
            }
            _completedRaised = true;

            Action<LlmStreamResult> handler = Completed;
            if (handler == null)
            {
                return;
            }

            handler(new LlmStreamResult
            {
                Success = success,
                Content = _content.ToString(),
                Reasoning = _reasoning.ToString(),
                Error = _error,
                HttpStatus = _status,
                PromptTokens = _promptTokens,
                CompletionTokens = _completionTokens,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                FirstTokenMs = _firstTokenMs,
                // Only meaningful on success: a stream that ended without [DONE] may
                // still hold most of the answer, and the caller should know that.
                Truncated = success && !_sse.SawDoneMarker,
                // Likewise: a failed turn's partial calls are not executable, and
                // handing them over invites the host to run half a request.
                ToolCalls = success ? BuildToolCalls() : null,
            });
        }

        // ------------------------------------------------------------- chunk handling

        private void OnSseEvent(string payload)
        {
            JsonValue chunk;
            string parseError;
            if (!JsonParser.TryParse(payload, out chunk, out parseError))
            {
                _sse.ParseErrors++;
                return;
            }

            JsonValue usage = chunk["usage"];
            if (!usage.IsNull)
            {
                _promptTokens = usage.Path("prompt_tokens").AsInt(_promptTokens);
                _completionTokens = usage.Path("completion_tokens").AsInt(_completionTokens);
            }

            JsonValue error = chunk["error"];
            if (!error.IsNull)
            {
                _serverError = true;
                string message = error.PathString("message");
                if (string.IsNullOrEmpty(message) && error.Kind == JsonKind.String)
                {
                    message = error.StringValue;
                }
                _error = string.IsNullOrEmpty(message) ? "server reported an error" : message;
                return;
            }

            JsonValue choices = chunk["choices"];
            if (choices.IsNull || choices.Count == 0)
            {
                return;
            }

            JsonValue delta = choices[0]["delta"];
            if (delta.IsNull)
            {
                return;
            }

            string reasoning = delta["reasoning_content"].AsNonEmptyString();
            if (reasoning != null)
            {
                MarkFirstToken();
                _reasoning.Append(reasoning);

                Action<string> reasoningHandler = ReasoningDelta;
                if (reasoningHandler != null)
                {
                    reasoningHandler(reasoning);
                }
            }

            string text = delta["content"].AsNonEmptyString();
            if (text != null)
            {
                MarkFirstToken();
                _content.Append(text);

                Action<string> contentHandler = ContentDelta;
                if (contentHandler != null)
                {
                    contentHandler(text);
                }
            }

            JsonValue toolCalls = delta["tool_calls"];
            if (!toolCalls.IsNull && toolCalls.Count > 0)
            {
                MarkFirstToken();
                AccumulateToolCalls(toolCalls);
            }
        }

        /// <summary>
        /// Folds one delta's tool_calls array into the accumulated calls.
        ///
        /// This is the fiddliest part of the wire format and the part most likely to
        /// be got wrong. Tool calls do not arrive whole: the first fragment carries the
        /// id and the function name, and every later fragment carries a slice of the
        /// arguments *string* -- a JSON document cut mid-token, reassembled by
        /// concatenation. Parsing each fragment on arrival therefore fails on anything
        /// longer than one delta, and the failure looks like a malformed call rather
        /// than a streaming bug.
        ///
        /// Index is what ties fragments together. Some servers omit it on continuation
        /// fragments, which is only safe for a single call, so the array position is
        /// used as the fallback rather than dropping the fragment.
        /// </summary>
        private void AccumulateToolCalls(JsonValue toolCalls)
        {
            for (int i = 0; i < toolCalls.Count; i++)
            {
                JsonValue call = toolCalls[i];
                if (call.IsNull)
                {
                    continue;
                }

                JsonValue indexValue = call["index"];
                int index = indexValue.IsNull ? i : indexValue.AsInt(i);

                ToolCallBuilder builder = At(index);

                string id = call.PathString("id");
                if (!string.IsNullOrEmpty(id))
                {
                    builder.Id = id;
                }

                JsonValue function = call["function"];
                if (function.IsNull)
                {
                    continue;
                }

                string name = function.PathString("name");
                if (!string.IsNullOrEmpty(name))
                {
                    builder.Name = name;
                }

                JsonValue arguments = function["arguments"];
                if (!arguments.IsNull && arguments.Kind == JsonKind.String)
                {
                    builder.Arguments.Append(arguments.StringValue);
                }
            }
        }

        private ToolCallBuilder At(int index)
        {
            if (index < 0)
            {
                index = 0;
            }
            while (_toolCalls.Count <= index)
            {
                _toolCalls.Add(new ToolCallBuilder());
            }
            return _toolCalls[index];
        }

        private List<ToolInvocation> BuildToolCalls()
        {
            if (_toolCalls.Count == 0)
            {
                return null;
            }

            List<ToolInvocation> calls = new List<ToolInvocation>(_toolCalls.Count);
            for (int i = 0; i < _toolCalls.Count; i++)
            {
                ToolCallBuilder builder = _toolCalls[i];

                // A builder with no name is a slot that only ever received argument
                // fragments, which happens when a server numbers from a non-zero base.
                // Forwarding it would hand the caller a call it cannot execute.
                if (string.IsNullOrEmpty(builder.Name))
                {
                    continue;
                }

                calls.Add(new ToolInvocation
                {
                    ToolName = builder.Name,
                    CallId = builder.Id,
                    ArgumentsJson = builder.Arguments.Length > 0 ? builder.Arguments.ToString() : "{}",
                });
            }

            return calls.Count > 0 ? calls : null;
        }

        private sealed class ToolCallBuilder
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Arguments = new StringBuilder(128);
        }

        private void MarkFirstToken()
        {
            if (_firstTokenMs < 0)
            {
                _firstTokenMs = _clock.Elapsed.TotalMilliseconds;
            }
        }

        // ------------------------------------------------------------------- reporting

        private string DescribeHttpFailure()
        {
            string fromBody = TryReadErrorMessage(_bodyText.ToString());
            if (!string.IsNullOrEmpty(fromBody))
            {
                return "HTTP " + _status + " from '" + _providerName + "': " + fromBody;
            }
            return "HTTP " + _status + " from '" + _providerName + "'";
        }

        private string DescribeNonStreamBody()
        {
            string text = _bodyText.ToString();

            string message = TryReadErrorMessage(text);
            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }

            if (text.Length == 0)
            {
                return "provider '" + _providerName + "' returned an empty body";
            }

            return "unexpected non-stream response from '" + _providerName + "': " + Clip(text);
        }

        /// <summary>Digs an error message out of a response body, or returns null.</summary>
        private static string TryReadErrorMessage(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            JsonValue root;
            string parseError;
            if (!JsonParser.TryParse(text, out root, out parseError))
            {
                return null;
            }

            JsonValue error = root["error"];
            if (!error.IsNull)
            {
                if (error.Kind == JsonKind.String)
                {
                    return error.StringValue;
                }
                string nested = error.PathString("message");
                if (!string.IsNullOrEmpty(nested))
                {
                    return nested;
                }
            }

            string topLevel = root.PathString("message");
            if (!string.IsNullOrEmpty(topLevel))
            {
                return topLevel;
            }

            return null;
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder flat = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                flat.Append(c == '\n' || c == '\r' || c == '\t' ? ' ' : c);
            }

            return flat.Length <= MaxErrorClip
                ? flat.ToString()
                : flat.ToString(0, MaxErrorClip) + "...";
        }
    }
}
