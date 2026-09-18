using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Memory
{
    /// <summary>
    /// A keyless, deterministic embedding provider: tokens hashed into a fixed-width
    /// vector, two buckets per token with opposing signs, then L2 normalised.
    ///
    /// <b>What this is not.</b> It is not a semantic model and it does not understand
    /// anything. Two words that mean the same thing but share no characters land in
    /// unrelated buckets, so this provider does not recover the paraphrase case that
    /// motivated vector retrieval in the first place. Saying so up front matters more
    /// than the implementation, because a reader who assumes otherwise will measure
    /// retrieval quality and conclude the hybrid store is broken rather than
    /// unconfigured.
    ///
    /// <b>What it is for.</b> Three things, none of them semantic quality:
    ///
    /// 1. It makes the vector path real. <see cref="HybridMemoryStore"/> runs, is
    ///    tested, and has a working provider behind it instead of null, so the seam
    ///    is exercised rather than merely declared.
    /// 2. It works with no key, no model file, no network and no disk, which is the
    ///    same constraint that shaped the rest of this layer. A project that never
    ///    configures an embedding model still gets a store that behaves identically
    ///    to the lexical one, rather than a store that refuses to answer.
    /// 3. It is the reference implementation for anyone writing the real one: replace
    ///    <see cref="TryEmbed"/> with a model call and nothing else changes.
    ///
    /// The hashing is FNV-1a rather than <c>string.GetHashCode()</c>. The latter is
    /// randomised per process on modern .NET runtimes, which would make the same
    /// memory rank differently in two runs of the same build: unreproducible, and
    /// with a saved memory file, actively confusing.
    ///
    /// The signed projection is what keeps collisions survivable. With one unsigned
    /// bucket per token, two tokens colliding always add to each other and similarity
    /// inflates; with two buckets and a sign drawn from the token hash, colliding
    /// tokens cancel about as often as they reinforce, which is the standard hashing
    /// trick and costs one extra multiply.
    /// </summary>
    public sealed class HashedEmbeddingProvider : IEmbeddingProvider
    {
        public const int DefaultDimensions = 128;

        private const uint SeedA = 2166136261;
        private const uint SeedB = 16777619;
        private const uint Prime = 16777619;

        private readonly int _dimensions;

        public HashedEmbeddingProvider()
            : this(DefaultDimensions)
        {
        }

        /// <param name="dimensions">Vector width. 128 is plenty for a few hundred short records.</param>
        public HashedEmbeddingProvider(int dimensions)
        {
            _dimensions = dimensions > 0 ? dimensions : DefaultDimensions;
        }

        public string Name
        {
            get { return "hashed-" + _dimensions; }
        }

        public bool IsAvailable
        {
            get { return true; }
        }

        public string UnavailableReason
        {
            get { return string.Empty; }
        }

        public int Dimensions
        {
            get { return _dimensions; }
        }

        /// <summary>
        /// Optional allow-list of features. Null means every token counts, which is
        /// right for a few hundred records. Setting it stops a handful of very common
        /// words from dominating every vector, at the cost of a build step to work out
        /// what belongs in it.
        /// </summary>
        public HashSet<string> Vocabulary { get; set; }

        public bool TryEmbed(string text, List<float> into)
        {
            if (into == null)
            {
                return false;
            }

            into.Clear();
            for (int i = 0; i < _dimensions; i++)
            {
                into.Add(0f);
            }

            if (string.IsNullOrEmpty(text))
            {
                // A zero vector has no direction, so cosine against it is undefined.
                // Returning true with a zero vector would push that decision onto
                // every caller; returning false says "nothing to embed, rank this
                // lexically instead", which is what an empty record deserves.
                return false;
            }

            HashSet<string> tokens = MemoryTextRanker.Tokenize(text);
            if (tokens.Count == 0)
            {
                return false;
            }

            int used = 0;
            foreach (string token in tokens)
            {
                if (Vocabulary != null && !Vocabulary.Contains(token))
                {
                    continue;
                }

                uint a = Fnv(token, SeedA);
                uint b = Fnv(token, SeedB);

                int indexA = (int)(a % (uint)_dimensions);
                int indexB = (int)(b % (uint)_dimensions);

                into[indexA] = into[indexA] + 1f;
                into[indexB] = into[indexB] - 1f;
                used++;
            }

            if (used == 0)
            {
                return false;
            }

            Normalise(into);
            return true;
        }

        /// <summary>
        /// Nothing to precompute: the vector is one hash away, so warming would only
        /// add bookkeeping around a no-op. Present to satisfy the interface.
        /// </summary>
        public void Warm(string text)
        {
        }

        /// <summary>
        /// Scales the vector to unit length in place. Cosine similarity then reduces
        /// to a dot product, which keeps the per-turn cost to one multiply per
        /// dimension instead of three.
        /// </summary>
        private static void Normalise(List<float> vector)
        {
            double sum = 0;
            for (int i = 0; i < vector.Count; i++)
            {
                double v = vector[i];
                sum += v * v;
            }

            if (sum <= 0)
            {
                return;
            }

            float scale = (float)(1.0 / Math.Sqrt(sum));
            for (int i = 0; i < vector.Count; i++)
            {
                vector[i] = vector[i] * scale;
            }
        }

        /// <summary>
        /// FNV-1a over UTF-16 code units. Chosen for being eight lines, stable across
        /// runtimes and versions, and good enough for a projection; it is not a
        /// cryptographic hash and does not need to be.
        /// </summary>
        private static uint Fnv(string token, uint seed)
        {
            uint hash = seed;
            for (int i = 0; i < token.Length; i++)
            {
                hash = hash ^ token[i];
                hash = hash * Prime;
            }
            return hash;
        }
    }
}
