using System;
using System.Collections.Generic;
using YourAI.Core.Json;

namespace YourFramework.Localization
{
    /// <summary>
    /// 一张语言表：某个语言下的一组 key → 文本。
    ///
    /// 设计取舍：
    /// 1. 内容只从 JSON 进（LoadJson），且**同语言再 Load = 整表替换** —— 与
    ///    LocalizationStore / DataTableSet 的热更语义完全一致：新表里没有的 key
    ///    就是真的没有，不留旧值残影；
    /// 2. 坏 JSON、非字符串值、空 key 全是构建期产物错误，当场 fail-fast（与
    ///    LocalizationStore 同一判断），不做"跳过这一条继续"的宽容解析；
    /// 3. 表本身不管语言回退 —— 那是 collection 与 pack 的事，表只回答
    ///    "我这个语言里有没有这个 key"。
    /// </summary>
    public sealed class StringTable
    {
        private readonly string _locale;
        private readonly string _name;
        private Dictionary<string, string> _entries;

        /// <summary>Creates an empty table. Locale is the normalized tag the table serves.</summary>
        public StringTable(string locale, string name = "")
        {
            if (string.IsNullOrEmpty(locale))
            {
                throw new ArgumentException("StringTable: locale must not be empty.", "locale");
            }

            _locale = locale;
            _name = name ?? string.Empty;
            _entries = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>Normalized locale tag this table serves.</summary>
        public string Locale { get { return _locale; } }

        /// <summary>Diagnostic name (shared tables use it to tell siblings apart).</summary>
        public string Name { get { return _name; } }

        /// <summary>Entry count.</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>Keys in table order; a snapshot, for diagnostics and tooling.</summary>
        public List<string> Keys { get { return new List<string>(_entries.Keys); } }

        /// <summary>
        /// Replaces the whole table from a flat JSON object of key → text
        /// ({"ui.title":"山河问剑录"}). Fail-fast on bad JSON, non-object roots,
        /// non-string values and empty keys: all three are build artifacts.
        /// </summary>
        public void LoadJson(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }

            JsonValue parsed;
            try
            {
                parsed = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    "StringTable: invalid JSON for locale '" + _locale + "': " + ex.Message, ex);
            }

            if (parsed == null || parsed.Kind != JsonKind.Object)
            {
                throw new ArgumentException(
                    "StringTable: root must be a JSON object of key → text (locale '" + _locale + "').");
            }

            Dictionary<string, string> next =
                new Dictionary<string, string>(parsed.Members.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonValue> member in parsed.Members)
            {
                if (string.IsNullOrEmpty(member.Key))
                {
                    throw new ArgumentException(
                        "StringTable: locale '" + _locale + "' has an empty key entry.");
                }

                if (member.Value.Kind != JsonKind.String)
                {
                    throw new ArgumentException(
                        "StringTable: key '" + member.Key + "' in '" + _locale
                        + "' is not a string (values are text; encode numbers into the text).");
                }

                if (next.ContainsKey(member.Key))
                {
                    throw new ArgumentException(
                        "StringTable: key '" + member.Key + "' appears twice in '" + _locale + "'.");
                }

                next.Add(member.Key, member.Value.StringValue);
            }

            // Swap only after the whole table parsed: a rejected load must not
            // leave a half-replaced table behind.
            _entries = next;
        }

