using System;
using System.Collections.Generic;
using YourAI.Core.Json;

namespace YourFramework.Catalog
{
    /// <summary>
    /// 地址目录表：把散落在代码里的字符串地址收敛成一张**可离线校验的数据表**。
    ///
    /// 存在理由：地址写死在代码里时，"这个地址打哪个包、哪些是必载、换了画质档该取哪个"
    /// 这些问题只能在运行中靠试。目录表把它们变成构建期就能验的声明，并在合并补丁时
    /// 留下来源归属（Source）。它**不认识**任何具体的打包/热更实现（那是 Pack 的事），
    /// 两者只通过"地址 → 定位符"这一层数据对接，符合模块间不互相引用的红线。
    ///
    /// 错误姿态（与 DataTable 一致）：
    ///   - 目录是构建期产物 → 结构错误（坏 JSON、重复地址、空分组、未知分类、重复标签组合）
    ///     一律 **fail-fast**，启动就炸好过运行中静默取到空；
    ///   - 运行期查询 → **宽松**：查不到就是"没解析出来"，是值不是异常，由调用方与
    ///     校验器决定要不要当问题。
    ///
    /// 线程：解析/合并完成后只读，可多线程并发 Resolve。
    /// </summary>
    public sealed class AddressCatalog
    {
        private readonly Dictionary<string, CatalogEntry> _entries =
            new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

        /// <summary>地址的文件顺序（合并时被覆盖的地址保留原槽位，保证遍历确定性）。</summary>
        private readonly List<string> _order = new List<string>();

        private string _name;

        private AddressCatalog()
        {
            _name = string.Empty;
        }

        /// <summary>An empty catalog (nothing resolves until entries are merged in).</summary>
        public static AddressCatalog Empty()
        {
            return new AddressCatalog();
        }

        /// <summary>Catalog name; entries parsed from it carry this as their Source.</summary>
        public string Name { get { return _name; } }

        /// <summary>Entry count.</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>Addresses in file order (a copy; safe to iterate while resolving).</summary>
        public List<string> Addresses { get { return new List<string>(_order); } }

        // ------------------------------------------------------------ 解析

        /// <summary>
        /// Parses a catalog document. Every structural problem throws immediately
        /// (build-time artifact, fail-fast); parser exceptions are normalized to
        /// <see cref="ArgumentException"/> so callers see one error shape.
        /// </summary>
        public static AddressCatalog FromJson(string json)
        {
            JsonValue parsed;
            try
            {
                parsed = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    "AddressCatalog: invalid JSON: " + ex.Message, ex);
            }

            return FromValue(parsed);
        }

        /// <summary>Same contract as <see cref="FromJson"/>, from an already-parsed root.</summary>
        public static AddressCatalog FromValue(JsonValue root)
        {
            if (root == null || root.Kind != JsonKind.Object)
            {
                throw new ArgumentException("AddressCatalog: root must be a JSON object.");
            }

            JsonValue nameValue = root["name"];
            string name = nameValue == null || nameValue.Kind == JsonKind.Null
                ? string.Empty
                : nameValue.AsString(string.Empty);

            JsonValue rows = root["entries"];
            if (rows == null || rows.Kind != JsonKind.Array)
            {
                throw new ArgumentException(
                    "AddressCatalog: root must carry an 'entries' array.");
            }

            AddressCatalog catalog = new AddressCatalog();
            catalog._name = name;

            for (int i = 0; i < rows.Items.Count; i++)
            {
                JsonValue row = rows.Items[i];
                if (row == null || row.Kind != JsonKind.Object)
                {
                    throw new ArgumentException(
                        "AddressCatalog: entry " + i + " is not a JSON object.");
                }

                CatalogEntry entry = ParseEntry(row, i, name);
                if (catalog._entries.ContainsKey(entry.Address))
                {
                    throw new ArgumentException(
                        "AddressCatalog: duplicate address '" + entry.Address
                        + "' (entries are 0-based).");
                }

                catalog._entries.Add(entry.Address, entry);
                catalog._order.Add(entry.Address);
            }

            return catalog;
        }

