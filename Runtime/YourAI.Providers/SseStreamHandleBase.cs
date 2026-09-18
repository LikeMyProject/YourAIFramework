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
    /// The part of a streaming provider that is the same for every vendor.
    ///
    /// OpenAI, Anthropic and Gemini all stream server-sent events, and all three report
    /// failure as a status code or an error object rather than as an exception. What
    /// differs is only how one event's JSON is read. So the state machine, the SSE
    /// frame parsing, the delta fan-out, the failure policy and the result object live
    /// here, and a subclass supplies <see cref="ParseEvent"/>.
    ///
    /// One deliberate asymmetry: <c>OpenAiStreamHandle</c> predates this base class and
    /// was left alone rather than folded into it. It carries a dozen end-to-end
    /// assertions, and refactoring it would buy tidiness at the cost of regression risk
    /// on the path everything else already depends on. The two should be unified when
    /// the next vendor arrives and can share one test run.
    ///
    /// Threading: <see cref="IHttpSink"/> callbacks arrive on the thread that called
    /// <see cref="IHttpExchange.Pump"/>, which is the same thread that calls
    /// <see cref="Pump"/> here. No locks, by construction.
    /// </summary>
    internal abstract class SseStreamHandleBase : ILlmStreamHandle, IHttpSink
    {
        /// <summary>Cap on retained non-stream body text, so a huge HTML error page cannot bloat a handle.</summary>
        private const int MaxBodyTextChars = 8192;

        private const int MaxErrorClip = 200;

        protected readonly string ProviderName;

        private readonly SseEventBuffer _sse = new SseEventBuffer();
        private readonly StringBuilder _content = new StringBuilder(1024);
        private readonly StringBuilder _reasoning = new StringBuilder(256);
        private readonly StringBuilder _bodyText = new StringBuilder(256);
        private readonly Stopwatch _clock = new Stopwatch();

        private IHttpExchange _exchange;
        private LlmStreamState _state = LlmStreamState.Running;
        private bool _responseIsSse = true;
        private long _status;
        private string _error;
        private double _firstTokenMs = -1;

        protected SseStreamHandleBase(string providerName)
        {
            ProviderName = string.IsNullOrEmpty(providerName) ? "provider" : providerName;
            _sse.EventReceived += OnSseEvent;
            _clock.Start();
        }

        /// <summary>Input tokens, when the dialect reports them.</summary>
        protected int PromptTokens;

        /// <summary>Output tokens, when the dialect reports them.</summary>
        protected int CompletionTokens;

        /// <summary>Tool calls the dialect parser recognised, in the order the model emitted them.</summary>
        protected readonly List<ToolInvocation> ToolCalls = new List<ToolInvocation>(2);

        /// <summary>
        /// Set by the subclass when it sees the dialect's "the message is over" event.
        /// Its absence at completion is how a truncated stream is distinguished from a
        /// finished one, which matters: an answer that lost its ending should be
        /// reported as truncated rather than silently accepted.
        /// </summary>
        protected bool SawTerminalEvent;

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

                if (_content.Length > 0 || _sse.EventCount > 0)
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

            if (_exchange != null)
            {
                _exchange.Cancel();
            }

            _clock.Stop();
            _state = LlmStreamState.Cancelled;
            // Silent on purpose: whoever cancelled already stopped caring, and a
            // completion event after a cancel only invites double handling.
        }

        // -------------------------------------------------------------- sink surface

        public void OnResponseStarted(long statusCode, string contentType)
        {
            _status = statusCode;

            // Absent content type is treated as a stream, because a server that says
            // nothing is more likely to be conformant than to be sending an error page.
            _responseIsSse = string.IsNullOrEmpty(contentType)
                || contentType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void OnBodyChunk(byte[] data, int offset, int count)
        {
            if (_state != LlmStreamState.Running || data == null || count <= 0)
            {
                return;
            }

            if (!_responseIsSse)
            {
                AppendBodyText(data, offset, count);
                return;
            }

            _sse.Feed(data, offset, count);
        }

        public void OnCompleted()
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _sse.Complete();
            _clock.Stop();

            if (_status >= 400)
            {
                Fail(DescribeHttpFailure(), _status);
                return;
            }

            OnStreamEnd();

            if (_state != LlmStreamState.Running)
            {
                return;
            }

            if (_content.Length > 0 || _sse.EventCount > 0)
            {
                Succeed();
            }
            else if (_responseIsSse)
            {
                Fail("the stream closed without producing a single event", _status);
            }
            else
            {
                Fail("expected a stream, got: " + Clip(_bodyText.ToString()), _status);
            }
        }

        public void OnFailed(string error, long statusCode)
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            Fail(string.IsNullOrEmpty(error) ? DescribeHttpFailure() : Clip(error), statusCode);
        }

        // ------------------------------------------------------------- dialect hook

        /// <summary>
        /// Reads one SSE payload. Return true when the dialect's terminal event arrived,
        /// false otherwise. Throwing is tolerated: the frame is counted as a parse error
        /// and the stream continues, because one unreadable frame should not discard an
        /// answer that is otherwise intact.
        /// </summary>
        protected abstract bool ParseEvent(string data);

        /// <summary>
        /// Last chance to flush anything the dialect holds back -- an unterminated tool
        /// block, a pending usage object. Called once, before the result is built.
        /// </summary>
        protected virtual void OnStreamEnd()
        {
        }

        // ------------------------------------------------------------------ plumbing

        private void OnSseEvent(string data)
        {
            if (_state != LlmStreamState.Running || string.IsNullOrEmpty(data))
            {
                return;
            }

            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                // OpenAI's sentinel. Harmless for a dialect that never sends it.
                SawTerminalEvent = true;
                return;
            }

            bool ended;
            try
            {
                ended = ParseEvent(data);
            }
            catch (Exception)
            {
                _sse.ParseErrors++;
                return;
            }

            if (ended)
            {
                Succeed();
            }
        }

        protected void EmitDelta(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (_firstTokenMs < 0)
            {
                _firstTokenMs = _clock.Elapsed.TotalMilliseconds;
            }

            _content.Append(text);

            Action<string> handler = ContentDelta;
            if (handler != null)
            {
                handler(text);
            }
        }

        protected void EmitReasoning(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _reasoning.Append(text);

            Action<string> handler = ReasoningDelta;
            if (handler != null)
            {
                handler(text);
            }
        }

        protected void Succeed()
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _clock.Stop();
            _state = LlmStreamState.Completed;

            Raise(new LlmStreamResult
            {
                Success = true,
                Content = _content.ToString(),
                Reasoning = _reasoning.ToString(),
                PromptTokens = PromptTokens,
                CompletionTokens = CompletionTokens,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                FirstTokenMs = _firstTokenMs,
                ToolCalls = ToolCalls.Count > 0 ? ToolCalls : null,
                // A stream that stopped without its terminal event lost whatever came
                // after; saying so lets the caller decide whether to trust the text.
                Truncated = !SawTerminalEvent,
            });
        }

        protected void Fail(string error, long status)
        {
            if (_state != LlmStreamState.Running)
            {
                return;
            }

            _clock.Stop();
            _error = error;
            _state = LlmStreamState.Faulted;

            Raise(new LlmStreamResult
            {
                Success = false,
                HttpStatus = status,
                Error = error,
                Content = _content.ToString(),
                Reasoning = _reasoning.ToString(),
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                FirstTokenMs = _firstTokenMs,
            });
        }

        protected bool IsRunning
        {
            get { return _state == LlmStreamState.Running; }
        }

        /// <summary>Fails the request from inside a dialect parser, e.g. on an error event.</summary>
        protected void FailFromStream(string error)
        {
            Fail(error, _status);
        }

        // -------------------------------------------------------------- json helpers

        /// <summary>Reads a nested value without null-checking every step.</summary>
        protected static JsonValue At(JsonValue root, string path)
        {
            return root == null ? null : root.Path(path);
        }

        protected static string TextAt(JsonValue root, string path)
        {
            JsonValue value = At(root, path);
            return value == null ? null : value.AsString();
        }

        protected static int IntAt(JsonValue root, string path, int fallback)
        {
            JsonValue value = At(root, path);
            return value == null ? fallback : value.AsInt(fallback);
        }

        protected static JsonValue Parse(string data)
        {
            return JsonParser.Parse(data);
        }

        // ----------------------------------------------------------------- internal

        private void Raise(LlmStreamResult result)
        {
            Action<LlmStreamResult> handler = Completed;
            if (handler != null)
            {
                handler(result);
            }
        }

        private void AppendBodyText(byte[] data, int offset, int count)
        {
            if (_bodyText.Length >= MaxBodyTextChars)
            {
                return;
            }

            int room = MaxBodyTextChars - _bodyText.Length;
            int take = count < room ? count : room;

            try
            {
                _bodyText.Append(Encoding.UTF8.GetString(data, offset, take));
            }
            catch (Exception)
            {
                // A malformed byte sequence in an error page is not worth reporting.
            }
        }

        private string DescribeHttpFailure()
        {
            string detail = Clip(_bodyText.ToString());
            string head = "HTTP " + _status.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " from " + ProviderName;

            return string.IsNullOrEmpty(detail) ? head : head + ": " + detail;
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();
            return trimmed.Length <= MaxErrorClip ? trimmed : trimmed.Substring(0, MaxErrorClip) + "...";
        }
    }
}
