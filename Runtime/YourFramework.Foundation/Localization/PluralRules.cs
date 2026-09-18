using System;
using System.Collections.Generic;

namespace YourFramework.Localization
{
    /// <summary>
    /// CLDR 复数类别。一个语言用哪些类别、怎么判定，由 <see cref="PluralRules"/> 决定；
    /// 内容作者按类别各写一份文案（key.one / key.other ...）。
    /// </summary>
    public enum PluralCategory
    {
        /// <summary>Explicit zero form (Arabic, Latvian, Welsh).</summary>
        Zero,

        /// <summary>Singular form.</summary>
        One,

        /// <summary>Dual form (Arabic, Hebrew, Irish, Welsh, Slovenian).</summary>
        Two,

        /// <summary>Paucal form (Slavic 2-4, Czech 2-4, Arabic 3-10 ...).</summary>
        Few,

        /// <summary>Greater paucal / "many" form (Russian 5-20, Polish fractions ...).</summary>
        Many,

        /// <summary>Default form. Every rule set has it, so it is the always-available fallback.</summary>
        Other
    }

    /// <summary>
    /// CLDR 复数规则（**精选子集**）：覆盖中文、日韩、东南亚、英语与西欧诸语、
    /// 斯拉夫三形态语族、阿拉伯/希伯来/爱尔兰/威尔士/斯洛文尼亚等多形态语族。
    ///
    /// 设计取舍：
    /// 1. 规则按"语族"归类而不是按语言逐条写 —— 英语/德语/荷兰语/北欧诸语的规则完全
    ///    一样（i=1 且 v=0 取 one），逐语言抄一遍只会带来抄错的机会；
    /// 2. 语言一律**只看基础语言**（zh-Hans-CN → zh）。区域内差异真实存在但极少
    ///    （pt 与 pt-PT 的 0 归属就不同），这里按 CLDR 的 pt 规则统一处理并在文档里
    ///    写明；真要区域级精修，规则表就在本文件，改起来是局部的；
    /// 3. 只有一个"other"形态的语言（中文、日文、韩文、泰文、越南文、印尼文…）规则
    ///    为空 —— 这不是"没实现"，而是 CLDR 原文如此；
    /// 4. 不认识的语言返回 <see cref="PluralCategory.Other"/>，并且可以用
    ///    <see cref="IsKnownLanguage"/> 显式问出来。不抛异常：设备上报的语言是运行期
    ///    数据，缺规则应该让内容退到 other 形态继续出，而不是把游戏打崩；
    /// 5. 换算用 i（整数部分）与 v（可见小数位数）两个 operand，与 CLDR 的定义对齐：
    ///    1.5 是 i=1、v=1，因此不会命中"i==1 即单数"这类只对整数成立的规则。
    /// </summary>
    public static class PluralRules
    {
        private enum Family
        {
            OtherOnly,
            OneOther,
            FrenchOne,
            HindiOne,
            OneIfN,
            RussianThree,
            Polish,
            Czech,
            Arabic,
            Hebrew,
            Lithuanian,
            Latvian,
            Romanian,
            Irish,
            Welsh,
            Slovenian,
            Macedonian,
            Icelandic
        }

        private static readonly Dictionary<string, Family> Families = BuildFamilies();

        /// <summary>Languages with an explicit rule here; anything else falls to Other.</summary>
        public static List<string> KnownLanguages
        {
            get { return new List<string>(Families.Keys); }
        }

        /// <summary>Whether a language has an explicit (non-default) rule.</summary>
        public static bool IsKnownLanguage(string language)
        {
            string baseLanguage = BaseLanguage(language);
            return baseLanguage != null && Families.ContainsKey(baseLanguage);
        }

        /// <summary>Integer-value convenience (counts, item numbers).</summary>
        public static PluralCategory Select(string language, long value)
        {
            return Select(language, (double)value);
        }

