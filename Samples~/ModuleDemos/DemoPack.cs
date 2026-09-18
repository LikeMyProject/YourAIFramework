using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YourFramework.Assets;
using YourFramework.Assets.Pack;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// Demo17：自研资源包 —— 一次演示走完"清单 → 版本差量 → 下载队列 → 依赖全链装载"。
    ///
    /// 零资产依赖：清单与版本文件内联在代码里，bundle 装载与下载都由内存替身完成。
    /// 真实项目里这两条边界（`IBundleBackend` / `IDownloadBackend`）的实现是
    /// `AssetBundleBackend`（LoadFromFileAsync）与 `UnityWebDownloadBackend`
    /// （UnityWebRequest，断点续传 + Range 校验），其余逻辑一模一样。
    /// </summary>
    internal static class DemoPack
    {
        // 内联清单：main → ui → atlas → shader 的四层依赖链，闭包顺序一眼可见。
        private const string ManifestJson =
            "{\"packageName\":\"main\",\"version\":\"1.1.0\",\"bundles\":["
            + "{\"name\":\"shader\",\"hash\":\"h-shader\",\"crc\":11,\"size\":1024},"
            + "{\"name\":\"atlas\",\"hash\":\"h-atlas\",\"crc\":22,\"size\":2048,\"dependencies\":[\"shader\"]},"
            + "{\"name\":\"ui\",\"hash\":\"h-ui\",\"crc\":33,\"size\":4096,\"dependencies\":[\"atlas\"],\"tags\":[\"preload\"]},"
            + "{\"name\":\"main\",\"hash\":\"h-main\",\"crc\":44,\"size\":8192,\"dependencies\":[\"ui\"]}],"
            + "\"assets\":[{\"location\":\"ui/hero\",\"assetName\":\"hero.png\",\"bundle\":\"main\"}]}";

        private const string LocalVersionJson =
            "{\"packageName\":\"main\",\"version\":\"1.0.0\",\"bundles\":["
            + "{\"name\":\"shader\",\"hash\":\"h-shader\",\"size\":1024,\"crc\":11},"
            + "{\"name\":\"ui\",\"hash\":\"h-ui-old\",\"size\":4000,\"crc\":31},"
            + "{\"name\":\"retired\",\"hash\":\"h-retired\",\"size\":512,\"crc\":99}]}";

        private const string RemoteVersionJson =
            "{\"packageName\":\"main\",\"version\":\"1.1.0\",\"bundles\":["
            + "{\"name\":\"shader\",\"hash\":\"h-shader\",\"size\":1024,\"crc\":11},"
            + "{\"name\":\"atlas\",\"hash\":\"h-atlas\",\"size\":2048,\"crc\":22},"
            + "{\"name\":\"ui\",\"hash\":\"h-ui\",\"size\":4096,\"crc\":33},"
            + "{\"name\":\"main\",\"hash\":\"h-main\",\"size\":8192,\"crc\":44}]}";

        /// <summary>内存版 bundle 后端：每个 bundle 轮询两次就绪，并记录发起顺序。</summary>
        private sealed class MemoryBundleBackend : IBundleBackend
        {
            public readonly List<string> BeginOrder = new List<string>();
            public readonly List<string> Released = new List<string>();

            private readonly Dictionary<string, int> _polls = new Dictionary<string, int>(System.StringComparer.Ordinal);
            private readonly Dictionary<string, int> _ready = new Dictionary<string, int>(System.StringComparer.Ordinal);

            public string Name { get { return "MemoryBundles"; } }

            /// <summary>设置某个 bundle 需要几次轮询才就绪（默认 1 次，即当帧可用）。</summary>
            public void SetPollsToReady(string bundle, int polls)
            {
                _ready[bundle] = polls;
            }

            public void BeginLoadBundle(string bundleName)
            {
                if (!_polls.ContainsKey(bundleName))
                {
                    _polls[bundleName] = 0;
                    BeginOrder.Add(bundleName);
                }
            }

            public BundleLoadState PollBundle(string bundleName, out string error)
            {
                error = null;
                int polls;
                _polls.TryGetValue(bundleName, out polls);
                polls++;
                _polls[bundleName] = polls;

                int target;
                if (!_ready.TryGetValue(bundleName, out target))
                {
                    target = 1;
                }

                return polls >= target ? BundleLoadState.Ready : BundleLoadState.Loading;
            }

            public bool TryLoadAsset(string bundleName, string assetName, out object asset, out string error)
            {
                error = null;
                if (bundleName == "main" && assetName == "hero.png")
                {
                    asset = "hero.png(演示资源对象)";
                    return true;
                }

                asset = null;
                error = "asset '" + assetName + "' is not inside bundle '" + bundleName + "'";
                return false;
            }

            public void ReleaseBundle(string bundleName)
            {
                Released.Add(bundleName);
            }
        }

        /// <summary>内存版下载后端：两次轮询把字节"写进文件"，第二次报 Done。</summary>
        private sealed class InstantDownloadBackend : IDownloadBackend
        {
            private sealed class Attempt
            {
                public long Size;
                public int Polls;
            }

            private readonly Dictionary<object, Attempt> _live = new Dictionary<object, Attempt>();
            private int _handleSeq;

            public string Name { get { return "InstantTransport"; } }
            public int CancelCalls;

            public object Begin(string url, string savePath, long resumeFrom, out string error)
            {
                error = null;
                object handle = "dl-" + (++_handleSeq);
                _live[handle] = new Attempt { Size = 2048, Polls = 0 };
                return handle;
            }

            public DownloadPoll Poll(object handle, out long receivedBytes, out string error)
            {
                error = null;
                receivedBytes = 0;

                Attempt attempt;
                if (handle == null || !_live.TryGetValue(handle, out attempt))
                {
                    error = "unknown handle";
                    return DownloadPoll.Failed;
                }

                attempt.Polls++;
                if (attempt.Polls < 2)
                {
                    receivedBytes = attempt.Size / 2;
                    return DownloadPoll.Running;
                }

                receivedBytes = attempt.Size;
                _live.Remove(handle);
                return DownloadPoll.Done;
            }

            public void Cancel(object handle)
            {
                CancelCalls++;
            }
        }

        public static IEnumerator AssetPack()
        {
            // ---- 1. 清单：构建期产物的运行期读法 ----
            PackageManifest manifest = PackageManifest.LoadJson(ManifestJson);
            Debug.Log("[Demo17] 清单：包 " + manifest.PackageName + " v" + manifest.Version
                + "，bundle " + manifest.Bundles.Count + " 个、可寻址资源 " + manifest.Assets.Count
                + " 个，共 " + manifest.TotalBytes + " 字节（坏清单当场抛，不拖到运行时）");

            // ---- 2. 版本差量：本地版本 × 远端版本 → 下载名单 ----
            PackageVersion local = PackageVersion.LoadJson(LocalVersionJson);
            PackageVersion remote = PackageVersion.LoadJson(RemoteVersionJson);
            PackageDiffResult diff = PackageDiff.Compute(local, remote);
            Debug.Log("[Demo17] 差量：" + diff.Explain()
                + "（新增 " + string.Join(",", diff.Added.ToArray())
                + "；更新 " + string.Join(",", diff.Changed.ToArray())
                + "；未变 " + string.Join(",", diff.Unchanged.ToArray())
                + "；可清理 " + string.Join(",", diff.Removed.ToArray()) + "）");

            // ---- 3. 下载队列：并发 + 重试 + 断点续传，逐帧驱动 ----
            DownloadQueue queue = new DownloadQueue(new InstantDownloadBackend());
            queue.MaxConcurrent = 2;
            queue.MaxAttempts = 3;
            queue.RetryBackoffSeconds = 0.5f;

            for (int i = 0; i < diff.Added.Count; i++)
            {
                Enqueue(queue, manifest, diff.Added[i]);
            }

            for (int i = 0; i < diff.Changed.Count; i++)
            {
                Enqueue(queue, manifest, diff.Changed[i]);
            }

            float guard = 0f;
            while (!queue.IsIdle && guard < 15f)
            {
                queue.Pump(Time.deltaTime);
                guard += Time.deltaTime;
                yield return null;
            }

            if (queue.HasFailures)
            {
                Debug.LogWarning("[Demo17] 下载有失败（失败也是值，带原因）: " + queue.FirstFailureReason);
            }
            else
            {
                Debug.Log("[Demo17] 下载完成：" + queue.DoneCount + "/" + queue.Tasks.Count + " 个 bundle，"
                    + queue.ReceivedBytes + " 字节（真实后端此时会做 CRC 校验，并把结果记进缓存记录）");
            }

            // ---- 4. 依赖全链装载：一个地址 → 拓扑序装载整条闭包 → 取出资源 ----
            MemoryBundleBackend bundles = new MemoryBundleBackend();
            bundles.SetPollsToReady("shader", 2); // 让闭包必须跨帧，才能看到"逐帧推进"

            CacheLedger ledger = new CacheLedger();
            for (int i = 0; i < manifest.Bundles.Count; i++)
            {
                ManifestBundle entry = manifest.Bundles[i];
                ledger.Track(entry.Name, "cache/" + entry.Name, entry.Size);
            }

            AssetManager assets = new AssetManager(new IAssetProvider[]
            {
                new PackAssetProvider(manifest, bundles, ledger),
            });

            AssetRequest request = assets.Load("ui/hero");
            int frames = 0;
            while (!request.IsDone && frames < 120)
            {
                assets.Pump();
                frames++;
                yield return null;
            }

            if (request.IsReady)
            {
                Debug.Log("[Demo17] 装载顺序（拓扑序，被依赖者在前）："
                    + string.Join(" → ", bundles.BeginOrder.ToArray())
                    + "，共 " + frames + " 帧（每帧只推进当前一个 bundle，工作量有界）");
                Debug.Log("[Demo17] 资源到手：" + request.Payload.Raw);

                int held = ledger.RefCount("main");
                assets.Drop("ui/hero"); // 逐出：Provider 归还整条闭包的引用计数
                Debug.Log("[Demo17] 引用计数：Drop 前 main=" + held + "，Drop 后 main="
                    + ledger.RefCount("main") + "；后端收到 " + bundles.Released.Count
                    + " 次释放通知（bundle 到这才有机会卸载）");
            }
            else
            {
                Debug.LogWarning("[Demo17] 装载失败（失败也是值，原因原文）: " + request.Error);
            }

            Debug.Log("[Demo17] 真实项目怎么接：菜单 YourAI → 资源包 → 构建资源包（当前平台）"
                + " 产出 bundle + manifest.json + version.json，把上面两处内存替身换成"
                + " AssetBundleBackend 与 UnityWebDownloadBackend 即可。");
        }

        private static void Enqueue(DownloadQueue queue, PackageManifest manifest, string bundle)
        {
            ManifestBundle entry;
            long size = manifest.TryGetBundle(bundle, out entry) ? entry.Size : 0;
            queue.Enqueue(bundle, "https://cdn.example.com/" + bundle, "cache/" + bundle, size);
        }
    }
}
