using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using YourFramework.Assets.Pack;

namespace YourFramework.HotUpdate
{
    /// <summary>程序集重载阶段的状态。</summary>
    public enum AssemblyReloadState
    {
        /// <summary>还没 Begin。</summary>
        Idle,
        /// <summary>正在逐帧装载。</summary>
        Loading,
        /// <summary>全部装载完成，入口点已执行（或本阶段没有入口点）。</summary>
        Done,
        /// <summary>失败；原因是权威的，UI 有话可说。</summary>
        Failed
    }

    /// <summary>
    /// 程序集字节的来源。引擎边界：默认实现读本地文件（<see cref="FileAssemblyBytesSource"/>），
    /// 想把 dll 塞进加密包 / 从内存流解压的宿主换一个实现即可。
    ///
    /// 失败是值：读不到就返回 false + 原因，不抛异常 —— 磁盘 IO 与网络 IO 同级，都是运行期故障。
    /// </summary>
    public interface IAssemblyBytesSource
    {
        /// <summary>
        /// Reads one assembly image. Returns false with a reason on failure; never throws
        /// for content (a null <paramref name="error"/> with true on success).
        /// </summary>
        bool TryRead(string fileName, out byte[] bytes, out string error);
    }

    /// <summary>
    /// 默认字节来源：缓存目录下的相对路径。
    ///
    /// 路径纪律（清单是从网上来的，文件名不可全信）：拒绝绝对路径、拒绝任何一段 ".."，
    /// 一个被篡改的清单不能借此读到缓存目录之外的任意文件。
    /// </summary>
    public sealed class FileAssemblyBytesSource : IAssemblyBytesSource
    {
        /// <summary>Builds a source rooted at a cache directory. A null/empty root is a wiring bug.</summary>
        public FileAssemblyBytesSource(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException(
                    "FileAssemblyBytesSource: rootDirectory must not be null or empty.", "rootDirectory");
            }

            RootDirectory = rootDirectory;
        }

        /// <summary>缓存目录（清单里的 FileName 相对它解析）。</summary>
        public string RootDirectory { get; private set; }

        /// <summary>Reads one assembly image from the cache directory.</summary>
        public bool TryRead(string fileName, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;

            if (string.IsNullOrEmpty(fileName))
            {
                error = "assembly file name must not be empty";
                return false;
            }

            if (Path.IsPathRooted(fileName) || HasParentSegment(fileName))
            {
                error = "assembly file name '" + fileName
                    + "' must be a relative path without '..' (a manifest is not allowed to "
                    + "escape the cache directory)";
                return false;
            }

            string path = Path.Combine(RootDirectory, fileName);
            if (!File.Exists(path))
            {
                error = "assembly file not found: " + path;
                return false;
            }

            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                bytes = null;
                error = "reading '" + path + "' failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            return true;
        }

        private static bool HasParentSegment(string path)
        {
            string normalized = path.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "..")
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 程序集装载器：字节 → Assembly。引擎边界，也是平台边界。
    ///
    /// 默认实现 <see cref="ByteArrayAssemblyLoader"/> 走 <see cref="Assembly.Load(byte[])"/>，
    /// 这在 JIT 平台（编辑器 / Android-Mono / Windows 桌面）上是原生支持的；IL2CPP 上会抛
    /// <c>PlatformNotSupportedException</c> —— 它被本实现折算成"失败 + 原因"，而不是崩溃。
    /// IL2CPP 要热更代码，换 HybridCLR 的实现塞进这个插槽，上层一个字节都不用改。
    /// </summary>
    public interface IAssemblyLoader
    {
        /// <summary>
        /// Loads a raw assembly image. Returns null with a reason on failure; never throws
        /// for content (a null <paramref name="error"/> with a non-null assembly on success).
        /// </summary>
        Assembly Load(byte[] raw, string name, out string error);
    }

