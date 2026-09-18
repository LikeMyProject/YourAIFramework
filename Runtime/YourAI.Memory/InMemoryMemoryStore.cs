using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Memory
{
    /// <summary>
    /// A working <see cref="IMemoryStore"/> that keeps everything in process memory.
    ///
    /// It is not a placeholder for a "real" store. For a companion NPC it is usually
    /// the correct choice end to end: a few hundred short strings per actor cost
    /// almost nothing, survive a scene load if the store is held by the runtime, and
    /// avoid a persistence layer whose failure modes (half-written files, schema
    /// drift, save-file corruption) are worse than the thing they prevent. What it
    /// does not do is survive process exit; a project that needs that either writes
    /// its own store or serialises this one's records, and the records are plain
    /// fields precisely so that either is easy.
    ///
    /// Three design notes worth stating plainly:
    ///
    /// Threading: not thread safe, and it should not be. Every writer in this
    /// framework runs on the pumping thread by construction, so adding a lock would
    /// buy nothing and would hide a caller that broke the rule. Retrieval is
    /// allocation-light for the same reason -- query runs every turn.
    ///
    /// Eviction: capped, and it never evicts Semantic records. Working and episodic
    /// memory are logs; semantic memory is the distilled conclusion drawn from them,
    /// and losing the conclusion while keeping the log is exactly backwards. When
    /// only semantic records remain, the cap is simply exceeded rather than
    /// discarding facts.
    /// </summary>
    public sealed class InMemoryMemoryStore : IMemoryStore
    {
        public const int DefaultMaxRecordsPerActor = 512;

        /// <summary>
        /// Three days. Long enough that a session's events stay fully vivid, short
        /// enough that last week's gossip fades behind today's. Deliberately wall
        /// clock rather than in-game time: memory is about how long the player has
        /// been away, which is what a returning player experiences.
        ///
        /// The value lives in <see cref="MemoryRanker"/> because the semantic store
        /// ranks with the same decay, and two definitions of "recent" would make the
        /// two stores disagree about everything except the part meant to differ.
        /// </summary>
        public const double DefaultHalfLifeSeconds = MemoryRanker.DefaultHalfLifeSeconds;

        private readonly Dictionary<string, List<MemoryRecord>> _byActor =
            new Dictionary<string, List<MemoryRecord>>(StringComparer.Ordinal);

        private readonly List<Scored> _scratch = new List<Scored>(64);
        private readonly Func<double> _clock;
        private readonly int _maxRecordsPerActor;

        private int _nextId;

        public InMemoryMemoryStore()
            : this(DefaultMaxRecordsPerActor)
        {
        }

        public InMemoryMemoryStore(int maxRecordsPerActor)
            : this(maxRecordsPerActor, null)
        {
        }

        /// <param name="clock">
        /// Returns seconds since the Unix epoch, UTC. Injected so that recency
        /// ranking can be tested without waiting three days for a decay to matter.
        /// </param>
        public InMemoryMemoryStore(int maxRecordsPerActor, Func<double> clock)
        {
            _maxRecordsPerActor = maxRecordsPerActor > 0
                ? maxRecordsPerActor
                : DefaultMaxRecordsPerActor;
            _clock = clock ?? MemoryRanker.DefaultClock;
            HalfLifeSeconds = DefaultHalfLifeSeconds;
        }

        /// <summary>Recency half-life used for ranking and eviction.</summary>
        public double HalfLifeSeconds { get; set; }

        public void Append(MemoryRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException("record");
            }

            if (string.IsNullOrEmpty(record.Id))
            {
                record.Id = "m" + (++_nextId).ToString();
            }
            if (record.ActorId == null)
            {
                // The interface documents a null actor on Clear as "all actors", so
                // an empty string is the only safe representation of "no actor".
                record.ActorId = string.Empty;
            }
            if (record.UnixTime <= 0)
            {
                record.UnixTime = _clock();
            }
            if (record.EstimatedTokens <= 0)
            {
                record.EstimatedTokens = ContextSection.EstimateTokens(record.Text);
            }

            List<MemoryRecord> bucket;
            if (!_byActor.TryGetValue(record.ActorId, out bucket))
            {
                bucket = new List<MemoryRecord>(16);
                _byActor[record.ActorId] = bucket;
            }

            bucket.Add(record);
            EnforceCapacity(bucket);
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

            _scratch.Clear();
            double now = _clock();

            if (string.IsNullOrEmpty(query.ActorId))
            {
                foreach (KeyValuePair<string, List<MemoryRecord>> pair in _byActor)
                {
                    Collect(pair.Value, query, now);
                }
            }
            else
            {
                List<MemoryRecord> bucket;
                if (_byActor.TryGetValue(query.ActorId, out bucket))
                {
                    Collect(bucket, query, now);
                }
            }

            if (_scratch.Count == 0)
            {
                return 0;
            }

            _scratch.Sort(CompareByRankDescending);

            int limit = query.MaxResults > 0 ? query.MaxResults : _scratch.Count;
            int usedTokens = 0;
            int added = 0;

            for (int i = 0; i < _scratch.Count && added < limit; i++)
            {
                MemoryRecord record = _scratch[i].Record;

                if (query.MaxTokens > 0)
                {
                    int cost = record.EstimatedTokens;
                    if (usedTokens + cost > query.MaxTokens)
                    {
                        // Stop rather than skip: the list is ranked, so everything
                        // after this entry is a worse fit, and padding the budget with
                        // worse material is not an improvement.
                        break;
                    }
                    usedTokens += cost;
                }

                results.Add(record);
                added++;
            }

            return added;
        }

        public bool Remove(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            foreach (KeyValuePair<string, List<MemoryRecord>> pair in _byActor)
            {
                List<MemoryRecord> bucket = pair.Value;
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (string.Equals(bucket[i].Id, id, StringComparison.Ordinal))
                    {
                        // Removing from the list, not the dictionary, so enumeration
                        // over the dictionary stays valid.
                        bucket.RemoveAt(i);
                        return true;
                    }
                }
            }

            return false;
        }

        public void Clear(string actorId)
        {
            if (actorId == null)
            {
                _byActor.Clear();
                return;
            }
            _byActor.Remove(actorId);
        }

        public int Count(string actorId)
        {
            if (actorId == null)
            {
                int total = 0;
                foreach (KeyValuePair<string, List<MemoryRecord>> pair in _byActor)
                {
                    total += pair.Value.Count;
                }
                return total;
            }

            List<MemoryRecord> bucket;
            return _byActor.TryGetValue(actorId, out bucket) ? bucket.Count : 0;
        }

        /// <summary>
        /// Drops every record of one tier for one actor. The intended use is clearing
        /// Working memory at the end of a turn -- that tier is defined as "vanishes
        /// with the exchange", and a host that never clears it turns a conversation
        /// into a leak.
        /// </summary>
        public int ClearKind(string actorId, MemoryKind kind)
        {
            if (actorId == null)
            {
                return 0;
            }

            List<MemoryRecord> bucket;
            if (!_byActor.TryGetValue(actorId, out bucket))
            {
                return 0;
            }

            int removed = 0;
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                if (bucket[i].Kind == kind)
                {
                    bucket.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        public int CountOfKind(string actorId, MemoryKind kind)
        {
            List<MemoryRecord> bucket;
            if (actorId == null || !_byActor.TryGetValue(actorId, out bucket))
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i].Kind == kind)
                {
                    total++;
                }
            }
            return total;
        }

        /// <summary>
        /// Copies records into <paramref name="into"/>. A null ActorId means every
        /// actor, matching <see cref="Clear"/> and <see cref="Count"/>, so that the
        /// three of them agree about what a null actor means; a persistence layer
        /// asking for "everything" is the caller that depends on it.
        /// </summary>
        public void Snapshot(string actorId, List<MemoryRecord> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            if (actorId == null)
            {
                foreach (KeyValuePair<string, List<MemoryRecord>> pair in _byActor)
                {
                    into.AddRange(pair.Value);
                }
                return;
            }

            List<MemoryRecord> bucket;
            if (!_byActor.TryGetValue(actorId, out bucket))
            {
                return;
            }
            into.AddRange(bucket);
        }

        // ----------------------------------------------------------------- internals

        private void Collect(List<MemoryRecord> bucket, MemoryQuery query, double now)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                MemoryRecord record = bucket[i];

                if (query.Kinds != null && query.Kinds.Length > 0 && !ContainsKind(query.Kinds, record.Kind))
                {
                    continue;
                }
                if (query.SinceUnixTime > 0 && record.UnixTime < query.SinceUnixTime)
                {
                    continue;
                }

                _scratch.Add(new Scored { Record = record, Rank = Rank(record, query, now) });
            }
        }

        private double Rank(MemoryRecord record, MemoryQuery query, double now)
        {
            // With no query text there is nothing to be relevant to, so relevance is
            // neutral rather than zero: recency and salience alone decide, which is
            // what "most recent first" is supposed to mean.
            double relevance = string.IsNullOrEmpty(query.Text)
                ? 1.0
                : MemoryTextRanker.Relevance(query.Text, record.Text);

            double recency = MemoryRanker.Recency(record.UnixTime, now, HalfLifeSeconds);

            // The floors (0.35, 0.55, 0.55) live in MemoryRanker.Blend, with the
            // reasoning. Read that before changing a weight here.
            return MemoryRanker.Blend(relevance, record.Salience, recency);
        }

        /// <summary>
        /// Evicts the least valuable record until the bucket fits. Valuable means
        /// salient and recent; semantic memory is never a candidate.
        /// </summary>
        private void EnforceCapacity(List<MemoryRecord> bucket)
        {
            while (bucket.Count > _maxRecordsPerActor)
            {
                double now = _clock();
                int victim = -1;
                double worst = double.MaxValue;

                for (int i = 0; i < bucket.Count; i++)
                {
                    MemoryRecord record = bucket[i];

                    // Distilled facts outlive the log they were distilled from.
                    if (record.Kind == MemoryKind.Semantic)
                    {
                        continue;
                    }

                    double score = MemoryRanker.RetentionScore(record, now, HalfLifeSeconds);
                    if (score < worst)
                    {
                        worst = score;
                        victim = i;
                    }
                }

                if (victim < 0)
                {
                    // Only semantic records left. The cap is documentation of intent,
                    // not a hard invariant, and discarding facts to satisfy a number
                    // would be the wrong trade.
                    return;
                }

                bucket.RemoveAt(victim);
            }
        }

        private static bool ContainsKind(MemoryKind[] kinds, MemoryKind kind)
        {
            for (int i = 0; i < kinds.Length; i++)
            {
                if (kinds[i] == kind)
                {
                    return true;
                }
            }
            return false;
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
