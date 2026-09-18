using System;
using System.Collections.Generic;
using YourAI.Core.Json;

namespace YourFramework.Localization
{
    /// <summary>
    /// 本地化资源表：把一个逻辑 key 映射到**当前语言对应的资源地址**。有了它，
    /// "logo" 这样的 key 能在 zh 下指向 `Assets/UI/zh/logo.png`、在 en 下指向
    /// `Assets/UI/en/logo.png`，而调用方只写 key —— 地址解析完再交给资源系统
    /// （AssetManager / 自研资源包）去加载，本地化与资源加载因此完全解耦。
    ///
    /// 设计取舍：
    /// 1. 地址里支持两个占位：<c>{locale}</c>（完整标签，如 zh-Hans-CN）与
    ///    <c>{language}</c>（基础语言，如 zh）。按语言分目录是最常见的组织方式，
    ///    用占位符写一次比给每个语言抄一行地址更不容易漏；
    /// 2. 解析顺序是**语言链精确匹配优先，兜底地址最后**：链上哪个标签命中就用它
    ///    展开 {locale}，不会出现"命中 zh 的地址却渲染了 zh-CN 的路径"；
    /// 3. 兜底地址（SetDefault / JSON 里直接写字符串）用于与语言无关的资源 ——
    ///    换语言时它不该跟着变，例如纯图标；
    /// 4. 缺地址是业务（返回 false），空 key / 空地址 / 重复 key 是构建期错误
    ///    （fail-fast），判断标准与 StringTable 一致。
    /// </summary>
    public sealed class AssetTable
    {
        private sealed class Entry
        {
            public readonly Dictionary<string, string> ByLocale =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public string Any;
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Key count.</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>Keys in registration order, for diagnostics and tooling.</summary>
        public List<string> Keys { get { return new List<string>(_entries.Keys); } }

        /// <summary>Whether the key is known (regardless of locale).</summary>
        public bool Contains(string key)
        {
            return key != null && _entries.ContainsKey(key);
        }

        /// <summary>Sets a locale-independent address for the key (the last-resort layer).</summary>
        public void SetDefault(string key, string address)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("AssetTable.SetDefault: key must not be empty.", "key");
            }

            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("AssetTable.SetDefault: address must not be empty.", "address");
            }

            Entry entry = GetOrCreate(key);
            entry.Any = address;
        }

        /// <summary>Sets the address for one locale tag (exact match at lookup time).</summary>
        public void Set(string key, string locale, string address)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("AssetTable.Set: key must not be empty.", "key");
            }

            if (string.IsNullOrEmpty(locale))
            {
                throw new ArgumentException("AssetTable.Set: locale must not be empty.", "locale");
            }

            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("AssetTable.Set: address must not be empty.", "address");
            }

            GetOrCreate(key).ByLocale[locale] = address;
        }

        /// <summary>
        /// Loads entries from JSON. Each member is either a string (a
        /// locale-independent address) or an object of locale tag → address:
        ///
        ///     {"logo":"Assets/UI/logo.png",
        ///      "title":{"zh":"Assets/UI/{language}/title.png","en":"Assets/UI/en/title.png"}}
        ///
        /// Wholesale replace: keys absent from the new JSON are gone.
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
                throw new ArgumentException("AssetTable: invalid JSON: " + ex.Message, ex);
            }

            if (parsed == null || parsed.Kind != JsonKind.Object)
            {
                throw new ArgumentException(
                    "AssetTable: root must be a JSON object of key → address or key → {locale: address}.");
            }

            Dictionary<string, Entry> next = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonValue> member in parsed.Members)
            {
                if (string.IsNullOrEmpty(member.Key))
                {
                    throw new ArgumentException("AssetTable: an entry has an empty key.");
                }

                if (next.ContainsKey(member.Key))
                {
                    throw new ArgumentException("AssetTable: key '" + member.Key + "' appears twice.");
                }

                Entry entry = new Entry();
                if (member.Value.Kind == JsonKind.String)
                {
                    entry.Any = RequireAddress(member.Key, null, member.Value.StringValue);
                }
                else if (member.Value.Kind == JsonKind.Object)
                {
                    foreach (KeyValuePair<string, JsonValue> localized in member.Value.Members)
                    {
                        if (string.IsNullOrEmpty(localized.Key))
                        {
                            throw new ArgumentException(
                                "AssetTable: key '" + member.Key + "' has an empty locale entry.");
                        }

                        if (localized.Value.Kind != JsonKind.String)
                        {
                            throw new ArgumentException(
                                "AssetTable: key '" + member.Key + "' locale '" + localized.Key
                                + "' must map to a string address.");
                        }

                        if (entry.ByLocale.ContainsKey(localized.Key))
                        {
                            throw new ArgumentException(
                                "AssetTable: key '" + member.Key + "' declares locale '"
                                + localized.Key + "' twice.");
                        }

                        entry.ByLocale.Add(
                            localized.Key,
                            RequireAddress(member.Key, localized.Key, localized.Value.StringValue));
                    }
                }
                else
                {
                    throw new ArgumentException(
                        "AssetTable: key '" + member.Key + "' must be a string or a locale → string object.");
                }

                next.Add(member.Key, entry);
            }

            // Swap after a fully successful parse: a rejected load leaves no residue.
            _entries.Clear();
            foreach (KeyValuePair<string, Entry> pair in next)
            {
                _entries.Add(pair.Key, pair.Value);
            }
        }

        /// <summary>
        /// Resolves the address for a key by walking the caller's locale chain:
        /// exact locale match first (that tag expands the placeholders), then the
        /// locale-independent address. False means "no address known".
        /// </summary>
        public bool TryGetAddress(string key, IList<string> localeChain, out string address)
        {
            address = null;
            if (key == null)
            {
                return false;
            }

            Entry entry;
            if (!_entries.TryGetValue(key, out entry))
            {
                return false;
            }

            if (localeChain != null)
            {
                for (int i = 0; i < localeChain.Count; i++)
                {
                    string locale = localeChain[i];
                    if (string.IsNullOrEmpty(locale))
                    {
                        continue;
                    }

                    string localized;
                    if (entry.ByLocale.TryGetValue(locale, out localized))
                    {
                        address = Expand(localized, locale);
                        return true;
                    }
                }
            }

            if (entry.Any != null)
            {
                address = Expand(entry.Any, localeChain != null && localeChain.Count > 0 ? localeChain[0] : "");
                return true;
            }

            return false;
        }

        private Entry GetOrCreate(string key)
        {
            Entry entry;
            if (!_entries.TryGetValue(key, out entry))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            return entry;
        }

        private static string RequireAddress(string key, string locale, string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException(
                    "AssetTable: key '" + key + "'"
                    + (locale == null ? "" : " locale '" + locale + "'")
                    + " has an empty address.");
            }

            return address;
        }

        private static string Expand(string address, string locale)
        {
            if (address.IndexOf('{') < 0)
            {
                return address;
            }

            string language = BaseLanguage(locale);
            return address.Replace("{locale}", locale).Replace("{language}", language);
        }

        private static string BaseLanguage(string locale)
        {
            if (string.IsNullOrEmpty(locale))
            {
                return "";
            }

            int cut = locale.Length;
            for (int i = 0; i < locale.Length; i++)
            {
                char c = locale[i];
                if (c == '-' || c == '_')
                {
                    cut = i;
                    break;
                }
            }

            return locale.Substring(0, cut);
        }
    }
}