        private static CatalogEntry ParseEntry(JsonValue row, int index, string source)
        {
            string address = RequiredString(row, "address", index);
            ValidateToken(address, "address", index, false);
            string group = RequiredString(row, "group", index);
            ValidateToken(group, "group", index, true);

            return new CatalogEntry(
                address,
                group,
                ParseKind(row, index),
                OptionalBool(row, "required", false),
                // 条目自己的 source 优先：合并过的表被写出再读回来时，归属不会丢。
                OptionalString(row, "source", source),
                ParseVariants(row, address));
        }

        private static List<CatalogVariant> ParseVariants(JsonValue row, string address)
        {
            List<CatalogVariant> variants = new List<CatalogVariant>();

            // 简写形式：直接给一个 locator，等价于单个默认变体。
            JsonValue locator = row["locator"];
            if (locator != null && locator.Kind != JsonKind.Null)
            {
                string text = locator.AsString(null);
                if (text == null || text.Length == 0)
                {
                    throw new ArgumentException(
                        "AddressCatalog: '" + address + "' has an empty 'locator'.");
                }

                variants.Add(new CatalogVariant(text, null));
            }

            JsonValue variantRows = row["variants"];
            if (variantRows != null && variantRows.Kind != JsonKind.Null)
            {
                if (variantRows.Kind != JsonKind.Array)
                {
                    throw new ArgumentException(
                        "AddressCatalog: '" + address + "' has a non-array 'variants'.");
                }

                for (int i = 0; i < variantRows.Items.Count; i++)
                {
                    JsonValue variantRow = variantRows.Items[i];
                    if (variantRow == null || variantRow.Kind != JsonKind.Object)
                    {
                        throw new ArgumentException(
                            "AddressCatalog: variant " + i + " of '" + address
                            + "' is not a JSON object.");
                    }

                    JsonValue variantLocator = variantRow["locator"];
                    string text = variantLocator == null || variantLocator.Kind == JsonKind.Null
                        ? null
                        : variantLocator.AsString(null);
                    if (text == null || text.Length == 0)
                    {
                        throw new ArgumentException(
                            "AddressCatalog: variant " + i + " of '" + address
                            + "' is missing a non-empty 'locator'.");
                    }

                    variants.Add(new CatalogVariant(text, ParseTags(variantRow, address, i)));
                }
            }

            if (variants.Count == 0)
            {
                throw new ArgumentException(
                    "AddressCatalog: '" + address
                    + "' declares no locator and no variants (an address that points nowhere).");
            }

            // 标签组合重复 = 同一个环境有两个候选，选谁取决于书写顺序 —— 隐性歧义，拒绝。
            for (int i = 0; i < variants.Count; i++)
            {
                for (int j = i + 1; j < variants.Count; j++)
                {
                    if (string.Equals(
                        variants[i].TagSignature, variants[j].TagSignature,
                        StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "AddressCatalog: '" + address + "' has two variants with the same "
                            + "tag set '" + variants[i].TagSignature + "' (ambiguous).");
                    }
                }
            }

            return variants;
        }

        private static List<string> ParseTags(JsonValue variantRow, string address, int index)
        {
            JsonValue tags = variantRow["tags"];
            if (tags == null || tags.Kind == JsonKind.Null)
            {
                return null;
            }

            if (tags.Kind != JsonKind.Array)
            {
                throw new ArgumentException(
                    "AddressCatalog: variant " + index + " of '" + address
                    + "' has a non-array 'tags'.");
            }

            List<string> parsed = new List<string>(tags.Items.Count);
            for (int i = 0; i < tags.Items.Count; i++)
            {
                JsonValue tag = tags.Items[i];
                string text = tag == null ? null : tag.AsString(null);
                if (text == null || text.Length == 0)
                {
                    throw new ArgumentException(
                        "AddressCatalog: variant " + index + " of '" + address
                        + "' has a non-string or empty tag.");
                }

                parsed.Add(text);
            }

            return parsed;
        }

