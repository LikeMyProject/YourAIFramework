using System;
using System.Collections.Generic;

namespace YourFramework.Stats
{
    /// <summary>
    /// 修饰符的三种作用方式。分三层而不是"加法 + 乘法"两层，是因为数值策划真正要区分的是
    /// "多个 +10% 该相加还是连乘" —— 前者是常见装备/天赋（应该相加：+10% +10% = +20%），
    /// 后者是罕见高阶加成（应该连乘）。混成一层，谁也没法调。
    /// </summary>
    public enum StatKind
    {
        /// <summary>Adds to the base value: (Base + ΣFlat).</summary>
        Flat,

        /// <summary>Summed first, then applied once: ×(1 + ΣPercentAdd).</summary>
        PercentAdd,

        /// <summary>Each applied separately, multiplied together: ×Π(1 + PercentMul).</summary>
        PercentMul
    }

    /// <summary>一个修饰符：改哪个属性、怎么改、谁给的（来源用于批量移除）。</summary>
    public sealed class StatModifier
    {
        /// <summary>Target stat id (must be declared on the sheet it is added to).</summary>
        public string Stat;

        /// <summary>How the value combines.</summary>
        public StatKind Kind;

        /// <summary>The amount (a fraction, not a percentage, for the two percent kinds).</summary>
        public float Value;

        /// <summary>
        /// Who granted it. The single most useful field in practice: unequipping an item is
        /// one <c>RemoveBySource("weapon#12")</c> instead of tracking every modifier by hand.
        /// </summary>
        public string Source;

        /// <summary>Builds a modifier.</summary>
        public StatModifier(string stat, StatKind kind, float value, string source)
        {
            Stat = stat;
            Kind = kind;
            Value = value;
            Source = source;
        }

        /// <summary>Human-readable form for diagnostics ("atk +8 flat ← weapon#12").</summary>
        public string Describe()
        {
            string kind = Kind == StatKind.Flat ? "flat"
                : Kind == StatKind.PercentAdd ? "pct+" : "pct×";
            return Stat + " " + (Value >= 0f ? "+" : string.Empty) + Value.ToString("0.###")
                + " " + kind
                + (string.IsNullOrEmpty(Source) ? string.Empty : " ← " + Source);
        }
    }

    /// <summary>
    /// 属性的上下限。用 struct 且默认值就是"不限制" —— 默认构造出来的语义是"没有边界"，
    /// 这与"属性本来就是无界的"直觉一致，不会出现"忘了设范围于是被夹到 [0,0]"的坑。
    /// </summary>
    public struct StatRange
    {
        /// <summary>Lower bound (only meaningful when <see cref="Bounded"/>).</summary>
        public float Min;

        /// <summary>Upper bound (only meaningful when <see cref="Bounded"/>).</summary>
        public float Max;

        /// <summary>Whether clamping applies at all.</summary>
        public bool Bounded;

        /// <summary>No clamping — the default.</summary>
        public static StatRange Unbounded { get { return default(StatRange); } }

        /// <summary>A clamped range. min &gt; max is a wiring bug and throws.</summary>
        public static StatRange Between(float min, float max)
        {
            if (min > max)
            {
                throw new ArgumentException(
                    "StatRange.Between: min (" + min + ") must not exceed max (" + max + ").");
            }

            StatRange range = default(StatRange);
            range.Min = min;
            range.Max = max;
            range.Bounded = true;
            return range;
        }

        /// <summary>Clamps when bounded, returns the value unchanged otherwise.</summary>
        public float Apply(float value)
        {
            if (!Bounded)
            {
                return value;
            }

            if (value < Min)
            {
                return Min;
            }

            return value > Max ? Max : value;
        }
    }

    /// <summary>
    /// 一个属性的完整推导过程 —— "为什么最终是 37"的答案。
    /// 只在诊断/调优路径构造（会分配），战斗热路径不要碰它。
    /// </summary>
    public sealed class StatBreakdown
    {
        /// <summary>Which stat.</summary>
        public string Stat;

        /// <summary>The declared base value.</summary>
        public float Base;

        /// <summary>Sum of all Flat modifiers.</summary>
        public float Flat;

        /// <summary>Sum of all PercentAdd modifiers (a fraction: 0.2 = +20%).</summary>
        public float PercentAdd;

        /// <summary>The product of all (1 + PercentMul) factors.</summary>
        public float PercentMulFactor;

