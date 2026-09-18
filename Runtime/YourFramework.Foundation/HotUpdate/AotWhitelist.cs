using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace YourFramework.HotUpdate
{
    /// <summary>
    /// AOT 白名单（link.xml）生成：把"被实例化过的泛型定义"扫出来，写成 Unity 链接器
    /// （以及 HybridCLR / AOT 泛型补全）认识的 link.xml。
    ///
    /// 为什么需要它：JIT 平台（编辑器 / Android-Mono / Windows）现成可用，因为 JIT 会在第一次
    /// 用到 <c>List&lt;MyStruct&gt;</c> 时现场编译出代码。IL2CPP 上泛型要在构建期补全，没在
    /// AOT 里生成过的值类型泛型实例化运行时会缺代码 —— link.xml 的职责就是告诉链接器
    /// "这些泛型定义别裁掉、都给我留全"。
    ///
    /// **诚实的能力边界**（写清楚比假装全能有用）：本工具用反射枚举类型、字段、属性、方法
    /// 签名、基类与接口里出现的泛型实例化，这是**不读 IL 就能拿到**的那部分。只在方法体
    /// 内部出现（既不进签名也不进字段）的实例化，反射看不见 —— 需要读 IL 才能覆盖，那要
    /// 引 Cecil 之类的库，框架不引三方依赖，所以那部分留给调用方用"额外种子类型"显式补上
    /// （见 AotWhitelistWindow 的第二栏）。宁可说清覆盖到哪，也不给一个"应该是全的"的假象。
    ///
    /// 本类只做纯逻辑（类型集合 → XML 文本），不碰文件系统；写文件、菜单、窗口在 Editor 层。
    /// </summary>
    public static class AotWhitelist
    {
        private const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// 扫描给定的类型集合（连同它们的字段 / 属性 / 方法签名 / 基类 / 接口 / 泛型约束），
        /// 把出现的泛型定义收进 <paramref name="definitions"/>。反射读取失败的成员记进
        /// <paramref name="problems"/>（可为 null），不影响其它类型。
        /// </summary>
        public static void CollectGenericDefinitions(
            IEnumerable<Type> types, HashSet<Type> definitions, List<string> problems)
        {
            if (types == null)
            {
                throw new ArgumentNullException("types");
            }

            if (definitions == null)
            {
                throw new ArgumentNullException("definitions");
            }

            HashSet<Type> seen = new HashSet<Type>();
            Stack<Type> pending = new Stack<Type>();

            foreach (Type type in types)
            {
                Push(type, seen, pending);
            }

            while (pending.Count > 0)
            {
                Scan(pending.Pop(), definitions, seen, pending, problems);
            }
        }

        /// <summary>
        /// 一步到位：扫描类型集合 → link.xml 文本。输出经过排序与去重，**同样的输入永远
        /// 产出同样的字节**（进版本库的构建产物不该每次都不一样）。
        /// </summary>
        public static string BuildLinkXmlFromTypes(IEnumerable<Type> types, List<string> problems)
        {
            HashSet<Type> definitions = new HashSet<Type>();
            CollectGenericDefinitions(types, definitions, problems);
            return BuildLinkXml(definitions);
        }

        /// <summary>
        /// 把泛型定义渲染成 link.xml。按程序集名、类型全名 Ordinal 排序；
        /// 嵌套类型的分隔符按 link.xml 惯例用 '/'（反射给的是 '+'）。
        /// </summary>
        public static string BuildLinkXml(IEnumerable<Type> definitions)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException("definitions");
            }

            Dictionary<string, List<string>> byAssembly =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            int total = 0;

            foreach (Type type in definitions)
            {
                if (type == null)
                {
                    continue;
                }

                Assembly assembly = type.Assembly;
                AssemblyName assemblyName = assembly == null ? null : assembly.GetName();
                string owner = assemblyName == null || string.IsNullOrEmpty(assemblyName.Name)
                    ? "(unknown)" : assemblyName.Name;
                string linkName = LinkNameOf(type);

                if (!emitted.Add(owner + "|" + linkName))
                {
                    continue;
                }

                List<string> list;
                if (!byAssembly.TryGetValue(owner, out list))
                {
                    list = new List<string>();
                    byAssembly.Add(owner, list);
                }

                list.Add(linkName);
                total++;
            }

            List<string> assemblies = new List<string>(byAssembly.Keys);
            assemblies.Sort(StringComparer.Ordinal);

            StringBuilder xml = new StringBuilder();
            xml.Append("<!-- 由 YourAIFramework AotWhitelist 生成：")
                .Append(assemblies.Count).Append(" 个程序集，")
                .Append(total).Append(" 个泛型定义（去重后）。请勿手工编辑。 -->\n");
            xml.Append("<linker>\n");

            for (int i = 0; i < assemblies.Count; i++)
            {
                string owner = assemblies[i];
                List<string> list = byAssembly[owner];
                list.Sort(StringComparer.Ordinal);

                xml.Append("  <assembly fullname=\"").Append(Escape(owner)).Append("\">\n");
                for (int t = 0; t < list.Count; t++)
                {
                    xml.Append("    <type fullname=\"").Append(Escape(list[t]))
                        .Append("\" preserve=\"all\" />\n");
                }

                xml.Append("  </assembly>\n");
            }

            xml.Append("</linker>\n");
            return xml.ToString();
        }

        /// <summary>
        /// link.xml 里用的类型名（嵌套用 '/'，泛型定义带 `n 元数后缀）。
        ///
        /// 落到**类型定义**而不是构造类型：<c>typeof(Outer&lt;int&gt;.Inner).FullName</c> 会把
        /// 整段 <c>[[System.Int32, System.Private.CoreLib, Version=...]]</c> 一起吐出来，
        /// 链接器拿这个字符串一个字都匹配不上 —— 所以先退回泛型定义再取名。
        /// </summary>
        public static string LinkNameOf(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }

            Type target = type;
            if (target.IsGenericType && !target.IsGenericTypeDefinition)
            {
                try
                {
                    target = target.GetGenericTypeDefinition();
                }
                catch (InvalidOperationException)
                {
                    // 个别构造类型退不回定义，就用它自己的名字（仍然好过抛出去）。
                }
            }

            string full = target.FullName;
            if (string.IsNullOrEmpty(full))
            {
                full = target.Name;
            }

            return full.Replace('+', '/');
        }

        private static void Scan(
            Type type, HashSet<Type> definitions, HashSet<Type> seen, Stack<Type> pending, List<string> problems)
        {
            if (type == null)
            {
                return;
            }

            if (type.IsGenericParameter)
            {
                // 泛型参数本身不是定义，但它的约束可能引用具体的泛型实例化。
                try
                {
                    Type[] constraints = type.GetGenericParameterConstraints();
                    for (int i = 0; i < constraints.Length; i++)
                    {
                        Push(constraints[i], seen, pending);
                    }
                }
                catch (Exception ex)
                {
                    Add(problems, "generic constraints of '" + type.Name + "' could not be read: "
                        + ex.GetType().Name + ": " + ex.Message);
                }

                return;
            }

            if (type.HasElementType)
            {
                // 数组 / byref / 指针：把元素类型继续摊开。
                Push(type.GetElementType(), seen, pending);
                return;
            }

            if (type.IsGenericType)
            {
                definitions.Add(type.GetGenericTypeDefinition());
                try
                {
                    Type[] arguments = type.GetGenericArguments();
                    for (int i = 0; i < arguments.Length; i++)
                    {
                        Push(arguments[i], seen, pending);
                    }
                }
                catch (Exception ex)
                {
                    Add(problems, "generic arguments of '" + type.Name + "' could not be read: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (type.IsNested && type.DeclaringType != null)
            {
                Push(type.DeclaringType, seen, pending);
            }

            try
            {
                Push(type.BaseType, seen, pending);
                Type[] interfaces = type.GetInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    Push(interfaces[i], seen, pending);
                }
            }
            catch (Exception ex)
            {
                Add(problems, "base types of '" + NameOf(type) + "' could not be read: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            ScanMembers(type, seen, pending, problems);
        }

        private static void ScanMembers(Type type, HashSet<Type> seen, Stack<Type> pending, List<string> problems)
        {
            // 字段：泛型实例化最常出现在这里，且反射一定能看见。
            try
            {
                FieldInfo[] fields = type.GetFields(MemberFlags);
                for (int i = 0; i < fields.Length; i++)
                {
                    Push(fields[i].FieldType, seen, pending);
                }
            }
            catch (Exception ex)
            {
                Add(problems, "fields of '" + NameOf(type) + "' could not be read: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                PropertyInfo[] properties = type.GetProperties(MemberFlags);
                for (int i = 0; i < properties.Length; i++)
                {
                    Push(properties[i].PropertyType, seen, pending);
                    ParameterInfo[] indexers = properties[i].GetIndexParameters();
                    for (int p = 0; p < indexers.Length; p++)
                    {
                        Push(indexers[p].ParameterType, seen, pending);
                    }
                }
            }
            catch (Exception ex)
            {
                Add(problems, "properties of '" + NameOf(type) + "' could not be read: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                MethodInfo[] methods = type.GetMethods(MemberFlags);
                for (int i = 0; i < methods.Length; i++)
                {
                    Push(methods[i].ReturnType, seen, pending);
                    ParameterInfo[] parameters = methods[i].GetParameters();
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        Push(parameters[p].ParameterType, seen, pending);
                    }

                    Type[] genericArguments = methods[i].GetGenericArguments();
                    for (int g = 0; g < genericArguments.Length; g++)
                    {
                        Push(genericArguments[g], seen, pending);
                    }
                }
            }
            catch (Exception ex)
            {
                Add(problems, "methods of '" + NameOf(type) + "' could not be read: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string NameOf(Type type)
        {
            string full = type.FullName;
            return string.IsNullOrEmpty(full) ? type.Name : full;
        }

        private static void Push(Type type, HashSet<Type> seen, Stack<Type> pending)
        {
            if (type != null && seen.Add(type))
            {
                pending.Push(type);
            }
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder escaped = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '&')
                {
                    escaped.Append("&amp;");
                }
                else if (c == '<')
                {
                    escaped.Append("&lt;");
                }
                else if (c == '>')
                {
                    escaped.Append("&gt;");
                }
                else if (c == '"')
                {
                    escaped.Append("&quot;");
                }
                else
                {
                    escaped.Append(c);
                }
            }

            return escaped.ToString();
        }

        private static void Add(List<string> list, string message)
        {
            if (list != null)
            {
                list.Add(message);
            }
        }
    }
}
