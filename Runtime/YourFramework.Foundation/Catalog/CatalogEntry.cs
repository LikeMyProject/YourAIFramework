using System;
using System.Collections.Generic;

namespace YourFramework.Catalog
{
    /// <summary>
    /// 目录项的用途分类。分类只影响查询与工具（构建期按类分流、按类打包），
    /// 不参与地址解析 —— 所以以后给它加成员是向后兼容的改动。
    /// </summary>
    public enum CatalogKind
    {
        Asset = 0,
        Scene = 1,
        Config = 2,
        Table = 3
    }

    /// <summary>
    /// 变体：同一逻辑地址在不同标签组合下指向不同的实际定位符（画质档 / 平台 / 语言）。
    ///
    /// 构造时把标签**去重 + Ordinal 升序**，于是"标签集合"有唯一表示 —— 这是变体之间
    /// 可比较、可确定性排序、可检测"重复标签组合"的前提。空标签集 = 默认变体（兜底）。
    ///
    /// 变体本身不可变：地址目录是构建期产物，运行期只读，避免共享实例被就地改坏。
    /// </summary>
    public sealed class CatalogVariant
    {
        /// <summary>实际定位符（资源地址 / 文件路径 / bundle 内的 asset 名）。</summary>
        public readonly string Locator;

        /// <summary>去重并 Ordinal 升序后的标签；空 = 默认变体。</summary>
        public readonly List<string> Tags;

        /// <summary>标签签名（"a+b"；空标签集为 ""）。用于确定性排序与重复组合检测。</summary>
        public readonly string TagSignature;

        public CatalogVariant(string locator, IEnumerable<string> tags)
        {
            if (locator == null || locator.Length == 0)
            {
                throw new ArgumentException("CatalogVariant: locator must not be empty.", "locator");
            }

            List<string> normalized = new List<string>();
            if (tags != null)
            {
                foreach (string tag in tags)
                {
                    if (tag == null || tag.Length == 0)
                    {
                        throw new ArgumentException(
                            "CatalogVariant: tags must be non-empty strings.", "tags");
                    }

                    if (normalized.Contains(tag))
                    {
                        // 同一个变体里写两遍同一个标签，几乎总是配置笔误；
                        // 静默去重会把笔误藏起来，所以这里 fail-fast。
                        throw new ArgumentException(
                            "CatalogVariant: duplicate tag '" + tag + "' on one variant.", "tags");
                    }

                    normalized.Add(tag);
                }
            }

            normalized.Sort(StringComparer.Ordinal);
            Locator = locator;
            Tags = normalized;
            TagSignature = string.Join("+", normalized.ToArray());
        }

        /// <summary>Tag count; 0 means this is the default variant.</summary>
        public int TagCount { get { return Tags.Count; } }

        /// <summary>Whether this is the untagged default variant.</summary>
        public bool IsDefault { get { return Tags.Count == 0; } }

