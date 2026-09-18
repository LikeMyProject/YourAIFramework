using System;
using System.Collections.Generic;
using YourFramework.Core;

namespace YourFramework.Assets
{
    /// <summary>Asset request state machine. Queued → Loading → Ready | Failed.</summary>
    public enum AssetState
    {
        /// <summary>Waiting for a free concurrency slot.</summary>
        Queued,
        /// <summary>Dispatched to a provider, work in progress.</summary>
        Loading,
        /// <summary>Payload available.</summary>
        Ready,
        /// <summary>Settled with a reason. Failure carries a value, never a throw.</summary>
        Failed
    }

    /// <summary>What kind of payload an asset carries.</summary>
    public enum AssetPayloadKind
    {
        /// <summary>Decoded UTF-8 text (configs, tables, shaders-as-text).</summary>
        Text,
        /// <summary>Raw bytes (binaries, native files).</summary>
        Bytes,
        /// <summary>Provider-specific object (e.g. a UnityEngine.Object from a bundle loader).</summary>
        Raw
    }

    /// <summary>
    /// The loaded thing. One small sealed class instead of an object-returning API:
    /// callers read <c>Kind</c> once and take the right field, no casting roulette.
    /// </summary>
    public sealed class AssetPayload
    {
        /// <summary>Payload kind.</summary>
        public AssetPayloadKind Kind;

        /// <summary>Text payload (Kind == Text).</summary>
        public string Text;

        /// <summary>Bytes payload (Kind == Bytes).</summary>
        public byte[] Bytes;

        /// <summary>Provider-specific payload (Kind == Raw).</summary>
        public object Raw;

        /// <summary>Text payload.</summary>
        public static AssetPayload FromText(string text)
        {
            return new AssetPayload { Kind = AssetPayloadKind.Text, Text = text };
        }

        /// <summary>Bytes payload.</summary>
        public static AssetPayload FromBytes(byte[] bytes)
        {
            return new AssetPayload { Kind = AssetPayloadKind.Bytes, Bytes = bytes };
        }

        /// <summary>Provider-specific payload.</summary>
        public static AssetPayload FromRaw(object raw)
        {
            if (raw == null)
            {
                throw new ArgumentNullException("raw");
            }

            return new AssetPayload { Kind = AssetPayloadKind.Raw, Raw = raw };
        }
    }

    /// <summary>
    /// One asset load, from Load() to settle. Poll it; do not wait on it --
    /// the framework's whole loading posture is pump-driven (red line: no bare
    /// async/await in the load path, the host decides when a frame advances).
    /// Terminal states are Ready and Failed; both are sticky.
    /// </summary>
    public sealed class AssetRequest
    {
        private AssetState _state = AssetState.Queued;

        /// <summary>The key this request loads.</summary>
        public string Key { get; private set; }

        /// <summary>Current state.</summary>
        public AssetState State { get { return _state; } }

        /// <summary>Payload when State == Ready, else null.</summary>
        public AssetPayload Payload { get; internal set; }

        /// <summary>Failure reason when State == Failed, else null. Always non-empty.</summary>
        public string Error { get; internal set; }

        /// <summary>Whether the request settled (Ready or Failed).</summary>
        public bool IsDone { get { return _state == AssetState.Ready || _state == AssetState.Failed; } }

        /// <summary>Whether the request settled successfully.</summary>
        public bool IsReady { get { return _state == AssetState.Ready; } }

        /// <summary>Whether the request settled with a failure.</summary>
        public bool IsFailed { get { return _state == AssetState.Failed; } }

        /// <summary>Provider bound at dispatch time; null while queued. Internal wiring.</summary>
        internal IAssetProvider BoundProvider;

        /// <summary>Queued by the manager; used internally for dedup and cache.</summary>
        internal AssetRequest(string key)
        {
            Key = key;
        }

        /// <summary>
        /// Marks the request ready with a payload. Provider-side entry point:
        /// IAssetProvider implementations live in other assemblies, so this is
        /// public by contract. Sticky: once Ready or Failed, later calls are no-ops.
        /// </summary>
        public void Complete(AssetPayload payload)
        {
            if (_state == AssetState.Ready || _state == AssetState.Failed)
            {
                return; // sticky terminal states: later provider chatter is ignored
            }

            if (payload == null)
            {
                Fail("provider completed with a null payload");
                return;
            }

            Payload = payload;
            _state = AssetState.Ready;
        }

        /// <summary>
        /// Marks the request failed with a reason. Provider-side entry point
        /// (public for the same reason as Complete). Sticky terminal state;
        /// a null/empty reason becomes "unspecified failure".
        /// </summary>
        public void Fail(string reason)
        {
            if (_state == AssetState.Ready || _state == AssetState.Failed)
            {
                return; // sticky terminal states
            }

            Error = string.IsNullOrEmpty(reason) ? "unspecified failure" : reason;
            Payload = null;
            _state = AssetState.Failed;
        }