    /// <summary>默认装载器：<see cref="Assembly.Load(byte[])"/>，JIT 平台直接可用。</summary>
    public sealed class ByteArrayAssemblyLoader : IAssemblyLoader
    {
        /// <summary>Loads a raw assembly image from memory.</summary>
        public Assembly Load(byte[] raw, string name, out string error)
        {
            error = null;
            if (raw == null || raw.Length == 0)
            {
                error = "assembly '" + (name ?? "?") + "' image is empty";
                return null;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(raw);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            if (assembly == null)
            {
                error = "loader returned null for '" + (name ?? "?") + "'";
                return null;
            }

            return assembly;
        }
    }

    /// <summary>
    /// 程序集重载：热更流水线的最后一个阶段，插进 <see cref="IHotUpdateStep"/> 插槽。
    ///
    /// 每帧只处理一个程序集（有界工作量，跟资源包按拓扑序装 bundle 同一形状），顺序由
    /// <see cref="AssemblyManifest"/> 的依赖决定（被依赖者先）。
    ///
    /// **回滚点在哪里**：.NET 无法从默认 ALC 卸载已装载的程序集，所以"回滚"不可能指
    /// "把装载过的退回去"。真正有意义的回滚点是**入口点执行之前** —— 入口点是热更真正
    /// 生效的唯一时刻，装载期（读 / 校验 / Load）任何一步失败，一个入口都没跑，游戏仍旧
    /// 跑在旧代码上，这就是一次干净的整批回滚（<see cref="RolledBack"/> 为 true）。
    /// 入口期失败则如实汇报（<see cref="EntriesAttempted"/> + 逐条原因），不假装回滚过。
    ///
    /// 计划非法（清单坏、root 名写错、依赖成环）沿用清单层的 fail-fast，但在本阶段被折算成
    /// "失败 + 带 'build-time bug' 前缀的原因"：既守住 IHotUpdateStep "不向流水线抛异常"的
    /// 合约，又不让构建期 bug 伪装成运行期故障。
    /// </summary>
    public sealed class AssemblyReloadStep : IHotUpdateStep, IHotUpdateStepFailure
    {
        private readonly string _name;
        private readonly AssemblyManifest _manifest;
        private readonly List<string> _roots = new List<string>();
        private readonly IAssemblyBytesSource _source;
        private readonly IAssemblyLoader _loader;
        private readonly HotfixEntryRunner _entries;
        private readonly AssemblyLoadOrder _order = new AssemblyLoadOrder();

        private readonly List<string> _plan = new List<string>();
        private readonly List<string> _loadedNames = new List<string>();
        private readonly List<Assembly> _loaded = new List<Assembly>();
        private readonly List<string> _entryProblems = new List<string>();
        private readonly List<string> _entryFailures = new List<string>();

        private int _index;
        private int _entriesAttempted;
        private AssemblyReloadState _state = AssemblyReloadState.Idle;
        private string _failureStage;
        private string _failureReason;
        private bool _rolledBack;

        /// <summary>Builds the stage without hotfix entry points (assemblies still get loaded).</summary>
        public AssemblyReloadStep(
            AssemblyManifest manifest,
            IList<string> roots,
            IAssemblyBytesSource source,
            IAssemblyLoader loader)
            : this("ReloadAssemblies", manifest, roots, source, loader, null)
        {
        }

        /// <summary>
        /// Builds the stage. <paramref name="entries"/> may be null (no entry point scan);
        /// a null manifest / source / loader / empty roots is a wiring bug and throws here,
        /// where it is still traceable to the caller.
        /// </summary>
        public AssemblyReloadStep(
            string name,
            AssemblyManifest manifest,
            IList<string> roots,
            IAssemblyBytesSource source,
            IAssemblyLoader loader,
            HotfixEntryRunner entries)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("AssemblyReloadStep: name must not be empty.", "name");
            }

            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            if (roots == null || roots.Count == 0)
            {
                throw new ArgumentException("AssemblyReloadStep: roots must not be empty.", "roots");
            }

            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (loader == null)
            {
                throw new ArgumentNullException("loader");
            }

