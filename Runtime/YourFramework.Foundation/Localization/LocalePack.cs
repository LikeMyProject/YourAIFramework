using System;
using System.Collections.Generic;

namespace YourFramework.Localization
{
    /// <summary>
    /// 自研本地化包：把语言标识、多集合表、复数规则、内联格式化与本地化资源地址
    /// 收在一个门面后面，并实现 <see cref="ILocalizationSource"/> —— 因此它可以直接
    /// 插进 LocalizationStore 的源插槽，也可以被别处直接使用。
    ///
    /// 设计取舍：
    /// 1. **职责分层**：LocalizationStore 管"语言链 + 兜底 + 源插槽"（谁说得清
    ///    当前语言、缺 key 怎么办）；本包管"内容怎么组织"（哪些表、哪些形态、资源
    ///    地址怎么算）。两者不互相复制能力；
    /// 2. **语言链每条都先按规范截断再入链**：配置写 "zh-Hans-CN" 会自动展开成
    ///    zh-Hans-CN → zh-Hans → zh，配置写 "zh" 就只有 zh。链按
    ///    CurrentLanguage → Fallbacks → DefaultLanguage 顺序拼接并去重；
    /// 3. **复数用文本实际命中的那个语言来判定**，不是当前语言 —— 文案回退到 en 时，
    ///    就该按英语规则选形态，否则中文界面回退出来的英文会用错复数；
    /// 4. 链与格式化器都按语言标签缓存，查表的重复调用不再反复解析标签；
    /// 5. 集合查找顺序 = 注册顺序，先命中先用。跨集合的相同 key **不做**唯一性校验：
    ///    覆盖是常见需求（"游戏内 UI" 盖掉"通用 UI"），顺序即优先级，写在文档里；
    /// 6. 格式化前必须有可用语言（CurrentLanguage 或命中的表语言）。都没设置就调
    ///    格式化属于接线 bug，fail-fast 抛，不做"悄悄用 en"的静默兜底。
    /// </summary>
    public sealed class LocalePack : ILocalizationSource
    {
        private readonly List<StringTableCollection> _collections = new List<StringTableCollection>();
        private readonly Dictionary<string, StringTableCollection> _byName =
            new Dictionary<string, StringTableCollection>(StringComparer.Ordinal);
        private readonly Dictionary<string, LocaleFormatter> _formatters =
            new Dictionary<string, LocaleFormatter>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _chains =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly List<string> _fallbacks = new List<string>();
        private readonly AssetTable _assets = new AssetTable();
        private string _currentLanguage;
        private string _defaultLanguage;
        private Action<string> _languageChanged;

        /// <summary>本地化资源地址表（key → 按语言算出的资源地址）。</summary>
        public AssetTable Assets { get { return _assets; } }

        /// <inheritdoc/>
        public string Name { get { return "LocalePack"; } }

        /// <summary>
        /// Current language tag. Setting it to an unloaded language is not an
        /// error: lookups walk the chain (that is how a game boots before its
        /// device-locale tables arrive). Fires <see cref="OnLanguageChanged"/>
        /// once per real change.
        /// </summary>
        public string CurrentLanguage
        {
            get { return _currentLanguage; }
            set
            {
                if (string.Equals(value, _currentLanguage, StringComparison.Ordinal))
                {
                    return;
                }

                _currentLanguage = value;
                Action<string> handler = _languageChanged;
                if (handler != null)
                {
                    handler(value);
                }
            }
        }

        /// <summary>Last stop of the chain, after every fallback (development language).</summary>
        public string DefaultLanguage
        {
            get { return _defaultLanguage; }
            set
            {
                if (string.Equals(value, _defaultLanguage, StringComparison.Ordinal))
                {
                    return;
                }

                _defaultLanguage = value;
                _chains.Clear();
            }
        }

        /// <summary>Registered collections in lookup-priority order.</summary>
        public List<string> CollectionNames { get { return new List<string>(_byName.Keys); } }

        /// <summary>Configured fallback tags, in order (each is expanded by truncation at lookup).</summary>
        public List<string> Fallbacks { get { return new List<string>(_fallbacks); } }

