using System;
using UnityEngine;
using UnityEngine.Networking;
using YourAI.Core.Net;

namespace YourAI.Transport
{
    /// <summary>
    /// <see cref="IHttpTransport"/> on top of UnityWebRequest, which is the only
    /// HTTP stack a player build actually has.
    ///
    /// Two things make this a thin file despite it being the layer that talks to the
    /// network.
    ///
    /// First, UnityWebRequest fits the kernel's polling model unusually well. It
    /// exposes <c>isDone</c> and <c>responseCode</c> as properties, so streaming is
    /// driven by the same Pump call everything else in the framework uses, with no
    /// coroutine, no callback, and no Awaitable anywhere in the path.
    ///
    /// Second, the part that is not Unity-specific -- staging bytes across threads,
    /// ordering headers and body and outcome, cancelling cleanly -- lives in
    /// <see cref="HttpExchangeCore"/>, where it is covered by offline tests. What is
    /// left here is genuinely about UnityWebRequest: building the request, polling
    /// it, and translating its result enum.
    ///
    /// Threading. UnityWebRequest must be created and sent from the main thread;
    /// <see cref="Begin"/> documents that. Whether the download handler's
    /// <c>ReceiveData</c> runs on the main thread or off it differs by platform and
    /// Unity version, and this class is correct either way -- bytes go into the
    /// core's staging queue, and the sink sees them from <see cref="IHttpExchange.Pump"/>
    /// regardless. That is the same contract <c>SseFrameQueue</c> enforces for code
    /// that bypasses this abstraction.
    /// </summary>
    public sealed class UnityHttpTransport : IHttpTransport
    {
        /// <summary>
        /// 16 KB is comfortably above Unity's default 1 KB handler buffer, so most
        /// chunks arrive whole and the copy in <see cref="HttpExchangeCore.Enqueue"/>
        /// happens once per network read rather than once per kilobyte.
        /// </summary>
        public const int DefaultReceiveBufferSize = 16 * 1024;

        private readonly CertificateHandler _certificateHandler;
        private readonly int _receiveBufferSize;

        public UnityHttpTransport()
            : this(null, DefaultReceiveBufferSize)
        {
        }

        /// <param name="certificateHandler">
        /// Applied to every request. Supplying one is what makes a project's own
        /// self-signed HTTPS gateway reachable from a build; the framework never
        /// installs a permissive handler on its own. The handler is not disposed with
        /// the request, so it can be shared across requests and kept for the
        /// lifetime of the transport.
        /// </param>
        public UnityHttpTransport(CertificateHandler certificateHandler)
            : this(certificateHandler, DefaultReceiveBufferSize)
        {
        }

        public UnityHttpTransport(CertificateHandler certificateHandler, int receiveBufferSize)
        {
            _certificateHandler = certificateHandler;
            _receiveBufferSize = receiveBufferSize > 0 ? receiveBufferSize : DefaultReceiveBufferSize;
        }

        /// <summary>
        /// Starts an exchange. Must be called from the main thread, because both
        /// UnityWebRequest construction and <c>SendWebRequest</c> are main-thread-only
        /// Unity APIs.
        ///
        /// Never throws for a malformed request: a bad URL, an unusable header, or a
        /// stack that refuses to start produces an exchange that reports the failure
        /// through the sink on its first Pump. The one exception is a null sink,
        /// which is a programming error rather than a runtime condition.
        /// </summary>
        public IHttpExchange Begin(HttpRequestSpec spec, IHttpSink sink)
        {
            if (sink == null)
            {
                throw new ArgumentNullException("sink");
            }
            if (spec == null)
            {
                throw new ArgumentNullException("spec");
            }

            HttpExchangeCore core = new HttpExchangeCore(sink);
            UnityWebRequest request = null;

            try
            {
                request = UnityRequestBuilder.Create(
                    spec,
                    new UnityByteSink(core, _receiveBufferSize),
                    _certificateHandler);
                request.SendWebRequest();
            }
            catch (Exception ex)
            {
                if (request != null)
                {
                    SafeDispose(request);
                }
                core.SignalFailed("UnityWebRequest could not be started: " + ex.Message, 0);
                return core;
            }

            return new UnityExchange(core, request);
        }

        internal static void SafeDispose(UnityWebRequest request)
        {
            if (request == null)
            {
                return;
            }
            try
            {
                request.Dispose();
            }
            catch (Exception)
            {
                // Disposal failures are not actionable and must not mask the
                // outcome the caller is about to be told about.
            }
        }
    }

