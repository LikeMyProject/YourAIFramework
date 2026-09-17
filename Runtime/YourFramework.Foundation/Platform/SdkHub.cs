using System;
using System.Collections.Generic;
using YourFramework.Core;

namespace YourFramework.Platform
{
    /// <summary>
    /// One SDK channel (login / pay / ads / push / platform stats...). Channels
    /// are the ONLY place platform SDKs touch the framework: the hub owns the
    /// lifecycle, the game asks the hub.
    /// </summary>
    public interface ISdkChannel
    {
        /// <summary>Channel name (ordinal key, e.g. "weChat", "tapTap").</summary>
        string Name { get; }

        /// <summary>Lower initializes earlier; dependencies are expressed here.</summary>
        int InitOrder { get; }

        /// <summary>Initializes the channel. Throwing marks it unavailable with the reason.</summary>
        void Initialize();

        /// <summary>Whether the channel is usable (post-Initialize).</summary>
        bool IsAvailable { get; }
    }

    /// <summary>
    /// SDK 管道：渠道初始化顺序编排 + 降级。渠道 Init 抛异常 = 该渠道不可用
    /// （失败带原因，游戏可查询），**绝不妨碍其他渠道** —— 渠道故障是运行期
    /// 故障，不是程序 bug，走"失败是值"。
    ///
    /// 游戏侧约定：永远经 <c>Get(name)</c> / <c>IsReady(name)</c> 访问渠道，
    /// 不可用渠道返回 null —— UI 与业务自然降级（没有支付按钮），而不是崩溃。
    /// </summary>
    public sealed class SdkHub : IModule
    {
        private readonly List<ISdkChannel> _channels = new List<ISdkChannel>();
        private readonly Dictionary<string, ISdkChannel> _byName =
            new Dictionary<string, ISdkChannel>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _failures =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _initialized;

        /// <summary>Registered channel count.</summary>
        public int Count { get { return _channels.Count; } }

        /// <summary>
        /// Registers a channel. Setup phase only; duplicate names are wiring
        /// bugs and fail fast.
        /// </summary>
        public void Register(ISdkChannel channel)
        {
            if (channel == null)
            {
                throw new ArgumentNullException("channel");
            }

            if (_initialized)
            {
                throw new InvalidOperationException(
                    "SdkHub.Register: too late -- the hub is initialized.");
            }

            if (string.IsNullOrEmpty(channel.Name))
            {
                throw new ArgumentException("SdkHub.Register: channel name must not be empty.");
            }

            if (_byName.ContainsKey(channel.Name))
            {
                throw new ArgumentException(
                    "SdkHub.Register: duplicate channel '" + channel.Name + "'.");
            }

            _channels.Add(channel);
            _byName.Add(channel.Name, channel);
        }

        /// <summary>
        /// Initializes every channel in stable InitOrder order. A channel that
        /// throws is recorded as unavailable (with the reason) and skipped; the
        /// rest initialize normally.
        /// </summary>
        public void Init(ModuleCenter host)
        {
            for (int i = 1; i < _channels.Count; i++)
            {
                ISdkChannel current = _channels[i];
                int j = i - 1;
                while (j >= 0 && _channels[j].InitOrder > current.InitOrder)
                {
                    _channels[j + 1] = _channels[j];
                    j--;
                }

                _channels[j + 1] = current;
            }

            for (int i = 0; i < _channels.Count; i++)
            {
                ISdkChannel channel = _channels[i];
                try
                {
                    channel.Initialize();
                }
                catch (Exception ex)
                {
                    // 渠道故障降级：记录原因，游戏查得到，业务照常跑。
                    _failures[channel.Name] = ex.GetType().Name + ": " + ex.Message;
                }
            }

            _initialized = true;
        }

        /// <summary>
        /// The usable channel, or null when missing/unavailable/uninitialized
        /// (callers degrade; that is the whole point).
        /// </summary>
        public T Get<T>(string name) where T : class, ISdkChannel
        {
            if (name == null || !_initialized)
            {
                return null;
            }

            ISdkChannel channel;
            if (!_byName.TryGetValue(name, out channel))
            {
                return null;
            }

            return channel.IsAvailable ? channel as T : null;
        }

        /// <summary>Whether the channel initialized successfully.</summary>
        public bool IsReady(string name)
        {
            return Get<ISdkChannel>(name) != null;
        }

        /// <summary>The recorded failure reason for a channel, or null when it is fine.</summary>
        public string FailureReason(string name)
        {
            string reason;
            return name != null && _failures.TryGetValue(name, out reason) ? reason : null;
        }

        /// <summary>Module plumbing.</summary>
        public string Name { get { return "Sdk"; } }

        /// <summary>Module plumbing: late infrastructure (channels touch platform glue).</summary>
        public int InitOrder { get { return 50; } }

        /// <summary>Module plumbing: channels own their lifetime; nothing to pump.</summary>
        public void Pump()
        {
        }

        /// <summary>Module plumbing: channels are passive after init; nothing to release.</summary>
        public void Shutdown()
        {
        }
    }
}
