using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using YourAI.Core.Contracts;
using YourAI.Core.Json;
using YourAI.Core.Net;

namespace YourAI.Memory
{
    /// <summary>Connection settings for an OpenAI-shaped embeddings endpoint.</summary>
    public sealed class RemoteEmbeddingOptions
    {
        public string ApiKey;

        /// <summary>Scheme and host, no trailing slash.</summary>
        public string BaseUrl = "https://api.openai.com";

        public string EmbeddingsPath = "/v1/embeddings";

        public string DefaultModel = "text-embedding-3-small";

        /// <summary>
        /// Expected vector width. Zero means "whatever the server returns", which is the
        /// right default: guessing a width from the model name is how a provider ends up
        /// rejecting its own answers.
        /// </summary>
        public int Dimensions;

        public int TimeoutSeconds = 30;

        /// <summary>
        /// Upper bound on cached vectors. Bounded because a long session has unbounded
        /// text: without a cap this is a slow leak that only shows up in a long playtest.
        /// </summary>
        public int MaxCachedVectors = 4096;

        /// <summary>
        /// How many embedding requests may be in flight at once. The default of one is
        /// deliberate: embedding is a background nicety, and a burst of them competing
        /// with the player's actual request is the wrong trade.
        /// </summary>
        public int MaxInFlight = 1;

        public Dictionary<string, string> ExtraHeaders;

