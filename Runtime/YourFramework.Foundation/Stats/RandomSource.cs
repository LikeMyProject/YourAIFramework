using System;
using System.Collections.Generic;

namespace YourFramework.Stats
{
    /// <summary>
    /// 随机源。可替换的落点：战斗内用确定性实现（可回放/可同步），抽奖走服务端下发序列时
    /// 换一个实现塞进来即可，上层一个字节都不用改。
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>Next raw 32-bit value (the only primitive every other method builds on).</summary>
        uint NextUInt();

        /// <summary>Uniform in [0,1).</summary>
        double NextUnit();

        /// <summary>Uniform integer in [minInclusive, maxExclusive). max &lt;= min returns min.</summary>
        int Range(int minInclusive, int maxExclusive);

        /// <summary>Uniform float in [minInclusive, maxInclusive].</summary>
        float Range(float minInclusive, float maxInclusive);

        /// <summary>True with the given probability. p &lt;= 0 never, p &gt;= 1 always.</summary>
        bool Chance(float probability);

        /// <summary>Uniform element. An empty list is a wiring bug and throws.</summary>
        T Pick<T>(IList<T> items);
    }

    /// <summary>
    /// 确定性随机（PCG32），带**命名分流**。
    ///
    /// 为什么不用 System.Random：
    /// 1. 它的序列不保证跨 .NET 版本 / 跨平台稳定（.NET Core 换过算法），而战斗回放、
    ///    帧同步、断线重连都要求"同样的种子 → 同样的结果"；
    /// 2. 它没有分流的概念，于是"加了一个新的随机调用点"会顺移掉其后所有随机结果 ——
    ///    战斗里加一行特效随机就能把整场战斗的暴击序列改掉。
    ///
    /// PCG32 的取舍：64 位状态 + 64 位增量，周期 2^64，统计质量远好于 LCG/xorshift，
    /// 实现只有十几行，且**状态就是一个 ulong** —— 存档、回放、网络同步都只要搬这一个数。
    ///
    /// 命名分流 <see cref="Fork"/>：同一个种子下，"loot" 与 "crit" 各自成流，互不扰动；
    /// 新增消费点不会改变已有流的结果。这是把"随机不可复现"从工程上根除的做法。
    ///
    /// 单线程使用（泵、主线程）；跨线程共享需要外部加锁。
    /// </summary>
    public sealed class DeterministicRandom : IRandomSource
    {
        private const ulong Multiplier = 6364136223846793005UL;
        private const ulong StreamIncrement = 1442695040888963407UL;

        private readonly ulong _seed;
        private readonly string _stream;
        private readonly ulong _increment;
        private ulong _state;

        /// <summary>Creates a stream from a seed and an optional stream name (see <see cref="Fork"/>).</summary>
        public DeterministicRandom(ulong seed, string stream)
        {
            _seed = seed;
            _stream = stream;
            _increment = Mix(seed, stream) | 1UL; // must be odd for the full period
            _state = 0UL;
            NextUInt();
            _state += Mix(seed ^ 0x9E3779B97F4A7C15UL, stream);
            NextUInt();
        }

        /// <summary>Creates the default stream of a seed.</summary>
        public DeterministicRandom(ulong seed)
            : this(seed, "default")
        {
        }

        /// <summary>The seed this instance was built from (diagnostics, save files).</summary>
        public ulong Seed { get { return _seed; } }

        /// <summary>The stream name (diagnostics, save files).</summary>
        public string StreamName { get { return _stream; } }

        /// <summary>
        /// The whole generator state as one number. Save it to a save file or a network
        /// packet and restore it later; nothing else is needed because the increment is
        /// derived from the seed.
        /// </summary>
        public ulong State { get { return _state; } }

        /// <summary>Restores a state captured by <see cref="State"/> on an instance with the same seed.</summary>
        public void Restore(ulong state)
        {
            _state = state;
        }

        /// <summary>
        /// A derived stream: same seed + same stream name gives the same sequence, and the
        /// sequence is independent of how many values other streams have consumed. Use one
        /// stream per concern ("crit", "loot", "spawn") instead of one shared sequence.
        /// </summary>
        public DeterministicRandom Fork(string streamName)
        {
            if (string.IsNullOrEmpty(streamName))
            {
                throw new ArgumentException("DeterministicRandom.Fork: stream name must not be empty.", "streamName");
            }

            return new DeterministicRandom(_seed, _stream + "/" + streamName);
        }

        /// <summary>Next raw 32-bit value.</summary>
        public uint NextUInt()
        {
            ulong previous = _state;
            _state = unchecked(previous * Multiplier + _increment);
            uint xorshifted = (uint)(((previous >> 18) ^ previous) >> 27);
            int rotation = (int)(previous >> 59);
            return (xorshifted >> rotation) | (xorshifted << ((-rotation) & 31));
        }

        /// <summary>Uniform in [0,1).</summary>
        public double NextUnit()
        {
            return NextUInt() * (1.0 / 4294967296.0);
        }

        /// <summary>
        /// Uniform integer in [minInclusive, maxExclusive). Rejection sampling, so every
        /// outcome is equally likely rather than "almost equally likely" (a plain modulo
        /// biases the low end whenever the range does not divide 2^32).
        /// </summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
            {
                return minInclusive;
            }

            ulong span = (ulong)((long)maxExclusive - minInclusive);
            if (span > 4294967296UL)
            {
                throw new ArgumentException(
                    "DeterministicRandom.Range: span must fit in 32 bits (got " + span + ").");
            }

            uint bound = (uint)span;
            uint threshold = (uint)(4294967296UL % bound);
            uint value;
            do
            {
                value = NextUInt();
            }
            while (value < threshold);

            return (int)(minInclusive + (long)(value % bound));
        }

        /// <summary>Uniform float in [minInclusive, maxInclusive].</summary>
        public float Range(float minInclusive, float maxInclusive)
        {
            if (maxInclusive <= minInclusive)
            {
                return minInclusive;
            }

            return (float)(minInclusive + NextUnit() * (maxInclusive - minInclusive));
        }

        /// <summary>True with the given probability.</summary>
        public bool Chance(float probability)
        {
            if (probability <= 0f)
            {
                return false;
            }

            if (probability >= 1f)
            {
                return true;
            }

            return NextUnit() < probability;
        }

        /// <summary>Uniform element. An empty list is a wiring bug and throws.</summary>
        public T Pick<T>(IList<T> items)
        {
            if (items == null)
            {
                throw new ArgumentNullException("items");
            }

            if (items.Count == 0)
            {
                throw new ArgumentException("DeterministicRandom.Pick: items must not be empty.", "items");
            }

            return items[Range(0, items.Count)];
        }

        /// <summary>
        /// 64-bit mix used for seed/stream derivation (SplitMix64 finaliser over an FNV-1a
        /// of the stream name) so that "seed 7" and "seed 8" - or "crit" and "crit2" -
        /// produce unrelated streams instead of neighbouring ones.
        /// </summary>
        private static ulong Mix(ulong seed, string stream)
        {
            ulong hash = 14695981039346656037UL;
            if (!string.IsNullOrEmpty(stream))
            {
                for (int i = 0; i < stream.Length; i++)
                {
                    char c = stream[i];
                    // Hash both bytes explicitly: the UTF-8 byte order must not depend on
                    // the platform's endianness, or the same stream name would produce
                    // different sequences on different machines.
                    hash = (hash ^ (byte)(c & 0xFF)) * 1099511628211UL;
                    hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
                }
            }

            ulong mixed = hash ^ (seed + StreamIncrement);
            mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
            mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;
            return mixed ^ (mixed >> 31);
        }
    }
}
