using System;
using System.Collections.Generic;

namespace YourFramework.Assets.Pack
{
    /// <summary>一个 bundle 的加载进度。</summary>
    public enum BundleLoadState
    {
        /// <summary>仍在加载（下一帧继续轮询）。</summary>
        Loading,
        /// <summary>可用了（可以取资源）。</summary>
        Ready,
        /// <summary>失败；原因由 out 参数带出。</summary>
        Failed,
    }

    /// <summary>
    /// Bundle 装载的引擎边界：自家资源包的 Provider 只跟这个接口打交道，具体是
    /// AssetBundle、还是打包进包体的加密文件、还是编辑器下的直读，都不影响上半场。
    ///
    /// 契约：
    /// - BeginLoadBundle 必须**幂等**：重复调用是空操作（Provider 只保证每个 bundle
    ///   在索引前进时调一次，但不假定后端没被别人调过）；
    /// - PollBundle 是纯粹的进度查询，不得阻塞、不得抛异常（失败经 Failed + error 表达）；
    /// - TryLoadAsset 返回 false + 原因表示"这个 bundle 里没有这个资源"，
    ///   属于内容问题，不是异常；
    /// - ReleaseBundle 由引用计数归零方（Provider）调用，实现可以不做事。
    /// </summary>
    public interface IBundleBackend
    {
        /// <summary>Backend name (diagnostics).</summary>
        string Name { get; }

        /// <summary>Starts (or reuses) a load. Idempotent by contract.</summary>
        void BeginLoadBundle(string bundleName);

        /// <summary>Queries one bundle's progress.</summary>
        BundleLoadState PollBundle(string bundleName, out string error);

        /// <summary>Extracts one asset out of a ready bundle.</summary>
        bool TryLoadAsset(string bundleName, string assetName, out object asset, out string error);

        /// <summary>Drops the backend's hold on a bundle (unload when it supports it).</summary>
        void ReleaseBundle(string bundleName);
    }

    /// <summary>
    /// 一次资源加载在途的进度载体：闭包名单 + 推进到第几个 + 是否已开过 Begin。
    /// 存活于请求的 Payload 里（与协商好的 Provider 约定一致：Raw 装 provider 自用态），
    /// 因此不需要旁挂表。
    /// </summary>
    public sealed class PackLoadProgress
    {
        /// <summary>依赖闭包（拓扑序：被依赖者在前）。</summary>
        public readonly List<string> Bundles = new List<string>();

        /// <summary>下一个待确认就绪的 bundle 下标。</summary>
        public int BundleIndex;

        /// <summary>已经 Begin 过的下标（避免每帧重复发起）。</summary>
        public int BegunIndex = -1;

        /// <summary>闭包声明字节总量（诊断/进度）。</summary>
        public long TotalBytes;
    }

    /// <summary>
    /// 自研资源包的 Provider：地址 → 依赖闭包 → 逐帧装载 → 取出资源。
    ///
    /// 设计取舍：
    /// 1. 有界工作量：每帧只推进闭包里的**当前一个** bundle（最多一次 Begin + 一次
    ///    Poll），所以闭包再长也不会在一帧里全量发起；已经就绪的 bundle 不做无谓的
    ///    跨帧等待，当帧继续下一个 —— 内存/编辑器后端因此可以在同一帧走完全程；
    /// 2. 依赖先于被依赖者（闭包已拓扑排序），所以永远不可能从半加载的依赖里取资源；
    /// 3. 失败是值 —— 地址不在清单、bundle 装载失败、资源不存在、后端抛异常，四条
    ///    失败路径全部落 Failed + 原因原文，且**释放已取得的引用**，不留半拉子引用计数；
    /// 4. 认领（Handles）只认清单里有的地址：路径探测不是本 Provider 的职责，
    ///    诚实路由给后面的 Provider（如 FileAssetProvider）留出空间。
    /// </summary>
    public sealed class PackAssetProvider : IAssetProvider, IAssetReleasingProvider
    {
        private readonly PackageManifest _manifest;
        private readonly IBundleBackend _backend;
        private readonly CacheLedger _cache;
        private readonly DependencyResolver _resolver = new DependencyResolver();
        private readonly List<string> _scratch = new List<string>();

        /// <summary>
        /// Builds the provider. The cache ledger is optional: hot-update flows pass
        /// one so eviction and integrity bookkeeping have a home; a plain build-time
        /// loader can pass null and skip the ref-counting.
        /// </summary>
        public PackAssetProvider(PackageManifest manifest, IBundleBackend backend, CacheLedger cache)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            if (backend == null)
            {
                throw new ArgumentNullException("backend");
            }

            _manifest = manifest;
            _backend = backend;
            _cache = cache;
        }

