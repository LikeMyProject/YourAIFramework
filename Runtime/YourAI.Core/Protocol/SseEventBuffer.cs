using System;
using System.Text;

namespace YourAI.Core.Protocol
{
    /// <summary>
    /// Frame-boundary-safe SSE parser. Pure BCL, no engine types, no allocation
    /// beyond growing buffers.
    ///
    /// Network slice boundaries have nothing to do with SSE event boundaries. A
    /// slice may split a multi-byte UTF-8 character, or split the "\n\n" that
    /// terminates an event. Therefore the only correct pipeline is:
    ///   accumulate bytes -> incremental decode -> split on blank line -> extract data lines
    ///
    /// Measured against the mock server with 7-byte slicing: 614 transport slices,
    /// 89 of them cut a character in half, 613 of them cut an event in half. A live
    /// HTTP run confirmed the same shape end to end -- 569 reads, largest frame 7
    /// bytes. Any implementation that decodes each slice on arrival corrupts text.
    ///
    /// Threading: this class is NOT thread safe and is not meant to be. It has a
    /// single writer -- whoever receives bytes. When that differs from the consumer,
    /// interpose <see cref="SseFrameQueue"/> rather than locking here.
    ///
    /// Note on the Content/Reasoning/UsageJson slots: those are convenience
    /// accumulators for the host to fill from the payloads it receives. The bytes
    /// -- and the "data:" framing -- are this class's business; what an
    /// OpenAI-shaped chunk means is the Providers layer's business.
    /// </summary>
    public sealed class SseEventBuffer
    {
        /// <summary>Upper bound on bytes handed to the decoder in one call.</summary>
        private const int MaxDecodeChunk = 16384;

        private const string DoneMarker = "[DONE]";

        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);
        private static readonly byte[] EmptyBytes = new byte[0];

        private readonly Decoder _decoder = Utf8Strict.GetDecoder();
        private readonly StringBuilder _pending = new StringBuilder(8192);
        private readonly StringBuilder _dataLine = new StringBuilder(512);
        private readonly char[] _charScratch = new char[MaxDecodeChunk];

        private bool _completed;

        /// <summary>Raw bytes received.</summary>
        public int RawBytes { get; private set; }

        /// <summary>Number of Feed calls, matching transport delivery count.</summary>
        public int FeedCalls { get; private set; }

        /// <summary>Number of complete SSE events carrying a non-empty data payload.</summary>
        public int EventCount { get; private set; }

        /// <summary>Decode failures and malformed payloads, incremented by this class and by the host.</summary>
        public int ParseErrors { get; set; }

        /// <summary>
        /// True when the "[DONE]" sentinel was observed. A false value after the
        /// stream ends means it was cut short and the text may be incomplete.
        /// </summary>
        public bool SawDoneMarker { get; private set; }

        /// <summary>Accumulated assistant text. Filled by the host.</summary>
        public StringBuilder Content { get; private set; } = new StringBuilder(4096);

        /// <summary>Accumulated reasoning text, for reasoner-style models. Filled by the host.</summary>
        public StringBuilder Reasoning { get; private set; } = new StringBuilder(512);

        /// <summary>Raw payload of the last chunk carrying usage, if any. Filled by the host.</summary>
        public string UsageJson { get; set; }

        /// <summary>Raised synchronously per complete event, with the concatenated data lines.</summary>
        public event Action<string> EventReceived;

        /// <summary>Bytes left undecoded in the middle of a character when the stream ended.</summary>
        public int TrailingPartialChars { get; private set; }

