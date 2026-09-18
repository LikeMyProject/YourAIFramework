using System;
using System.Collections.Generic;
using System.Text;
using YourAI.Core.Json;

namespace YourFramework.Scripting
{
    /// <summary>脚本补丁清单里的一段代码块。</summary>
    public sealed class ScriptPatchEntry
    {
        /// <summary>逻辑名（清单内唯一）。既是装载时的键，也是运行时侧报错用的 chunkName。</summary>
        public string Name;

        /// <summary>落地文件名（缓存目录下的相对路径）；缺省 = Name + <see cref="ScriptManifest.DefaultFileSuffix"/>。</summary>
        public string FileName;

        /// <summary>用途分类。</summary>
        public ScriptChunkKind Kind;

        /// <summary>该块自身的版本号（诊断用；缺省继承清单版本）。</summary>
        public string Version;

        /// <summary>内容指纹（内容变则变）；构建期算好，运行期可拿它做差量「要不要下这一块」。</summary>
        public string Hash;

        /// <summary>CRC32：落地后读出来做完整性校验（0 = 清单未提供，跳过校验）。</summary>
        public uint Crc;

        /// <summary>源码长度（字符数）：读取后的长度校验与进度估算（0 = 跳过长度校验）。</summary>
        public long Size;

        /// <summary>
        /// true = 装不上就是阶段失败；false = best-effort（装不上记进 Skipped，阶段照常往下走）。
        /// 缺省是 **true** —— 默认「静默跳过」是把补丁没生效这件事藏起来的最快方式。
        /// </summary>
        public bool Required = true;

        /// <summary>装载完成后要调用的入口点（运行时里的全局函数名）。空 = 只装不调。</summary>
        public List<string> Entries;

        /// <summary>依赖的其它块（同清单内的 Name）。装载顺序 = 拓扑序（被依赖者先）。</summary>
        public List<string> Dependencies;
    }

    /// <summary>
    /// 脚本补丁清单：这一版有哪些代码块、各自的指纹、入口点、以及彼此的依赖。
    ///
    /// 与热更程序集清单（AssemblyManifest）、资源包清单（PackageManifest）同一条纪律：
    /// 清单是**构建期产物**，坏清单（重名、悬空依赖、成环、未知分类、缺字段）一律 fail-fast
    /// 抛异常 —— 这是构建管线写错的 bug，不是运行期故障，不该拖到运行时才以「装不上」的面目出现。
    ///
    /// 它存在的理由是 IL2CPP：程序集热更在 IL2CPP 上被平台拒绝，而解释执行的代码块不被拒绝。
    /// 于是「热更什么」这件事在 IL2CPP 上就是「这一版有哪些 .lua 块、怎么装、装完调谁」。
    /// </summary>
    public sealed class ScriptManifest
    {
        /// <summary>清单缺 fileName 时的默认后缀。</summary>
        public const string DefaultFileSuffix = ".lua";

        private static readonly List<string> NoEntries = new List<string>(0);

        private readonly List<ScriptPatchEntry> _chunks = new List<ScriptPatchEntry>();
        private readonly Dictionary<string, ScriptPatchEntry> _byName =
            new Dictionary<string, ScriptPatchEntry>(StringComparer.Ordinal);

        private ScriptManifest(string version)
        {
            Version = version;
        }

        /// <summary>清单版本号（与资源包/程序集版本对齐，便于诊断「这次热更到底下了哪一版」）。</summary>
        public string Version { get; private set; }

        /// <summary>全部代码块条目，清单原始顺序。</summary>
        public List<ScriptPatchEntry> Chunks { get { return _chunks; } }

        /// <summary>条目数。</summary>
        public int Count { get { return _chunks.Count; } }

        /// <summary>全量源码字符数（数字来自清单声明，不是实测）。</summary>
        public long TotalSize { get; private set; }