        private static CatalogKind ParseKind(JsonValue row, int index)
        {
            JsonValue kind = row["kind"];
            if (kind == null || kind.Kind == JsonKind.Null)
            {
                return CatalogKind.Asset;
            }

            string text = kind.AsString(null);
            if (text == null)
            {
                throw new ArgumentException(
                    "AddressCatalog: entry " + index + " has a non-string 'kind'.");
            }

            if (string.Equals(text, "asset", StringComparison.OrdinalIgnoreCase))
            {
                return CatalogKind.Asset;
            }

            if (string.Equals(text, "scene", StringComparison.OrdinalIgnoreCase))
            {
                return CatalogKind.Scene;
            }

            if (string.Equals(text, "config", StringComparison.OrdinalIgnoreCase))
            {
                return CatalogKind.Config;
            }

            if (string.Equals(text, "table", StringComparison.OrdinalIgnoreCase))
            {
                return CatalogKind.Table;
            }

            throw new ArgumentException(
                "AddressCatalog: entry " + index + " has unknown kind '" + text
                + "' (expected asset|scene|config|table).");
        }

        private static string RequiredString(JsonValue row, string field, int index)
        {
            JsonValue value = row[field];
            if (value == null || value.Kind == JsonKind.Null)
            {
                throw new ArgumentException(
                    "AddressCatalog: entry " + index + " is missing '" + field + "'.");
            }

            string text = value.AsString(null);
            if (text == null || text.Length == 0)
            {
                throw new ArgumentException(
                    "AddressCatalog: entry " + index + " has an empty '" + field + "'.");
            }

            return text;
        }

        private static string OptionalString(JsonValue row, string field, string fallback)
        {
            JsonValue value = row[field];
            if (value == null || value.Kind == JsonKind.Null)
            {
                return fallback;
            }

            string text = value.AsString(null);
            return text == null ? fallback : text;
        }

        private static bool OptionalBool(JsonValue row, string field, bool fallback)
        {
            JsonValue value = row[field];
            if (value == null || value.Kind == JsonKind.Null)
            {
                return fallback;
            }

            return value.AsBool(fallback);
        }

