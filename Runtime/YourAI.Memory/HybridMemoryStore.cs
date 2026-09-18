using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Memory
{
    /// <summary>
    /// Adds a vector signal to any <see cref="IMemoryStore"/>, so that retrieval can
    /// match on meaning rather than only on shared words.
    ///
    /// A decorator, not a second store. Storage, capacity, eviction and the
    /// "semantic records are never evicted" rule stay exactly where they were; this
    /// class only takes over ordering, which is the one thing a vector index changes.
    /// That keeps the two rankers from disagreeing about anything except the part
    /// that was supposed to differ, and it means a project can add semantics to an
    /// existing store without migrating a byte.
    ///
    /// How ordering works. The inner store is asked for every candidate the filters
    /// allow, with no text and no limits, and this class then scores them:
    ///
    ///     relevance = (1 - w) * lexical + w * cosine
    ///     rank      = MemoryRanker.Blend(relevance, salience, recency)
    ///
    /// The floor-and-blend arithmetic is shared with <see cref="InMemoryMemoryStore"/>
    /// through <see cref="MemoryRanker"/>, so at <c>SemanticWeight = 0</c> the two
    /// stores return the same order for the same inputs. That equivalence is worth
    /// keeping honest: it is what makes this class safe to add to a live project.
    ///
    /// The weight defaults to 0.5 rather than 1.0 deliberately. A pure vector ranker
    /// loses exact-match performance, and exact match is what makes "长安城" retrieve
    /// the record about 长安城. Half and half buys paraphrase recall without paying
    /// for it in precision.
    ///
    /// Failure policy. If no embedder is configured, or it reports itself
    /// unavailable, or it fails on a particular record, the affected records fall
    /// back to lexical scoring rather than being dropped. Retrieval degrading to the
    /// previous behaviour is a far better outcome than a companion NPC going mute
    /// because a model file moved.
    /// </summary>
    public sealed class HybridMemoryStore : IMemoryStore
    {
        private readonly IMemoryStore _inner;
        private readonly IEmbeddingProvider _embedder;
        private readonly Func<double> _clock;

        private readonly Dictionary<string, float[]> _vectors =
            new Dictionary<string, float[]>(StringComparer.Ordinal);

        private readonly List<float> _scratchVector = new List<float>(256);
        private readonly List<float> _queryVector = new List<float>(256);
        private readonly List<MemoryRecord> _records = new List<MemoryRecord>(64);
        private readonly List<Scored> _scored = new List<Scored>(64);

        public HybridMemoryStore(IMemoryStore inner)
            : this(inner, null, null)
        {
        }

        public HybridMemoryStore(IMemoryStore inner, IEmbeddingProvider embedder)
            : this(inner, embedder, null)
        {
        }

        public HybridMemoryStore(IMemoryStore inner, IEmbeddingProvider embedder, Func<double> clock)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }
            _inner = inner;
            _embedder = embedder;
            _clock = clock ?? MemoryRanker.DefaultClock;
        }

        public IMemoryStore Inner
        {
            get { return _inner; }
        }

        public IEmbeddingProvider Embedder
        {
            get { return _embedder; }
        }

        /// <summary>
        /// 0..1. Zero is the lexical store exactly; one is pure vector ranking. Values
        /// outside the range are clamped rather than rejected, because a designer
        /// typing 1.5 in an inspector means "as semantic as possible", not "crash".
        /// </summary>
        public double SemanticWeight { get; set; } = 0.5;

        /// <summary>Recency half-life, shared with the lexical store's arithmetic.</summary>
        public double HalfLifeSeconds { get; set; } = MemoryRanker.DefaultHalfLifeSeconds;

        /// <summary>
        /// Upper bound on cached vectors. Reaching it clears the cache rather than
        /// evicting cleverly: a vector is cheap to recompute, the inner store's own
        /// eviction is what keeps the working set small, and an LRU here would be
        /// machinery guarding against a leak that cannot happen.
        /// </summary>
        public int MaxCachedVectors { get; set; } = 2048;

        /// <summary>Candidates whose embedding failed during the last query.</summary>
        public int LastEmbeddingFailures { get; private set; }

        /// <summary>Candidates scored by the last query, before the limits applied.</summary>
        public int LastCandidateCount { get; private set; }

        /// <summary>True when a vector signal was actually used by the last query.</summary>
        public bool LastQueryUsedVectors { get; private set; }

        /// <summary>
        /// False when nothing can be embedded. Retrieval still works; it is the
        /// lexical store again. Read <see cref="EmbeddingUnavailableReason"/> to find
        /// out why, rather than inferring it from ranking that "feels wrong".
        /// </summary>
        public bool IsEmbeddingAvailable
        {
            get { return _embedder != null && _embedder.IsAvailable; }
        }

        public string EmbeddingUnavailableReason
        {
            get
            {
                if (_embedder == null)
                {
                    return "No embedding provider configured; ranking lexically.";
                }
                return _embedder.IsAvailable
                    ? string.Empty
                    : _embedder.UnavailableReason;
            }
        }

        /// <summary>Number of vectors currently cached. Diagnostic, and used by the tests.</summary>
        public int CachedVectorCount
        {
            get { return _vectors.Count; }
        }

        // ------------------------------------------------------------ IMemoryStore

        public void Append(MemoryRecord record)
        {
            _inner.Append(record);
        }

        public bool Remove(string id)
        {
            bool removed = _inner.Remove(id);
            if (removed && !string.IsNullOrEmpty(id))
            {
                _vectors.Remove(id);
            }
            return removed;
        }

        public void Clear(string actorId)
        {
            _inner.Clear(actorId);

            // A null actor means "every actor", so every vector is stale. A named
            // actor cannot be mapped back to vector keys, because the cache is keyed
            // by record id and ids do not encode their actor; those entries are left
            // to the capacity bound instead of being guessed at.
            if (actorId == null)
            {
                _vectors.Clear();
            }
        }

        public int Count(string actorId)
        {
            return _inner.Count(actorId);
        }

        public int Query(MemoryQuery query, List<MemoryRecord> results)
        {
            if (results == null)
            {
                throw new ArgumentNullException("results");
            }
            if (query == null)
            {
                return 0;
            }

            LastEmbeddingFailures = 0;
            LastCandidateCount = 0;
            LastQueryUsedVectors = false;

            // Ask the inner store for everything the filters allow, unranked by text
            // and unlimited, because this class owns the ordering now. Text is null so
            // the inner relevance term is neutral; the limits are zero so nothing is
            // cut before the re-ranking has seen it.
            MemoryQuery wide = new MemoryQuery
            {
                ActorId = query.ActorId,
                Text = null,
                MaxResults = 0,
                Kinds = query.Kinds,
                SinceUnixTime = query.SinceUnixTime,
                MaxTokens = 0,
            };

            _records.Clear();
            _inner.Query(wide, _records);
            LastCandidateCount = _records.Count;

            if (_records.Count == 0)
            {
                return 0;
            }

            double now = _clock();
            bool hasText = !string.IsNullOrEmpty(query.Text);
            double weight = MemoryRanker.Clamp01(SemanticWeight);
            bool useVectors = false;

            if (hasText && weight > 0 && IsEmbeddingAvailable)
            {
                if (_embedder.TryEmbed(query.Text, _queryVector))
                {
                    useVectors = true;
                }
                else
                {
                    // The query itself could not be embedded. Nothing can be compared,
                    // so the whole query degrades rather than half of it.
                    LastEmbeddingFailures = _records.Count;

                    // Degrading this query must not stall the warming, or a remote
                    // embedder stays cold until its third query: the first queues the
                    // query text, the second the candidates, and only the third has
                    // both. Queue the candidates here so the next query finds them
                    // ready. A Warm miss is a queue append, never I/O.
                    for (int i = 0; i < _records.Count; i++)
                    {
                        _embedder.Warm(_records[i].Text);
                    }
                }
            }

            LastQueryUsedVectors = useVectors;

            _scored.Clear();
            for (int i = 0; i < _records.Count; i++)
            {
                MemoryRecord record = _records[i];

                double lexical = hasText ? MemoryTextRanker.Relevance(query.Text, record.Text) : 1.0;

                double relevance = lexical;
                if (useVectors)
                {
                    float[] vector = VectorFor(record);
                    if (vector != null)
                    {
                        relevance = (1.0 - weight) * lexical + weight * Cosine(_queryVector, vector);
                    }
                    else
                    {
                        // This record alone falls back. Dropping it, or zeroing its
                        // relevance, would punish the player for a provider hiccup.
                        LastEmbeddingFailures++;
                    }
                }

                double recency = MemoryRanker.Recency(record.UnixTime, now, HalfLifeSeconds);
                _scored.Add(new Scored
                {
                    Record = record,
                    Rank = MemoryRanker.Blend(relevance, record.Salience, recency),
                });
            }

            _scored.Sort(CompareByRankDescending);

            int limit = query.MaxResults > 0 ? query.MaxResults : _scored.Count;
            int usedTokens = 0;
            int added = 0;

            for (int i = 0; i < _scored.Count && added < limit; i++)
            {
                MemoryRecord record = _scored[i].Record;

                if (query.MaxTokens > 0)
                {
                    int cost = record.EstimatedTokens;
                    if (usedTokens + cost > query.MaxTokens)
                    {
                        // Stop rather than skip, same as the lexical store: the list is
                        // ranked, so everything after this is a worse fit.
                        break;
                    }
                    usedTokens += cost;
                }

                results.Add(record);
                added++;
            }

            return added;
        }

        /// <summary>
        /// Drops every cached vector. Intended for a host that has changed the
        /// embedder or its vocabulary and wants the change to take effect at once.
        /// </summary>
        public void InvalidateVectors()
        {
            _vectors.Clear();
        }

        // -------------------------------------------------------------- internals

        private float[] VectorFor(MemoryRecord record)
        {
            if (string.IsNullOrEmpty(record.Id))
            {
                // No stable key, so no cache. The inner store always assigns one, so
                // this is a tolerance for a custom store rather than a normal path.
                if (!_embedder.TryEmbed(record.Text, _scratchVector))
                {
                    return null;
                }
                return _scratchVector.ToArray();
            }

            float[] cached;
            if (_vectors.TryGetValue(record.Id, out cached))
            {
                return cached;
            }

            if (!_embedder.TryEmbed(record.Text, _scratchVector))
            {
                return null;
            }

            if (_vectors.Count >= MaxCachedVectors)
            {
                _vectors.Clear();
            }

            float[] copy = _scratchVector.ToArray();
            _vectors[record.Id] = copy;
            return copy;
        }

        /// <summary>
        /// Full cosine, magnitudes included, rather than a bare dot product.
        ///
        /// The shipped embedder normalises, so a dot product would be enough and
        /// would be about a third of the cost. Assuming it is not worth it: a custom
        /// provider is not obliged to normalise, and the failure mode would be
        /// silently wrong ranking that looks like a bad model rather than like a bug.
        /// </summary>
        private static double Cosine(List<float> a, float[] b)
        {
            int n = a.Count < b.Length ? a.Count : b.Length;
            if (n == 0)
            {
                return 0;
            }

            double dot = 0;
            double normA = 0;
            double normB = 0;

            for (int i = 0; i < n; i++)
            {
                double x = a[i];
                double y = b[i];
                dot += x * y;
                normA += x * x;
                normB += y * y;
            }

            if (normA <= 0 || normB <= 0)
            {
                return 0;
            }
            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }

        private static int CompareByRankDescending(Scored a, Scored b)
        {
            return b.Rank.CompareTo(a.Rank);
        }

        private struct Scored
        {
            public MemoryRecord Record;
            public double Rank;
        }
    }
}
