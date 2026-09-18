using System;
using System.Collections.Generic;

namespace YourAI.Memory
{
    /// <summary>
    /// Cheap lexical relevance, scoped to the job memory retrieval actually has:
    /// given a line of player speech, rank remembered lines by how much they talk
    /// about the same things.
    ///
    /// Why not embeddings. A vector index would rank better, and it is the right
    /// answer at scale, but it drags in a second service (an embedding endpoint or a
    /// local model), a second failure mode, and a second budget -- and it cannot run
    /// at all when the AI layer is absent, which is a state this framework explicitly
    /// supports. Lexical overlap needs no network, no model, and no key, so memory
    /// retrieval keeps working on a machine that has never talked to an LLM. A
    /// project that wants vectors can implement IMemoryStore and keep every caller
    /// unchanged; that is the point of the seam.
    ///
    /// The tokenisation is hybrid on purpose. Latin text splits on non-alphanumerics,
    /// while CJK contributes both single characters and adjacent bigrams: single
    /// characters give recall ("剑" finds "长剑" and "剑术"), bigrams give precision
    /// ("长剑" does not match "剑术" on a two-character basis). Chinese has no spaces,
    /// so a whitespace tokeniser would treat a whole sentence as one token and score
    /// everything as either identical or unrelated.
    /// </summary>
    internal static class MemoryTextRanker
    {
        /// <summary>Returns 0..1. Zero means no shared vocabulary at all.</summary>
        public static double Relevance(string query, string candidate)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate))
            {
                return 0;
            }

            HashSet<string> queryTokens = Tokenize(query);
            if (queryTokens.Count == 0)
            {
                return 0;
            }

            HashSet<string> candidateTokens = Tokenize(candidate);
            if (candidateTokens.Count == 0)
            {
                return 0;
            }

            int overlap = 0;
            foreach (string token in queryTokens)
            {
                if (candidateTokens.Contains(token))
                {
                    overlap++;
                }
            }

            if (overlap == 0)
            {
                return 0;
            }

            // Precision is weighted higher than coverage: a long record that covers
            // most of the query is a hit even though it also covers plenty else,
            // whereas a record that merely shares one common word with the query is
            // not a hit at all.
            double precision = (double)overlap / queryTokens.Count;
            double coverage = (double)overlap / candidateTokens.Count;
            double score = 0.75 * precision + 0.25 * coverage;

            return score > 1.0 ? 1.0 : score;
        }

        public static HashSet<string> Tokenize(string text)
        {
            HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text))
            {
                return tokens;
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (IsCjk(c))
                {
                    tokens.Add(text.Substring(i, 1));
                    if (i + 1 < text.Length && IsCjk(text[i + 1]))
                    {
                        tokens.Add(text.Substring(i, 2));
                    }
                    i++;
                    continue;
                }

                // Note the IsCjk guard: char.IsLetterOrDigit is true for CJK, so
                // without it a Chinese sentence would be swallowed as one "word".
                if (char.IsLetterOrDigit(c))
                {
                    int start = i;
                    while (i < text.Length && char.IsLetterOrDigit(text[i]) && !IsCjk(text[i]))
                    {
                        i++;
                    }
                    tokens.Add(text.Substring(start, i - start).ToLowerInvariant());
                    continue;
                }

                i++;
            }

            return tokens;
        }

        public static bool IsCjk(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0x3040 && c <= 0x30FF);
        }
    }
}
