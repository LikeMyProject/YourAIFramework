using System;
using UnityEngine.Networking;
using YourAI.Core.Net;
using YourAI.Core.Protocol;

namespace YourAI.Transport
{
    /// <summary>
    /// The five-line way to get a live token stream out of an OpenAI-shaped
    /// endpoint, for the case where a project wants exactly that and nothing else.
    ///
    ///     var client = UnitySseClient.Start(new HttpRequestSpec {
    ///         Url    = "https://api.deepseek.com/chat/completions",
    ///         Body   = requestJson,
    ///         Headers = new Dictionary&lt;string, string&gt; {
    ///             { "Authorization", "Bearer " + key } },
    ///     });
    ///
    ///     void Update() {
    ///         client.Pump(evt =&gt; Debug.Log(evt));
    ///     }
    ///
    /// Why this exists alongside <see cref="UnityHttpTransport"/>. The full stack --
    /// transport, provider, context assembly, tool calls, validators -- is the point
    /// of the framework, but nobody should have to assemble five components to print
    /// tokens on screen. This is the shallow end: it does the part that is genuinely
    /// hard to get right (SSE frames that do not respect your read boundaries, bytes
    /// that arrive off the main thread) and leaves the rest of the decisions to the
    /// caller. A project that outgrows it moves to the provider path without
    /// rewriting the networking, because both sit on the same parsing core.
    ///
    /// It is also the reason <see cref="SseFrameQueue"/> is not dead weight. That
    /// class is one object that both stages bytes across the thread boundary and
    /// turns them into complete SSE events; this client is its intended host.
    /// </summary>
    public sealed class UnitySseClient
    {
        private readonly SseFrameQueue _queue;
        private readonly int _receiveBufferSize;

        private UnityWebRequest _request;
        private string _error;
        private bool _finished;

        private UnitySseClient(SseFrameQueue queue, int receiveBufferSize)
        {
            _queue = queue;
            _receiveBufferSize = receiveBufferSize;
        }

        /// <summary>
        /// Starts a request. Must be called from the main thread. A request that
        /// cannot be started is reported through <see cref="Error"/> rather than
        /// thrown, matching the rest of the framework's failure policy.
        /// </summary>
        public static UnitySseClient Start(HttpRequestSpec spec)
        {
            return Start(spec, null);
        }

        /// <param name="certificateHandler">
        /// Supply one to reach a project's own self-signed HTTPS gateway.
        /// </param>
        public static UnitySseClient Start(HttpRequestSpec spec, CertificateHandler certificateHandler)
        {
            if (spec == null)
            {
                throw new ArgumentNullException("spec");
            }

            UnitySseClient client = new UnitySseClient(
                new SseFrameQueue(),
                UnityHttpTransport.DefaultReceiveBufferSize);

            try
            {
                client._request = UnityRequestBuilder.Create(
                    spec,
                    new QueueSink(client._queue, client._receiveBufferSize),
                    certificateHandler);
                client._request.SendWebRequest();
            }
            catch (Exception ex)
            {
                // _finished is already true here, so Pump stays out of its polling
                // branch and simply reports the failure through Error. No separate
                // "start failed" flag is needed, and one that nothing reads is worse
                // than none: it looks like state that matters.
                client._error = "UnityWebRequest could not be started: " + ex.Message;
                client._finished = true;
                if (client._request != null)
                {
                    UnityHttpTransport.SafeDispose(client._request);
                    client._request = null;
                }
            }

            return client;
        }

        /// <summary>
        /// Delivers any completed SSE events to <paramref name="onEvent"/> on the
        /// calling thread, and returns true while the stream is still worth pumping.
        ///
        /// A conventional host calls this once per frame and ignores the return value;
        /// it is returned anyway so a loop can be written as
        /// <c>while (client.Pump(handle)) { yield return null; }</c>.
        /// </summary>
        /// <param name="maxEvents">
        /// Caps how many events one call delivers, so a burst cannot stall a frame.
        /// Zero drains everything available.
        /// </param>
        public bool Pump(Action<string> onEvent, int maxEvents = 0)
        {
            if (!_finished)
            {
                if (_request != null && _request.isDone)
                {
                    _finished = true;

                    // Flush the decoder before reading diagnostics, so a stream that
                    // ended mid-character is recorded as truncated rather than merely
                    // "short".
                    _queue.CompleteFromTransport();

                    UnityWebRequest.Result result = _request.result;
                    if (result != UnityWebRequest.Result.Success
                        && result != UnityWebRequest.Result.ProtocolError)
                    {
                        _error = string.IsNullOrEmpty(_request.error)
                            ? "UnityWebRequest failed: " + result
                            : _request.error;
                    }

                    UnityHttpTransport.SafeDispose(_request);
                    _request = null;
                }
            }

            _queue.Pump(onEvent, maxEvents);

            // Still worth pumping if the request is in flight, or if the previous
            // call hit the event cap and left a backlog behind.
            return !_finished || _queue.HasPending;
        }

        /// <summary>Aborts the request. Events already queued are dropped, not delivered.</summary>
        public void Cancel()
        {
            if (_finished)
            {
                return;
            }
            _finished = true;

            UnityWebRequest request = _request;
            _request = null;
            if (request != null)
            {
                try
                {
                    request.Abort();
                }
                catch (Exception)
                {
                    // Aborting a finished request is not worth reporting.
                }
                UnityHttpTransport.SafeDispose(request);
            }

            // Deliberately no CompleteFromTransport here: the receive callback may
            // still be on the wire, and the queue's contract is a single producer.
            // The stream is simply abandoned.
        }

        // ------------------------------------------------------------------ reporting

        /// <summary>True until the request finishes, fails, or is cancelled.</summary>
        public bool IsRunning
        {
            get { return !_finished; }
        }

        /// <summary>True once the stream has ended and every queued event has been delivered.</summary>
        public bool Completed
        {
            get { return _finished && !_queue.HasPending; }
        }

        /// <summary>Non-null only when the request failed.</summary>
        public string Error
        {
            get { return _error; }
        }

        /// <summary>Events handed to <see cref="Pump"/> so far.</summary>
        public int EventCount
        {
            get { return _queue.DeliveredCount; }
        }

        public int BytesReceived
        {
            get { return _queue.BytesReceived; }
        }

        /// <summary>
        /// True if the "[DONE]" sentinel arrived. False after completion means the
        /// answer may be truncated, which is worth surfacing rather than guessing at.
        /// </summary>
        public bool SawDoneMarker
        {
            get { return _queue.SawDoneMarker; }
        }

        public int PendingEvents
        {
            get { return _queue.PendingCount; }
        }

        /// <summary>Events dropped because pumping stopped. Non-zero is a host bug.</summary>
        public int OverflowDrops
        {
            get { return _queue.OverflowDrops; }
        }

        /// <summary>Half a character left undecoded when the stream ended.</summary>
        public int TrailingPartialChars
        {
            get { return _queue.TrailingPartialChars; }
        }

        internal sealed class QueueSink : DownloadHandlerScript
        {
            private readonly SseFrameQueue _queue;

            public QueueSink(SseFrameQueue queue, int bufferSize)
                : base(new byte[bufferSize > 0 ? bufferSize : UnityHttpTransport.DefaultReceiveBufferSize])
            {
                _queue = queue;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data != null && dataLength > 0)
                {
                    // No copy needed here, unlike the transport path: the queue decodes
                    // synchronously inside Feed, so nothing outlives this call.
                    _queue.Feed(data, 0, dataLength);
                }
                return true;
            }

            protected override void CompleteContent()
            {
                _queue.CompleteFromTransport();
            }
        }
    }
}
