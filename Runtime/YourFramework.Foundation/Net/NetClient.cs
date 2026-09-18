using System;
using System.Collections.Generic;
using YourFramework.Core;

namespace YourFramework.Net
{
    /// <summary>Link lifecycle. Idle → Connecting → Open → (Reconnecting ⇄ Connecting) → Closed.</summary>
    public enum NetState
    {
        /// <summary>Not configured or never started.</summary>
        Idle,
        /// <summary>Connect in flight (first attempt or a reconnect attempt).</summary>
        Connecting,
        /// <summary>Link is open and passing traffic.</summary>
        Open,
        /// <summary>Link dropped; waiting out the backoff delay before the next attempt.</summary>
        Reconnecting,
        /// <summary>Final: gave up, or closed by the caller. Terminal.</summary>
        Closed
    }

    /// <summary>
    /// One byte pipe (WebSocket, mock, kcp...). Contract:
    ///
    /// - Connect/Close are intentions; progress happens inside Pump;
    /// - Pump delivers lifecycle and data synchronously to the listener
    ///   (OnOpen / OnData / OnClose); the data buffer is link-owned and only
    ///   valid during the callback;
    /// - Send must not throw: a send failure surfaces as a close at the next
    ///   Pump ("failure as value" end to end);
    /// - OnClose(reason) with an empty reason means a clean close.
    /// </summary>
    public interface INetLink
    {
        /// <summary>Link name (diagnostics).</summary>
        string Name { get; }

        /// <summary>Begins connecting. Called only from Idle/Closed-adjacent attempts.</summary>
        void Connect(string url);

        /// <summary>Sends bytes on an open link. Must not throw; failures surface as close.</summary>
        void Send(byte[] buffer, int offset, int count);

        /// <summary>Requests a graceful close.</summary>
        void Close();

        /// <summary>Advances the link one frame; delivers events to the listener.</summary>
        void Pump(INetLinkListener listener);
    }

    /// <summary>Raw link events (the client adapts these into its state machine).</summary>
    public interface INetLinkListener
    {
        /// <summary>Link established.</summary>
        void OnLinkOpen();

        /// <summary>Bytes arrived. Buffer is link-owned, valid during the call only.</summary>
        void OnLinkData(byte[] buffer, int offset, int count);

        /// <summary>Link ended; empty reason = clean close.</summary>
        void OnLinkClose(string reason);
    }

    /// <summary>All timing knobs, explicit by red line: nothing retries silently.</summary>
    public sealed class NetClientConfig
    {
        /// <summary>Endpoint; the link interprets the scheme (ws://, mock://...).</summary>
        public string Url;

        /// <summary>Seconds before a connect attempt is abandoned. 0 disables.</summary>
        public float ConnectTimeoutSeconds;

        /// <summary>Seconds between keepalive pings. 0 disables sending pings.</summary>
        public float HeartbeatIntervalSeconds;

        /// <summary>Seconds of silence (no inbound bytes) before the link is deemed dead. 0 disables.</summary>
        public float HeartbeatTimeoutSeconds;

        /// <summary>Bytes sent as the keepalive ping. Default ASCII "ping".</summary>
        public byte[] PingPayload;

        /// <summary>Reconnect attempts before giving up. 0 = keep trying forever.</summary>
        public int MaxReconnectAttempts;

        /// <summary>First backoff delay in seconds.</summary>
        public float ReconnectBaseDelaySeconds;

        /// <summary>Backoff ceiling in seconds (delays stop doubling here).</summary>
        public float ReconnectMaxDelaySeconds;

        /// <summary>Outbound queue capacity while not open; overflow drops the OLDEST item.</summary>
        public int QueueCapacity;

        /// <summary>Defaults: 10s connect timeout, 15s ping / 30s silence, 5 retries 1s→8s, queue 64.</summary>
        public static NetClientConfig CreateDefault(string url)
        {
            return new NetClientConfig
            {
                Url = url,
                ConnectTimeoutSeconds = 10f,
                HeartbeatIntervalSeconds = 15f,
                HeartbeatTimeoutSeconds = 30f,
                PingPayload = new byte[] { (byte)'p', (byte)'i', (byte)'n', (byte)'g' },
                MaxReconnectAttempts = 5,
                ReconnectBaseDelaySeconds = 1f,
                ReconnectMaxDelaySeconds = 8f,
                QueueCapacity = 64
            };
        }
    }

    /// <summary>Client events (game-facing).</summary>
    public interface INetClientListener
    {
        /// <summary>Link open; queued outbound traffic is being flushed.</summary>
        void OnOpen();

        /// <summary>Inbound bytes. Buffer is link-owned, valid during the call only.</summary>
        void OnData(byte[] buffer, int offset, int count);

        /// <summary>
        /// A transient drop: the client will reconnect (or is backing off).
        /// Fired when entering Reconnecting, with the drop reason.
        /// </summary>
        void OnDropped(string reason);