        /// <summary>条目的入口点列表，永不返回 null（缺省字段 / 显式 null → 空列表）。</summary>
        public static List<string> EntriesOf(ScriptPatchEntry entry)
        {
            return entry == null || entry.Entries == null ? NoEntries : entry.Entries;
        }

        /// <summary>条目的依赖列表，永不返回 null。</summary>
        public static List<string> DependenciesOf(ScriptPatchEntry entry)
        {
            return entry == null || entry.Dependencies == null ? NoEntries : entry.Dependencies;
        }

        /// <summary>按逻辑名取条目；不存在返回 false。</summary>
        public bool TryGet(string name, out ScriptPatchEntry entry)
        {
            if (name == null)
            {
                entry = null;
                return false;
            }

            return _byName.TryGetValue(name, out entry);
        }

        /// <summary>
        /// Parses a manifest. Every structural defect throws <see cref="ArgumentException"/>
        /// with the offending identity in the message, because a bad manifest is a build
        /// artifact bug.
        /// </summary>
        public static ScriptManifest LoadJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("ScriptManifest: json must not be null or empty.", "json");
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("ScriptManifest: invalid JSON: " + ex.Message, ex);
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                throw new ArgumentException("ScriptManifest: root must be a JSON object.");
            }

            string version = RequiredString(root, "version", "(root)");
            JsonValue items = root["chunks"];
            if (items == null || items.Kind != JsonKind.Array)
            {
                throw new ArgumentException("ScriptManifest: (root) is missing an array 'chunks'.");
            }

            ScriptManifest manifest = new ScriptManifest(version);
            for (int i = 0; i < items.Count; i++)
            {
                manifest.ReadChunk(items[i], i, version);
            }

