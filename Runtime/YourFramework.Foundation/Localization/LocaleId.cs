using System;
using System.Collections.Generic;

namespace YourFramework.Localization
{
    /// <summary>
    /// 语言标识（BCP-47 里游戏真正用得上的子集）：language[-Script][-REGION]，
    /// 例如 zh、zh-Hans、zh-Hans-CN、en-US、fil。
    ///
    /// 设计取舍：
    /// 1. 只做三件真用得上的事 —— 解析、按规范归一大小写、生成回退链。variant /
    ///    extension / privateuse 这些游戏里几乎用不到的部分**明确不解析**（遇到就拒），
    ///    宁可当场报错，也不要"看起来支持、实际走不到"；
    /// 2. 大小写归在解析时完成（zh-hans-cn → zh-Hans-CN）。设备上报的 "zh-cn"
    ///    与作者手写的 "zh-CN" 因此是同一个表，不会分裂成两份内容；
    /// 3. 回退链是**规范内的显式截断**，不是模糊匹配：zh-Hans-CN → zh-Hans → zh。
    ///    截断只从右往左丢（丢地区、再丢文字），不做 CLDR likely-subtags 的"补全"
    ///    （zh-TW ⇒ zh-Hant）——那需要内置一张区域映射表，而且会掩盖内容缺失。
    ///    真需要那层语义，请在 store / pack 的 FallbackLanguages 里显式写出来；
    /// 4. 坏标签分两种姿态：Parse 用于程序与配置 bug（fail-fast 抛出）；TryParse 用于
    ///    设备上报这类不可信输入（失败是值，返回 false，调用方自己兜底）。
    ///
    /// 分隔符同时接受 '-' 与 '_'：Android 的 Locale.toString() 给的是 "zh_CN"，
    /// 没必要逼调用方先自己做字符串替换。
    /// </summary>
    public sealed class LocaleId : IEquatable<LocaleId>
    {
        private readonly string _tag;
        private readonly string _language;
        private readonly string _script;
        private readonly string _region;

        private LocaleId(string tag, string language, string script, string region)
        {
            _tag = tag;
            _language = language;
            _script = script;
            _region = region;
        }

        /// <summary>Normalized tag, e.g. "zh-Hans-CN". Never empty.</summary>
        public string Tag { get { return _tag; } }

        /// <summary>ISO-639 language, lower case, 2-3 letters.</summary>
        public string Language { get { return _language; } }

        /// <summary>ISO-15924 script, Title case; null when the tag omits it.</summary>
        public string Script { get { return _script; } }

        /// <summary>ISO-3166 region (upper case) or UN M.49 area (3 digits); null when omitted.</summary>
        public string Region { get { return _region; } }

        /// <summary>Whether the tag pins a script (zh-Hans vs zh-Hant).</summary>
        public bool HasScript { get { return _script != null; } }

        /// <summary>Whether the tag pins a region.</summary>
        public bool HasRegion { get { return _region != null; } }

        /// <summary>Same as <see cref="Tag"/>; exists so the type prints readably in logs.</summary>
        public override string ToString()
        {
            return _tag;
        }

        /// <summary>
        /// Parses a tag, throwing on malformed input. Use this for author-written
        /// configuration: a bad tag there is a build/ wiring bug and must be loud.
        /// Separators '-' and '_' are both accepted.
        /// </summary>
        public static LocaleId Parse(string raw)
        {
            LocaleId parsed;
            if (!TryParse(raw, out parsed))
            {
                throw new ArgumentException(
                    "LocaleId.Parse: '" + (raw ?? "<null>") + "' is not a supported language tag. "
                    + "Expected language[-Script][-REGION] with 2-3 letter language "
                    + "(e.g. zh, zh-Hans, zh-Hans-CN, en-US); variants and extensions are not parsed.",
                    "raw");
            }

            return parsed;
        }

        /// <summary>Non-throwing parse for untrusted input (device locale strings).</summary>
        public static bool TryParse(string raw, out LocaleId locale)
        {
            locale = null;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            string normalized = raw.Replace('_', '-');
            string[] parts = normalized.Split('-');
            if (parts.Length < 1 || parts.Length > 3)
            {
                return false;
            }

            string language = NormalizeLanguage(parts[0]);
            if (language == null)
            {
                return false;
            }

            string script = null;
            string region = null;

            if (parts.Length == 2)
            {
                script = NormalizeScript(parts[1]);
                if (script == null)
                {
                    region = NormalizeRegion(parts[1]);
                    if (region == null)
                    {
                        return false;
                    }
                }
            }
            else if (parts.Length == 3)
            {
                script = NormalizeScript(parts[1]);
                if (script == null)
                {
                    return false;
                }

                region = NormalizeRegion(parts[2]);
                if (region == null)
                {
                    return false;
                }
            }

            string tag = language;
            if (script != null)
            {
                tag = tag + "-" + script;
            }

            if (region != null)
            {
                tag = tag + "-" + region;
            }

            locale = new LocaleId(tag, language, script, region);
            return true;
        }

        /// <summary>
        /// Appends the tag's fallback chain, most specific first, into the caller's
        /// list (no allocation of its own -- the lookup path must stay clean).
        /// zh-Hans-CN yields zh-Hans-CN, zh-Hans, zh; duplicates are skipped so a
        /// chain built from several sources stays free of repeated hops.
        /// </summary>
        public void AutoChain(List<string> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            AddUnique(into, _tag);

            if (_script != null && _region != null)
            {
                AddUnique(into, _language + "-" + _script);
            }

            AddUnique(into, _language);
        }

        /// <summary>Convenience form of <see cref="AutoChain"/> for one-off callers.</summary>
        public List<string> AutoChain()
        {
            List<string> chain = new List<string>(3);
            AutoChain(chain);
            return chain;
        }

        /// <summary>Ordinal tag equality; casing was already normalized at parse time.</summary>
        public bool Equals(LocaleId other)
        {
            return other != null && string.Equals(_tag, other._tag, StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return Equals(obj as LocaleId);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return _tag.GetHashCode();
        }

        private static void AddUnique(List<string> into, string tag)
        {
            for (int i = 0; i < into.Count; i++)
            {
                if (string.Equals(into[i], tag, StringComparison.Ordinal))
                {
                    return;
                }
            }

            into.Add(tag);
        }

        private static string NormalizeLanguage(string value)
        {
            if (!IsAlpha(value) || value.Length < 2 || value.Length > 3)
            {
                return null;
            }

            return value.ToLowerInvariant();
        }

        private static string NormalizeScript(string value)
        {
            if (!IsAlpha(value) || value.Length != 4)
            {
                return null;
            }

            string lower = value.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }

        private static string NormalizeRegion(string value)
        {
            if (IsAlpha(value) && value.Length == 2)
            {
                return value.ToUpperInvariant();
            }

            if (IsDigits(value) && value.Length == 3)
            {
                return value;
            }

            return null;
        }

        private static bool IsAlpha(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (!letter)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsDigits(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