        /// <summary>
        /// Picks the CLDR category for a value. Unknown languages yield
        /// <see cref="PluralCategory.Other"/> rather than an exception.
        /// </summary>
        public static PluralCategory Select(string language, double value)
        {
            string baseLanguage = BaseLanguage(language);
            Family family;
            if (baseLanguage == null || !Families.TryGetValue(baseLanguage, out family))
            {
                return PluralCategory.Other;
            }

            double n = value < 0 ? -value : value;
            long i = (long)Math.Floor(n);
            int v = FractionDigits(n);
            long mod10 = i % 10;
            long mod100 = i % 100;

            // CLDR 里有两类取模，混用会算错：
            //   - 斯拉夫语族的 %10 / %100 只在整数形态（v==0）下参与判断，用整数部分即可；
            //   - 立陶宛语、拉脱维亚语、阿拉伯语的规则本身作用在**带小数的 n** 上
            //     （lt 的 1.5 应落 many 而不是 one，lv 的 1.5 不属于 zero/one）。
            double nMod10 = n % 10d;
            double nMod100 = n % 100d;

            switch (family)
            {
                case Family.OtherOnly:
                    return PluralCategory.Other;

                case Family.OneOther:
                    return (i == 1 && v == 0) ? PluralCategory.One : PluralCategory.Other;

                case Family.FrenchOne:
                    // fr/pt: 0 and 1 are both singular.
                    return (i == 0 || i == 1) ? PluralCategory.One : PluralCategory.Other;

                case Family.HindiOne:
                    // hi/bn/am: 0 or exactly 1.
                    return (i == 0 || n == 1) ? PluralCategory.One : PluralCategory.Other;

                case Family.OneIfN:
                    return n == 1 ? PluralCategory.One : PluralCategory.Other;

                case Family.RussianThree:
                    if (v == 0 && mod10 == 1 && mod100 != 11)
                    {
                        return PluralCategory.One;
                    }

                    if (v == 0 && mod10 >= 2 && mod10 <= 4 && !(mod100 >= 12 && mod100 <= 14))
                    {
                        return PluralCategory.Few;
                    }

                    if (v == 0 && (mod10 == 0 || (mod10 >= 5 && mod10 <= 9) || (mod100 >= 11 && mod100 <= 14)))
                    {
                        return PluralCategory.Many;
                    }

                    return PluralCategory.Other;

                case Family.Polish:
                    if (i == 1 && v == 0)
                    {
                        return PluralCategory.One;
                    }

                    if (v == 0 && mod10 >= 2 && mod10 <= 4 && !(mod100 >= 12 && mod100 <= 14))
                    {
                        return PluralCategory.Few;
                    }

                    if (v == 0 && i != 1 && (mod10 == 0 || mod10 == 1))
                    {
                        return PluralCategory.Many;
                    }

                    if (v == 0 && ((mod10 >= 5 && mod10 <= 9) || (mod100 >= 12 && mod100 <= 14)))
                    {
                        return PluralCategory.Many;
                    }

                    return PluralCategory.Other;

                case Family.Czech:
                    if (i == 1 && v == 0)
                    {
                        return PluralCategory.One;
                    }

                    if (i >= 2 && i <= 4 && v == 0)
                    {
                        return PluralCategory.Few;
                    }

                    return v != 0 ? PluralCategory.Many : PluralCategory.Other;

                case Family.Arabic:
                    if (n == 0)
                    {
                        return PluralCategory.Zero;
                    }

                    if (n == 1)
                    {
                        return PluralCategory.One;
                    }

                    if (n == 2)
                    {
                        return PluralCategory.Two;
                    }

                    if (nMod100 >= 3d && nMod100 <= 10d)
                    {
                        return PluralCategory.Few;
                    }

                    return (nMod100 >= 11d && nMod100 <= 99d) ? PluralCategory.Many : PluralCategory.Other;

                case Family.Hebrew:
                    if (i == 1 && v == 0)
                    {
                        return PluralCategory.One;
                    }

                    if (i == 2 && v == 0)
                    {
                        return PluralCategory.Two;
                    }

                    return (v == 0 && n != 0 && mod10 == 0) ? PluralCategory.Many : PluralCategory.Other;

                case Family.Lithuanian:
                    if (nMod10 == 1d && !(nMod100 >= 11d && nMod100 <= 19d))
                    {
                        return PluralCategory.One;
                    }

                    if (nMod10 >= 2d && nMod10 <= 9d && !(nMod100 >= 11d && nMod100 <= 19d))
                    {
                        return PluralCategory.Few;
                    }

                    return v != 0 ? PluralCategory.Many : PluralCategory.Other;

                case Family.Latvian:
                    if (nMod10 == 0d || (nMod100 >= 11d && nMod100 <= 19d))
                    {
                        return PluralCategory.Zero;
                    }

                    return (nMod10 == 1d && nMod100 != 11d) ? PluralCategory.One : PluralCategory.Other;

                case Family.Romanian:
                    if (i == 1 && v == 0)
                    {
                        return PluralCategory.One;
                    }

                    if (v != 0 || n == 0 || (mod100 >= 1 && mod100 <= 19))
                    {
                        return PluralCategory.Few;
                    }

                    return PluralCategory.Other;

                case Family.Irish:
                    if (n == 1)
                    {
                        return PluralCategory.One;
                    }

                    if (n == 2)
                    {
                        return PluralCategory.Two;
                    }

                    if (i >= 3 && i <= 6)
                    {
                        return PluralCategory.Few;
                    }

                    return (i >= 7 && i <= 10) ? PluralCategory.Many : PluralCategory.Other;

                case Family.Welsh:
                    if (n == 0)
                    {
                        return PluralCategory.Zero;
                    }

                    if (n == 1)
                    {
                        return PluralCategory.One;
                    }

                    if (n == 2)
                    {
                        return PluralCategory.Two;
                    }

                    if (n == 3)
                    {
                        return PluralCategory.Few;
                    }

                    return n == 6 ? PluralCategory.Many : PluralCategory.Other;

                case Family.Slovenian:
                    if (v == 0 && mod100 == 1)
                    {
                        return PluralCategory.One;
                    }

                    if (v == 0 && mod100 == 2)
                    {
                        return PluralCategory.Two;
                    }

                    if (v != 0 || (mod100 >= 3 && mod100 <= 4))
                    {
                        return PluralCategory.Few;
                    }

                    return PluralCategory.Other;

                case Family.Macedonian:
                    if (v == 0 && mod10 == 1 && mod100 != 11)
                    {
                        return PluralCategory.One;
                    }

                    return (v == 0 && mod10 >= 2 && mod10 <= 4 && !(mod100 >= 12 && mod100 <= 14))
                        ? PluralCategory.One
                        : PluralCategory.Other;

                case Family.Icelandic:
                    return (v == 0 && mod10 == 1 && mod100 != 11)
                        ? PluralCategory.One
                        : PluralCategory.Other;

                default:
                    return PluralCategory.Other;
            }
        }

