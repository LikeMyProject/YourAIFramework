using System;
using System.Collections.Concurrent;
using System.Threading;

namespace YourAI.Core.Net
{
    /// <summary>
    /// The transport-independent half of an HTTP exchange: a thread-safe staging
    /// area for received bytes, plus the state machine that turns "bytes arrived on
    /// some thread" into "sink callbacks on the pumping thread".
    ///
    /// Why it is separate from the transports. Everything interesting about an
    /// exchange -- ordering of headers/body/outcome, not losing the final chunk to a
    /// race with the completion flag, cancelling without firing late callbacks -- has
    /// nothing to do with UnityWebRequest or HttpClient. Only two things are
    /// genuinely library-specific: issuing the request, and handing received bytes
    /// to <see cref="Enqueue"/>. Isolating those leaves the rest testable without a
    /// network, a server, or an engine, on every build.
    ///
    /// Threading contract, restated because it is the whole point:
    ///   producer thread   Enqueue / SignalHeaders / SignalCompleted / SignalFailed
    ///   consumer thread   Pump / Cancel
    /// The two may be the same thread (Unity's download handler is documented as
    /// running on the main thread on some platforms and off it on others), and the
    /// design is correct either way -- a same-thread producer simply hands over data
    /// that the next Pump drains immediately.
    /// </summary>
    public sealed class HttpExchangeCore : IHttpExchange
    {
        /// <summary>
        /// Chunks per Drain pass are unconstrained, but the staging queue is not:
        /// a consumer that stops pumping must not be able to grow it without bound.
        /// Exceeding this drops the newest chunks and records it, because silent
        /// unbounded growth is the failure mode that takes a game down.
        /// </summary>
        public const int DefaultMaxQueuedChunks = 8192;

        private readonly IHttpSink _sink;
        private readonly ConcurrentQueue<byte[]> _chunks = new ConcurrentQueue<byte[]>();
        private readonly int _maxQueuedChunks;
        private readonly int _frameSize;

        private string _error;
        private string _contentType;
        private long _status;

        private int _queued;
        private int _dropped;
        private int _bytesStaged;
        private int _headersReady;
        private int _finished;
        private int _failed;
        private int _cancelRequested;
        private int _stateValue;

        private bool _headersDelivered;

        public HttpExchangeCore(IHttpSink sink)
            : this(sink, 0, DefaultMaxQueuedChunks)
        {
        }

        /// <param name="frameSize">
        /// When greater than zero, each staged chunk is re-sliced into pieces of this
        /// size before reaching the sink. Real transports pass zero. Tests pass a
        /// small value so they can state where the cut points fall -- including
        /// inside a multi-byte UTF-8 sequence -- instead of hoping the network
        /// happens to split the way the interesting case needs.
        /// </param>
        public HttpExchangeCore(IHttpSink sink, int frameSize)
            : this(sink, frameSize, DefaultMaxQueuedChunks)
        {
        }

        public HttpExchangeCore(IHttpSink sink, int frameSize, int maxQueuedChunks)
        {
            if (sink == null)
            {
                throw new ArgumentNullException("sink");
            }
            _sink = sink;
            _frameSize = frameSize > 0 ? frameSize : 0;
            _maxQueuedChunks = maxQueuedChunks > 0 ? maxQueuedChunks : DefaultMaxQueuedChunks;
            _stateValue = (int)HttpExchangeState.Pending;
        }

        // ------------------------------------------------------------ producer side

        /// <summary>
        /// Stages a piece of the response body. Call from the producer thread.
        ///
        /// The bytes are copied. Networking libraries own their receive buffers and
        /// reuse them the instant the callback returns -- UnityWebRequest documents
        /// this explicitly for <c>DownloadHandlerScript.ReceiveData</c> -- so holding
        /// the caller's array would hand the parser a buffer that gets overwritten
        /// underneath it. One copy per chunk is the correct price.
        /// </summary>
        public void Enqueue(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0)
            {
                return;
            }
            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException(
                    "count", "offset/count fall outside the supplied buffer.");
            }

