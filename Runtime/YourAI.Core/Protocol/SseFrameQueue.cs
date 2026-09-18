using System;
using System.Collections.Concurrent;
using System.Threading;

namespace YourAI.Core.Protocol
{
    /// <summary>
    /// The hand-off point between the thread that receives bytes and the thread
    /// that owns the game.
    ///
    /// Why this exists. UnityWebRequest delivers bytes on a background thread, but
    /// everything downstream of an SSE payload -- JSON parsing, writing to shared
    /// state, touching transforms, updating UI -- must happen on the main thread.
    /// A transport that parses and accumulates directly inside its receive callback
    /// therefore commits two mistakes at once: it mutates non-thread-safe state from
    /// two threads, and it calls engine APIs (JsonUtility among them) off the main
    /// thread, which Unity has never promised to support.
    ///
    /// The split enforced here:
    ///
    ///   transport thread   Feed / CompleteFromTransport            (sole writer)
    ///                      -> completed events into a concurrent queue
    ///   main thread        Pump                                    (sole reader)
    ///                      -> parse, accumulate, raise, render
    ///
    /// <see cref="SseEventBuffer"/> is touched by exactly one thread in this design,
    /// which is what makes it safe despite not being thread safe itself. The queue is
    /// the only shared structure, and ConcurrentQueue is built for that.
    ///
    /// The queue is bounded because an unbounded one is not a queue, it is a leak:
    /// if the main thread stops pumping, drops are counted and reported rather than
    /// quietly consuming memory.
    /// </summary>
    public sealed class SseFrameQueue
    {
        /// <summary>Roughly a few hundred kilobytes of payloads. Far above any real burst.</summary>
        public const int DefaultMaxQueuedEvents = 4096;

        private readonly ConcurrentQueue<string> _handoff = new ConcurrentQueue<string>();
        private readonly SseEventBuffer _buffer = new SseEventBuffer();
        private readonly int _maxQueuedEvents;

        private int _queued;
        private int _overflowDrops;
        private int _bytesReceived;
        private int _transportCompleted;
        private int _sawDone;
        private int _deliveredCount;

        public SseFrameQueue()
            : this(DefaultMaxQueuedEvents)
        {
        }

        public SseFrameQueue(int maxQueuedEvents)
        {
            _maxQueuedEvents = maxQueuedEvents > 0 ? maxQueuedEvents : DefaultMaxQueuedEvents;
            _buffer.EventReceived += OnEventCompleted;
        }

        // ---------------------------------------------------------- transport side

        /// <summary>Feeds bytes. Call from the transport thread only; not thread safe.</summary>
        public void Feed(byte[] data, int offset, int count)
        {
            Interlocked.Add(ref _bytesReceived, count);
            _buffer.Feed(data, offset, count);

            // Publish the sentinel the moment it appears rather than waiting for
            // completion. It only ever moves 0 -> 1, so a reader cannot observe a
            // torn value, and a caller polling mid-stream gets a truthful answer
            // instead of a false negative that looks like truncation.
            if (_buffer.SawDoneMarker && Volatile.Read(ref _sawDone) == 0)
            {
                Volatile.Write(ref _sawDone, 1);
            }
        }

        /// <summary>
        /// Ends the stream and flushes the decoder. Call from the transport thread,
        /// after the last <see cref="Feed"/>. Idempotent.
        /// </summary>
        public void CompleteFromTransport()
        {
            _buffer.Complete();

            // Publish the final diagnostics before announcing completion, so a main
            // thread that observes TransportCompleted also observes these values.
            _sawDone = _buffer.SawDoneMarker ? 1 : 0;
            Volatile.Write(ref _transportCompleted, 1);
        }

        private void OnEventCompleted(string payload)
        {
            // Runs on the transport thread, inside Feed/Complete. Queuing is the only
            // work that may happen here.
            if (Interlocked.Increment(ref _queued) > _maxQueuedEvents)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _overflowDrops);
                return;
            }
            _handoff.Enqueue(payload);
        }

        // --------------------------------------------------------------- main side

        /// <summary>
        /// Delivers pending payloads to <paramref name="onPayload"/> on the calling
        /// thread and returns how many were delivered.
        /// </summary>
        /// <param name="maxEvents">
        /// Stop after this many, so a backlog cannot stall a frame. Zero means drain
        /// everything available.
        /// </param>
        public int Pump(Action<string> onPayload, int maxEvents = 0)
        {
            int delivered = 0;
            string payload;

            while ((maxEvents <= 0 || delivered < maxEvents) && _handoff.TryDequeue(out payload))
            {
                Interlocked.Decrement(ref _queued);
                delivered++;

                if (onPayload != null)
                {
                    onPayload(payload);
                }
            }

            _deliveredCount += delivered;
            return delivered;
        }

        // -------------------------------------------------------------- diagnostics

        /// <summary>True once the transport signalled completion. Safe to read any time.</summary>
        public bool TransportCompleted
        {
            get { return Volatile.Read(ref _transportCompleted) != 0; }
        }

        /// <summary>
        /// True once a "[DONE]" sentinel has been observed. Safe to read at any time:
        /// the value only ever moves false to true, so a mid-stream read is truthful
        /// rather than merely "not yet known". Once <see cref="TransportCompleted"/> is
        /// set, a false value means the response was cut short.
        /// </summary>
        public bool SawDoneMarker
        {
            get { return Volatile.Read(ref _sawDone) != 0; }
        }

        /// <summary>Bytes handed to <see cref="Feed"/>, across all calls.</summary>
        public int BytesReceived
        {
            get { return Volatile.Read(ref _bytesReceived); }
        }

        /// <summary>Events delivered to callers so far. Written by the pumping thread.</summary>
        public int DeliveredCount
        {
            get { return _deliveredCount; }
        }

        /// <summary>
        /// Events dropped because the main thread stopped pumping. Non-zero means the
        /// stream was truncated by the consumer, not by the network -- a real bug in
        /// the host, which is why it is surfaced rather than swallowed.
        /// </summary>
        public int OverflowDrops
        {
            get { return Volatile.Read(ref _overflowDrops); }
        }

        /// <summary>Events parsed but not yet delivered.</summary>
        public int PendingCount
        {
            get { return Volatile.Read(ref _queued); }
        }

        /// <summary>Half a character left undecoded when the stream ended. Normal on a dropped connection.</summary>
        public int TrailingPartialChars
        {
            get { return _buffer.TrailingPartialChars; }
        }

        public bool HasPending
        {
            get { return !_handoff.IsEmpty; }
        }

        /// <summary>Clears queued work. Call from the main thread only, when no transport is running.</summary>
        public void Reset()
        {
            string discarded;
            while (_handoff.TryDequeue(out discarded))
            {
                Interlocked.Decrement(ref _queued);
            }
            _buffer.Reset();
            _deliveredCount = 0;
            Interlocked.Exchange(ref _overflowDrops, 0);
        }
    }
}