            manifest.VerifyDependenciesResolve();
            manifest.VerifyAcyclic();
            return manifest;
        }

        private void ReadChunk(JsonValue item, int index, string manifestVersion)
        {
            string where = "chunks[" + index + "]";
            if (item == null || item.Kind != JsonKind.Object)
            {
                throw new ArgumentException("ScriptManifest: " + where + " must be a JSON object.");
            }

            ScriptPatchEntry entry = new ScriptPatchEntry();
            entry.Name = RequiredString(item, "name", where);
            entry.FileName = item["fileName"].AsNonEmptyString();
            if (entry.FileName == null)
            {
                entry.FileName = entry.Name + DefaultFileSuffix;
            }

            // kind 缺省按 patch 处理：这份清单驱动的是补丁阶段，主模块清单会显式写 module。
            string kindToken = item["kind"].AsNonEmptyString();
            if (kindToken == null)
            {
                entry.Kind = ScriptChunkKind.Patch;
            }
            else
            {
                ScriptChunkKind kind;
                if (!ScriptChunkKinds.TryParse(kindToken, out kind))
                {
                    throw new ArgumentException(
                        "ScriptManifest: " + where + " has an unknown kind '" + kindToken
                        + "' (expected module / patch / config).");
                }

                entry.Kind = kind;
            }

            entry.Version = item["version"].AsNonEmptyString();
            if (entry.Version == null)
            {
                entry.Version = manifestVersion;
            }

            entry.Hash = item["hash"].AsNonEmptyString();

            double crc = item["crc"].AsDouble(0);
            if (crc < 0 || crc > 4294967295d)
            {
                throw new ArgumentException("ScriptManifest: " + where + " has a crc outside uint32: " + crc);
            }

            entry.Crc = (uint)crc;

            double size = item["size"].AsDouble(0);
            if (size < 0)
            {
                throw new ArgumentException("ScriptManifest: " + where + " has a negative size: " + size);
            }

            entry.Size = (long)size;

            // 注意 JsonValue 的索引器语义：键不存在时返回的是 JsonValue.Null 单例，
            // 不是 C# 的 null。"没有这个字段"要按 Kind 判，不能按引用判。
            // required 的缺省是 true —— 没写就是必需，这是安全的那一侧。
            JsonValue required = item["required"];
            entry.Required = required.Kind == JsonKind.Null ? true : required.AsBool(true);

            entry.Entries = ReadStringArray(item["entries"], where + ".entries");
            entry.Dependencies = ReadStringArray(item["dependencies"], where + ".dependencies");

            if (_byName.ContainsKey(entry.Name))
            {
                throw new ArgumentException("ScriptManifest: duplicate chunk name '" + entry.Name + "'.");
            }

            _byName.Add(entry.Name, entry);
            _chunks.Add(entry);
            TotalSize += entry.Size;
        }

        private static List<string> ReadStringArray(JsonValue value, string where)
        {
            if (value == null || value.Kind == JsonKind.Null)
            {
                return null;
            }

            if (value.Kind != JsonKind.Array)
            {
                throw new ArgumentException("ScriptManifest: " + where + " must be an array.");
            }

            List<string> list = new List<string>(value.Count);
            for (int i = 0; i < value.Count; i++)
            {
                string text = value[i].AsNonEmptyString();
                if (text == null)
                {
                    throw new ArgumentException("ScriptManifest: " + where + "[" + i + "] must be a non-empty string.");
                }

                list.Add(text);
            }

            return list;
        }

        private void VerifyDependenciesResolve()
        {
            for (int i = 0; i < _chunks.Count; i++)
            {
                ScriptPatchEntry entry = _chunks[i];
                List<string> dependencies = DependenciesOf(entry);
                for (int d = 0; d < dependencies.Count; d++)
                {
                    if (!_byName.ContainsKey(dependencies[d]))
                    {
                        throw new ArgumentException(
                            "ScriptManifest: chunk '" + entry.Name
                            + "' depends on unknown chunk '" + dependencies[d] + "'.");
                    }

                    if (string.Equals(dependencies[d], entry.Name, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "ScriptManifest: chunk '" + entry.Name + "' depends on itself.");
                    }
                }
            }
        }

        /// <summary>Kahn 拓扑排序：能出完 = 无环；出不完 = 成环，报出环上的块名（供人排查清单）。</summary>
        private void VerifyAcyclic()
        {
            Dictionary<string, int> inDegree =
                new Dictionary<string, int>(_chunks.Count, StringComparer.Ordinal);
            Dictionary<string, List<string>> dependents =
                new Dictionary<string, List<string>>(_chunks.Count, StringComparer.Ordinal);

            for (int i = 0; i < _chunks.Count; i++)
            {
                List<string> dependencies = DependenciesOf(_chunks[i]);
                inDegree[_chunks[i].Name] = dependencies.Count;
                if (!dependents.ContainsKey(_chunks[i].Name))
                {
                    dependents.Add(_chunks[i].Name, new List<string>(0));
                }

                for (int d = 0; d < dependencies.Count; d++)
                {
                    List<string> list;
                    if (!dependents.TryGetValue(dependencies[d], out list))
                    {
                        list = new List<string>(1);
                        dependents.Add(dependencies[d], list);
                    }

                    list.Add(_chunks[i].Name);
                }
            }

            List<string> queue = new List<string>(_chunks.Count);
            foreach (KeyValuePair<string, int> pair in inDegree)
            {
                if (pair.Value == 0)
                {
                    queue.Add(pair.Key);
                }
            }

            int emitted = 0;
            for (int head = 0; head < queue.Count; head++)
            {
                string name = queue[head];
                emitted++;
                List<string> list = dependents[name];
                for (int i = 0; i < list.Count; i++)
                {
                    int remaining = inDegree[list[i]] - 1;
                    inDegree[list[i]] = remaining;
                    if (remaining == 0)
                    {
                        queue.Add(list[i]);
                    }
                }
            }

            if (emitted == _chunks.Count)
            {
                return;
            }

            List<string> stuck = new List<string>();
            foreach (KeyValuePair<string, int> pair in inDegree)
            {
                if (pair.Value > 0)
                {
                    stuck.Add(pair.Key);
                }
            }

            stuck.Sort(StringComparer.Ordinal);
            throw new ArgumentException(
                "ScriptManifest: dependency cycle among chunks [" + string.Join(", ", stuck.ToArray()) + "].");
        }

        private static string RequiredString(JsonValue owner, string key, string where)
        {
            string text = owner[key].AsNonEmptyString();
            if (text == null)
            {
                throw new ArgumentException(
                    "ScriptManifest: " + where + " is missing a non-empty string '" + key + "'.");
            }

            return text;
        }
    }

    /// <summary>
    /// 装载顺序解析：给定「想装哪几块」（root），把它们的依赖全链按**拓扑序**填进调用方给的
    /// 列表（被依赖者在前，root 在后）。
    ///
    /// 这是同一套算法在框架里的第二次落地（第一次是 <see cref="YourFramework.HotUpdate.AssemblyLoadOrder"/>），
    /// 形状刻意保持一致：填调用方列表、显式栈做后序 DFS 不用递归、0/1/2 三态标记所以手搓清单成环也报得出。
    /// 等第三次出现再抽公共件 —— 两次就抽象，抽象出来的往往是巧合。
    ///
    /// 单线程使用（泵、主线程）。
    /// </summary>
    public sealed class ScriptLoadOrder
    {
        private const int Unvisited = 0;
        private const int InProgress = 1;
        private const int Emitted = 2;

        private readonly Dictionary<string, int> _state = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _stack = new List<string>();
        private readonly List<int> _stackDepIndex = new List<int>();

        /// <summary>
        /// Resolves the load order of the requested chunks plus their dependency closure,
        /// dependencies first. Throws (build-time bug, deliberately loud) when a requested
        /// name is unknown, a dependency dangles, or the manifest is cyclic.
        /// Leaves <paramref name="into"/> empty on the throwing paths.
        /// </summary>
        public void Resolve(ScriptManifest manifest, IList<string> roots, List<string> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            into.Clear();
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            if (roots == null || roots.Count == 0)
            {
                throw new ArgumentException(
                    "ScriptLoadOrder.Resolve: roots must not be empty; a patch stage with no chunks is "
                    + "a wiring bug, not an empty hot update.", "roots");
            }

            _state.Clear();
            _stack.Clear();
            _stackDepIndex.Clear();

            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                if (string.IsNullOrEmpty(root))
                {
                    throw new ArgumentException(
                        "ScriptLoadOrder.Resolve: roots[" + i + "] must be a non-empty chunk name.", "roots");
                }

                ScriptPatchEntry entry;
                if (!manifest.TryGet(root, out entry))
                {
                    into.Clear();
                    throw new ArgumentException("ScriptLoadOrder.Resolve: unknown chunk '" + root + "'.");
                }

                int state;
                if (_state.TryGetValue(root, out state) && state == Emitted)
                {
                    continue;
                }

                _state[root] = InProgress;
                _stack.Add(root);
                _stackDepIndex.Add(0);
                Drain(manifest, into);
            }
        }

        private void Drain(ScriptManifest manifest, List<string> into)
        {
            while (_stack.Count > 0)
            {
                int top = _stack.Count - 1;
                ScriptPatchEntry entry;
                manifest.TryGet(_stack[top], out entry);
                List<string> dependencies = ScriptManifest.DependenciesOf(entry);

                if (_stackDepIndex[top] < dependencies.Count)
                {
                    string dependency = dependencies[_stackDepIndex[top]];
                    _stackDepIndex[top] = _stackDepIndex[top] + 1;

                    ScriptPatchEntry child;
                    if (!manifest.TryGet(dependency, out child))
                    {
                        string owner = _stack[top];
                        into.Clear();
                        throw new ArgumentException(
                            "ScriptLoadOrder.Resolve: chunk '" + owner + "' depends on unknown chunk '"
                            + dependency + "'.");
                    }

                    int state;
                    if (_state.TryGetValue(dependency, out state))
                    {
                        if (state == InProgress)
                        {
                            into.Clear();
                            throw new ArgumentException(
                                "ScriptLoadOrder.Resolve: dependency cycle through '" + dependency
                                + "' (reached again while still being expanded).");
                        }

                        if (state == Emitted)
                        {
                            continue;
                        }
                    }

                    _state[dependency] = InProgress;
                    _stack.Add(dependency);
                    _stackDepIndex.Add(0);
                    continue;
                }

                // 依赖展开完了：标记 + 落进结果，再回到父节点。
                _stack.RemoveAt(top);
                _stackDepIndex.RemoveAt(top);
                _state[entry.Name] = Emitted;
                into.Add(entry.Name);
            }
        }
    }

    /// <summary>
    /// 补丁清单的确定性写出：**与书写顺序解耦**（按 Name Ordinal 排序），同一份内容永远得到
    /// 同一串字节 —— 这样清单可以进版本控制、可以 diff、也可以被检查项逐字比对。
    ///
    /// 与地址目录的写出（CatalogWriter）同一条纪律：构建期产物的字节必须是确定的，
    /// 否则「这次构建和上次构建到底差在哪」就永远说不清。
    /// </summary>
    public static class ScriptManifestWriter
    {
        /// <summary>写出清单 JSON（缩进两格，字段顺序固定）。</summary>
        public static string Write(ScriptManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            List<ScriptPatchEntry> sorted = new List<ScriptPatchEntry>(manifest.Chunks);
            sorted.Sort(CompareByName);

            StringBuilder builder = new StringBuilder();
            builder.Append("{\n  \"version\": \"").Append(JsonParser.Escape(manifest.Version)).Append("\",\n");
            builder.Append("  \"chunks\": [");

            for (int i = 0; i < sorted.Count; i++)
            {
                builder.Append(i == 0 ? "\n" : ",\n");
                WriteEntry(builder, sorted[i]);
            }

            builder.Append(sorted.Count == 0 ? "]\n}\n" : "\n  ]\n}\n");
            return builder.ToString();
        }

        private static void WriteEntry(StringBuilder builder, ScriptPatchEntry entry)
        {
            builder.Append("    {\n");
            builder.Append("      \"name\": \"").Append(JsonParser.Escape(entry.Name)).Append("\",\n");
            builder.Append("      \"fileName\": \"").Append(JsonParser.Escape(entry.FileName)).Append("\",\n");
            builder.Append("      \"kind\": \"").Append(ScriptChunkKinds.ToToken(entry.Kind)).Append("\",\n");
            builder.Append("      \"version\": \"").Append(JsonParser.Escape(entry.Version)).Append("\",\n");
            builder.Append("      \"required\": ").Append(entry.Required ? "true" : "false").Append(",\n");

            if (!string.IsNullOrEmpty(entry.Hash))
            {
                builder.Append("      \"hash\": \"").Append(JsonParser.Escape(entry.Hash)).Append("\",\n");
            }

            builder.Append("      \"crc\": ").Append(entry.Crc).Append(",\n");
            builder.Append("      \"size\": ").Append(entry.Size).Append(",\n");
            builder.Append("      \"entries\": ").Append(StringArray(entry.Entries)).Append(",\n");
            builder.Append("      \"dependencies\": ").Append(StringArray(entry.Dependencies)).Append('\n');
            builder.Append("    }");
        }

        private static string StringArray(List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "[]";
            }

            StringBuilder builder = new StringBuilder("[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('"').Append(JsonParser.Escape(values[i])).Append('"');
            }

            return builder.Append(']').ToString();
        }

        private static int CompareByName(ScriptPatchEntry a, ScriptPatchEntry b)
        {
            return string.CompareOrdinal(a.Name, b.Name);
        }
    }
}