            if (Interlocked.Increment(ref _queued) > _maxQueuedChunks)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _dropped);
                return;
            }

            byte[] copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            Interlocked.Add(ref _bytesStaged, count);
            _chunks.Enqueue(copy);
        }

        /// <summary>Records the response status and content type. Idempotent.</summary>
        public void SignalHeaders(long statusCode, string contentType)
        {
            Interlocked.Exchange(ref _status, statusCode);
            _contentType = contentType;
            Volatile.Write(ref _headersReady, 1);
        }

        /// <summary>
        /// Marks the body as finished cleanly. Idempotent, so a transport may call it
        /// from a completion callback and again while polling without harm.
        /// </summary>
        public void SignalCompleted()
        {
            Volatile.Write(ref _finished, 1);
        }

        /// <summary>
        /// Marks the exchange as failed.
        ///
        /// Failure outranks completion, in both directions and regardless of arrival
        /// order, which is a deliberate asymmetry rather than an oversight. The
        /// reason is Unity's <c>DownloadHandlerScript.CompleteContent</c>: it fires
        /// when a request stops, not when it succeeds, so a dropped connection
        /// produces a completion signal and then a failure signal. Letting completion
        /// win would report that as a successful answer with a truncation flag --
        /// technically informative, but wrong, and different from what the same
        /// failure produces through any other transport.
        ///
        /// The reverse case, a failure followed by a completion, cannot arise from a
        /// correct transport and lands in the same place if it does.
        /// </summary>
        public void SignalFailed(string error, long statusCode)
        {
            if (!string.IsNullOrEmpty(error))
            {
                _error = error;
            }
            if (statusCode != 0)
            {
                Interlocked.Exchange(ref _status, statusCode);
            }
            Volatile.Write(ref _failed, 1);
        }

        // ------------------------------------------------------------ consumer side

        public HttpExchangeState State
        {
            get { return (HttpExchangeState)Volatile.Read(ref _stateValue); }
        }

        public long StatusCode
        {
            get { return Interlocked.Read(ref _status); }
        }

        public string Error
        {
            get { return _error; }
        }

        public bool Pump()
        {
            HttpExchangeState state = State;
            if (state == HttpExchangeState.Completed
                || state == HttpExchangeState.Faulted
                || state == HttpExchangeState.Cancelled)
            {
                return false;
            }

            // Headers first, because the status code is what tells a caller whether
            // the body is worth reading at all.
            if (!_headersDelivered && Volatile.Read(ref _headersReady) != 0)
            {
                _headersDelivered = true;
                SetState(HttpExchangeState.Receiving);
                _sink.OnResponseStarted(Interlocked.Read(ref _status), _contentType);
            }

            Drain();

            // Failure is checked first on purpose; see SignalFailed for why the
            // ordering cannot be made symmetric.
            if (Volatile.Read(ref _failed) != 0)
            {
                // Drain again: bytes staged alongside the failure still carry the
                // partial answer, and a caller that gets them can show what arrived
                // before the connection dropped.
                Drain();
                SetState(HttpExchangeState.Faulted);
                _sink.OnFailed(_error, Interlocked.Read(ref _status));
                return false;
            }

            if (Volatile.Read(ref _finished) != 0)
            {
                // The same second Drain, for the same reason: chunks staged between
                // the first Drain and the completion flag are the ones most likely to
                // carry the end of the answer.
                Drain();
                SetState(HttpExchangeState.Completed);
                _sink.OnCompleted();
                return false;
            }

            return true;
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) != 0)
            {
                return;
            }
            SetState(HttpExchangeState.Cancelled);
        }

        /// <summary>
        /// True once <see cref="Cancel"/> has been called. Transports poll this to
        /// abort the underlying request; the core cannot do it for them.
        /// </summary>
        public bool CancelRequested
        {
            get { return Volatile.Read(ref _cancelRequested) != 0; }
        }

        /// <summary>Bytes staged for delivery, across all calls.</summary>
        public int BytesStaged
        {
            get { return Volatile.Read(ref _bytesStaged); }
        }

        /// <summary>Chunks dropped because the consumer stopped pumping.</summary>
        public int DroppedChunks
        {
            get { return Volatile.Read(ref _dropped); }
        }

        public int PendingChunks
        {
            get { return Volatile.Read(ref _queued); }
        }

        private void Drain()
        {
            byte[] chunk;
            while (_chunks.TryDequeue(out chunk))
            {
                Interlocked.Decrement(ref _queued);

                if (_frameSize <= 0)
                {
                    _sink.OnBodyChunk(chunk, 0, chunk.Length);
                    continue;
                }

                for (int at = 0; at < chunk.Length; at += _frameSize)
                {
                    int count = chunk.Length - at;
                    if (count > _frameSize)
                    {
                        count = _frameSize;
                    }
                    _sink.OnBodyChunk(chunk, at, count);
                }
            }
        }

        private void SetState(HttpExchangeState state)
        {
            Volatile.Write(ref _stateValue, (int)state);
        }
    }
}