        /// <summary>The value before clamping.</summary>
        public float Raw;

        /// <summary>The final value the sheet returns.</summary>
        public float Value;

        /// <summary>True when the range changed the result.</summary>
        public bool Clamped;

        /// <summary>Every modifier that took part, in the order it was applied.</summary>
        public List<string> Parts = new List<string>();

        /// <summary>A one-line explanation for a log or a debug overlay.</summary>
        public string Explain()
        {
            return Stat + " = (" + Base.ToString("0.###") + " + " + Flat.ToString("0.###")
                + ") × " + (1f + PercentAdd).ToString("0.###")
                + " × " + PercentMulFactor.ToString("0.###")
                + " = " + Raw.ToString("0.###")
                + (Clamped ? " → 夹到 " + Value.ToString("0.###") : string.Empty)
                + "（" + Parts.Count + " 个来源）";
        }
    }

    /// <summary>
    /// 属性表：一张"属性 id → 数值"的表，数值由**基础值 + 一串修饰符**推导出来。
    ///
    /// 求值公式（三层，顺序固定，不随插入顺序漂移）：
    /// <code>
    /// Value = clamp( (Base + ΣFlat) × max(0, 1 + ΣPercentAdd) × Π max(0, 1 + PercentMul) )
    /// </code>
    /// 两个 max(0, …) 是刻意的：把加成压到 0 是合法语义（伤害减免 100%），
    /// 压成负数不是（那等于"打一下给敌人回血"）。所以乘数下限是 0，不是 -∞。
    ///
    /// 设计取舍：
    /// 1. **写严读松**：写入（Declare 之外改 base / 加 modifier）对未声明的属性 fail-fast ——
    ///    属性名拼错在装配那一刻就爆；读取对未声明的属性返回 0，因为"配配置驱动的属性集合"
    ///    本来就会动态长出来，读侧报错会把正常流程也打断。要区分"0"和"不存在"用 TryValue；
    /// 2. **按插入顺序累加**：浮点加法不满足结合律，同样的插入顺序才给出同样的结果。
    ///    帧同步/回放要的是可复现，不是"数学上等价"；
    /// 3. **脏标记只标受影响的属性**：改一个属性不会让整张表重算，热路径读值是 O(1) 命中缓存；
    /// 4. 属性之间**不互相引用**（不做 atk = str×2 这类派生）。派生值放配表层算好再写进来 ——
    ///    一旦让属性互指，就得处理依赖图与求值顺序，那是把配表的复杂度搬进运行时；
    /// 5. 单线程使用（泵、主线程）。
    /// </summary>
    public sealed class StatSheet
    {
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<string> _declaredOrder = new List<string>();

        private sealed class Entry
        {
            public float Base;
            public float Cached;
            public bool Dirty;
            public bool HasRange;
            public StatRange Range;
            public List<StatModifier> Modifiers;
        }

        /// <summary>Declared stat ids in declaration order (stable for UI and diagnostics).</summary>
        public int Count { get { return _declaredOrder.Count; } }

        /// <summary>The declared stat ids, in declaration order.</summary>
        public List<string> Declared { get { return _declaredOrder; } }

        /// <summary>Declares a stat with base 0 and no range.</summary>
        public void Declare(string stat)
        {
            Declare(stat, 0f, StatRange.Unbounded);
        }

        /// <summary>Declares a stat with a base value and no range.</summary>
        public void Declare(string stat, float baseValue)
        {
            Declare(stat, baseValue, StatRange.Unbounded);
        }

        /// <summary>
        /// Declares a stat. Declaring the same id twice is a wiring bug (a config that
        /// silently resets an existing stat is far worse than a loud failure).
        /// </summary>
        public void Declare(string stat, float baseValue, StatRange range)
        {
            RequireStat(stat, "Declare");
            if (_entries.ContainsKey(stat))
            {
                throw new ArgumentException("StatSheet.Declare: stat '" + stat + "' is already declared.");
            }

            Entry entry = new Entry();
            entry.Base = baseValue;
            entry.Dirty = true;
            entry.HasRange = range.Bounded;
            entry.Range = range;
            _entries.Add(stat, entry);
            _declaredOrder.Add(stat);
        }

        /// <summary>Whether the sheet knows this stat.</summary>
        public bool IsDeclared(string stat)
        {
            return stat != null && _entries.ContainsKey(stat);
        }