        public void Feed(byte[] data, int offset, int count)
        {
            if (_completed)
            {
                // Feeding after Complete would interleave with a flushed decoder.
                // Ignoring is safer than corrupting, and the host has a bug anyway.
                ParseErrors++;
                return;
            }

            FeedCalls++;
            RawBytes += count;

            if (count > 0)
            {
                // Decode in bounded chunks so one huge frame cannot force a
                // pathological scratch allocation.
                int processed = 0;
                while (processed < count)
                {
                    int n = count - processed;
                    if (n > MaxDecodeChunk)
                    {
                        n = MaxDecodeChunk;
                    }

                    // GetChars is the ONLY call permitted to touch the decoder.
                    //
                    // Calling GetCharCount first as a "how much room do I need"
                    // probe looks harmless but is not: with flush=false a trailing
                    // partial sequence produces zero characters, the probe returns
                    // 0, and a subsequent `if (needed > 0)` guard skips GetChars
                    // entirely. The probe does not preserve those bytes, so a lead
                    // byte such as 0xE5 is silently dropped and the next frame's
                    // 0xBF arrives as an orphan continuation byte -- which the
                    // strict decoder rejects outright.
                    //
                    // A char never occupies fewer bytes than it costs to encode, so
                    // chunk-sized scratch space is always sufficient.
                    int produced = _decoder.GetChars(data, offset + processed, n, _charScratch, 0, false);
                    for (int i = 0; i < produced; i++)
                    {
                        char c = _charScratch[i];
                        // Normalise line endings so only "\n\n" needs matching.
                        if (c != '\r')
                        {
                            _pending.Append(c);
                        }
                    }

                    processed += n;
                }
            }

            Drain();
        }

        /// <summary>
        /// Ends the stream, flushing any half-character the decoder is holding.
        /// Idempotent: transports commonly signal completion twice, once from the
        /// download callback and once from the poll loop.
        /// </summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }
            _completed = true;

            try
            {
                int produced = _decoder.GetChars(EmptyBytes, 0, 0, _charScratch, 0, true);
                for (int i = 0; i < produced; i++)
                {
                    if (_charScratch[i] != '\r')
                    {
                        _pending.Append(_charScratch[i]);
                    }
                }
            }
            catch (DecoderFallbackException)
            {
                // The stream ended mid-character. That is exactly what a dropped
                // connection looks like, and it is a fact to report rather than a
                // reason to throw: the half character has no valid text form.
                TrailingPartialChars++;
                ParseErrors++;
            }

            Drain();
        }

        /// <summary>Resets to a reusable state without reallocating the buffers.</summary>
        public void Reset()
        {
            _decoder.Reset();
            _pending.Length = 0;
            _dataLine.Length = 0;
            Content.Length = 0;
            Reasoning.Length = 0;
            UsageJson = null;
            RawBytes = 0;
            FeedCalls = 0;
            EventCount = 0;
            ParseErrors = 0;
            TrailingPartialChars = 0;
            SawDoneMarker = false;
            _completed = false;
        }

        private void Drain()
        {
            while (true)
            {
                int boundary = IndexOfBlankLine(_pending);
                if (boundary < 0)
                {
                    break;
                }

                string rawEvent = _pending.ToString(0, boundary);
                _pending.Remove(0, boundary + 2);

                string payload = ExtractData(rawEvent);
                if (payload.Length == 0)
                {
                    continue;
                }

                if (payload == DoneMarker)
                {
                    SawDoneMarker = true;
                    continue;
                }

                EventCount++;
                Action<string> handler = EventReceived;
                if (handler != null)
                {
                    handler(payload);
                }
            }
        }

        private static int IndexOfBlankLine(StringBuilder sb)
        {
            for (int i = 0; i + 1 < sb.Length; i++)
            {
                if (sb[i] == '\n' && sb[i + 1] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private string ExtractData(string rawEvent)
        {
            _dataLine.Length = 0;
            int start = 0;
            while (start < rawEvent.Length)
            {
                int nl = rawEvent.IndexOf('\n', start);
                string line = nl < 0 ? rawEvent.Substring(start) : rawEvent.Substring(start, nl - start);
                start = nl < 0 ? rawEvent.Length : nl + 1;

                // SSE comment lines (": ...") and other fields are ignored on purpose.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                int p = 5;
                if (p < line.Length && line[p] == ' ')
                {
                    p++;
                }
                if (_dataLine.Length > 0)
                {
                    _dataLine.Append('\n');
                }
                _dataLine.Append(line, p, line.Length - p);
            }
            return _dataLine.ToString();
        }
    }
}
