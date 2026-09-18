using System;
using System.Collections.Generic;
using YourAI.Core.Json;

namespace YourFramework.HotUpdate
{
    /// <summary>热更清单里的一个程序集条目。</summary>
    public sealed class HotAssemblyEntry
    {
        /// <summary>逻辑名（清单内唯一），通常就是程序集简单名。</summary>
        public string Name;

        /// <summary>落地文件名（缓存目录下的相对路径）；缺省 = Name + ".dll.bytes"。</summary>
        public string FileName;

        /// <summary>内容指纹：内容变则变，版本差量比对的就是它。</summary>
        public string Hash;

        /// <summary>CRC32：读取落地后的完整性校验值（0 = 清单未提供，跳过校验）。</summary>
        public uint Crc;

        /// <summary>字节数：进度估算、容量规划、读取后的长度校验都用它（0 = 跳过长度校验）。</summary>
        public long Size;

        /// <summary>
        /// true = AOT 补充元数据（HybridCLR 的 AOT dll），只装载、不参与 IHotfixEntry 扫描。
        /// JIT 平台（编辑器 / Android-Mono / Windows）用不到这类条目，留字段是为了同一份清单
        /// 在两种平台下都能读。
        /// </summary>
        public bool AotMetadata;

        /// <summary>依赖的其它程序集（同清单内的 Name）。装载顺序 = 拓扑序（被依赖者先）。</summary>
        public List<string> Dependencies;
    }

    /// <summary>
    /// 热更程序集清单：这一版有哪些热更程序集、各自的指纹、以及彼此的依赖。
    ///
    /// 与资源包清单（<see cref="YourFramework.Assets.Pack.PackageManifest"/>）同一条纪律：
    /// 清单是**构建期产物**，坏清单（重名、悬空依赖、成环、缺字段）一律 fail-fast 抛异常
    /// ——这是构建管线写错的 bug，不是运行期故障，不该拖到运行时才以"装载失败"的面目出现。
    ///
    /// 解析后建一张名字索引，运行期查找 O(1) 零分配；成环检测用 Kahn 拓扑排序一次性完成：
    /// 既报出环上的程序集名，又顺带证明清单可拓扑排序（装载顺序的前提）。
    /// </summary>
    public sealed class AssemblyManifest
    {
        /// <summary>清单缺 fileName 时的默认后缀。</summary>
        public const string DefaultFileSuffix = ".dll.bytes";

        private static readonly List<string> NoDependencies = new List<string>(0);

        private readonly List<HotAssemblyEntry> _assemblies = new List<HotAssemblyEntry>();
        private readonly Dictionary<string, HotAssemblyEntry> _byName =
            new Dictionary<string, HotAssemblyEntry>(StringComparer.Ordinal);

        private AssemblyManifest(string version)
        {
            Version = version;
        }

        /// <summary>清单版本号（与资源包版本对齐，便于诊断"这次热更到底下了哪一版"）。</summary>
        public string Version { get; private set; }

        /// <summary>全部程序集条目，清单原始顺序。</summary>
        public List<HotAssemblyEntry> Assemblies { get { return _assemblies; } }

        /// <summary>条目数。</summary>
        public int Count { get { return _assemblies.Count; } }

        /// <summary>全量字节总量（数字来自清单声明，不是实测）。</summary>
        public long TotalBytes { get; private set; }

        /// <summary>条目的依赖列表，永不返回 null（缺省字段 / 显式 null → 空列表）。</summary>
        public static List<string> DependenciesOf(HotAssemblyEntry entry)
        {
            return entry == null || entry.Dependencies == null ? NoDependencies : entry.Dependencies;
        }

        /// <summary>按逻辑名取条目；不存在返回 false。</summary>
        public bool TryGet(string name, out HotAssemblyEntry entry)
        {
            if (name == null)
            {
                entry = null;
                return false;
            }

            return _byName.TryGetValue(name, out entry);
        }

        /// <summary>
        /// Parses a manifest. Every structural defect throws
        /// <see cref="ArgumentException"/> with the offending identity in the
        /// message, because a bad manifest is a build artifact bug.
        /// </summary>
        public static AssemblyManifest LoadJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("AssemblyManifest: json must not be null or empty.", "json");
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("AssemblyManifest: invalid JSON: " + ex.Message, ex);
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                throw new ArgumentException("AssemblyManifest: root must be a JSON object.");
            }

            string version = RequiredString(root, "version", "(root)");
            JsonValue items = root["assemblies"];
            if (items == null || items.Kind != JsonKind.Array)
            {
                throw new ArgumentException("AssemblyManifest: (root) is missing an array 'assemblies'.");
            }