            _name = name;
            _manifest = manifest;
            for (int i = 0; i < roots.Count; i++)
            {
                _roots.Add(roots[i]);
            }

            _source = source;
            _loader = loader;
            _entries = entries;
        }

        /// <summary>Stage name shown by <see cref="HotUpdateFlow"/>.</summary>
        public string Name { get { return _name; } }

        /// <summary>Current state.</summary>
        public AssemblyReloadState State { get { return _state; } }

        /// <summary>Which step of the stage failed: plan / fetch / integrity / load / entry.</summary>
        public string FailureStage { get { return _failureStage; } }

        /// <summary>
        /// The authoritative reason for a failure (surfaced through
        /// <see cref="IHotUpdateStepFailure"/> so <see cref="HotUpdateFlow"/> reports
        /// something a player can act on rather than "step failed").
        /// </summary>
        public string FailureReason { get { return _failureReason; } }

        /// <summary>装载顺序（依赖在前），Begin 之后可读（诊断用）。</summary>
        public IList<string> LoadOrder { get { return _plan; } }

        /// <summary>已成功装载的程序集名，顺序与 <see cref="LoadOrder"/> 一致。</summary>
        public IList<string> LoadedNames { get { return _loadedNames; } }

        /// <summary>已成功装载的程序集。</summary>
        public IList<Assembly> LoadedAssemblies { get { return _loaded; } }

        /// <summary>已成功装载的数量。</summary>
        public int LoadedCount { get { return _loaded.Count; } }

        /// <summary>入口点扫描到的问题（类型没无参构造等）。</summary>
        public IList<string> EntryProblems { get { return _entryProblems; } }

        /// <summary>入口点执行失败的原因，逐条。</summary>
        public IList<string> EntryFailures { get { return _entryFailures; } }

        /// <summary>实际调用过的入口点数量。</summary>
        public int EntriesAttempted { get { return _entriesAttempted; } }

        /// <summary>
        /// true = 失败发生在任何入口点执行之前，旧代码仍在生效，整批热更等于没发生。
        /// false = 已经有入口点跑过了，装载无法撤销 —— 此时的失败必须如实上报。
        /// </summary>
        public bool RolledBack { get { return _rolledBack; } }

        /// <summary>Clears state so the stage can run again (a retry after fixing the cause).</summary>
        public void Reset()
        {
            _plan.Clear();
            _loadedNames.Clear();
            _loaded.Clear();
            _entryProblems.Clear();
            _entryFailures.Clear();
            _index = 0;
            _entriesAttempted = 0;
            _state = AssemblyReloadState.Idle;
            _failureStage = null;
            _failureReason = null;
            _rolledBack = false;
            if (_entries != null)
            {
                _entries.Clear();
            }
        }

        /// <summary>Resolves the load plan. Idempotent: calling it again starts a fresh attempt.</summary>
        public void Begin(HotUpdateContext context)
        {
            Reset();
            try
            {
                _order.Resolve(_manifest, _roots, _plan);
            }
            catch (Exception ex)
            {
                _state = AssemblyReloadState.Failed;
                _failureStage = "plan";
                _rolledBack = true;
                _failureReason = "assembly reload plan is invalid (build-time bug): " + ex.Message;
                return;
            }

            _state = AssemblyReloadState.Loading;
        }

