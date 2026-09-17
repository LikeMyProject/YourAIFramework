namespace YourAI.Core.Contracts
{
    /// <summary>
    /// Turns text into a dense vector so that memory retrieval can rank by meaning
    /// rather than by shared words.
    ///
    /// Why this is a seam rather than a dependency. The framework's first answer to
    /// retrieval was lexical overlap, chosen because a vector index drags in a second
    /// service, a second failure mode and a second budget, and because it cannot run
    /// at all on a machine with no key. That trade is still the right default. It is
    /// not the right ceiling, and the two are not in conflict: retrieval asks this
    /// interface, ships a keyless implementation, and lets a project that has a model
    /// plug it in without any caller changing.
    ///
    /// Synchronous on purpose, and this is the one abrasive part of the design.
    /// A remote embedding model would want to stream, and this interface denies it:
    /// retrieval runs on the memory path, which runs on the framing thread, and a
    /// network round trip placed there would silently turn "the NPC remembers you"
    /// into a stall. The intended implementations are a local model, a small ONNX
    /// graph, a bundled word-vector table, or a pre-computed cache. If a project
    /// genuinely needs a remote model, the embedding belongs in a background pass
    /// that fills the cache, not in the query.
    ///
    /// Implementations must be deterministic for a given text. Ranking that shifts
    /// between calls is not diagnosable.
    /// </summary>
    public interface IEmbeddingProvider
    {
        /// <summary>Stable identifier, e.g. "hashed-128" or "bge-small-zh".</summary>
        string Name { get; }

        /// <summary>False when nothing can be embedded, typically because no model is loaded.</summary>
        bool IsAvailable { get; }

        /// <summary>Explains the false case in one sentence. Empty when available.</summary>
        string UnavailableReason { get; }

        /// <summary>Vector length every call produces. Fixed for the lifetime of the provider.</summary>
        int Dimensions { get; }

        /// <summary>
        /// Writes the embedding of <paramref name="text"/> into <paramref name="into"/>,
        /// clearing it first, and returns false when the text could not be embedded.
        ///
        /// The destination is caller-owned for the same reason <see cref="IMemoryStore"/>
        /// fills a caller-owned list: this runs once per candidate on a path that runs
        /// every turn, and allocating a vector per candidate per turn is how a friendly
        /// NPC becomes a garbage collector spike.
        ///
        /// Must not throw. A provider that cannot embed reports it as a return value,
        /// because the caller's fallback is to rank lexically, not to fail the turn.
        /// </summary>
        bool TryEmbed(string text, System.Collections.Generic.List<float> into);

        /// <summary>
        /// Queues a text for background embedding without reading the cache and
        /// without blocking. Lets a caller pay the cost up front -- after a save
        /// load, say -- so a later query finds the vector ready instead of paying
        /// for it with a lexical fallback.
        ///
        /// A miss here must never wait on I/O: queueing is the entire job. For a
        /// provider with no background work -- a synchronous keyless one -- this is
        /// a deliberate no-op, because computing is cheaper than queueing.
        /// </summary>
        void Warm(string text);
    }
}
