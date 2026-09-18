using System;
using System.Collections.Generic;
using YourFramework.HotUpdate;

namespace YourFramework.Scripting
{
    /// <summary>脚本补丁阶段的状态。</summary>
    public enum ScriptPatchState
    {
        /// <summary>还没 Begin。</summary>
        Idle,
        /// <summary>正在逐块装载。</summary>
        Loading,
        /// <summary>全部装载完成，入口点已执行（或本阶段没有入口点）。</summary>
        Done,
        /// <summary>失败；原因是权威的，UI 有话可说。</summary>
        Failed
    }

    /// <summary>
    /// 脚本补丁阶段：热更流水线里「把这一版 .lua 块装进运行时并调入口」的那一步，
    /// 插进 <see cref="IHotUpdateStep"/> 插槽。
    ///
    /// **这是 IL2CPP 上热更代码的落脚点。** 程序集重载在 IL2CPP 上被平台拒绝
    /// （<c>Assembly.Load(byte[])</c> 抛 PlatformNotSupportedException），而解释执行的代码块
    /// 不受这条限制。于是两个阶段是同构的：同一份清单纪律、同一套阶段、同一副失败时的处理，
    /// 只是换了「装什么」。
    ///
    /// 每帧只处理一块（有界工作量），顺序由 <see cref="ScriptManifest"/> 的依赖决定（被依赖者先）。
    ///
    /// **回滚点在哪里 —— 这里比程序集热更硬气一点**：.NET 无法卸载已装载的程序集，而 Lua 能卸载代码块。
    /// 所以装载期（读 / 校验 / 装）任何一步失败，本阶段会把**本次已装载的块反序卸掉**，真的回到
    /// 补丁前的状态（<see cref="RolledBack"/> 为 true，前提是卸载全部成功；卸载失败会如实写进
    /// <see cref="UnloadFailures"/> 并让 RolledBack 变 false —— 不假装干净）。
    /// 入口点一旦跑过就不再卸载：补丁的函数已经被业务代码引用，卸掉只是把「代码块不在表里」
    /// 变成一句废话，此时如实汇报（<see cref="EntriesAttempted"/> + 逐条原因）。
    ///
    /// 计划非法（清单坏、root 名写错、依赖成环）沿用清单层的 fail-fast，但在本阶段被折算成
    /// 「失败 + 带 'build-time bug' 前缀的原因」：既守住 IHotUpdateStep「不向流水线抛异常」的合约，
    /// 又不让构建期 bug 伪装成运行期故障。
    /// </summary>
    public sealed class ScriptPatchStep : IHotUpdateStep, IHotUpdateStepFailure
    {
        private readonly string _name;
        private readonly ScriptManifest _manifest;
        private readonly List<string> _roots = new List<string>();
        private readonly IScriptChunkSource _source;
        private readonly IScriptRuntime _runtime;
        private readonly ScriptLoadOrder _order = new ScriptLoadOrder();

        private readonly List<string> _plan = new List<string>();
        private readonly List<string> _loadedNames = new List<string>();
        private readonly List<string> _unloadedNames = new List<string>();
        private readonly List<string> _unloadFailures = new List<string>();
        private readonly List<string> _skipped = new List<string>();
        private readonly List<string> _entryFailures = new List<string>();

        private int _index;
        private int _entriesAttempted;
        private ScriptPatchState _state = ScriptPatchState.Idle;
        private string _failureStage;
        private string _failureReason;
        private bool _rolledBack;

        /// <summary>Builds the stage with the default name.</summary>
        public ScriptPatchStep(
            ScriptManifest manifest,
            IList<string> roots,
            IScriptChunkSource source,
            IScriptRuntime runtime)
            : this("ApplyScriptPatch", manifest, roots, source, runtime)
        {
        }

        /// <summary>
        /// Builds the stage. A null manifest / source / runtime / empty roots is a wiring bug and
        /// throws here, where it is still traceable to the caller.
        /// </summary>
        public ScriptPatchStep(
            string name,
            ScriptManifest manifest,
            IList<string> roots,
            IScriptChunkSource source,
            IScriptRuntime runtime)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("ScriptPatchStep: name must not be empty.", "name");
            }

            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            if (roots == null || roots.Count == 0)
            {
                throw new ArgumentException("ScriptPatchStep: roots must not be empty.", "roots");
            }

            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (runtime == null)
            {
                throw new ArgumentNullException("runtime");
            }

            _name = name;
            _manifest = manifest;
            for (int i = 0; i < roots.Count; i++)
            {
                _roots.Add(roots[i]);
            }