    /// <summary>
    /// Bridges the UnityWebRequest lifecycle onto <see cref="HttpExchangeCore"/>.
    /// </summary>
    internal sealed class UnityExchange : IHttpExchange
    {
        private readonly HttpExchangeCore _core;
        private UnityWebRequest _request;
        private bool _headersSignalled;

        public UnityExchange(HttpExchangeCore core, UnityWebRequest request)
        {
            _core = core;
            _request = request;
        }

        public HttpExchangeState State
        {
            get { return _core.State; }
        }

        public long StatusCode
        {
            get { return _core.StatusCode; }
        }

        public string Error
        {
            get { return _core.Error; }
        }

        public bool Pump()
        {
            UnityWebRequest request = _request;
            if (request == null)
            {
                // Already torn down; drain whatever the core still holds.
                return _core.Pump();
            }

            if (_core.CancelRequested)
            {
                Abort(request);
                return _core.Pump();
            }

            if (!_headersSignalled)
            {
                long code = SafeStatusCode(request);
                // responseCode is 0 until headers land, so the guard is what keeps
                // a caller from being told "HTTP 0" while the request is still in
                // flight. Once isDone, 0 is itself the answer: no response arrived.
                if (code != 0 || request.isDone)
                {
                    _headersSignalled = true;
                    _core.SignalHeaders(code, SafeHeader(request, "Content-Type"));
                }
            }

            if (request.isDone)
            {
                UnityWebRequest.Result result = request.result;

                if (result == UnityWebRequest.Result.Success
                    || result == UnityWebRequest.Result.ProtocolError)
                {
                    // ProtocolError means the server answered -- a 429, a 401, a
                    // provider-specific error object. Those bodies are the most
                    // informative ones there are, and the status code has already
                    // travelled with OnResponseStarted. So the bytes are handed over
                    // and the verdict is left upstream, where the error text can
                    // actually be read; calling this a transport failure here would
                    // throw away the only explanation the provider gave.
                    _core.SignalCompleted();
                }
                else
                {
                    string message = request.error;
                    _core.SignalFailed(
                        string.IsNullOrEmpty(message)
                            ? "UnityWebRequest failed: " + result
                            : message,
                        SafeStatusCode(request));
                }

                Tear();
            }

            return _core.Pump();
        }

        public void Cancel()
        {
            _core.Cancel();
            UnityWebRequest request = _request;
            if (request != null)
            {
                Abort(request);
            }
        }

        private void Abort(UnityWebRequest request)
        {
            try
            {
                request.Abort();
            }
            catch (Exception)
            {
                // Aborting an already-finished request is not an error worth
                // reporting; the disposal below is what actually matters.
            }
            Tear();
        }

        private void Tear()
        {
            UnityWebRequest request = _request;
            if (request == null)
            {
                return;
            }
            _request = null;
            UnityHttpTransport.SafeDispose(request);
        }

        private static long SafeStatusCode(UnityWebRequest request)
        {
            try
            {
                return request.responseCode;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string SafeHeader(UnityWebRequest request, string name)
        {
            try
            {
                return request.GetResponseHeader(name);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Receives body bytes off UnityWebRequest and stages them in the core.
    ///
    /// Deliberately does no parsing. Whatever thread this runs on -- and Unity has
    /// changed the answer across versions and platforms -- the only work done here
    /// is a copy and an enqueue, both of which are safe anywhere. Everything that
    /// touches engine state or shared parsers happens later, on the pumping thread.
    /// </summary>
    internal sealed class UnityByteSink : DownloadHandlerScript
    {
        private readonly HttpExchangeCore _core;

        public UnityByteSink(HttpExchangeCore core, int bufferSize)
            : base(new byte[bufferSize > 0 ? bufferSize : UnityHttpTransport.DefaultReceiveBufferSize])
        {
            _core = core;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data != null && dataLength > 0)
            {
                _core.Enqueue(data, 0, dataLength);
            }

            // Returning false tells Unity to abort the download. There is no
            // condition here under which that is the right answer: a chunk this
            // layer cannot use is still a chunk the caller may want, and the
            // decision about whether the response is usable belongs upstream.
            return true;
        }

        protected override void CompleteContent()
        {
            // Not every Unity version calls this on failure, and the core's
            // completion flag is idempotent, so UnityExchange.Pump calls it again
            // while polling isDone. Whichever arrives first wins; both are correct.
            _core.SignalCompleted();
        }
    }
}