        /// <summary>Looks the key up in this table only.</summary>
        public bool TryGet(string key, out string text)
        {
            if (key == null)
            {
                text = null;
                return false;
            }

            return _entries.TryGetValue(key, out text);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return _name.Length == 0
                ? _locale + "(" + _entries.Count + ")"
                : _locale + "/" + _name + "(" + _entries.Count + ")";
        }
    }

    /// <summary>
    /// 一组**同一主题、跨语言**的表（Unity Localization 的 StringTableCollection）。
    /// 例如集合 "UI" 持有 zh 表与 en 表；查一个 key 时按调用方给的语言链逐语言下落。
    ///
    /// 设计取舍：
    /// 1. 一个语言只有一张"自己的"表 —— 再 AddTable 同一个 locale 就是**整表替换**
    ///    （热更语义），不会出现同一语言两张表互相遮蔽的糊涂账；
    /// 2. 共享表（AddSharedTable）是**集合内**的第二层：自己的表没命中才查共享表。
    ///    要让两个集合共用同一份内容，把同一个 StringTable 实例分别 Add 进去即可，
    ///    不是复制数据 —— 共享的是对象，不是副本；
    /// 3. 集合内的 key 不做唯一性校验，因为"自己的表 vs 共享表"本来就是有意的覆写
    ///    关系（自己的表优先）；跨集合的优先级由 pack 的注册顺序显式决定；
    /// 4. 本类自给自足：只依赖语言链参数，不依赖 pack，单独可测。
    /// </summary>
    public sealed class StringTableCollection
    {
        private readonly string _name;
        private readonly Dictionary<string, StringTable> _own =
            new Dictionary<string, StringTable>(StringComparer.Ordinal);
        private readonly List<string> _ownOrder = new List<string>();
        private readonly List<StringTable> _shared = new List<StringTable>();

        /// <summary>Creates an empty collection with a diagnostic name.</summary>
        public StringTableCollection(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("StringTableCollection: name must not be empty.", "name");
            }

            _name = name;
        }

        /// <summary>Collection name (diagnostics, and pack lookups by name).</summary>
        public string Name { get { return _name; } }

        /// <summary>Locales that own a table here, in registration order.</summary>
        public List<string> Locales { get { return new List<string>(_ownOrder); } }

        /// <summary>Shared table count (the second lookup layer).</summary>
        public int SharedCount { get { return _shared.Count; } }

        /// <summary>
        /// Adds (or wholesale-replaces) the collection's own table for
        /// <see cref="StringTable.Locale"/>.
        /// </summary>
        public void AddTable(StringTable table)
        {
            if (table == null)
            {
                throw new ArgumentNullException("table");
            }

            if (!_own.ContainsKey(table.Locale))
            {
                _ownOrder.Add(table.Locale);
            }

            _own[table.Locale] = table;
        }

        /// <summary>
        /// Adds a shared table, tried after every own table of the whole chain
        /// misses. Sharing the same instance between collections is the intended
        /// way to reuse content.
        /// </summary>
        public void AddSharedTable(StringTable table)
        {
            if (table == null)
            {
                throw new ArgumentNullException("table");
            }

            _shared.Add(table);
        }

        /// <summary>Own table for a locale, or null.</summary>
        public StringTable TableFor(string locale)
        {
            if (string.IsNullOrEmpty(locale))
            {
                return null;
            }

            StringTable table;
            return _own.TryGetValue(locale, out table) ? table : null;
        }

        /// <summary>Whether this collection holds its own table for the locale.</summary>
        public bool HasLocale(string locale)
        {
            return TableFor(locale) != null;
        }

        /// <summary>
        /// Looks the key up: for each locale in the caller's chain, the own table
        /// first, then the shared tables (which are also locale-keyed). Returns
        /// false when the whole chain misses -- a miss is business, not an error.
        /// </summary>
        public bool TryGet(string key, IList<string> localeChain, out string text)
        {
            string ignored;
            return TryGet(key, localeChain, out text, out ignored);
        }

        /// <summary>
        /// Same lookup, also reporting **which locale answered**. Callers that
        /// format the text need this: plural rules follow the language the text
        /// actually came from, not the one that was asked for.
        /// </summary>
        public bool TryGet(string key, IList<string> localeChain, out string text, out string locale)
        {
            text = null;
            locale = null;
            if (key == null || localeChain == null)
            {
                return false;
            }

            for (int i = 0; i < localeChain.Count; i++)
            {
                string candidate = localeChain[i];
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                StringTable own;
                if (_own.TryGetValue(candidate, out own) && own.TryGet(key, out text))
                {
                    locale = candidate;
                    return true;
                }

                for (int s = 0; s < _shared.Count; s++)
                {
                    if (string.Equals(_shared[s].Locale, candidate, StringComparison.Ordinal)
                        && _shared[s].TryGet(key, out text))
                    {
                        locale = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return _name + "(own=" + _ownOrder.Count + ", shared=" + _shared.Count + ")";
        }
    }
}
