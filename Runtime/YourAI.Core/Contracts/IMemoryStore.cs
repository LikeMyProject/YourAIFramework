using System.Collections.Generic;

namespace YourAI.Core.Contracts
{
    /// <summary>
    /// The three tiers a companion-style NPC needs, ordered by how fast they decay.
    ///
    /// Working memory is the current exchange and vanishes with it. Episodic memory
    /// is "what happened to us", retrieved by relevance and recency. Semantic memory
    /// is distilled fact -- who the player is, what they have done -- and is the only
    /// tier that is expected to survive compaction.
    /// </summary>
    public enum MemoryKind
    {
        Working = 0,
        Episodic = 1,
        Semantic = 2,
    }

    /// <summary>One remembered thing.</summary>
    public sealed class MemoryRecord
    {
        public string Id;

        /// <summary>Which agent this belongs to. Memory is never global by default.</summary>
        public string ActorId;

        public MemoryKind Kind;

        /// <summary>The remembered text, already in the form it will be replayed in.</summary>
        public string Text;

        /// <summary>Seconds since the Unix epoch, UTC.</summary>
        public double UnixTime;

        /// <summary>Free-form labels for filtering, e.g. "combat", "quest:sword".</summary>
        public string[] Tags;

        /// <summary>
        /// 0..1 importance. Set by the writer, not inferred. Used both for retrieval
        /// ranking and for deciding what survives compaction.
        /// </summary>
        public float Salience = 0.5f;

        /// <summary>Approximate token cost, so retrieval can respect a budget.</summary>
        public int EstimatedTokens;
    }

    /// <summary>Retrieval criteria. All fields are optional; defaults mean "no constraint".</summary>
    public sealed class MemoryQuery
    {
        public string ActorId;

        /// <summary>Free text to rank against. Null means "most recent first".</summary>
        public string Text;

        public int MaxResults = 8;

        /// <summary>Restrict to these tiers. Null means all tiers.</summary>
        public MemoryKind[] Kinds;

        /// <summary>Ignore records older than this. 0 means no time limit.</summary>
        public double SinceUnixTime;

        /// <summary>Stop once the accumulated token cost would exceed this. 0 means no limit.</summary>
        public int MaxTokens;
    }

    /// <summary>
    /// Storage and retrieval for agent memory.
    ///
    /// The framework ships no implementation in Core: persistence choice (in-memory,
    /// JSON on disk, SQLite, a vector database) is a deployment decision, and Core is
    /// forbidden from depending on UnityEngine, so it cannot use PlayerPrefs or
    /// Application.persistentDataPath either.
    ///
    /// Query fills a caller-owned list rather than returning a fresh collection,
    /// because retrieval runs every turn and allocating there is how a friendly NPC
    /// turns into a garbage collector spike.
    /// </summary>
    public interface IMemoryStore
    {
        /// <summary>Adds a record. Implementations may assign <c>Id</c> if it is null.</summary>
        void Append(MemoryRecord record);

        /// <summary>Appends matches to <paramref name="results"/> and returns how many were added.</summary>
        int Query(MemoryQuery query, List<MemoryRecord> results);

        bool Remove(string id);

        /// <summary>Forgets everything for one actor. A null ActorId clears all actors.</summary>
        void Clear(string actorId);

        /// <summary>Number of records currently held for an actor.</summary>
        int Count(string actorId);
    }
}