        /// <summary>Each reconnect attempt, with the delay that preceded it.</summary>
        void OnReconnectAttempt(int attemptNumber, float delaySeconds);

        /// <summary>Terminal close: gave up, gave up mid-connect, or closed by the caller.</summary>
        void OnClosed(string reason);
    }

    /// <summary>
    /// 网络客户端：连接状态机 + 心跳 + 指数退避重连 + 断线排队补发。
    ///
    /// 必须守的规矩四条的落点（公子拍板）：超时/重试/心跳/断线重连全部显式可配（一个
    /// config 对象摆在一起）；失败带原因（OnDropped/OnClosed 的 reason 非空，
    /// 空串=干净关闭）；轮询逐帧驱动——Pump(delta) 由调用方注入时间增量，无
    /// async/await 出现在任何公开 API 上，回放/快进/离线测试天然支持。
    ///
    /// 排队：未 Open 时的 Send 进有界队列（容量可配），Open 后按序补发；溢出丢
    /// 最旧并计数（游戏语义：新状态比旧状态重要）。
    ///
    /// 数据零拷贝：入站字节直接透传给 listener（link 所有权语义）；出站 Send
    /// 即拷贝（调用方缓冲区立即归还）。
    /// </summary>
    public sealed class NetClient : IModule, INetLinkListener
    {
        private readonly INetLink _link;
        private readonly NetClientConfig _config;
        private readonly Queue<byte[]> _outbound = new Queue<byte[]>();
        private readonly INetClientListener _listener;
        private NetState _state = NetState.Idle;
        private float _stateElapsed;
        private float _sinceLastPing;
        private float _sinceLastInbound;
        private float _pendingDelay;
        private int _reconnectAttempts;
        private int _droppedOldestCount;
        private byte[] _singlePing = new byte[1];

        /// <summary>Builds the client over a link, a config and a listener.</summary>
        public NetClient(INetLink link, NetClientConfig config, INetClientListener listener)
        {
            if (link == null)
            {
                throw new ArgumentNullException("link");
            }

            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            if (listener == null)
            {
                throw new ArgumentNullException("listener");
            }

            _link = link;
            _config = config;
            _listener = listener;
        }

        /// <summary>Current state.</summary>
        public NetState State { get { return _state; } }

        /// <summary>Reconnect attempts made for the current session (reset on manual Open/Close).</summary>
        public int ReconnectAttempts { get { return _reconnectAttempts; } }

        /// <summary>Outbound items waiting to flush.</summary>
        public int QueuedCount { get { return _outbound.Count; } }

        /// <summary>How many queued items were dropped as too old.</summary>
        public int DroppedOldestCount { get { return _droppedOldestCount; } }

        /// <summary>Last drop/give-up reason (empty = clean).</summary>
        public string LastReason { get; private set; }

        /// <summary>Begins the session: Idle/Closed → Connecting. Reconnecting is a no-op.</summary>
        public void Open()
        {
            if (_state == NetState.Open || _state == NetState.Connecting || _state == NetState.Reconnecting)
            {
                return; // already running; Open twice is business, not a bug
            }

            _reconnectAttempts = 0;
            _droppedOldestCount = 0;
            LastReason = null;
            BeginConnect();
        }

        /// <summary>Caller-ordered terminal close; no reconnect follows.</summary>
        public void Close(string reason)
        {
            if (_state == NetState.Closed)
            {
                return;
            }

            if (_state == NetState.Open || _state == NetState.Connecting)
            {
                _link.Close();
            }

            _outbound.Clear();
            _state = NetState.Closed;
            LastReason = string.IsNullOrEmpty(reason) ? "closed by caller" : reason;
            _listener.OnClosed(LastReason);
        }

        /// <summary>
        /// Sends bytes. Open → straight to the link; otherwise queued for the
        /// flush on open. The buffer is copied immediately. Closed sessions
        /// reject sends (caller bug at that point: the client is terminal).
        /// </summary>
        public void Send(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (_state == NetState.Closed)
            {
                throw new InvalidOperationException(
                    "NetClient.Send: the client is terminally closed; open a new session.");
            }

            byte[] copy = new byte[count];
            Array.Copy(buffer, offset, copy, 0, count);

            if (_state == NetState.Open)
            {
                _link.Send(copy, 0, copy.Length);
                return;
            }

            if (_outbound.Count >= _config.QueueCapacity)
            {
                _outbound.Dequeue(); // drop oldest: the newest state matters most
                _droppedOldestCount++;
            }

            _outbound.Enqueue(copy);
        }