        /// <summary>
        /// 子集匹配：本变体的标签**全部**处于生效集合中时才算命中。
        ///
        /// 刻意不是"有交集就算" —— 否则 "hd" 变体会因为生效集合里恰好带了 "hd"
        /// 而被无关场景命中（比如同一台机器上语言标签也在，交集判断会把画质档选歪）。
        /// 默认变体（无标签）恒命中，所以没有标签匹配时它总是最后的兜底。
        /// </summary>
        public bool Matches(CatalogTagSet active)
        {
            if (Tags.Count == 0)
            {
                return true;
            }

            if (active == null)
            {
                return false;
            }

            for (int i = 0; i < Tags.Count; i++)
            {
                if (!active.Contains(Tags[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 目录项：一个逻辑地址 + 它的变体集合 + 构建期元信息。
    ///
    /// 地址、分组、变体都是构建期约定，所以在**解析期**做严格校验（写严）；
    /// 运行期查询只做宽松判断。这与 DataTable 的错误姿态一致：坏目录在启动时就炸，
    /// 好过运行中静默取到空定位符。
    /// </summary>
    public sealed class CatalogEntry
    {
        /// <summary>逻辑地址，全表唯一（Ordinal 比较）。</summary>
        public readonly string Address;

        /// <summary>所属分组；构建期按组打包与预载的单位。</summary>
        public readonly string Group;

        /// <summary>用途分类。</summary>
        public readonly CatalogKind Kind;

        /// <summary>必需项：运行期解析不到由校验器报错，解析本身不当异常。</summary>
        public readonly bool Required;

        /// <summary>来源标注（哪张目录表供的这条），合并补丁后用于追溯归属。</summary>
        public readonly string Source;

        /// <summary>变体集合，至少一条；默认变体（空标签）至多一条。</summary>
        public readonly List<CatalogVariant> Variants;

        public CatalogEntry(
            string address,
            string group,
            CatalogKind kind,
            bool required,
            string source,
            List<CatalogVariant> variants)
        {
            Address = address;
            Group = group;
            Kind = kind;
            Required = required;
            Source = source ?? string.Empty;
            Variants = variants;
        }

        /// <summary>默认变体（无标签的那条），没有则返回 null。</summary>
        public CatalogVariant DefaultVariant
        {
            get
            {
                for (int i = 0; i < Variants.Count; i++)
                {
                    if (Variants[i].IsDefault)
                    {
                        return Variants[i];
                    }
                }

                return null;
            }
        }

        /// <summary>是否带默认变体 —— 决定"无标签环境"下这条地址能不能解析出来。</summary>
        public bool HasDefaultVariant { get { return DefaultVariant != null; } }

        /// <summary>
        /// 按生效标签挑变体：命中集合里取**标签最多的**（最具体者优先）；
        /// 标签数相同时按 Locator 的 Ordinal 序取最小 —— 让"并列"有确定解，
        /// 而不是取决于变体在文件里的书写顺序。
        /// </summary>
        public bool TryPickVariant(CatalogTagSet active, out CatalogVariant picked)
        {
            picked = null;
            for (int i = 0; i < Variants.Count; i++)
            {
                CatalogVariant candidate = Variants[i];
                if (!candidate.Matches(active))
                {
                    continue;
                }

                if (picked == null
                    || candidate.TagCount > picked.TagCount
                    || (candidate.TagCount == picked.TagCount
                        && string.CompareOrdinal(candidate.Locator, picked.Locator) < 0))
                {
                    picked = candidate;
                }
            }

            return picked != null;
        }

        public override string ToString()
        {
            return Address + " [" + Group + "/" + Kind + (Required ? ",required" : "")
                + "] x" + Variants.Count;
        }
    }

    /// <summary>
    /// 生效标签集合（运行期环境：平台 / 画质档 / 语言 / 渠道）。
    ///
    /// 内部是**排序数组 + 二分查找**：构造成本一次性，查询零分配、零装箱，也不依赖
    /// HashSet 的迭代顺序（那会影响确定性）。运行期每帧解析地址时这是热路径。
    /// </summary>
    public sealed class CatalogTagSet
    {
        private static readonly string[] NoTags = new string[0];

        /// <summary>空集合：只有默认变体能匹配。</summary>
        public static readonly CatalogTagSet Empty = new CatalogTagSet(null);

        private readonly string[] _sorted;

        public CatalogTagSet(IEnumerable<string> tags)
        {
            if (tags == null)
            {
                _sorted = NoTags;
                return;
            }

            List<string> collected = new List<string>();
            foreach (string tag in tags)
            {
                if (tag == null || tag.Length == 0)
                {
                    throw new ArgumentException(
                        "CatalogTagSet: tags must be non-empty strings.", "tags");
                }

                if (!collected.Contains(tag))
                {
                    collected.Add(tag);
                }
            }

            collected.Sort(StringComparer.Ordinal);
            _sorted = collected.ToArray();
        }

        public int Count { get { return _sorted.Length; } }

        /// <summary>Sorted copy of the active tags (deterministic order).</summary>
        public List<string> Tags { get { return new List<string>(_sorted); } }

        /// <summary>Membership test; null is never a member.</summary>
        public bool Contains(string tag)
        {
            return tag != null && Array.BinarySearch(_sorted, tag, StringComparer.Ordinal) >= 0;
        }

        public override string ToString()
        {
            return "{" + string.Join(",", _sorted) + "}";
        }
    }

    /// <summary>
    /// 解析结果。
    ///
    /// **解析不到不是异常**：可选地址本就不该存在（比如某语言没有配音），这是业务常态，
    /// 由调用方决定要不要当问题 —— 与 DataTable"缺键是业务"、Stats"属性缺失是值"
    /// 是同一条红线。必需项缺失由校验器（构建期/启动期）负责报出来。
    /// </summary>
    public struct CatalogResolution
    {
        /// <summary>是否解析出定位符。</summary>
        public bool Resolved;

        /// <summary>命中的是不是默认变体（用于区分"精确命中"与"兜底命中"）。</summary>
        public bool IsDefault;

        /// <summary>命中变体携带的标签数（特异性；越大越具体）。</summary>
        public int MatchedTags;

        /// <summary>被查询的地址。</summary>
        public string Address;

        /// <summary>解析出的定位符；未解析时为 null。</summary>
        public string Locator;

        public override string ToString()
        {
            if (!Resolved)
            {
                return (Address == null ? "<null>" : Address) + " -> <unresolved>";
            }

            return Address + " -> " + Locator + " (tags=" + MatchedTags
                + (IsDefault ? ", default" : string.Empty) + ")";
        }
    }
}
