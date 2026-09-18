using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using YourFramework.HotUpdate;

namespace YourAI.EditorTools
{
    /// <summary>
    /// AOT 白名单窗口：扫出被实例化过的泛型定义，写成 link.xml，给 IL2CPP 构建期补全泛型用。
    ///
    /// 逻辑全在引擎无关的 <see cref="AotWhitelist"/> 里（也是离线断言测过的那个）；
    /// 本类只做三件事：选程序集、选输出路径、写文件。窗口里的说明不是装饰 ——
    /// AotWhitelist 用反射扫描，**只在方法体内部出现**的泛型实例化它看不见，
    /// 那部分必须由人在"额外种子类型"里补上，界面上写清楚比事后排查便宜。
    ///
    /// 打开：YourAI / 热更 / AOT 白名单（link.xml）。
    /// </summary>
    public sealed class AotWhitelistWindow : EditorWindow
    {
        private const string DefaultFilter = "Assembly-CSharp;HotUpdate";

        private string _assemblyFilter = DefaultFilter;
        private string _seedTypes = string.Empty;
        private string _outputPath = "Assets/link.xml";
        private string _status = "填好程序集名后点“生成 link.xml”。";
        private Vector2 _scroll;

        [MenuItem("YourAI/热更/AOT 白名单（link.xml）", false, 1)]
        private static void Open()
        {
            AotWhitelistWindow window = GetWindow<AotWhitelistWindow>(false, "AOT 白名单", true);
            window.minSize = new Vector2(460f, 420f);
            window.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("扫描范围", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "要扫描的程序集简单名，分号或换行分隔（默认 Assembly-CSharp 与 HotUpdate）。"
                + "\n只扫这些程序集里的类型 —— 全量扫会产出一份几万行的 link.xml，链接器会被拖慢。",
                MessageType.Info);
            _assemblyFilter = EditorGUILayout.TextArea(_assemblyFilter, GUILayout.MinHeight(48f));

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("额外种子类型（可选）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "一行一个类型全名，如 System.Collections.Generic.List`1。"
                + "\n反射只看得到字段 / 属性 / 方法签名里的泛型实例化；只在方法体内部 new 出来的"
                + "（例如方法里写了 new Dictionary<int, Foo>()）它看不见 —— 把这类补在这里。",
                MessageType.Warning);
            _seedTypes = EditorGUILayout.TextArea(_seedTypes, GUILayout.MinHeight(48f));

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("输出", EditorStyles.boldLabel);
            _outputPath = EditorGUILayout.TextField("link.xml 路径", _outputPath);

            EditorGUILayout.Space(8f);
            if (GUILayout.Button("生成 link.xml", GUILayout.Height(28f)))
            {
                Generate(true);
            }

            if (GUILayout.Button("只扫描，看统计"))
            {
                Generate(false);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("结果", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private void Generate(bool writeFile)
        {
            List<string> problems = new List<string>();
            List<Type> types = GatherTypes(problems);

            if (types.Count == 0)
            {
                _status = "没扫到任何类型。检查程序集名是否写对（大小写敏感），"
                    + "或在编辑器里先 Play 一次让热更程序集被装载。";
                return;
            }

            HashSet<Type> definitions = new HashSet<Type>();
            AotWhitelist.CollectGenericDefinitions(types, definitions, problems);

            StringBuilder status = new StringBuilder();
            status.Append("扫描 ").Append(types.Count).Append(" 个类型，得到 ")
                .Append(definitions.Count).Append(" 个泛型定义。");

            if (!writeFile)
            {
                status.Append("\n（只扫描模式，未写文件。）");
                AppendProblems(status, problems);
                _status = status.ToString();
                return;
            }

            if (string.IsNullOrEmpty(_outputPath))
            {
                _status = "输出路径不能为空。";
                return;
            }

            string xml = AotWhitelist.BuildLinkXml(definitions);
            try
            {
                string directory = Path.GetDirectoryName(_outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_outputPath, xml, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _status = "写文件失败：" + ex.GetType().Name + ": " + ex.Message;
                return;
            }

            AssetDatabase.Refresh();
            status.Append("\n已写入 ").Append(_outputPath).Append("（")
                .Append(xml.Length).Append(" 字节）。");
            AppendProblems(status, problems);
            _status = status.ToString();
            Debug.Log("YourAI AotWhitelist: " + _status);
        }

        private List<Type> GatherTypes(List<string> problems)
        {
            List<Type> types = new List<Type>();
            List<string> filters = SplitList(_assemblyFilter);

            if (filters.Count > 0)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (assembly.IsDynamic)
                    {
                        continue;
                    }

                    AssemblyName name = assembly.GetName();
                    if (name == null || !filters.Contains(name.Name))
                    {
                        continue;
                    }

                    try
                    {
                        types.AddRange(assembly.GetTypes());
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        Type[] partial = ex.Types;
                        if (partial != null)
                        {
                            for (int t = 0; t < partial.Length; t++)
                            {
                                if (partial[t] != null)
                                {
                                    types.Add(partial[t]);
                                }
                            }
                        }

                        if (problems != null)
                        {
                            problems.Add("程序集 '" + name.Name + "' 只加载出一部分类型：" + ex.Message);
                        }
                    }
                }
            }

            List<string> seeds = SplitList(_seedTypes);
            for (int i = 0; i < seeds.Count; i++)
            {
                Type seed = ResolveSeed(seeds[i]);
                if (seed == null)
                {
                    if (problems != null)
                    {
                        problems.Add("种子类型 '" + seeds[i] + "' 找不到（名字写错，或它所在的程序集还没装载）。");
                    }

                    continue;
                }

                types.Add(seed);
            }

            return types;
        }

        private static Type ResolveSeed(string fullName)
        {
            Type direct = Type.GetType(fullName, false);
            if (direct != null)
            {
                return direct;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type found = assemblies[i].GetType(fullName, false);                    if (found != null)
                    {
                        return found;
                    }
                }
                catch (Exception)
                {
                    // 单个程序集查不了不影响找下一个。
                }
            }

            return null;
        }

        private static List<string> SplitList(string text)
        {
            List<string> values = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return values;
            }

            string[] parts = text.Replace('\r', '\n').Replace(';', '\n').Replace(',', '\n').Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                string value = parts[i].Trim();
                if (value.Length > 0)
                {
                    values.Add(value);
                }
            }

            return values;
        }

        private static void AppendProblems(StringBuilder status, List<string> problems)
        {
            if (problems == null || problems.Count == 0)
            {
                return;
            }

            status.Append("\n另外 ").Append(problems.Count).Append(" 条提示：");
            for (int i = 0; i < problems.Count; i++)
            {
                status.Append("\n  - ").Append(problems[i]);
            }
        }
    }
}