            _source = source;
            _runtime = runtime;
        }

        /// <summary>Stage name shown by <see cref="HotUpdateFlow"/>.</summary>
        public string Name { get { return _name; } }

        /// <summary>Current state.</summary>
        public ScriptPatchState State { get { return _state; } }

        /// <summary>Which step of the stage failed: plan / fetch / integrity / load / entry / unexpected.</summary>
        public string FailureStage { get { return _failureStage; } }

        /// <summary>
        /// The authoritative reason for a failure (surfaced through
        /// <see cref="IHotUpdateStepFailure"/> so <see cref="HotUpdateFlow"/> reports something an
        /// operator can act on rather than "step failed").
        /// </summary>
        public string FailureReason { get { return _failureReason; } }

        /// <summary>装载顺序（依赖在前），Begin 之后可读（诊断用）。</summary>
        public IList<string> LoadOrder { get { return _plan; } }

        /// <summary>已成功装载的块名，顺序与 <see cref="LoadOrder"/> 一致。</summary>
        public IList<string> LoadedNames { get { return _loadedNames; } }

        /// <summary>已成功装载的数量。</summary>
        public int LoadedCount { get { return _loadedNames.Count; } }

        /// <summary>回滚时成功卸下的块名（反序）。</summary>
        public IList<string> UnloadedNames { get { return _unloadedNames; } }

        /// <summary>回滚时卸不掉的块及其原因 —— 非空即说明「干净回滚」不成立，要如实上报。</summary>
        public IList<string> UnloadFailures { get { return _unloadFailures; } }

        /// <summary>
        /// best-effort 块（required=false）装不上时的记录。**非空就说明这一版补丁只生效了一部分** ——
        /// 阶段算成功，但这件事必须能被看到，所以它是一条独立的信息，不是一个沉默的 continue。
        /// </summary>
        public IList<string> Skipped { get { return _skipped; } }

        /// <summary>入口点执行失败的原因，逐条。</summary>
        public IList<string> EntryFailures { get { return _entryFailures; } }

        /// <summary>实际调用过的入口点数量。</summary>
        public int EntriesAttempted { get { return _entriesAttempted; } }

        /// <summary>
        /// true = 失败发生在任何入口点执行之前，且本次装载的块已全部卸掉 —— 这一次热更等于没发生。
        /// false = 已经有入口点跑过（或卸载没卸干净），此时的失败必须如实上报。
        /// </summary>
        public bool RolledBack { get { return _rolledBack; } }

        /// <summary>Clears state so the stage can run again (a retry after fixing the cause).</summary>
        public void Reset()
        {
            _plan.Clear();
            _loadedNames.Clear();
            _unloadedNames.Clear();
            _unloadFailures.Clear();
            _skipped.Clear();
            _entryFailures.Clear();
            _index = 0;
            _entriesAttempted = 0;
            _state = ScriptPatchState.Idle;
            _failureStage = null;
            _failureReason = null;
            _rolledBack = false;
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
                _state = ScriptPatchState.Failed;
                _failureStage = "plan";
                _rolledBack = true;
                _failureReason = "script patch plan is invalid (build-time bug): " + ex.Message;
                return;
            }

            _state = ScriptPatchState.Loading;
        }

        /// <summary>Advances one chunk per frame; runs the entry points at the end.</summary>
        public HotUpdateStepState Pump()
        {
            if (_state != ScriptPatchState.Loading)
            {
                return ToStepState(_state);
            }

            try
            {
                if (_index < _plan.Count)
                {
                    LoadCurrent();
                    if (_state != ScriptPatchState.Loading)
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
                string unexpected = "unexpected " + ex.GetType().Name + ": " + ex.Message;
                if (_entriesAttempted > 0)
                {
                    // 补丁的函数已经跑过：卸载救不回来，如实汇报。
                    _state = ScriptPatchState.Failed;
                    _failureStage = "unexpected";
                    _rolledBack = false;
                    _failureReason = unexpected;
                }
                else
                {
                    Fail("unexpected", unexpected);
                }

                return HotUpdateStepState.Failed;
            }

            return HotUpdateStepState.Running;
        }

        private void LoadCurrent()
        {
            string name = _plan[_index];
            ScriptPatchEntry entry;
            if (!_manifest.TryGet(name, out entry))
            {
                Fail("plan", "chunk '" + name + "' vanished from the manifest between planning and loading");
                return;
            }

            string stage;
            string reason;
            if (TryApplyChunk(entry, out stage, out reason))
            {
                _loadedNames.Add(name);
                _index++;
                return;
            }

            if (entry.Required)
            {
                Fail(stage, reason);
                return;
            }

            _skipped.Add(name + ": " + reason);
            _index++;
        }

        /// <summary>
        /// 读一块并装进运行时。三步各自的失败原因都带上块名与文件名 —— 补丁装不上时，
        /// 「哪一块、哪个文件、哪一步」是排查需要的全部信息。
        /// </summary>
        private bool TryApplyChunk(ScriptPatchEntry entry, out string stage, out string reason)
        {
            stage = "fetch";
            reason = null;

            string text;
            string error;
            if (!_source.TryReadText(entry.FileName, out text, out error))
            {
                reason = "chunk '" + entry.Name + "' could not be read (" + entry.FileName + "): " + error;
                return false;
            }

            stage = "integrity";
            if (entry.Size > 0 && text.Length != entry.Size)
            {
                reason = "chunk '" + entry.Name + "' is " + text.Length + " characters but the manifest declares "
                    + entry.Size + " (truncated download, or a manifest that does not match the payload)";
                return false;
            }

            ScriptChunk chunk = new ScriptChunk(entry.Name, text, entry.Kind, _source.Name + ":" + entry.FileName);

            if (entry.Crc != 0 && chunk.Crc != entry.Crc)
            {
                reason = "chunk '" + entry.Name + "' crc32 is " + ScriptChunk.ToHex(chunk.Crc)
                    + " but the manifest declares " + ScriptChunk.ToHex(entry.Crc)
                    + " (the text on disk is not the text that was published)";
                return false;
            }

            stage = "load";
            ScriptOutcome outcome = _runtime.LoadChunk(chunk);
            if (!outcome.Succeeded)
            {
                reason = "chunk '" + entry.Name + "' was rejected by the runtime (" + _runtime.Name + "): " + outcome.Error;
                return false;
            }

            stage = null;
            return true;
        }

        private void RunEntries()
        {
            _entryFailures.Clear();

            for (int i = 0; i < _loadedNames.Count; i++)
            {
                ScriptPatchEntry entry;
                _manifest.TryGet(_loadedNames[i], out entry);
                List<string> entries = ScriptManifest.EntriesOf(entry);
                for (int e = 0; e < entries.Count; e++)
                {
                    _entriesAttempted++;
                    ScriptOutcome outcome = _runtime.CallEntry(entries[e]);
                    if (!outcome.Succeeded)
                    {
                        _entryFailures.Add("chunk '" + _loadedNames[i] + "' entry '" + entries[e]
                            + "' failed: " + outcome.Error);
                    }
                }
            }

            if (_entryFailures.Count == 0)
            {
                _state = ScriptPatchState.Done;
                return;
            }

            Fail("entry", _entryFailures.Count + " script entry point(s) threw after "
                + _entriesAttempted + " were attempted: " + string.Join(" | ", _entryFailures.ToArray()));
        }

        private void Fail(string stage, string reason)
        {
            _state = ScriptPatchState.Failed;
            _failureStage = stage;

            if (stage == "entry")
            {
                // 入口跑过了：卸载只是自欺欺人，如实汇报。
                _rolledBack = false;
                _failureReason = reason;
                return;
            }

            RollbackLoaded();
            _failureReason = _unloadFailures.Count == 0
                ? reason
                : reason + "; rollback incomplete: " + string.Join(" | ", _unloadFailures.ToArray());
        }

        /// <summary>
        /// 反序卸掉本次装载的块。反序不是讲究：先装的往往是被依赖者，后装的可能引用了它，
        /// 从后往前卸能把「卸到一半发现依赖没了」这种窗口压到最小。
        /// </summary>
        private void RollbackLoaded()
        {
            _unloadedNames.Clear();
            _unloadFailures.Clear();

            for (int i = _loadedNames.Count - 1; i >= 0; i--)
            {
                string name = _loadedNames[i];
                ScriptOutcome outcome = _runtime.UnloadChunk(name);
                if (outcome.Succeeded)
                {
                    _unloadedNames.Add(name);
                }
                else
                {
                    _unloadFailures.Add("chunk '" + name + "' could not be unloaded: " + outcome.Error);
                }
            }

            _loadedNames.Clear();
            _rolledBack = _unloadFailures.Count == 0;
        }

        private static HotUpdateStepState ToStepState(ScriptPatchState state)
        {
            if (state == ScriptPatchState.Done)
            {
                return HotUpdateStepState.Done;
            }

            if (state == ScriptPatchState.Failed)
            {
                return HotUpdateStepState.Failed;
            }

            return HotUpdateStepState.Running;
        }
    }
}