        /// <summary>
        /// Advances one frame with a caller-supplied time delta (seconds) --
        /// this is the whole timing model: no wall clock anywhere, so tests can
        /// fast-forward and the host decides what a "frame" is.
        /// </summary>
        public void Pump(float deltaSeconds)
        {
            _stateElapsed += deltaSeconds;

            switch (_state)
            {
                case NetState.Connecting:
                    _link.Pump(this);
                    if (_state == NetState.Connecting
                        && _config.ConnectTimeoutSeconds > 0f
                        && _stateElapsed >= _config.ConnectTimeoutSeconds)
                    {
                        _link.Close();
                        OnDropped("connect timeout after " + _config.ConnectTimeoutSeconds + "s");
                    }

                    break;

                case NetState.Open:
                    _link.Pump(this);
                    if (_state != NetState.Open)
                    {
                        break; // the pump delivered a close; already handled
                    }

                    _sinceLastInbound += deltaSeconds;
                    if (_config.HeartbeatTimeoutSeconds > 0f
                        && _sinceLastInbound >= _config.HeartbeatTimeoutSeconds)
                    {
                        // The silence IS the reason: no inbound for too long.
                        _link.Close();
                        OnDropped("heartbeat timeout: no inbound for "
                            + _config.HeartbeatTimeoutSeconds.ToString() + "s");
                        break;
                    }

                    if (_config.HeartbeatIntervalSeconds > 0f)
                    {
                        _sinceLastPing += deltaSeconds;
                        if (_sinceLastPing >= _config.HeartbeatIntervalSeconds)
                        {
                            _sinceLastPing = 0f;
                            byte[] ping = _config.PingPayload ?? new byte[0];
                            _link.Send(ping, 0, ping.Length);
                        }
                    }

                    break;

                case NetState.Reconnecting:
                    _pendingDelay -= deltaSeconds;
                    if (_pendingDelay <= 0f)
                    {
                        BeginConnect();
                    }

                    break;
            }
        }

        /// <summary>Module plumbing.</summary>
        public string Name { get { return "Net"; } }

        /// <summary>Module plumbing: self-contained behind its link.</summary>
        public int InitOrder { get { return 30; } }

        /// <summary>Module plumbing. Nothing to fetch.</summary>
        public void Init(ModuleCenter host)
        {
        }

        /// <summary>Module plumbing.</summary>
        public void Pump()
        {
            Pump(0f); // module pump without a time delta: lifecycle-only progress
        }

        /// <summary>Module plumbing: terminal close, listeners still get the event.</summary>
        public void Shutdown()
        {
            if (_state != NetState.Closed)
            {
                Close("module shutdown");
            }
        }

        private void BeginConnect()
        {
            _state = NetState.Connecting;
            _stateElapsed = 0f;
            _sinceLastPing = 0f;
            _sinceLastInbound = 0f;
            _link.Connect(_config.Url);
        }

        private void OnDropped(string reason)
        {
            LastReason = reason;
            if (_config.MaxReconnectAttempts != 0 && _reconnectAttempts >= _config.MaxReconnectAttempts)
            {
                _outbound.Clear();
                _state = NetState.Closed;
                _listener.OnClosed("gave up after " + _reconnectAttempts + " reconnect attempts: " + reason);
                return;
            }

            _reconnectAttempts++;
            _pendingDelay = BackoffDelay(_reconnectAttempts);
            _state = NetState.Reconnecting;
            _stateElapsed = 0f;
            _listener.OnDropped(reason);
            _listener.OnReconnectAttempt(_reconnectAttempts, _pendingDelay);
        }

        private float BackoffDelay(int attempt)
        {
            float delay = _config.ReconnectBaseDelaySeconds;
            for (int i = 1; i < attempt && delay < _config.ReconnectMaxDelaySeconds; i++)
            {
                delay *= 2f;
            }

            return Math.Min(delay, _config.ReconnectMaxDelaySeconds);
        }

        void INetLinkListener.OnLinkOpen()
        {
            if (_state == NetState.Closed)
            {
                return; // the caller closed mid-connect; ignore the straggler
            }

            _state = NetState.Open;
            _stateElapsed = 0f;
            _sinceLastPing = 0f;
            _sinceLastInbound = 0f;
            _listener.OnOpen();
            FlushOutbound();
        }

        void INetLinkListener.OnLinkData(byte[] buffer, int offset, int count)
        {
            if (_state != NetState.Open)
            {
                return;
            }

            _sinceLastInbound = 0f; // any inbound byte proves liveness
            _listener.OnData(buffer, offset, count);
        }

        void INetLinkListener.OnLinkClose(string reason)
        {
            if (_state == NetState.Closed)
            {
                return;
            }

            OnDropped(string.IsNullOrEmpty(reason) ? "link closed cleanly" : "link closed: " + reason);
        }

        private void FlushOutbound()
        {
            while (_outbound.Count > 0)
            {
                byte[] item = _outbound.Dequeue();
                _link.Send(item, 0, item.Length);
            }
        }
    }
}