        /// <summary>Promotes Queued → Loading under the given provider. Manager wiring.</summary>
        internal void Begin(IAssetProvider provider)
        {
            if (_state != AssetState.Queued)
            {
                return; // only queued requests begin
            }

            BoundProvider = provider;
            _state = AssetState.Loading;
        }
    }

    /// <summary>
    /// Where an AssetManager gets payloads from. Implementations drive the pump:
    /// settle requests synchronously (file/memory providers) or across frames
    /// (the package provider, downloaders). Contract:
    ///
    /// - Handles(key) must be a pure, allocation-free predicate;
    /// - Pump(request) is called every frame until the request settles; the
    ///   provider must eventually call Complete or Fail (both sticky);
    /// - a provider that throws inside Pump gets its request Failed with the
    ///   exception's reason by the manager -- "failure as value" end to end.
    /// </summary>
    public interface IAssetProvider
    {
        /// <summary>Human-readable provider name (shown in diagnostics).</summary>
        string Name { get; }

        /// <summary>Whether this provider claims the key. Pure predicate.</summary>
        bool Handles(string key);

        /// <summary>Advances one frame of load work for the request.</summary>
        void Pump(AssetRequest request);
    }

    /// <summary>
    /// 可选扩展点：Provider 需要知道"这个 key 被逐出了"时实现它。AssetManager 在
    /// Drop / Clear 移出请求时回调 Release(key)，让 Provider 归还它为该 key 占用的
    /// 句柄与引用计数（资源包、bundle 引用计数靠这个走完整个流程收尾）。不实现即不通知。
    ///
    /// 合约：Release 必须**不抛异常**（它在逐出/关停路径上）；实现若抛，视为接线
    /// bug 当场 fail-fast，不做静默吞掉。
    /// </summary>
    public interface IAssetReleasingProvider
    {
        /// <summary>Returns whatever the provider holds for the key.</summary>
        void Release(string key);
    }

    /// <summary>
    /// 资源管理器：Load(key) → 队列 → 并发上限内派发到 Provider → 每帧 Pump 至落定。
    ///
    /// 设计取舍：
    /// 1. 自家接口在前、引擎后端在后 —— IAssetProvider 是唯一扩展点，自研资源包
    ///    (PackAssetProvider) / 编辑器直读 / 任何第三方都只是 Provider 实现，
    ///    游戏代码永远面对本类；
    /// 2. 轮询逐帧驱动 —— 没有 Task/回调风暴，Pump 每帧推进一帧的量，与 ModuleCenter、
    ///    AI 层同一哲学，谁驱动、何时驱动宿主说了算；
    /// 3. 失败也是值 —— 加载失败落 Failed + Error 原因，不抛异常，UI 有话可说；
    ///    Load 的参数错误（空 key）才是 fail-fast。
    ///
    /// 缓存：同 key 的请求全程唯一（去重 + Ready 缓存一体），Drop 显式逐出。
    /// 热路径零分配：Pump 单遍原地扫描，无 LINQ、无快照拷贝。
    /// </summary>
    public sealed class AssetManager : IModule
    {
        private readonly List<IAssetProvider> _providers = new List<IAssetProvider>();
        private readonly Dictionary<string, AssetRequest> _byKey =
            new Dictionary<string, AssetRequest>(StringComparer.Ordinal);
        private readonly List<AssetRequest> _inFlight = new List<AssetRequest>();
        private int _maxConcurrent;

        /// <summary>True while pumping; re-entrant Load during Pump is deferred by design.</summary>
        private bool _pumping;

        /// <summary>
        /// Builds the manager over the given providers (priority = list order:
        /// the first provider whose Handles(key) is true wins). MaxConcurrent
        /// defaults to 4.
        /// </summary>
        public AssetManager(IList<IAssetProvider> providers)
        {
            if (providers == null)
            {
                throw new ArgumentNullException("providers");
            }

            for (int i = 0; i < providers.Count; i++)
            {
                if (providers[i] == null)
                {
                    throw new ArgumentException("AssetManager: providers contains a null entry.", "providers");
                }

                _providers.Add(providers[i]);
            }

            _maxConcurrent = 4;
        }

        /// <summary>Max simultaneously Loading requests. Settable between pumps.</summary>
        public int MaxConcurrent
        {
            get { return _maxConcurrent; }
            set
            {
                if (value < 1)
                {
                    throw new ArgumentException("AssetManager.MaxConcurrent: must be >= 1.", "value");
                }

                _maxConcurrent = value;
            }
        }

        /// <summary>Requests currently not settled (queued or loading).</summary>
        public int InFlightCount { get { return _inFlight.Count; } }

        /// <summary>Live requests, cached-ready ones included.</summary>
        public int TrackedCount { get { return _byKey.Count; } }

        /// <summary>Module plumbing.</summary>
        public string Name { get { return "Assets"; } }

        /// <summary>Module plumbing: infrastructure-ish; depends on nothing but providers.</summary>
        public int InitOrder { get { return 20; } }

        /// <summary>Module plumbing. No cross-module dependencies.</summary>
        public void Init(ModuleCenter host)
        {
        }