        /// <summary>The package this provider serves.</summary>
        public string PackageName { get { return _manifest.PackageName; } }

        /// <summary>Provider name (diagnostics).</summary>
        public string Name { get { return "Pack:" + _manifest.PackageName; } }

        /// <summary>Claims only addresses the manifest knows.</summary>
        public bool Handles(string key)
        {
            return _manifest.ContainsLocation(key);
        }

        /// <summary>Advances one frame of the load (one bundle at most).</summary>
        public void Pump(AssetRequest request)
        {
            PackLoadProgress progress = request.Payload != null
                    && request.Payload.Kind == AssetPayloadKind.Raw
                ? request.Payload.Raw as PackLoadProgress
                : null;

            if (progress == null)
            {
                progress = new PackLoadProgress();
                if (!_resolver.TryResolve(_manifest, request.Key, progress.Bundles))
                {
                    request.Fail(Name + ": address '" + request.Key + "' is not in the manifest");
                    return;
                }

                progress.TotalBytes = DependencyResolver.SumBytes(_manifest, progress.Bundles);
                request.Payload = AssetPayload.FromRaw(progress);
                AcquireClosure(progress.Bundles);
            }

            string error;
            while (progress.BundleIndex < progress.Bundles.Count)
            {
                string bundle = progress.Bundles[progress.BundleIndex];

                if (progress.BegunIndex < progress.BundleIndex)
                {
                    _backend.BeginLoadBundle(bundle);
                    progress.BegunIndex = progress.BundleIndex;
                }

                BundleLoadState state;
                try
                {
                    state = _backend.PollBundle(bundle, out error);
                }
                catch (Exception ex)
                {
                    // A backend fault is a runtime fault: fail the request with the
                    // reason and give back the references taken for it.
                    FailAndRelease(request, progress, Name + ": backend '" + _backend.Name
                        + "' threw on bundle '" + bundle + "': " + ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                if (state == BundleLoadState.Failed)
                {
                    FailAndRelease(request, progress, Name + ": bundle '" + bundle + "' failed: "
                        + (string.IsNullOrEmpty(error) ? "unknown error" : error));
                    return;
                }

                if (state == BundleLoadState.Loading)
                {
                    // Bounded work: the current bundle is the only thing this
                    // frame is waiting on; the closure is not fired off at once.
                    return;
                }

                progress.BundleIndex++;
            }

            ManifestAsset asset;
            if (!_manifest.TryGetAsset(request.Key, out asset))
            {
                FailAndRelease(request, progress, Name + ": address '" + request.Key
                    + "' vanished from the manifest between resolve and load");
                return;
            }

            object payload;
            try
            {
                if (!_backend.TryLoadAsset(asset.Bundle, asset.AssetName, out payload, out error))
                {
                    FailAndRelease(request, progress, Name + ": asset '" + asset.AssetName
                        + "' not found in bundle '" + asset.Bundle + "': "
                        + (string.IsNullOrEmpty(error) ? "unknown error" : error));
                    return;
                }
            }
            catch (Exception ex)
            {
                FailAndRelease(request, progress, Name + ": backend '" + _backend.Name
                    + "' threw extracting '" + asset.AssetName + "': " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            request.Complete(AssetPayload.FromRaw(payload));
        }

        /// <summary>
        /// Releases the references this provider took for a key. Called when the
        /// asset is evicted; re-resolving the closure is cheap (address → bundle
        /// graph) and keeps the provider stateless between requests.
        /// </summary>
        public void Release(string key)
        {
            if (_cache == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            if (!_resolver.TryResolve(_manifest, key, _scratch))
            {
                return; // never loaded (or manifest changed): nothing to give back
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                string bundle = _scratch[i];
                if (_cache.IsTracked(bundle) && _cache.RefCount(bundle) > 0)
                {
                    _cache.Release(bundle);
                    if (_cache.RefCount(bundle) == 0)
                    {
                        _backend.ReleaseBundle(bundle);
                    }
                }
            }
        }

        private void AcquireClosure(List<string> bundles)
        {
            if (_cache == null)
            {
                return;
            }

            for (int i = 0; i < bundles.Count; i++)
            {
                string bundle = bundles[i];
                if (_cache.IsTracked(bundle))
                {
                    _cache.Acquire(bundle);
                }
            }
        }

        private void FailAndRelease(AssetRequest request, PackLoadProgress progress, string reason)
        {
            if (_cache != null)
            {
                for (int i = 0; i < progress.Bundles.Count; i++)
                {
                    string bundle = progress.Bundles[i];
                    if (_cache.IsTracked(bundle) && _cache.RefCount(bundle) > 0)
                    {
                        _cache.Release(bundle);
                    }
                }
            }

            request.Fail(reason);
        }
    }
}
