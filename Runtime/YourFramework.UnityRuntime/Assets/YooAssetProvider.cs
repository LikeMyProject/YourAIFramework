#if YOURAI_YOOASSET
using System;
using YooAsset;
using YourFramework.Assets;

namespace YourFramework.UnityRuntime.Assets
{
    /// <summary>
    /// YooAsset 集成层（公子拍板：热更引入不自研）。把 YooAsset 的资源包包在自家
    /// IAssetProvider 后面：游戏代码只面对 AssetManager / AssetRequest，后端随时
    /// 可换成 Addressables 或本地直读。
    ///
    /// 编译边界：本文件整体在 YOURAI_YOOASSET 符号内。该符号由
    /// YourFramework.UnityRuntime.asmdef 的 versionDefines 在宿主工程装了
    /// com.tuyoogame.yooasset（2.3+）时自动定义 —— 没装包，这段代码干净地
    /// 编译为空，框架其余部分照常工作；装了包，YooAsset 的 API 变更会立刻
    /// 以编译错误浮出，而不是运行到一半才炸。
    ///
    /// 语义映射：
    /// - key = YooAsset 的 location（可寻址地址或资源路径，由 Collector 配置决定）；
    /// - Pump 驱动 handle（YooAsset 的操作是异步可轮询的，与框架泵哲学同构）；
    /// - handle.Status 映射到请求终态；LastError 原文进 Error（失败带原因）；
    /// - 请求不再被轮询时由 AssetManager 的 Drop / Clear 负责 Release 句柄。
    /// </summary>
    public sealed class YooAssetProvider : IAssetProvider
    {
        private readonly ResourcePackage _package;
        private readonly string _packageName;

        /// <summary>
        /// Builds the provider over an initialized YooAsset package. Fail fast on
        /// null -- a half-initialized hot-update backend is a setup bug.
        /// </summary>
        public YooAssetProvider(ResourcePackage package, string packageName)
        {
            if (package == null)
            {
                throw new ArgumentNullException("package");
            }

            _package = package;
            _packageName = packageName ?? "(default)";
        }

        /// <summary>The package this provider serves.</summary>
        public string PackageName { get { return _packageName; } }

        /// <summary>Provider name.</summary>
        public string Name { get { return "YooAsset:" + _packageName; } }

        /// <summary>Claims every non-empty key (location validation is YooAsset's job).</summary>
        public bool Handles(string key)
        {
            return !string.IsNullOrEmpty(key);
        }

        /// <summary>Drives the YooAsset handle until it is done, then settles the request.</summary>
        public void Pump(AssetRequest request)
        {
            AssetHandle handle = request.Payload != null && request.Payload.Kind == AssetPayloadKind.Raw
                ? request.Payload.Raw as AssetHandle
                : null;

            if (handle == null)
            {
                // First pump: begin the load. The handle travels inside the
                // payload as Raw so later pumps find it without a side table.
                handle = _package.LoadAssetAsync(request.Key);
                if (handle == null)
                {
                    request.Fail("YooAsset(" + _packageName + "): LoadAssetAsync returned null for '" + request.Key + "'");
                    return;
                }

                request.Payload = new AssetPayload { Kind = AssetPayloadKind.Raw, Raw = handle };
                // Fall through: the operation may complete synchronously.
            }

            if (!handle.IsDone)
            {
                return; // still loading; pump again next frame
            }

            if (handle.Status == EOperationStatus.Succeed)
            {
                request.Complete(AssetPayload.FromRaw(handle.AssetObject));
            }
            else
            {
                request.Fail("YooAsset(" + _packageName + "): '" + request.Key
                    + "' failed: " + (string.IsNullOrEmpty(handle.LastError) ? "unknown error" : handle.LastError));
            }
        }
    }
}
#endif
