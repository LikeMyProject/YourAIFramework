#if YOURAI_HYBRIDCLR
using System;
using System.Collections.Generic;
using YourFramework.HotUpdate;

namespace YourFramework.UnityRuntime.HotUpdate
{
    /// <summary>
    /// HybridCLR 程序集重载步骤（引入不自研）：把元数据补全 + 热更程序集加载
    /// 包成 IHotUpdateStep 插进 HotUpdateFlow。执行前提：YooAsset 步骤已把
    /// 热更 DLL 以字节形式备好（宿主注入的加载器负责拿字节 —— 通常接
    /// AssetManager 的轮询包装）。
    ///
    /// 编译边界：整文件在 YOURAI_HYBRIDCLR 符号内，由
    /// YourFramework.UnityRuntime.asmdef 的 versionDefines 在宿主装了
    /// com.code-philosophy.hybridclr（1.12+）时自动定义 —— 与 YooAsset /
    /// Unity Localization 适配同一纪律。
    ///
    /// 顺序约定（context 键，宿主在 Run 前 Set）：
    /// - "hotUpdateAssemblies"：IList&lt;string&gt;，热更 DLL 名单（按依赖序）；
    /// - "aotMetadataAssemblies"：IList&lt;string&gt;，AOT 补元数据名单（可选）；
    /// 步骤内部先补 AOT 元数据（HybridCLR 的硬前提），再逐个加载热更程序集；
    /// 加载器见构造注入。
    /// </summary>
    public sealed class HybridClrReloadStep : IHotUpdateStep
    {
        private readonly Func<string, byte[]> _assemblyLoader;
        private IList<string> _hotUpdateAssemblies;
        private IList<string> _aotMetadataAssemblies;
        private int _metadataIndex;
        private int _assemblyIndex;
        private int _phase; // 0 = metadata, 1 = hot-update assemblies
        private HybridCLRLoadedAssemblies _loaded;

        /// <summary>
        /// Builds the step. The loader translates an assembly name to its bytes
        /// (already downloaded to a warm location by earlier steps); returning
        /// null fails the step with an honest reason.
        /// </summary>
        public HybridClrReloadStep(Func<string, byte[]> assemblyLoader)
        {
            if (assemblyLoader == null)
            {
                throw new ArgumentNullException("assemblyLoader");
            }

            _assemblyLoader = assemblyLoader;
        }

        /// <summary>Stage name.</summary>
        public string Name { get { return "ReloadAssemblies"; } }

        /// <summary>Snapshot of what got loaded (filled as the step runs).</summary>
        public HybridCLRLoadedAssemblies Loaded { get { return _loaded; } }

        /// <summary>Reads the assembly lists from the context and resets progress.</summary>
        public void Begin(HotUpdateContext context)
        {
            _hotUpdateAssemblies = context.Get<IList<string>>("hotUpdateAssemblies");
            _aotMetadataAssemblies = context.Get<IList<string>>("aotMetadataAssemblies");
            _metadataIndex = 0;
            _assemblyIndex = 0;
            _phase = 0;
            _loaded = new HybridCLRLoadedAssemblies();
        }

        /// <summary>Loads one assembly per pump (bounded per-frame work), polled to completion.</summary>
        public HotUpdateStepState Pump()
        {
            if (_phase == 0)
            {
                if (_aotMetadataAssemblies == null || _metadataIndex >= _aotMetadataAssemblies.Count)
                {
                    _phase = 1;
                    return HotUpdateStepState.Running;
                }

                string name = _aotMetadataAssemblies[_metadataIndex];
                byte[] bytes = _assemblyLoader(name);
                if (bytes == null)
                {
                    return FailReason("AOT metadata bytes missing for '" + name + "'");
                }

                // Metadata-only assembly: HybridCLR 的 LoadMetadataForAOTAssembly
                // 返回加载状态，非 0 即失败（失败带名单原文，UI 有话可说）。
                int status = HybridCLR.RuntimeApi.LoadMetadataForAOTAssembly(bytes);
                if (status != 0)
                {
                    return FailReason("AOT metadata load failed for '" + name
                        + "' (status " + status + ")");
                }

                _loaded.MetadataAssemblies.Add(name);
                _metadataIndex++;
                return HotUpdateStepState.Running;
            }

            if (_hotUpdateAssemblies == null || _hotUpdateAssemblies.Count == 0)
            {
                return FailReason("no hot-update assemblies configured");
            }

            if (_assemblyIndex >= _hotUpdateAssemblies.Count)
            {
                return HotUpdateStepState.Done;
            }

            string assemblyName = _hotUpdateAssemblies[_assemblyIndex];
            byte[] assemblyBytes = _assemblyLoader(assemblyName);
            if (assemblyBytes == null)
            {
                return FailReason("hot-update assembly bytes missing for '" + assemblyName + "'");
            }

            try
            {
                _loaded.Assemblies.Add(assemblyName, System.Reflection.Assembly.Load(assemblyBytes));
            }
            catch (Exception ex)
            {
                return FailReason("assembly load failed for '" + assemblyName
                    + "': " + ex.GetType().Name + ": " + ex.Message);
            }

            _assemblyIndex++;
            return _assemblyIndex >= _hotUpdateAssemblies.Count
                ? HotUpdateStepState.Done
                : HotUpdateStepState.Running;
        }

        private HotUpdateStepState FailReason(string reason)
        {
            _loaded.FailureReason = reason;
            return HotUpdateStepState.Failed;
        }
    }

    /// <summary>What the reload step loaded (diagnostics / context passthrough).</summary>
    public sealed class HybridCLRLoadedAssemblies
    {
        /// <summary>AOT metadata assemblies that completed.</summary>
        public readonly List<string> MetadataAssemblies = new List<string>();

        /// <summary>Hot-update assemblies by name.</summary>
        public readonly Dictionary<string, System.Reflection.Assembly> Assemblies =
            new Dictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);

        /// <summary>Set when the step failed; null otherwise.</summary>
        public string FailureReason;
    }
}
#endif