        /// <summary>
        /// Number of visible fraction digits (CLDR's operand v). Values that are
        /// integral count as 0 without any string round-trip, so the common case
        /// stays allocation free.
        /// </summary>
        private static int FractionDigits(double abs)
        {
            if (abs == Math.Floor(abs))
            {
                return 0;
            }

            double scaled = abs;
            for (int digits = 1; digits <= 6; digits++)
            {
                scaled *= 10d;
                if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9)
                {
                    return digits;
                }
            }

            return 6;
        }

        /// <summary>Base language of a tag ("zh-Hans-CN" → "zh"), lower case; null when unusable.</summary>
        private static string BaseLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return null;
            }

            int cut = language.Length;
            for (int i = 0; i < language.Length; i++)
            {
                char c = language[i];
                if (c == '-' || c == '_')
                {
                    cut = i;
                    break;
                }
            }

            return cut == 0 ? null : language.Substring(0, cut).ToLowerInvariant();
        }

        private static Dictionary<string, Family> BuildFamilies()
        {
            Dictionary<string, Family> map = new Dictionary<string, Family>(StringComparer.Ordinal);

            // n == 1：形同一个规则，但 1.0 也算单数。
            Add(map, Family.OneIfN,
                "hu", "tr", "az", "kk", "ky", "uz", "mn", "ka", "el", "bg", "ta", "te", "ne",
                "af", "sq", "eo", "eu");

            // i == 1 且 v == 0：只对整数 1 算单数（1.0 不算）。
            Add(map, Family.OneOther,
                "en", "de", "es", "it", "nl", "sv", "da", "no", "nb", "nn", "fi", "et", "gl", "ca",
                "sw", "ur");

            // i == 0 或 1：法语系，0 与 1 同为单数。
            Add(map, Family.FrenchOne, "fr", "pt", "hy", "ff", "kab");

            // i == 0 或 n == 1：印地语系。
            Add(map, Family.HindiOne, "hi", "bn", "am", "fa");

            // 只有 other 形态：CLDR 原文如此，不是"未实现"。
            Add(map, Family.OtherOnly, "zh", "ja", "ko", "th", "vi", "id", "ms", "my", "km", "lo", "yo", "ig");

            Add(map, Family.RussianThree, "ru", "uk", "be", "sr", "hr", "bs");
            Add(map, Family.Polish, "pl");
            Add(map, Family.Czech, "cs", "sk");
            Add(map, Family.Arabic, "ar");
            Add(map, Family.Hebrew, "he", "iw");
            Add(map, Family.Lithuanian, "lt");
            Add(map, Family.Latvian, "lv");
            Add(map, Family.Romanian, "ro");
            Add(map, Family.Irish, "ga");
            Add(map, Family.Welsh, "cy");
            Add(map, Family.Slovenian, "sl");
            Add(map, Family.Macedonian, "mk");
            Add(map, Family.Icelandic, "is");

            return map;
        }

        private static void Add(Dictionary<string, Family> map, Family family, params string[] languages)
        {
            for (int i = 0; i < languages.Length; i++)
            {
                map[languages[i]] = family;
            }
        }
    }
}