        /// <summary>
        /// Loads (or returns the already-running / cached request for) the key.
        /// Never throws for runtime problems: unknown keys settle as Failed with
        /// a reason. Empty/null keys are caller bugs and fail fast.
        /// </summary>
        public AssetRequest Load(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("AssetManager.Load: key must not be null or empty.", "key");
            }

            AssetRequest existing;
            if (_byKey.TryGetValue(key, out existing))
            {
                return existing;
            }

            AssetRequest request = new AssetRequest(key);
            _byKey.Add(key, request);
            _inFlight.Add(request);
            return request;
        }

        /// <summary>
        /// Evicts the key from the cache. A request still loading is Failed with
        /// "dropped while loading" (pollers see the failure, nothing throws);
        /// a settled request just leaves the cache and may load again.
        /// </summary>
        public bool Drop(string key)
        {
            if (key == null)
            {
                return false;
            }

            AssetRequest request;
            if (!_byKey.TryGetValue(key, out request))
            {
                return false;
            }

            _byKey.Remove(key);
            if (!request.IsDone)
            {
                request.Fail("dropped while loading");
                _inFlight.Remove(request);
            }

            // Whether it was still loading or already served from cache, the
            // provider may be holding handles and reference counts for this key:
            // they have to come back, or bundles never unload.
            NotifyRelease(request);
            return true;
        }

        /// <summary>Evicts everything; in-flight requests are failed, not orphaned.</summary>
        public void Clear()
        {
            // Every tracked key goes, cached-ready ones included, so every key is
            // offered back to its provider before the tables are dropped.
            foreach (KeyValuePair<string, AssetRequest> pair in _byKey)
            {
                if (!pair.Value.IsDone)
                {
                    pair.Value.Fail("cleared while loading");
                }

                NotifyRelease(pair.Value);
            }

            _inFlight.Clear();
            _byKey.Clear();
        }

        /// <summary>
        /// Advances one frame: dispatch queued work up to MaxConcurrent, then pump
        /// every loading request until it settles. Settled requests leave the
        /// in-flight list but stay in the cache (same-key Load then hits cache).
        /// </summary>
        public void Pump()
        {
            if (_pumping)
            {
                return; // re-entrancy guard: providers must not Load-and-pump the same manager
            }

            _pumping = true;
            try
            {
                // Pass 1 -- dispatch: promote Queued → Loading while under the cap.
                int loading = 0;
                for (int i = 0; i < _inFlight.Count; i++)
                {
                    if (_inFlight[i].State == AssetState.Loading)
                    {
                        loading++;
                    }
                }

                for (int i = 0; i < _inFlight.Count && loading < _maxConcurrent; i++)
                {
                    AssetRequest request = _inFlight[i];
                    if (request.State != AssetState.Queued)
                    {
                        continue;
                    }

                    IAssetProvider provider = SelectProvider(request.Key);
                    if (provider == null)
                    {
                        request.Fail("no provider handles key '" + request.Key + "'");
                        continue; // failure surfaced; removed in pass 2
                    }

                    request.Begin(provider);
                    loading++;
                }

                // Pass 2 -- pump loading requests and drop settled ones. Backwards:
                // in-flight removal must not disturb the scan.
                for (int i = _inFlight.Count - 1; i >= 0; i--)
                {
                    AssetRequest request = _inFlight[i];
                    if (request.IsDone)
                    {
                        _inFlight.RemoveAt(i);
                        continue;
                    }

                    if (request.State != AssetState.Loading)
                    {
                        continue; // still queued this frame
                    }

                    try
                    {
                        request.BoundProvider.Pump(request);
                    }
                    catch (Exception ex)
                    {
                        // A provider fault is a runtime fault, not a caller bug:
                        // the request fails with the reason, the manager lives on.
                        request.Fail("provider '" + request.BoundProvider.Name + "' threw: "
                            + ex.GetType().Name + ": " + ex.Message);
                    }

                    if (request.IsDone)
                    {
                        _inFlight.RemoveAt(i);
                    }
                }
            }
            finally
            {
                _pumping = false;
            }
        }

        /// <summary>Module plumbing: nothing to release (providers own their resources).</summary>
        public void Shutdown()
        {
        }

        /// <summary>
        /// Hands a dropped key back to a release-aware provider. A provider that
        /// throws here violates the interface contract, so it is allowed to
        /// propagate: silent swallowing would hide a leaking teardown path.
        /// </summary>
        private static void NotifyRelease(AssetRequest request)
        {
            if (request == null || request.BoundProvider == null)
            {
                return; // never dispatched: the provider never took anything
            }

            IAssetReleasingProvider releasing = request.BoundProvider as IAssetReleasingProvider;
            if (releasing != null)
            {
                releasing.Release(request.Key);
            }
        }

        private IAssetProvider SelectProvider(string key)
        {
            for (int i = 0; i < _providers.Count; i++)
            {
                if (_providers[i].Handles(key))
                {
                    return _providers[i];
                }
            }

            return null;
        }
    }
}
