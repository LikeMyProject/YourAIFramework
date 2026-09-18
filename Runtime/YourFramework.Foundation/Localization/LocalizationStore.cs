using System;
using System.Collections.Generic;
using System.Globalization;
using YourAI.Core.Json;

namespace YourFramework.Localization
{
    /// <summary>
    /// 本地化存储：语言表（JSON 平铺 key→value）→ 当前语言 → 回退链 → 主键兜底。
    ///
    /// 设计取舍：
    /// 1. 纯 C#、零引擎依赖 —— 整条链路离线可测（loc/* 用例组），语言表加载、
    ///    回退、切换、格式化不依赖编辑器资产；
    /// 2. 缺 key 是业务不是 bug —— 全链路未命中返回主键本身（调用方总有话可显），
    ///    配套 Has(key) 显式检查；坏 JSON / 空 key 是构建期产物错误，fail-fast；
    /// 3. 同语言再 Load = 整表替换（与 DataTableSet 同一热更语义）；
    /// 4. 格式化用 {0}/{1} 位置参数，CultureInfo.InvariantCulture —— 数字渲染
    ///    不随设备区域漂移。
    ///
    /// 源插槽（AddSource）留给任何外部数据源 —— 自研本地化包、远端热更表都从
    /// 这里接入；一个源都不挂，本类照常独立工作。
    /// </summary>
    public sealed class LocalizationStore
    {
        private readonly Dictionary<string, Dictionary<string, string>> _tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private readonly List<string> _fallbackLanguages = new List<string>();
        private string _currentLanguage;
        private string _defaultLanguage;
        private Action<string> _languageChanged;
        private readonly List<ILocalizationSource> _sources = new List<ILocalizationSource>();

        /// <summary>
        /// Loads (or wholesale-replaces) a language table. JSON format: a flat
        /// object of key → text, e.g. {"ui.title":"山河问剑录"}. Bad JSON and
        /// non-string values fail fast (build artifact).
        /// </summary>
        public void LoadJson(string language, string json)
        {
            if (string.IsNullOrEmpty(language))
            {
                throw new ArgumentException("LocalizationStore.LoadJson: language must not be empty.", "language");
            }

            JsonValue parsed;
            try
            {
                parsed = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                // Uniform fail-fast surface, same judgment as DataTable.FromJson.
                throw new ArgumentException(
                    "LocalizationStore: invalid JSON for language '" + language + "': " + ex.Message, ex);
            }

            if (parsed == null || parsed.Kind != JsonKind.Object)
            {
                throw new ArgumentException(
                    "LocalizationStore: root must be a JSON object of key → text.");
            }

            Dictionary<string, string> table = new Dictionary<string, string>(parsed.Members.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonValue> member in parsed.Members)
            {
                if (string.IsNullOrEmpty(member.Key))
                {
                    throw new ArgumentException(
                        "LocalizationStore: language '" + language + "' has an empty key entry.");
                }

                if (member.Value.Kind != JsonKind.String)
                {
                    throw new ArgumentException(
                        "LocalizationStore: key '" + member.Key + "' in '" + language
                        + "' is not a string (values are text, encode numbers into the text).");
                }

                table.Add(member.Key, member.Value.StringValue);
            }

            _tables[language] = table;
        }

        /// <summary>Languages with a loaded table, table order.</summary>
        public List<string> LoadedLanguages
        {
            get { return new List<string>(_tables.Keys); }
        }

        /// <summary>
        /// Fallback chain tried in order after the current language. Set freely,
        /// e.g. ["zh", "en"] so zh-CN users fall to zh then en. Matching is
        /// exact and ordinal ("zh-CN" never implicitly matches "zh" -- the
        /// chain makes every hop explicit).
        /// </summary>
        public List<string> FallbackLanguages { get { return _fallbackLanguages; } }

        /// <summary>
        /// Current language. Exact ordinal match; switching fires
        /// <see cref="OnLanguageChanged"/> once. Setting an unloaded language is
        /// not an error: lookups walk the chain (that is how you boot a game
        /// before device-locale tables arrive).
        /// </summary>
        public string CurrentLanguage
        {
            get { return _currentLanguage; }
            set
            {
                if (value == _currentLanguage)
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

        /// <summary>
        /// Last-resort language of the chain (tried after Current and the
        /// fallbacks). Typically the development language.
        /// </summary>
        public string DefaultLanguage
        {
            get { return _defaultLanguage; }
            set { _defaultLanguage = value; }
        }

        /// <summary>
        /// Registers an external source (e.g. the framework's own locale pack),
        /// tried after the whole table chain misses and before falling back to
        /// the key itself. Sources are optional plumbing; the store works
        /// standalone without any.
        /// </summary>
        public void AddSource(ILocalizationSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            _sources.Add(source);
        }

        /// <summary>Raised once when <see cref="CurrentLanguage"/> actually changes.</summary>
        public event Action<string> OnLanguageChanged
        {
            add { _languageChanged += value; }
            remove { _languageChanged -= value; }
        }

        /// <summary>Whether every layer of the chain can resolve the key.</summary>
        public bool Has(string key)
        {
            return key != null && Resolve(key) != null;
        }

        /// <summary>
        /// Looks the key up: Current → fallbacks → default. Miss returns the key
        /// itself (missing content is business, not an exception; callers always
        /// have something to display and the raw key is its own diagnostic).
        /// </summary>
        public string Get(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            string text = Resolve(key);
            return text ?? key;
        }

        /// <summary>
        /// Lookup plus {0}/{1} positional formatting under the invariant culture
        /// (numbers render identically on every device). Missing key behaves like
        /// <see cref="Get(string)"/>.
        /// </summary>
        public string Get(string key, params object[] args)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            if (args == null)
            {
                throw new ArgumentNullException("args");
            }

            string text = Resolve(key);
            if (text == null)
            {
                return key;
            }

            return string.Format(CultureInfo.InvariantCulture, text, args);
        }

        private string Resolve(string key)
        {
            string text = Lookup(_currentLanguage, key);
            if (text != null)
            {
                return text;
            }

            for (int i = 0; i < _fallbackLanguages.Count; i++)
            {
                text = Lookup(_fallbackLanguages[i], key);
                if (text != null)
                {
                    return text;
                }
            }

            string defaultText = Lookup(_defaultLanguage, key);
            if (defaultText != null)
            {
                return defaultText;
            }

            for (int i = 0; i < _sources.Count; i++)
            {
                string external;
                if (_sources[i].TryGet(_currentLanguage ?? _defaultLanguage, key, out external)
                    && external != null)
                {
                    return external;
                }
            }

            return null;
        }

        private string Lookup(string language, string key)
        {
            if (string.IsNullOrEmpty(language) || key == null)
            {
                return null;
            }

            Dictionary<string, string> table;
            if (!_tables.TryGetValue(language, out table))
            {
                return null;
            }

            string text;
            return table.TryGetValue(key, out text) ? text : null;
        }
    }
}