        public RemoteEmbeddingOptions Clone()
        {
            RemoteEmbeddingOptions copy = new RemoteEmbeddingOptions
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                EmbeddingsPath = EmbeddingsPath,
                DefaultModel = DefaultModel,
                Dimensions = Dimensions,
                TimeoutSeconds = TimeoutSeconds,
                MaxCachedVectors = MaxCachedVectors,
                MaxInFlight = MaxInFlight,
            };
            if (ExtraHeaders != null)
            {
                copy.ExtraHeaders = new Dictionary<string, string>(ExtraHeaders);
            }
            return copy;
        }
    }

    /// <summary>
    /// A real semantic embedder, served over HTTP.
    ///
    /// This is how the framework gets genuine meaning-based recall without shipping a
    /// model: the weights live behind an endpoint, the framework keeps only a cache.
    ///
    /// The awkward part is that the embedding interface is synchronous -- retrieval
    /// happens on the frame thread, inside a Query, and blocking there is not an option
    /// the framework is willing to offer. HTTP, meanwhile, is polled. So this class
    /// resolves the mismatch the only way that keeps both promises:
    ///
    ///   TryEmbed reads the cache, and only the cache. A miss does not wait; it queues
    ///   the text for a background request and returns false, and the caller degrades
    ///   to whatever it was going to do without an embedder. The next Query for the
    ///   same text finds a hit.
    ///
    /// Consequence worth stating plainly: the first query for a text is lexical, and the
    /// same query later is semantic. That is a real trade, and it is the one that keeps
    /// the frame budget intact. A host that wants the first query to be semantic calls
    /// Warm before it matters.
    ///
    /// Pump is the other half. The host drives it like everything else in the framework.
    /// </summary>
    public sealed class RemoteEmbeddingProvider : IEmbeddingProvider
    {
        private sealed class InFlight
        {
            public string Text;
            public IHttpExchange Exchange;
            public EmbeddingSink Sink;
        }

        private readonly string _name;
        private readonly IHttpTransport _transport;
        private readonly RemoteEmbeddingOptions _options;
        private readonly Dictionary<string, float[]> _cache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private readonly Queue<string> _pending = new Queue<string>(8);
        private readonly HashSet<string> _queued = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<InFlight> _inFlight = new List<InFlight>(2);

        private int _dimensions;
        private bool _overflowed;

        public RemoteEmbeddingProvider(string name, IHttpTransport transport, RemoteEmbeddingOptions options)
        {
            _name = string.IsNullOrEmpty(name) ? "remote-embeddings" : name;
            _transport = transport;
            _options = options ?? new RemoteEmbeddingOptions();
            _dimensions = _options.Dimensions;
        }

        public static RemoteEmbeddingProvider OpenAI(IHttpTransport transport, string apiKey)
        {
            return new RemoteEmbeddingProvider("openai-embeddings", transport, new RemoteEmbeddingOptions { ApiKey = apiKey });
        }

        public string Name
        {
            get { return _name; }
        }

        public bool IsAvailable
        {
            get { return !string.IsNullOrEmpty(_options.ApiKey) && _transport != null; }
        }

        public string UnavailableReason
        {
            get
            {
                if (string.IsNullOrEmpty(_options.ApiKey))
                {
                    return "No API key is set for embedder '" + _name + "'.";
                }
                if (_transport == null)
                {
                    return "Embedder '" + _name + "' has no transport wired up.";
                }
                return string.Empty;
            }
        }

        /// <summary>Vector width, once known. Zero until the first successful response.</summary>
        public int Dimensions
        {
            get { return _dimensions; }
        }

        // -------------------------------------------------------------- diagnostics

        /// <summary>Texts waiting for a request to be started for them.</summary>
        public int PendingCount
        {
            get { return _pending.Count + _inFlight.Count; }
        }

        public int CachedVectorCount
        {
            get { return _cache.Count; }
        }

        /// <summary>Lookups that had to fall back because the vector was not ready.</summary>
        public int CacheMisses { get; private set; }

        public int CompletedRequests { get; private set; }

        public int FailedRequests { get; private set; }

        /// <summary>Last failure, for a host that wants to explain why recall degraded.</summary>
        public string LastError { get; private set; }

        /// <summary>True once the cache has hit its cap and is no longer growing.</summary>
        public bool CacheIsFull
        {
            get { return _overflowed; }
        }

        // ------------------------------------------------------------------- lookup

        /// <summary>
        /// Reads the cache. Never blocks, never issues a request inline: a miss queues
        /// the text and reports false so the caller degrades to its lexical path.
        /// </summary>
        public bool TryEmbed(string text, List<float> into)
        {
            if (into == null)
            {
                return false;
            }

            // The interface contract says the destination is cleared first, and that
            // is not cosmetic: callers on the frame thread reuse their scratch lists
            // across queries, and an appending embedder silently concatenates the
            // previous query's vector into this one. Cosine then reads stale numbers
            // that look like a bad model rather than like a bug.
            into.Clear();

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            float[] cached;
            if (_cache.TryGetValue(text, out cached))
            {
                for (int i = 0; i < cached.Length; i++)
                {
                    into.Add(cached[i]);
                }
                return true;
            }

            CacheMisses++;
            Enqueue(text);
            return false;
        }

        /// <summary>
        /// Queues a text for embedding without reading the cache. Lets a host pay the
        /// cost up front -- after a save load, say -- so the first query is not the one
        /// that pays for it.
        /// </summary>
        public void Warm(string text)
        {
            if (string.IsNullOrEmpty(text) || _cache.ContainsKey(text))
            {
                return;
            }

            Enqueue(text);
        }

        /// <summary>Drops every cached vector. Pending work is left alone.</summary>
        public void Clear()
        {
            _cache.Clear();
            _overflowed = false;
        }

        // -------------------------------------------------------------------- pump

        /// <summary>
        /// Advances background embedding work. Returns true while anything is pending,
        /// so a host can skip the call once it settles.
        /// </summary>
        public bool Pump()
        {
            SweepInFlight();
            StartPending();
            return _pending.Count > 0 || _inFlight.Count > 0;
        }

        /// <summary>Abandons all pending and in-flight work.</summary>
        public void Cancel()
        {
            for (int i = 0; i < _inFlight.Count; i++)
            {
                IHttpExchange exchange = _inFlight[i].Exchange;
                if (exchange != null)
                {
                    exchange.Cancel();
                }
            }

            _inFlight.Clear();
            _pending.Clear();
            _queued.Clear();
        }

        // --------------------------------------------------------------- internals

        private void Enqueue(string text)
        {
            if (!IsAvailable || _overflowed)
            {
                return;
            }

            // Already in the queue or in flight. Without this the same miss on
            // consecutive frames would queue the same text once per frame.
            if (_queued.Contains(text))
            {
                return;
            }

            _queued.Add(text);
            _pending.Enqueue(text);
        }

        private void StartPending()
        {
            while (_pending.Count > 0 && _inFlight.Count < Math.Max(1, _options.MaxInFlight))
            {
                string text = _pending.Dequeue();
                _queued.Remove(text);

                IHttpExchange exchange = BeginRequest(text);
                if (exchange == null)
                {
                    FailedRequests++;
                    continue;
                }

                InFlight entry = new InFlight { Text = text, Exchange = exchange, Sink = _sinkOfLast };
                _inFlight.Add(entry);
            }
        }

        private EmbeddingSink _sinkOfLast;

        private IHttpExchange BeginRequest(string text)
        {
            string url = BuildUrl();

            HttpRequestSpec spec = new HttpRequestSpec
            {
                Url = url,
                Method = "POST",
                Body = BuildBody(text),
                TimeoutSeconds = _options.TimeoutSeconds,
                StreamResponse = false,
            };
            spec.SetHeader("Content-Type", "application/json");
            spec.SetHeader("Authorization", "Bearer " + _options.ApiKey);

            if (_options.ExtraHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in _options.ExtraHeaders)
                {
                    spec.SetHeader(header.Key, header.Value);
                }
            }

            EmbeddingSink sink = new EmbeddingSink();
            _sinkOfLast = sink;

            try
            {
                return _transport.Begin(spec, sink);
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private void SweepInFlight()
        {
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                InFlight entry = _inFlight[i];
                if (entry.Exchange == null)
                {
                    _inFlight.RemoveAt(i);
                    continue;
                }

                entry.Exchange.Pump();

                if (entry.Exchange.State == HttpExchangeState.Receiving
                    || entry.Exchange.State == HttpExchangeState.Pending)
                {
                    continue;
                }

                _inFlight.RemoveAt(i);
                Harvest(entry);
            }
        }

        private void Harvest(InFlight entry)
        {
            EmbeddingSink sink = entry.Sink;

            if (!sink.Succeeded || sink.Vector == null || sink.Vector.Count == 0)
            {
                FailedRequests++;
                LastError = string.IsNullOrEmpty(sink.Error)
                    ? "embedding request for '" + Shorten(entry.Text) + "' produced no vector"
                    : sink.Error;
                return;
            }

            if (_options.Dimensions > 0 && sink.Vector.Count != _options.Dimensions)
            {
                // A configured width that disagrees with the server is a host mistake,
                // and caching a vector of the wrong width would corrupt every later
                // comparison. Report it rather than store it.
                FailedRequests++;
                LastError = "embedder returned " + sink.Vector.Count + " dimensions, expected " + _options.Dimensions;
                return;
            }

            if (_cache.Count >= _options.MaxCachedVectors && !_cache.ContainsKey(entry.Text))
            {
                if (!_overflowed)
                {
                    _overflowed = true;
                    LastError = "embedding cache is full at " + _options.MaxCachedVectors
                        + " vectors; further texts fall back to lexical recall";
                }
                CompletedRequests++;
                return;
            }

            float[] vector = new float[sink.Vector.Count];
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] = sink.Vector[i];
            }

            _cache[entry.Text] = vector;
            _dimensions = vector.Length;
            CompletedRequests++;
        }

        private string BuildUrl()
        {
            string baseUrl = _options.BaseUrl ?? string.Empty;
            while (baseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
            }

            string path = _options.EmbeddingsPath ?? string.Empty;
            if (path.Length > 0 && path[0] != '/')
            {
                path = "/" + path;
            }

            return baseUrl + path;
        }

        private string BuildBody(string text)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"model\":\"").Append(JsonParser.Escape(_options.DefaultModel)).Append('"');
            sb.Append(",\"input\":\"").Append(JsonParser.Escape(text)).Append('"');

            if (_options.Dimensions > 0)
            {
                // Only sent when the host asked for a width. Some endpoints reject the
                // field outright, so sending it by default would break those servers.
                sb.Append(",\"dimensions\":").Append(_options.Dimensions.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return text.Length <= 24 ? text : text.Substring(0, 24) + "...";
        }
    }

    /// <summary>
    /// Collects a whole embeddings response. Unlike the chat endpoints this is a single
    /// JSON object, not a stream, so there is no framing to do -- only buffering until
    /// the body ends.
    /// </summary>
    internal sealed class EmbeddingSink : IHttpSink
    {
        private readonly List<byte> _body = new List<byte>(512);

        public bool Succeeded { get; private set; }

        public string Error { get; private set; }

        public List<float> Vector { get; private set; }

        public int PromptTokens { get; private set; }

        private long _status;

        public void OnResponseStarted(long statusCode, string contentType)
        {
            _status = statusCode;
        }

        public void OnBodyChunk(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0 || _body.Count > 1 << 20)
            {
                // A megabyte of embeddings response is already far past anything this
                // class should be asked to hold.
                return;
            }

            for (int i = 0; i < count; i++)
            {
                _body.Add(data[offset + i]);
            }
        }

        public void OnCompleted()
        {
            if (_status >= 400)
            {
                Error = "HTTP " + _status.ToString(CultureInfo.InvariantCulture) + ": " + Clip(Body());
                Succeeded = false;
                return;
            }

            JsonValue root = JsonParser.Parse(Body());
            if (root == null || !root.IsObject)
            {
                Error = "the embeddings response did not parse";
                return;
            }

            JsonValue error = root.Path("error");
            if (error != null && !error.IsNull)
            {
                string message = root.PathString("error.message");
                Error = string.IsNullOrEmpty(message) ? "the endpoint reported an error" : message;
                return;
            }

            PromptTokens = root.Path("usage.prompt_tokens").AsInt(0);

            JsonValue data = root.Path("data");
            if (data == null || !data.IsArray || data.Count == 0)
            {
                Error = "the embeddings response carried no data";
                return;
            }

            JsonValue embedding = data[0].Path("embedding");
            if (embedding == null || !embedding.IsArray || embedding.Count == 0)
            {
                Error = "the embeddings response carried no vector";
                return;
            }

            List<float> vector = new List<float>(embedding.Count);
            for (int i = 0; i < embedding.Count; i++)
            {
                vector.Add((float)embedding[i].AsDouble(0));
            }

            Vector = vector;
            Succeeded = true;
        }

        public void OnFailed(string error, long statusCode)
        {
            _status = statusCode;
            Error = string.IsNullOrEmpty(error)
                ? "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture)
                : error;
            Succeeded = false;
        }

        private string Body()
        {
            try
            {
                return Encoding.UTF8.GetString(_body.ToArray());
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();
            return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200) + "...";
        }
    }
}