        /// <summary>
        /// The stat's value. An undeclared stat reads as 0 (see the write-strict/read-loose
        /// note on the class); use <see cref="TryValue"/> when "0" and "unknown" differ.
        /// </summary>
        public float Value(string stat)
        {
            Entry entry;
            if (stat == null || !_entries.TryGetValue(stat, out entry))
            {
                return 0f;
            }

            if (entry.Dirty)
            {
                Evaluate(stat, entry);
            }

            return entry.Cached;
        }

        /// <summary>Reads a stat, reporting whether it exists at all.</summary>
        public bool TryValue(string stat, out float value)
        {
            Entry entry;
            if (stat == null || !_entries.TryGetValue(stat, out entry))
            {
                value = 0f;
                return false;
            }

            value = Value(stat);
            return true;
        }

        /// <summary>The declared base value (before modifiers), or 0 for an unknown stat.</summary>
        public float BaseValue(string stat)
        {
            Entry entry;
            if (stat == null || !_entries.TryGetValue(stat, out entry))
            {
                return 0f;
            }

            return entry.Base;
        }

        /// <summary>Sets the base value. An undeclared stat fails fast.</summary>
        public void SetBase(string stat, float baseValue)
        {
            Entry entry = Require(stat, "SetBase");
            entry.Base = baseValue;
            entry.Dirty = true;
        }

        /// <summary>Changes the range. An undeclared stat fails fast.</summary>
        public void SetRange(string stat, StatRange range)
        {
            Entry entry = Require(stat, "SetRange");
            entry.HasRange = range.Bounded;
            entry.Range = range;
            entry.Dirty = true;
        }

        /// <summary>Adds one modifier. An undeclared target stat fails fast.</summary>
        public void AddModifier(StatModifier modifier)
        {
            if (modifier == null)
            {
                throw new ArgumentNullException("modifier");
            }

            Entry entry = Require(modifier.Stat, "AddModifier");
            if (entry.Modifiers == null)
            {
                entry.Modifiers = new List<StatModifier>(4);
            }

            entry.Modifiers.Add(modifier);
            entry.Dirty = true;
        }

        /// <summary>Adds a batch (a null entry fails fast — half-applied batches hide bugs).</summary>
        public void AddModifiers(IList<StatModifier> modifiers)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException("modifiers");
            }