        /// <summary>
        /// 约定校验：地址与分组是"路径式记号"，一旦带上空白或首尾/连续斜杠，
        /// 拼接与匹配就会出现两套等价写法 —— 那时候表里就有两个"看起来不同其实同一个"
        /// 的地址，查起来必错。构建期拒绝，好过运行期猜。
        /// </summary>
        private static void ValidateToken(string text, string field, int index, bool singleSegment)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    throw new ArgumentException(
                        "AddressCatalog: entry " + index + " '" + field + "' must not contain "
                        + "whitespace ('" + text + "').");
                }
            }

            if (text[0] == '/' || text[text.Length - 1] == '/')
            {
                throw new ArgumentException(
                    "AddressCatalog: entry " + index + " '" + field
                    + "' must not start or end with '/' ('" + text + "').");
            }

            if (text.IndexOf("//", StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException(
                    "AddressCatalog: entry " + index + " '" + field
                    + "' must not contain '//' ('" + text + "').");
            }

            if (singleSegment && text.IndexOf('/') >= 0)
            {
                throw new ArgumentException(
                    "AddressCatalog: entry " + index + " 'group' must be a single segment "
                    + "(no '/') ('" + text + "').");
            }
        }

        // ------------------------------------------------------------ 合并

        /// <summary>
        /// 补丁式合并：同名地址被 <paramref name="overlay"/> 覆盖（保留原槽位，遍历顺序稳定），
        /// 新地址追加。被覆盖项的 Source 变成 overlay 的名字，于是"这条被哪张表改过"可追溯。
        ///
        /// 注意这是**覆盖**而不是"变体取并集"：补丁想改一条地址，就该把它的变体写全 ——
        /// 取并集会让"删掉一个变体"这件事表达不出来（旧变体会从底表漏回来）。
        /// </summary>
        public void Merge(AddressCatalog overlay)
        {
            if (overlay == null)
            {
                throw new ArgumentNullException("overlay");
            }

            for (int i = 0; i < overlay._order.Count; i++)
            {
                string address = overlay._order[i];
                CatalogEntry entry = overlay._entries[address];
                if (_entries.ContainsKey(address))
                {
                    _entries[address] = entry;
                }
                else
                {
                    _entries.Add(address, entry);
                    _order.Add(address);
                }
            }
        }

        // ------------------------------------------------------------ 查询

        /// <summary>Whether an entry with this address exists.</summary>
        public bool Has(string address)
        {
            return address != null && _entries.ContainsKey(address);
        }

        /// <summary>The entry, or null. Missing addresses are business, not errors.</summary>
        public CatalogEntry Get(string address)
        {
            CatalogEntry entry;
            return address != null && _entries.TryGetValue(address, out entry) ? entry : null;
        }

        /// <summary>The entry, failing loudly when absent (for callers that require it).</summary>
        public CatalogEntry Require(string address)
        {
            CatalogEntry entry = Get(address);
            if (entry == null)
            {
                throw new ArgumentException(
                    "AddressCatalog: no entry for address '" + address + "'.");
            }

            return entry;
        }

        /// <summary>
        /// Appends entries of a group into the caller's list, in file order.
        /// **Does not clear** (callers may accumulate several groups); no allocation
        /// of its own -- the scan path must stay clean.
        /// </summary>
        public void FindByGroup(string group, List<CatalogEntry> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            if (group == null)
            {
                return;
            }

            for (int i = 0; i < _order.Count; i++)
            {
                CatalogEntry entry = _entries[_order[i]];
                if (string.Equals(entry.Group, group, StringComparison.Ordinal))
                {
                    into.Add(entry);
                }
            }
        }

        /// <summary>Appends entries of a kind, in file order. Does not clear.</summary>
        public void FindByKind(CatalogKind kind, List<CatalogEntry> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            for (int i = 0; i < _order.Count; i++)
            {
                CatalogEntry entry = _entries[_order[i]];
                if (entry.Kind == kind)
                {
                    into.Add(entry);
                }
            }
        }

        /// <summary>Appends entries flagged required, in file order. Does not clear.</summary>
        public void FindRequired(List<CatalogEntry> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            for (int i = 0; i < _order.Count; i++)
            {
                CatalogEntry entry = _entries[_order[i]];
                if (entry.Required)
                {
                    into.Add(entry);
                }
            }
        }

        /// <summary>Distinct group names, Ordinal-sorted (deterministic for tooling).</summary>
        public List<string> Groups()
        {
            List<string> groups = new List<string>();
            for (int i = 0; i < _order.Count; i++)
            {
                string group = _entries[_order[i]].Group;
                if (!groups.Contains(group))
                {
                    groups.Add(group);
                }
            }

            groups.Sort(StringComparer.Ordinal);
            return groups;
        }

        // ------------------------------------------------------------ 解析地址

        /// <summary>
        /// 按生效标签解析地址。未命中时返回 <c>Resolved == false</c>（值，不是异常）。
        /// <paramref name="active"/> 为 null 等价于空标签集：只有默认变体能命中。
        /// </summary>
        public CatalogResolution Resolve(string address, CatalogTagSet active)
        {
            CatalogResolution result = new CatalogResolution();
            result.Address = address;

            CatalogEntry entry = Get(address);
            if (entry == null)
            {
                return result;
            }

            CatalogVariant picked;
            if (!entry.TryPickVariant(active, out picked))
            {
                return result;
            }

            result.Resolved = true;
            result.Locator = picked.Locator;
            result.IsDefault = picked.IsDefault;
            result.MatchedTags = picked.TagCount;
            return result;
        }

        /// <summary>Convenience form of <see cref="Resolve"/> for callers that just want the locator.</summary>
        public bool TryResolveLocator(string address, CatalogTagSet active, out string locator)
        {
            CatalogResolution result = Resolve(address, active);
            locator = result.Locator;
            return result.Resolved;
        }

        /// <summary>Resolves a list of addresses into the caller's locator list (misses are skipped).</summary>
        public void ResolveAll(IList<string> addresses, CatalogTagSet active, List<string> into)
        {
            if (addresses == null)
            {
                throw new ArgumentNullException("addresses");
            }

            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            for (int i = 0; i < addresses.Count; i++)
            {
                string locator;
                if (TryResolveLocator(addresses[i], active, out locator))
                {
                    into.Add(locator);
                }
            }
        }
    }
}