        /// <summary>Raised once when <see cref="CurrentLanguage"/> actually changes.</summary>
        public event Action<string> OnLanguageChanged
        {
            add { _languageChanged += value; }
            remove { _languageChanged -= value; }
        }

        /// <summary>Registers a collection; duplicate names are a wiring bug and throw.</summary>
        public StringTableCollection AddCollection(StringTableCollection collection)
        {
            if (collection == null)
            {
                throw new ArgumentNullException("collection");
            }

            if (_byName.ContainsKey(collection.Name))
            {
                throw new InvalidOperationException(
                    "LocalePack.AddCollection: a collection named '" + collection.Name + "' is already registered.");
            }

            _collections.Add(collection);
            _byName.Add(collection.Name, collection);
            return collection;
        }

        /// <summary>Collection by name, or null.</summary>
        public StringTableCollection Collection(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            StringTableCollection collection;
            return _byName.TryGetValue(name, out collection) ? collection : null;
        }

        /// <summary>
        /// Loads (or wholesale-replaces) one locale's table inside a collection,
        /// creating the collection on first use. The locale tag is normalized, so
        /// "zh_cn" and "zh-CN" land in the same table.
        /// </summary>
        public StringTable LoadTable(string locale, string collectionName, string json)
        {
            StringTable table = new StringTable(Normalize(locale), collectionName);
            table.LoadJson(json);

            StringTableCollection collection;
            if (!_byName.TryGetValue(collectionName, out collection))
            {
                collection = AddCollection(new StringTableCollection(collectionName));
            }

            collection.AddTable(table);
            return table;
        }

        /// <summary>Loads (or replaces) a shared table for one locale.</summary>
        public StringTable LoadSharedTable(string locale, string collectionName, string name, string json)
        {
            StringTable table = new StringTable(Normalize(locale), name);
            table.LoadJson(json);

            StringTableCollection collection;
            if (!_byName.TryGetValue(collectionName, out collection))
            {
                collection = AddCollection(new StringTableCollection(collectionName));
            }

            collection.AddSharedTable(table);
            return table;
        }

        /// <summary>Appends a fallback tag (expanded by truncation when looked up).</summary>
        public void AddFallback(string locale)
        {
            if (string.IsNullOrEmpty(locale))
            {
                throw new ArgumentException("LocalePack.AddFallback: locale must not be empty.", "locale");
            }

            _fallbacks.Add(locale);
            _chains.Clear();
        }

        /// <summary>Replaces the whole fallback list.</summary>
        public void SetFallbacks(params string[] locales)
        {
            _fallbacks.Clear();
            if (locales != null)
            {
                for (int i = 0; i < locales.Length; i++)
                {
                    if (string.IsNullOrEmpty(locales[i]))
                    {
                        throw new ArgumentException(
                            "LocalePack.SetFallbacks: a fallback tag must not be empty.", "locales");
                    }

                    _fallbacks.Add(locales[i]);
                }
            }

            _chains.Clear();
        }

        /// <summary>
        /// Builds the lookup chain for a language into the caller's list: the
        /// language's own truncation (zh-Hans-CN → zh-Hans → zh), then every
        /// fallback expanded the same way, then the default language. Duplicates
        /// are dropped, so the same tag never costs two lookups.
        /// </summary>
        public void BuildChain(string language, List<string> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            into.Clear();
            AppendChain(language, into);
            for (int i = 0; i < _fallbacks.Count; i++)
            {
                AppendChain(_fallbacks[i], into);
            }

            AppendChain(_defaultLanguage, into);
        }

        /// <summary>Snapshot of the chain that would be used for a language.</summary>
        public List<string> ChainFor(string language)
        {
            string key = language ?? string.Empty;
            List<string> chain;
            if (!_chains.TryGetValue(key, out chain))
            {
                chain = new List<string>(8);
                BuildChain(language, chain);
                _chains.Add(key, chain);
            }

            return chain;
        }

        /// <summary>
        /// Source-slot entry point: resolves under the given language (falling
        /// back through the pack's own chain). Never throws on a miss.
        /// </summary>
        public bool TryGet(string language, string key, out string text)
        {
            string locale;
            return Resolve(key, language, out text, out locale);
        }