            AssemblyManifest manifest = new AssemblyManifest(version);
            for (int i = 0; i < items.Count; i++)
            {
                manifest.ReadAssembly(items[i], i);
            }

            manifest.VerifyDependenciesResolve();
            manifest.VerifyAcyclic();
            return manifest;
        }

        private void ReadAssembly(JsonValue item, int index)
        {
            string where = "assemblies[" + index + "]";
            if (item == null || item.Kind != JsonKind.Object)
            {
                throw new ArgumentException("AssemblyManifest: " + where + " must be a JSON object.");
            }

            HotAssemblyEntry entry = new HotAssemblyEntry();
            entry.Name = RequiredString(item, "name", where);
            entry.FileName = item["fileName"].AsNonEmptyString();
            if (entry.FileName == null)
            {
                entry.FileName = entry.Name + DefaultFileSuffix;
            }

            entry.Hash = item["hash"].AsNonEmptyString();

            double crc = item["crc"].AsDouble(0);
            if (crc < 0 || crc > 4294967295d)
            {
                throw new ArgumentException("AssemblyManifest: " + where + " has a crc outside uint32: " + crc);
            }

            entry.Crc = (uint)crc;

            double size = item["size"].AsDouble(0);
            if (size < 0)
            {
                throw new ArgumentException("AssemblyManifest: " + where + " has a negative size: " + size);
            }

            entry.Size = (long)size;
            entry.AotMetadata = item["aotMetadata"].AsBool(false);

            // 注意 JsonValue 的索引器语义：键不存在时返回的是 JsonValue.Null 单例，
            // 不是 C# 的 null。"没有这个字段"要按 Kind 判，不能按引用判。
            JsonValue dependencies = item["dependencies"];
            if (dependencies.Kind != JsonKind.Null)
            {
                if (dependencies.Kind != JsonKind.Array)
                {
                    throw new ArgumentException(
                        "AssemblyManifest: " + where + ".dependencies must be an array.");
                }

                entry.Dependencies = new List<string>(dependencies.Count);
                for (int d = 0; d < dependencies.Count; d++)
                {
                    string name = dependencies[d].AsNonEmptyString();
                    if (name == null)
                    {
                        throw new ArgumentException(
                            "AssemblyManifest: " + where + ".dependencies[" + d + "] must be a non-empty string.");
                    }

                    entry.Dependencies.Add(name);
                }
            }

            if (_byName.ContainsKey(entry.Name))
            {
                throw new ArgumentException(
                    "AssemblyManifest: duplicate assembly name '" + entry.Name + "'.");
            }

            _byName.Add(entry.Name, entry);
            _assemblies.Add(entry);
            TotalBytes += entry.Size;
        }

        private void VerifyDependenciesResolve()
        {
            for (int i = 0; i < _assemblies.Count; i++)
            {
                HotAssemblyEntry entry = _assemblies[i];
                List<string> dependencies = DependenciesOf(entry);
                for (int d = 0; d < dependencies.Count; d++)
                {
                    if (!_byName.ContainsKey(dependencies[d]))
                    {
                        throw new ArgumentException(
                            "AssemblyManifest: assembly '" + entry.Name
                            + "' depends on unknown assembly '" + dependencies[d] + "'.");
                    }

                    if (string.Equals(dependencies[d], entry.Name, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "AssemblyManifest: assembly '" + entry.Name + "' depends on itself.");
                    }
                }
            }
        }

        /// <summary>
        /// Kahn 拓扑排序：能出完 = 无环；出不完 = 成环，报出环上的程序集名（供人排查清单）。
        /// </summary>
        private void VerifyAcyclic()
        {
            Dictionary<string, int> inDegree =
                new Dictionary<string, int>(_assemblies.Count, StringComparer.Ordinal);
            Dictionary<string, List<string>> dependents =
                new Dictionary<string, List<string>>(_assemblies.Count, StringComparer.Ordinal);

            for (int i = 0; i < _assemblies.Count; i++)
            {
                List<string> dependencies = DependenciesOf(_assemblies[i]);
                inDegree[_assemblies[i].Name] = dependencies.Count;
                if (!dependents.ContainsKey(_assemblies[i].Name))
                {
                    dependents.Add(_assemblies[i].Name, new List<string>(0));
                }

                for (int d = 0; d < dependencies.Count; d++)
                {
                    List<string> list;
                    if (!dependents.TryGetValue(dependencies[d], out list))
                    {
                        list = new List<string>(1);
                        dependents.Add(dependencies[d], list);
                    }

                    list.Add(_assemblies[i].Name);
                }
            }

            List<string> queue = new List<string>(_assemblies.Count);
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

            if (emitted == _assemblies.Count)
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
                "AssemblyManifest: dependency cycle among assemblies [" + string.Join(", ", stuck.ToArray()) + "].");
        }

        private static string RequiredString(JsonValue owner, string key, string where)
        {
            string text = owner[key].AsNonEmptyString();
            if (text == null)
            {
                throw new ArgumentException(
                    "AssemblyManifest: " + where + " is missing a non-empty string '" + key + "'.");
            }

            return text;
        }
    }

    /// <summary>
    /// 装载顺序解析：给定"想装载哪几个"（root），把它们的依赖闭包按**拓扑序**填进调用方给的
    /// 列表（被依赖者在前，root 在后）。
    ///
    /// 复用姿势与 <see cref="YourFramework.Assets.Pack.DependencyResolver"/> 一致：
    /// 1. 填调用方列表、内部暂存复用 —— 解析本身在首次调用后零分配；
    /// 2. 显式栈做后序 DFS，不用递归 —— 依赖链深度由工程规模决定，不该由调用栈决定；
    /// 3. 用 0/1/2 三态标记而不是"见过就算"，所以**手工构造的清单成环也能报出来**
    ///    （LoadJson 已在解析期拒绝成环，这里是给手搓清单兜底）；
    /// 4. 单线程使用（泵、主线程）。
    /// </summary>
    public sealed class AssemblyLoadOrder
    {
        private const int Unvisited = 0;
        private const int InProgress = 1;
        private const int Emitted = 2;

        private readonly Dictionary<string, int> _state = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _stack = new List<string>();
        private readonly List<int> _stackDepIndex = new List<int>();

        /// <summary>
        /// Resolves the load order of the requested assemblies plus their dependency
        /// closure, dependencies first. Throws (build-time bug, deliberately loud) when a
        /// requested name is unknown, a dependency dangles, or the manifest is cyclic.
        /// Leaves <paramref name="into"/> empty on the throwing paths.
        /// </summary>
        public void Resolve(AssemblyManifest manifest, IList<string> roots, List<string> into)
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
                    "AssemblyLoadOrder.Resolve: roots must not be empty; a reload stage with no "
                    + "assemblies is a wiring bug, not an empty hot update.", "roots");
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
                        "AssemblyLoadOrder.Resolve: roots[" + i + "] must be a non-empty assembly name.", "roots");
                }

                HotAssemblyEntry entry;
                if (!manifest.TryGet(root, out entry))
                {
                    into.Clear();
                    throw new ArgumentException(
                        "AssemblyLoadOrder.Resolve: unknown assembly '" + root + "'.");
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

        private void Drain(AssemblyManifest manifest, List<string> into)
        {
            while (_stack.Count > 0)
            {
                int top = _stack.Count - 1;
                HotAssemblyEntry entry;
                manifest.TryGet(_stack[top], out entry);
                List<string> dependencies = AssemblyManifest.DependenciesOf(entry);

                if (_stackDepIndex[top] < dependencies.Count)
                {
                    string dependency = dependencies[_stackDepIndex[top]];
                    _stackDepIndex[top] = _stackDepIndex[top] + 1;

                    HotAssemblyEntry child;
                    if (!manifest.TryGet(dependency, out child))
                    {
                        into.Clear();
                        throw new ArgumentException(
                            "AssemblyLoadOrder.Resolve: assembly '" + entry.Name
                            + "' depends on unknown assembly '" + dependency + "'.");
                    }

                    int state;
                    if (!_state.TryGetValue(dependency, out state))
                    {
                        state = Unvisited;
                    }

                    if (state == InProgress)
                    {
                        into.Clear();
                        throw new ArgumentException(
                            "AssemblyLoadOrder.Resolve: dependency cycle reached at '" + dependency
                            + "' (from '" + entry.Name + "').");
                    }

                    if (state == Unvisited)
                    {
                        _state[dependency] = InProgress;
                        _stack.Add(dependency);
                        _stackDepIndex.Add(0);
                    }

                    continue;
                }

                // Every dependency of this frame is emitted already: this is the
                // post-order position, i.e. dependencies before dependents.
                _state[entry.Name] = Emitted;
                into.Add(entry.Name);
                _stack.RemoveAt(top);
                _stackDepIndex.RemoveAt(top);
            }
        }

        /// <summary>Sum of the declared sizes of the given assemblies (progress estimation).</summary>
        public static long SumBytes(AssemblyManifest manifest, List<string> names)
        {
            if (manifest == null || names == null)
            {
                return 0;
            }

            long total = 0;
            for (int i = 0; i < names.Count; i++)
            {
                HotAssemblyEntry entry;
                if (manifest.TryGet(names[i], out entry))
                {
                    total += entry.Size;
                }
            }

            return total;
        }
    }
}
