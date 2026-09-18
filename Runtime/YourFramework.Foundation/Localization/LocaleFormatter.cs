using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YourFramework.Localization
{
    /// <summary>
    /// 格式化参数袋：具名取值与位置取值都从这里走。实现只需要两个纯查询，
    /// 因此可以是字典、数组、组件包装，或者调用方自己的结构。
    /// </summary>
    public interface ILocaleArguments
    {
        /// <summary>Named argument lookup ({name}).</summary>
        bool TryGet(string name, out object value);

        /// <summary>Positional argument lookup ({0}); index starts at 0.</summary>
        bool TryGet(int index, out object value);
    }

    /// <summary>
    /// 随手可用的参数袋：<c>Add("name", v)</c> 进具名表，<c>Add(v)</c> 按加入顺序
    /// 进位置表（两者互不干扰，同一个值想两种取法就写两次）。
    ///
    /// 这是给调用方便利用的实现，不是唯一实现 —— 热路径上想零分配，自己实现
    /// <see cref="ILocaleArguments"/> 即可，格式化器只认接口。
    /// </summary>
    public sealed class LocaleArguments : ILocaleArguments
    {
        private readonly Dictionary<string, object> _named =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly List<object> _positional = new List<object>();

        /// <summary>Adds a named argument; re-adding a name replaces it.</summary>
        public LocaleArguments Add(string name, object value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("LocaleArguments.Add: name must not be empty.", "name");
            }

            _named[name] = value;
            return this;
        }

        /// <summary>Adds a positional argument; the index is the current count.</summary>
        public LocaleArguments Add(object value)
        {
            _positional.Add(value);
            return this;
        }

        /// <summary>Positional argument count.</summary>
        public int Count { get { return _positional.Count; } }

        /// <inheritdoc/>
        public bool TryGet(string name, out object value)
        {
            if (name != null && _named.TryGetValue(name, out value))
            {
                return true;
            }

            value = null;
            return false;
        }

        /// <inheritdoc/>
        public bool TryGet(int index, out object value)
        {
            if (index >= 0 && index < _positional.Count)
            {
                value = _positional[index];
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// 文本格式化器：把模板里的占位符换成参数。模板语法（同时也是内容作者要遵守的规范）：
    ///
    ///     {name}                          具名参数
    ///     {0}                             位置参数
    ///     {0:N0}                          位置参数 + .NET 格式串（不变文化）
    ///     {count:plural:one{1 个}|other{{count} 个}}   内联复数形态
    ///     {{  }}                          花括号字面量
    ///
    /// 设计取舍：
    /// 1. **复数是内联形态**，不是"去查 key.one / key.other" —— 模板自带全部形态，
    ///    内容质量在一条字符串里就能看完，也不会因为少建一个 .one 键而在运行时静默
    ///    退化。形态缺 <c>other</c> 时由 <see cref="Validate"/> 直接判为坏模板；
    /// 2. **格式化器绑定语言**（复数类别取决于语言），因此是"每语言一个、可缓存"的
    ///    对象 —— 这正是 pack 按语言标签缓存它的原因；
    /// 3. 取值失败是**业务不是异常**：参数缺了、复数形态没写、位置越界，都保留原文
    ///    字面量并计入 <c>unresolved</c> 计数，UI 照常出内容、诊断有数可看。
    ///    与之相对，坏模板要在**构建期**被抓住，这是 <see cref="Validate"/> 的职责
    ///    （工具链调用它 fail-fast，运行时路径不抛）；
    /// 4. 数字一律按不变文化渲染，不随设备区域把 3.5 变成 3,5。
    /// </summary>
    public sealed class LocaleFormatter
    {
        private readonly List<string> _chainScratch = new List<string>(3);

        /// <summary>Creates a formatter bound to a language (used for plural selection).</summary>
        public LocaleFormatter(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                throw new ArgumentException("LocaleFormatter: language must not be empty.", "language");
            }

            Language = language;
        }

        /// <summary>Language whose plural rules this formatter applies.</summary>
        public string Language { get; set; }

        /// <summary>
        /// Formats a template, reporting how many placeholders could not be
        /// resolved (left as literal text). A non-zero count means the content or
        /// the call site is wrong; it is reported, not thrown.
        /// </summary>
        public string Format(string template, ILocaleArguments args, out int unresolved)
        {
            unresolved = 0;
            if (template == null)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(template.Length + 16);
            Expand(builder, template, args, ref unresolved);
            return builder.ToString();
        }

        /// <summary>Convenience overload for callers that do not track misses.</summary>
        public string Format(string template, ILocaleArguments args)
        {
            int ignored;
            return Format(template, args, out ignored);
        }

        /// <summary>
        /// Build-time check for content authors and packers: returns false with a
        /// human-readable reason for unclosed/empty placeholders, unknown plural
        /// category names, duplicate categories and a plural group without the
        /// mandatory <c>other</c> form. The runtime path stays tolerant; this is
        /// where a bad template is supposed to be loud.
        /// </summary>
        public static bool Validate(string template, out string reason)
        {
            reason = null;
            if (template == null)
            {
                reason = "template is null";
                return false;
            }

            List<string> bodies = new List<string>();
            if (!Split(template, bodies, out reason))
            {
                return false;
            }

            for (int i = 0; i < bodies.Count; i++)
            {
                string head;
                string rest;
                if (!SplitHead(bodies[i], out head, out rest))
                {
                    reason = "placeholder '{" + bodies[i] + "}' has an empty argument name";
                    return false;
                }

                if (rest == null || !rest.StartsWith("plural:", StringComparison.Ordinal))
                {
                    continue;
                }

                List<string> categories = new List<string>();
                List<string> forms = new List<string>();
                if (!SplitPluralForms(rest.Substring(7), categories, forms, out reason))
                {
                    return false;
                }

                bool hasOther = false;
                for (int c = 0; c < categories.Count; c++)
                {
                    if (string.Equals(categories[c], "other", StringComparison.OrdinalIgnoreCase))
                    {
                        hasOther = true;
                    }

                    for (int d = c + 1; d < categories.Count; d++)
                    {
                        if (string.Equals(categories[c], categories[d], StringComparison.OrdinalIgnoreCase))
                        {
                            reason = "plural group for '{" + head + "}' declares '" + categories[c] + "' twice";
                            return false;
                        }
                    }
                }

                if (!hasOther)
                {
                    reason = "plural group for '{" + head + "}' has no 'other' form; "
                        + "a language whose rule picks another category would have nothing to show";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Convenience form of <see cref="Validate"/> for tooling.</summary>
        public static bool Validate(string template)
        {
            string ignored;
            return Validate(template, out ignored);
        }

        private void Expand(StringBuilder builder, string template, ILocaleArguments args, ref int unresolved)
        {
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];

                if (c == '{')
                {
                    if (i + 1 < template.Length && template[i + 1] == '{')
                    {
                        builder.Append('{');
                        i += 2;
                        continue;
                    }

                    int close = FindClosingBrace(template, i);
                    if (close < 0)
                    {
                        // Unclosed placeholder: keep the rest verbatim and report it.
                        unresolved++;
                        builder.Append(template, i, template.Length - i);
                        return;
                    }

                    string body = template.Substring(i + 1, close - i - 1);
                    Emit(builder, body, args, ref unresolved);
                    i = close + 1;
                    continue;
                }

                if (c == '}')
                {
                    if (i + 1 < template.Length && template[i + 1] == '}')
                    {
                        builder.Append('}');
                        i += 2;
                        continue;
                    }

                    // A lone '}' is not a placeholder; treat it as text.
                    builder.Append('}');
                    i++;
                    continue;
                }

                builder.Append(c);
                i++;
            }
        }

        private void Emit(StringBuilder builder, string body, ILocaleArguments args, ref int unresolved)
        {
            string head;
            string rest;
            if (!SplitHead(body, out head, out rest))
            {
                unresolved++;
                builder.Append('{').Append(body).Append('}');
                return;
            }

            if (rest != null && rest.StartsWith("plural:", StringComparison.Ordinal))
            {
                EmitPlural(builder, head, rest.Substring(7), args, ref unresolved);
                return;
            }

            object value;
            if (!Resolve(head, args, out value))
            {
                unresolved++;
                builder.Append('{').Append(body).Append('}');
                return;
            }

            builder.Append(Render(value, rest));
        }

        private void EmitPlural(
            StringBuilder builder, string head, string forms, ILocaleArguments args, ref int unresolved)
        {
            object value;
            if (!Resolve(head, args, out value))
            {
                unresolved++;
                builder.Append('{').Append(head).Append(":plural:").Append(forms).Append('}');
                return;
            }

            double number;
            if (!TryToDouble(value, out number))
            {
                // A non-numeric argument cannot pick a plural category.
                unresolved++;
                builder.Append('{').Append(head).Append(":plural:").Append(forms).Append('}');
                return;
            }

            List<string> categories = new List<string>(4);
            List<string> bodies = new List<string>(4);
            string reason;
            if (!SplitPluralForms(forms, categories, bodies, out reason))
            {
                unresolved++;
                builder.Append('{').Append(head).Append(":plural:").Append(forms).Append('}');
                return;
            }

            PluralCategory wanted = PluralRules.Select(Language, number);
            int chosen = -1;
            int other = -1;
            for (int i = 0; i < categories.Count; i++)
            {
                if (string.Equals(categories[i], "other", StringComparison.OrdinalIgnoreCase))
                {
                    other = i;
                }

                if (chosen < 0 && string.Equals(categories[i], wanted.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    chosen = i;
                }
            }

            if (chosen < 0)
            {
                chosen = other;
            }

            if (chosen < 0)
            {
                unresolved++;
                builder.Append('{').Append(head).Append(":plural:").Append(forms).Append('}');
                return;
            }

            // Forms may nest placeholders, so expand recursively.
            Expand(builder, bodies[chosen], args, ref unresolved);
        }

        private bool Resolve(string head, ILocaleArguments args, out object value)
        {
            value = null;
            if (args == null || head.Length == 0)
            {
                return false;
            }

            if (IsAllDigits(head))
            {
                int index = 0;
                for (int i = 0; i < head.Length; i++)
                {
                    index = index * 10 + (head[i] - '0');
                    if (index > 100000)
                    {
                        return false;
                    }
                }

                return args.TryGet(index, out value);
            }

            return args.TryGet(head, out value);
        }

        private static string Render(object value, string format)
        {
            if (value == null)
            {
                return string.Empty;
            }

            IFormattable formattable = value as IFormattable;
            if (formattable != null)
            {
                return formattable.ToString(format, CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }

        private static bool TryToDouble(object value, out double number)
        {
            number = 0;
            if (value == null)
            {
                return false;
            }

            if (value is int) { number = (int)value; return true; }
            if (value is long) { number = (long)value; return true; }
            if (value is double) { number = (double)value; return true; }
            if (value is float) { number = (float)value; return true; }
            if (value is decimal) { number = (double)(decimal)value; return true; }
            if (value is short) { number = (short)value; return true; }
            if (value is byte) { number = (byte)value; return true; }
            if (value is uint) { number = (uint)value; return true; }
            if (value is ulong) { number = (ulong)value; return true; }
            if (value is ushort) { number = (ushort)value; return true; }
            if (value is sbyte) { number = (sbyte)value; return true; }
            return false;
        }

        /// <summary>Index of the '}' matching the '{' at <paramref name="open"/>, or -1.</summary>
        private static int FindClosingBrace(string template, int open)
        {
            int depth = 0;
            for (int i = open; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>Splits "name" / "name:rest" at the first top-level colon.</summary>
        private static bool SplitHead(string body, out string head, out string rest)
        {
            head = body;
            rest = null;
            if (body == null || body.Length == 0)
            {
                return false;
            }

            // The head is everything before the first colon; whatever follows is
            // either a .NET format spec or the inline plural group. Nested colons
            // inside plural forms are safe because we only split on the first one.
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == ':')
                {
                    head = body.Substring(0, i);
                    rest = body.Substring(i + 1);
                    break;
                }
            }

            return head.Length > 0;
        }

        /// <summary>
        /// Collects the body of every top-level "{...}" in the template (used by
        /// <see cref="Validate"/>), failing on an unclosed placeholder.
        /// </summary>
        private static bool Split(string template, List<string> bodies, out string reason)
        {
            reason = null;
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{')
                {
                    if (i + 1 < template.Length && template[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }

                    int close = FindClosingBrace(template, i);
                    if (close < 0)
                    {
                        reason = "unclosed placeholder starting at index " + i;
                        return false;
                    }

                    bodies.Add(template.Substring(i + 1, close - i - 1));
                    i = close + 1;
                    continue;
                }

                if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
                {
                    i += 2;
                    continue;
                }

                i++;
            }

            return true;
        }

        /// <summary>Parses "one{a}|other{b}" into parallel category/form lists.</summary>
        private static bool SplitPluralForms(
            string forms, List<string> categories, List<string> bodies, out string reason)
        {
            reason = null;
            int i = 0;
            while (i < forms.Length)
            {
                while (i < forms.Length && (forms[i] == ' ' || forms[i] == '|'))
                {
                    i++;
                }

                if (i >= forms.Length)
                {
                    break;
                }

                int nameStart = i;
                while (i < forms.Length && forms[i] != '{' && forms[i] != '|')
                {
                    i++;
                }

                string name = forms.Substring(nameStart, i - nameStart).Trim();
                if (i >= forms.Length || forms[i] != '{')
                {
                    reason = "plural form '" + name + "' is not followed by '{'";
                    return false;
                }

                if (!IsPluralCategoryName(name))
                {
                    reason = "'" + name + "' is not a CLDR plural category "
                        + "(zero/one/two/few/many/other)";
                    return false;
                }

                int close = FindClosingBrace(forms, i);
                if (close < 0)
                {
                    reason = "plural form '" + name + "' has an unclosed '{'";
                    return false;
                }

                categories.Add(name);
                bodies.Add(forms.Substring(i + 1, close - i - 1));
                i = close + 1;
            }

            if (categories.Count == 0)
            {
                reason = "plural group declares no forms";
                return false;
            }

            return true;
        }

        private static bool IsPluralCategoryName(string name)
        {
            return string.Equals(name, "zero", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "one", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "two", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "few", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "many", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "other", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAllDigits(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return value.Length > 0;
        }
    }
}