        /// <summary>Resolves under <see cref="CurrentLanguage"/>.</summary>
        public bool TryGet(string key, out string text)
        {
            return TryGet(_currentLanguage, key, out text);
        }

        /// <summary>Resolves under <see cref="CurrentLanguage"/>; a miss returns the key itself.</summary>
        public string Get(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            string text;
            return TryGet(_currentLanguage, key, out text) ? text : key;
        }

        /// <summary>Resolves inside one named collection; a miss returns the key itself.</summary>
        public string Get(string collectionName, string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            string text;
            string locale;
            return ResolveIn(collectionName, key, _currentLanguage, out text, out locale) ? text : key;
        }

        /// <summary>Address of a localized asset under <see cref="CurrentLanguage"/>'s chain.</summary>
        public bool TryGetAddress(string key, out string address)
        {
            return _assets.TryGetAddress(key, ChainFor(_currentLanguage), out address);
        }

        /// <summary>
        /// Formatter bound to a language, cached per tag. Plural selection depends
        /// on the language, so callers must pass the language the text came from.
        /// </summary>
        public LocaleFormatter FormatterFor(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                throw new ArgumentException(
                    "LocalePack.FormatterFor: a language is required (set CurrentLanguage or DefaultLanguage "
                    + "before formatting).", "language");
            }

            LocaleFormatter formatter;
            if (!_formatters.TryGetValue(language, out formatter))
            {
                formatter = new LocaleFormatter(language);
                _formatters.Add(language, formatter);
            }

            return formatter;
        }

        /// <summary>
        /// Resolves a key and expands its placeholders. The template is taken from
        /// the language that actually answered, so <c>{count:plural:...}</c>
        /// follows that language's rules. A missing key returns the key itself
        /// with <paramref name="unresolved"/> left at 0 (nothing was expanded).
        /// </summary>
        public string Format(string key, ILocaleArguments args, out int unresolved)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            string text;
            string locale;
            if (!Resolve(key, _currentLanguage, out text, out locale))
            {
                unresolved = 0;
                return key;
            }

            return FormatterFor(locale).Format(text, args, out unresolved);
        }

        /// <summary>Formats inside one named collection.</summary>
        public string Format(string collectionName, string key, ILocaleArguments args, out int unresolved)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            string text;
            string locale;
            if (!ResolveIn(collectionName, key, _currentLanguage, out text, out locale))
            {
                unresolved = 0;
                return key;
            }

            return FormatterFor(locale).Format(text, args, out unresolved);
        }

        private bool Resolve(string key, string language, out string text, out string locale)
        {
            text = null;
            locale = null;
            if (key == null)
            {
                return false;
            }

            List<string> chain = ChainFor(language);
            for (int i = 0; i < _collections.Count; i++)
            {
                if (_collections[i].TryGet(key, chain, out text, out locale))
                {
                    return true;
                }
            }

            text = null;
            locale = null;
            return false;
        }

        private bool ResolveIn(string collectionName, string key, string language, out string text, out string locale)
        {
            text = null;
            locale = null;
            if (key == null || string.IsNullOrEmpty(collectionName))
            {
                return false;
            }

            StringTableCollection collection;
            if (!_byName.TryGetValue(collectionName, out collection))
            {
                return false;
            }

            return collection.TryGet(key, ChainFor(language), out text, out locale);
        }

        private static void AppendChain(string language, List<string> into)
        {
            if (string.IsNullOrEmpty(language))
            {
                return;
            }

            LocaleId parsed;
            if (LocaleId.TryParse(language, out parsed))
            {
                parsed.AutoChain(into);
                return;
            }

            // A tag we cannot parse is kept verbatim: a table keyed by exactly that
            // string still has a chance to answer, which beats silently dropping it.
            for (int i = 0; i < into.Count; i++)
            {
                if (string.Equals(into[i], language, StringComparison.Ordinal))
                {
                    return;
                }
            }

            into.Add(language);
        }

        private static string Normalize(string locale)
        {
            if (string.IsNullOrEmpty(locale))
            {
                throw new ArgumentException("LocalePack: locale must not be empty.", "locale");
            }

            LocaleId parsed;
            return LocaleId.TryParse(locale, out parsed) ? parsed.Tag : locale;
        }
    }
}
