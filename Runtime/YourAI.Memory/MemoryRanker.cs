using System;
using YourAI.Core.Contracts;

namespace YourAI.Memory
{
    /// <summary>
    /// The scoring arithmetic shared by every store, extracted so that two stores
    /// cannot drift apart.
    ///
    /// This exists because a second store appeared. <see cref="InMemoryMemoryStore"/>
    /// ranks by lexical relevance, salience and recency, and a store that adds a
    /// vector signal needs the same salience and recency treatment or the two rankers
    /// disagree about everything except the part that was supposed to be the
    /// difference. Copying four constants into a second file is how a codebase ends
    /// up with two definitions of "recent" and a bug report nobody can reproduce.
    ///
    /// The floors are the interesting part, not the weights. See
    /// <see cref="Blend"/> for why none of the three terms is allowed to reach zero.
    /// </summary>
    internal static class MemoryRanker
    {
        public const double DefaultHalfLifeSeconds = 3 * 24 * 60 * 60;

        /// <summary>
        /// Multiplier applied to the relevance term. The floor (0.35) is what makes
        /// this a ranker rather than a filter: a record with no overlap at all can
        /// still surface if it is important and fresh, because "the player's name"
        /// belongs in the prompt even when the current sentence does not mention it.
        /// Human recall works the same way, and a hard cut-off is what makes an NPC
        /// feel like it has forgotten who you are the moment you change the subject.
        /// </summary>
        public static double Blend(double relevance, double salience, double recency)
        {
            return (0.35 + 0.65 * Clamp01(relevance))
                * (0.55 + 0.45 * Clamp01(salience))
                * (0.55 + 0.45 * Clamp01(recency));
        }

        /// <summary>
        /// Hyperbolic decay rather than exponential. Both fade smoothly, but this one
        /// keeps a floor under very old records: an exponentially decayed fact about
        /// the player's home village is indistinguishable from a fact that was never
        /// stored, and those are not the same thing.
        /// </summary>
        public static double Recency(double unixTime, double now, double halfLifeSeconds)
        {
            if (halfLifeSeconds <= 0)
            {
                return 1.0;
            }

            double age = now - unixTime;
            if (age < 0)
            {
                age = 0;
            }
            return 1.0 / (1.0 + age / halfLifeSeconds);
        }

        /// <summary>
        /// Eviction score: salience and freshness only. Relevance is deliberately
        /// absent because eviction cannot know what will be asked next, and dropping
        /// the record that happens not to match today's question is exactly the failure
        /// mode the floors above exist to prevent.
        /// </summary>
        public static double RetentionScore(MemoryRecord record, double now, double halfLifeSeconds)
        {
            return (0.5 + 0.5 * Clamp01(record.Salience))
                * (0.5 + 0.5 * Recency(record.UnixTime, now, halfLifeSeconds));
        }

        public static double Clamp01(double value)
        {
            if (value < 0)
            {
                return 0;
            }
            return value > 1 ? 1 : value;
        }

        /// <summary>Seconds since the Unix epoch, UTC.</summary>
        public static double DefaultClock()
        {
            return (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        }
    }
}