            for (int i = 0; i < modifiers.Count; i++)
            {
                AddModifier(modifiers[i]);
            }
        }

        /// <summary>
        /// Removes one specific modifier by reference (what a rollback or a targeted dispel
        /// needs). Returns false when it was not on the sheet.
        /// </summary>
        public bool RemoveModifier(StatModifier modifier)
        {
            if (modifier == null || modifier.Stat == null)
            {
                return false;
            }

            Entry entry;
            if (!_entries.TryGetValue(modifier.Stat, out entry) || entry.Modifiers == null)
            {
                return false;
            }

            int index = entry.Modifiers.IndexOf(modifier);
            if (index < 0)
            {
                return false;
            }

            entry.Modifiers.RemoveAt(index);
            entry.Dirty = true;
            return true;
        }

        /// <summary>
        /// Removes every modifier from a source and reports how many went away. This is the
        /// "unequip / dispel / leave the aura" operation, and it is why Source exists.
        /// </summary>
        public int RemoveBySource(string source)
        {
            if (source == null)
            {
                return 0;
            }

            int removed = 0;
            for (int i = 0; i < _declaredOrder.Count; i++)
            {
                Entry entry = _entries[_declaredOrder[i]];
                if (entry.Modifiers == null)
                {
                    continue;
                }

                for (int m = entry.Modifiers.Count - 1; m >= 0; m--)
                {
                    string candidate = entry.Modifiers[m].Source;
                    bool match = string.IsNullOrEmpty(source)
                        ? string.IsNullOrEmpty(candidate)
                        : string.Equals(candidate, source, StringComparison.Ordinal);

                    if (match)
                    {
                        entry.Modifiers.RemoveAt(m);
                        removed++;
                        entry.Dirty = true;
                    }
                }
            }

            return removed;
        }

        /// <summary>Removes every modifier from every stat (respawn / re-roll).</summary>
        public int ClearModifiers()
        {
            int removed = 0;
            for (int i = 0; i < _declaredOrder.Count; i++)
            {
                Entry entry = _entries[_declaredOrder[i]];
                if (entry.Modifiers == null || entry.Modifiers.Count == 0)
                {
                    continue;
                }

                removed += entry.Modifiers.Count;
                entry.Modifiers.Clear();
                entry.Dirty = true;
            }

            return removed;
        }

        /// <summary>How many modifiers currently sit on a stat.</summary>
        public int ModifierCount(string stat)
        {
            Entry entry;
            if (stat == null || !_entries.TryGetValue(stat, out entry) || entry.Modifiers == null)
            {
                return 0;
            }

            return entry.Modifiers.Count;
        }

        /// <summary>
        /// The full derivation of a stat (diagnostics only — this allocates). This is the
        /// difference between "damage looks wrong" and "the armour buff from the shield
        /// is being applied twice".
        /// </summary>
        public StatBreakdown Explain(string stat)
        {
            StatBreakdown breakdown = new StatBreakdown();
            breakdown.Stat = stat;

            Entry entry;
            if (stat == null || !_entries.TryGetValue(stat, out entry))
            {
                breakdown.Value = 0f;
                breakdown.Raw = 0f;
                breakdown.PercentMulFactor = 1f;
                return breakdown;
            }

            breakdown.Base = entry.Base;
            breakdown.PercentMulFactor = 1f;

            if (entry.Modifiers != null)
            {
                for (int i = 0; i < entry.Modifiers.Count; i++)
                {
                    StatModifier modifier = entry.Modifiers[i];
                    breakdown.Parts.Add(modifier.Describe());
                    if (modifier.Kind == StatKind.Flat)
                    {
                        breakdown.Flat += modifier.Value;
                    }
                    else if (modifier.Kind == StatKind.PercentAdd)
                    {
                        breakdown.PercentAdd += modifier.Value;
                    }
                    else
                    {
                        breakdown.PercentMulFactor *= NonNegative(1f + modifier.Value);
                    }
                }
            }

            breakdown.Raw = (breakdown.Base + breakdown.Flat)
                * NonNegative(1f + breakdown.PercentAdd)
                * breakdown.PercentMulFactor;
            breakdown.Value = entry.HasRange ? entry.Range.Apply(breakdown.Raw) : breakdown.Raw;
            breakdown.Clamped = breakdown.Value != breakdown.Raw;
            return breakdown;
        }

        /// <summary>Discards all declared stats and modifiers (a hard reset, e.g. on scene change).</summary>
        public void Clear()
        {
            _entries.Clear();
            _declaredOrder.Clear();
        }

        private void Evaluate(string stat, Entry entry)
        {
            float flat = 0f;
            float percentAdd = 0f;
            float percentMulFactor = 1f;

            if (entry.Modifiers != null)
            {
                for (int i = 0; i < entry.Modifiers.Count; i++)
                {
                    StatModifier modifier = entry.Modifiers[i];
                    if (modifier.Kind == StatKind.Flat)
                    {
                        flat += modifier.Value;
                    }
                    else if (modifier.Kind == StatKind.PercentAdd)
                    {
                        percentAdd += modifier.Value;
                    }
                    else
                    {
                        percentMulFactor *= NonNegative(1f + modifier.Value);
                    }
                }
            }

            float raw = (entry.Base + flat) * NonNegative(1f + percentAdd) * percentMulFactor;
            entry.Cached = entry.HasRange ? entry.Range.Apply(raw) : raw;
            entry.Dirty = false;
        }

        /// <summary>
        /// Keeps the multiplier non-negative: pushing a bonus to 0 is meaningful (100%
        /// mitigation), pushing it below 0 is not (it would invert the sign of the result).
        /// </summary>
        private static float NonNegative(float factor)
        {
            return factor < 0f ? 0f : factor;
        }

        private Entry Require(string stat, string where)
        {
            RequireStat(stat, where);
            Entry entry;
            if (!_entries.TryGetValue(stat, out entry))
            {
                throw new ArgumentException(
                    "StatSheet." + where + ": stat '" + stat
                    + "' is not declared. Declare it before writing to it (a typo in a stat id "
                    + "must not silently do nothing).");
            }

            return entry;
        }

        private static void RequireStat(string stat, string where)
        {
            if (string.IsNullOrEmpty(stat))
            {
                throw new ArgumentException("StatSheet." + where + ": stat id must not be empty.", "stat");
            }
        }
    }
}
