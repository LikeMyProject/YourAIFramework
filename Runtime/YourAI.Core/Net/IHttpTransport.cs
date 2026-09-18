using System;
using System.Collections.Generic;

namespace YourAI.Core.Net
{
    /// <summary>Lifecycle of one HTTP exchange.</summary>
    public enum HttpExchangeState
    {
        Pending = 0,
        Receiving = 1,
        Completed = 2,
        Faulted = 3,
        Cancelled = 4,
    }

    /// <summary>A request, expressed without reference to any HTTP library.</summary>
    public sealed class HttpRequestSpec
    {
        public string Url;
        public string Method = "POST";

        /// <summary>Raw request body. Already serialised; the transport does not inspect it.</summary>
        public string Body;

        public Dictionary<string, string> Headers;

        public int TimeoutSeconds = 60;

        /// <summary>
        /// When true the transport should deliver the body incrementally rather than
        /// buffering it. Streaming responses are the point of this interface; a
        /// transport that ignores the flag will still work, just without deltas.
        /// </summary>
        public bool StreamResponse = true;

        public void SetHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            if (Headers == null)
            {
                Headers = new Dictionary<string, string>();
            }
            Headers[name] = value;
        }
    }

    /// <summary>
    /// Receives a response body in pieces.
    ///
    /// Threading contract, and it is the same one the rest of the framework uses:
    /// every callback here runs on the thread that called
    /// <see cref="IHttpExchange.Pump"/>. A transport whose underlying library
    /// delivers bytes on a background thread must queue them and let Pump drain
    /// the queue -- <c>SseFrameQueue</c> exists for exactly that. Callers may
    /// therefore parse, accumulate, and touch engine state directly inside these
    /// methods.
    ///
    /// Buffer lifetime: <c>data</c> is only valid for the duration of the call.
    /// Transports are allowed to reuse one buffer across deliveries. Anything worth
    /// keeping must be copied.
    /// </summary>
    public interface IHttpSink
    {
        /// <summary>
        /// Response headers have arrived, so the status code is final.
        /// <paramref name="contentType"/> is the raw Content-Type header, or null.
        /// It is passed separately rather than as a header dictionary because the
        /// only decision callers make from headers is "is this a stream", and
        /// building a dictionary per request to answer that is waste.
        /// </summary>
        void OnResponseStarted(long statusCode, string contentType);

        /// <summary>A piece of the response body. May be called zero or many times.</summary>
        void OnBodyChunk(byte[] data, int offset, int count);

        /// <summary>Body finished cleanly.</summary>
        void OnCompleted();

        /// <summary>
        /// Failed, potentially before any body arrived. Reached at most once, and
        /// never after <see cref="OnCompleted"/>.
        /// </summary>
        void OnFailed(string error, long statusCode);
    }

    /// <summary>
    /// One in-flight exchange. Poll it; do not await it. The kernel has no access to
    /// Awaitable and no notion of frames, so progress is driven from outside.
    /// </summary>
    public interface IHttpExchange
    {
        HttpExchangeState State { get; }

        /// <summary>0 until headers arrive.</summary>
        long StatusCode { get; }

        /// <summary>Non-null only when the exchange failed.</summary>
        string Error { get; }

        /// <summary>
        /// Advances the exchange, delivering any pending sink callbacks on the
        /// calling thread. Returns true while the exchange is still active.
        /// </summary>
        bool Pump();

        /// <summary>Aborts. Safe to call at any point; subsequent Pumps do nothing.</summary>
        void Cancel();
    }

    /// <summary>
    /// Sends HTTP requests. Implemented once per networking stack: UnityWebRequest
    /// in the Unity transport assembly, HttpClient in tests.
    ///
    /// This seam is what keeps provider code testable. A provider that referenced
    /// UnityWebRequest directly could only ever be exercised inside a running
    /// editor; one that depends on this interface can be driven end to end by a
    /// plain console program, against a local server, on every build.
    /// </summary>
    public interface IHttpTransport
    {
        /// <summary>
        /// Begins an exchange. Must not throw and must not block: failures are
        /// reported through <paramref name="sink"/>. A null sink is a programming
        /// error and may throw.
        /// </summary>
        IHttpExchange Begin(HttpRequestSpec spec, IHttpSink sink);
    }
}