        /// <summary>Advances one assembly per frame; runs the hotfix entry points at the end.</summary>
        public HotUpdateStepState Pump()
        {
            if (_state != AssemblyReloadState.Loading)
            {
                return ToStepState(_state);
            }

            try
            {
                if (_index < _plan.Count)
                {
                    LoadCurrent();
                    if (_state != AssemblyReloadState.Loading)
                    {
                        return HotUpdateStepState.Failed;
                    }
                }

                if (_index >= _plan.Count)
                {
                    RunEntries();
                    return ToStepState(_state);
                }
            }
            catch (Exception ex)
            {
                _state = AssemblyReloadState.Failed;
                _failureStage = "unexpected";
                _rolledBack = _entriesAttempted == 0;
                _failureReason = "unexpected " + ex.GetType().Name + ": " + ex.Message;
                return HotUpdateStepState.Failed;
            }

            return HotUpdateStepState.Running;
        }

        private void LoadCurrent()
        {
            string name = _plan[_index];
            HotAssemblyEntry entry;
            if (!_manifest.TryGet(name, out entry))
            {
                Fail("plan", "assembly '" + name + "' vanished from the manifest between planning and loading");
                return;
            }

            byte[] raw;
            string error;
            if (!_source.TryRead(entry.FileName, out raw, out error))
            {
                Fail("fetch", "assembly '" + name + "' could not be read (" + entry.FileName + "): " + error);
                return;
            }

            if (entry.Size > 0 && raw.Length != entry.Size)
            {
                Fail("integrity", "assembly '" + name + "' is " + raw.Length
                    + " bytes but the manifest declares " + entry.Size
                    + " (truncated download, or a manifest that does not match the payload)");
                return;
            }

            if (entry.Crc != 0)
            {
                uint actual = Crc32.Compute(raw);
                if (actual != entry.Crc)
                {
                    Fail("integrity", "assembly '" + name + "' crc32 is 0x" + actual.ToString("X8")
                        + " but the manifest declares 0x" + entry.Crc.ToString("X8")
                        + " (the bytes on disk are not the bytes that were published)");
                    return;
                }
            }

            Assembly assembly = _loader.Load(raw, name, out error);
            if (assembly == null)
            {
                Fail("load", "assembly '" + name + "' was rejected by the loader: " + error);
                return;
            }

            _loaded.Add(assembly);
            _loadedNames.Add(name);
            _index++;
        }

        private void RunEntries()
        {
            if (_entries == null)
            {
                _state = AssemblyReloadState.Done;
                return;
            }

            _entries.Clear();
            _entryProblems.Clear();
            _entryFailures.Clear();

            for (int i = 0; i < _loaded.Count; i++)
            {
                _entries.Collect(_loaded[i], _entryProblems);
            }

            _entries.Sort();
            _entriesAttempted = _entries.RunAll(_entryFailures);

            if (_entryProblems.Count == 0 && _entryFailures.Count == 0)
            {
                _state = AssemblyReloadState.Done;
                return;
            }

            _state = AssemblyReloadState.Failed;
            _failureStage = _entryProblems.Count > 0 ? "entry-scan" : "entry";
            _rolledBack = false;

            string reason = string.Empty;
            if (_entryProblems.Count > 0)
            {
                reason = _entryProblems.Count + " hotfix entry type(s) are unusable (build-time bug): "
                    + string.Join(" | ", _entryProblems.ToArray());
            }

            if (_entryFailures.Count > 0)
            {
                if (reason.Length > 0)
                {
                    reason = reason + "; ";
                }

                reason = reason + _entryFailures.Count + " hotfix entry point(s) threw after "
                    + _entriesAttempted + " were attempted: "
                    + string.Join(" | ", _entryFailures.ToArray());
            }

            _failureReason = reason;
        }

        private void Fail(string stage, string reason)
        {
            _state = AssemblyReloadState.Failed;
            _failureStage = stage;
            _rolledBack = _entriesAttempted == 0;
            _failureReason = reason;
        }

        private static HotUpdateStepState ToStepState(AssemblyReloadState state)
        {
            if (state == AssemblyReloadState.Done)
            {
                return HotUpdateStepState.Done;
            }

            if (state == AssemblyReloadState.Failed)
            {
                return HotUpdateStepState.Failed;
            }

            return HotUpdateStepState.Running;
        }
    }
}
