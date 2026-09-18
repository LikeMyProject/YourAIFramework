using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YourFramework.Assets.Pack;

namespace YourFramework.UnityRuntime.Assets.Pack
{
    /// <summary>
    /// 自研资源包的 AssetBundle 后端：bundle 文件 → AssetBundle。装载走
    /// <c>AssetBundle.LoadFromFileAsync</c>（异步、可轮询，与框架泵语义同构），
    /// 因此每帧只推进一帧的量，不会因为一个大包卡住主线程。
    ///
    /// 设计要点：
    /// 1. **幂等 Begin**：已加载 / 加载中 / 已失败，三种情况下再调都是空操作 ——
    ///    Provider 的推进逻辑因此不需要跟后端对状态；
    /// 2. 文件缺失与加载失败是两件事，报错文案分开写（"缺文件"通常意味着热更没下全，
    ///    "返回 null" 通常意味着包体损坏或平台不支持），排查时不用猜；
    /// 3. 卸载：引用计数归零由 Provider 驱动本类 <c>ReleaseBundle</c>，
    ///    <c>Unload(false)</c> 保留已取出的资源对象（资源生命周期由 AssetManager 那层管）。
    /// </summary>
    public sealed class AssetBundleBackend : IBundleBackend
    {
        private sealed class Slot
        {
            public AssetBundleCreateRequest Request;
            public AssetBundle Bundle;
            public bool Failed;
            public string Error;
        }

        private readonly string _rootPath;
        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);

        /// <summary>
        /// Builds the backend over the directory holding the package's bundle files
        /// (the local cache root). An empty root is a setup bug: fail fast.
        /// </summary>
        public AssetBundleBackend(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException("AssetBundleBackend: rootPath must not be empty.", "rootPath");
            }

            _rootPath = rootPath;
        }

        /// <summary>Local cache root this backend reads from.</summary>
        public string RootPath { get { return _rootPath; } }

        /// <summary>Backend name (diagnostics).</summary>
        public string Name { get { return "AssetBundle(" + _rootPath + ")"; } }

        /// <summary>Number of bundles currently held.</summary>
        public int LoadedCount { get { return _slots.Count; } }

        /// <summary>Starts a load. Idempotent: a bundle already in the table is left alone.</summary>
        public void BeginLoadBundle(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName) || _slots.ContainsKey(bundleName))
            {
                return;
            }

            Slot slot = new Slot();
            _slots.Add(bundleName, slot);

            string path = Path.Combine(_rootPath, bundleName);
            if (!File.Exists(path))
            {
                slot.Failed = true;
                slot.Error = "bundle file is missing: " + path
                    + " (was the hot-update download completed, and is the manifest in sync?)";
                return;
            }

            try
            {
                slot.Request = AssetBundle.LoadFromFileAsync(path);
            }
            catch (Exception ex)
            {
                slot.Failed = true;
                slot.Error = "AssetBundle.LoadFromFileAsync threw for '" + path + "': "
                    + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>Queries one bundle's progress. Never blocks, never throws.</summary>
        public BundleLoadState PollBundle(string bundleName, out string error)
        {
            error = null;

            Slot slot;
            if (bundleName == null || !_slots.TryGetValue(bundleName, out slot))
            {
                error = "bundle '" + bundleName + "' was never begun";
                return BundleLoadState.Failed;
            }

            if (slot.Failed)
            {
                error = slot.Error;
                return BundleLoadState.Failed;
            }

            if (slot.Bundle != null)
            {
                return BundleLoadState.Ready;
            }

            if (slot.Request == null)
            {
                slot.Failed = true;
                slot.Error = "bundle '" + bundleName + "' has neither a request nor a bundle";
                error = slot.Error;
                return BundleLoadState.Failed;
            }

            if (!slot.Request.isDone)
            {
                return BundleLoadState.Loading;
            }

            slot.Bundle = slot.Request.assetBundle;
            if (slot.Bundle == null)
            {
                slot.Failed = true;
                slot.Error = "AssetBundle.LoadFromFileAsync returned null for '" + bundleName
                    + "' (corrupt file, wrong platform target, or an already-unloaded bundle)";
                error = slot.Error;
                return BundleLoadState.Failed;
            }

            return BundleLoadState.Ready;
        }

        /// <summary>Extracts an asset by name from a ready bundle.</summary>
        public bool TryLoadAsset(string bundleName, string assetName, out object asset, out string error)
        {
            asset = null;
            error = null;

            Slot slot;
            if (bundleName == null || !_slots.TryGetValue(bundleName, out slot) || slot.Bundle == null)
            {
                error = "bundle '" + bundleName + "' is not ready";
                return false;
            }

            UnityEngine.Object loaded;
            try
            {
                loaded = slot.Bundle.LoadAsset(assetName);
            }
            catch (Exception ex)
            {
                error = "LoadAsset('" + assetName + "') threw: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            if (loaded == null)
            {
                error = "asset '" + assetName + "' is not inside bundle '" + bundleName
                    + "' (name mismatch between the manifest and the built bundle)";
                return false;
            }

            asset = loaded;
            return true;
        }

        /// <summary>Drops a bundle (called when its reference count reaches zero).</summary>
        public void ReleaseBundle(string bundleName)
        {
            Slot slot;
            if (bundleName == null || !_slots.TryGetValue(bundleName, out slot))
            {
                return;
            }

            if (slot.Bundle != null)
            {
                // false: objects already handed out survive; the asset layer owns them.
                slot.Bundle.Unload(false);
            }

            _slots.Remove(bundleName);
        }

        /// <summary>Whether a bundle is held right now (diagnostics).</summary>
        public bool IsHeld(string bundleName)
        {
            return bundleName != null && _slots.ContainsKey(bundleName);
        }

        /// <summary>Unloads everything (shutdown).</summary>
        public void UnloadAll()
        {
            foreach (KeyValuePair<string, Slot> pair in _slots)
            {
                if (pair.Value.Bundle != null)
                {
                    pair.Value.Bundle.Unload(false);
                }
            }

            _slots.Clear();
        }
    }
}
