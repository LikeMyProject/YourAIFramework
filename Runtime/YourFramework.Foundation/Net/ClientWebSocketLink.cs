using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace YourFramework.Net
{
    /// <summary>
    /// BCL ClientWebSocket 链路：把异步套接字包进框架的轮询泵语义 —— 内部允许
    /// Task，公开面永远只有 Pump；每帧收割完成态（连接/发送/接收），失败一律
    /// 转成 OnLinkClose(reason)，绝不抛出（"失败是值"）。
    ///
    /// 平台注记：netstandard2.1 可编译；Windows/Android/iOS/macOS 运行时可用，
    /// WebGL 需要另写平台适配的 INetLink（接口可换，这正是分层的意义）。
    /// 本类只做编译门禁级验证；活链路联调在真机/本地回环上进行。
    /// </summary>
    public sealed class ClientWebSocketLink : INetLink
    {
        private readonly int _receiveBufferSize;
        private readonly int _maxDeliveriesPerPump;
        private ClientWebSocket _socket;
        private Task _connectTask;
        private Task<WebSocketReceiveResult> _receiveTask;
        private readonly List<Task> _sends = new List<Task>();
        private readonly byte[] _receiveBuffer;
        private string _url;
        private bool _closing;
        private bool _dead;
        private string _closeReason;
        private INetLinkListener _listener;

        /// <summary>Builds the link. Buffer size is the per-receive chunk; delivery cap bounds per-frame work.</summary>
        public ClientWebSocketLink(int receiveBufferSize = 16 * 1024, int maxDeliveriesPerPump = 8)
        {
            if (receiveBufferSize < 1)
            {
                throw new ArgumentException("ClientWebSocketLink: receive buffer must be >= 1 byte.", "receiveBufferSize");
            }

            if (maxDeliveriesPerPump < 1)
            {
                throw new ArgumentException("ClientWebSocketLink: delivery cap must be >= 1.", "maxDeliveriesPerPump");
            }

            _receiveBufferSize = receiveBufferSize;
            _maxDeliveriesPerPump = maxDeliveriesPerPump;
            _receiveBuffer = new byte[receiveBufferSize];
        }

        /// <summary>Link name (diagnostics).</summary>
        public string Name { get { return "ClientWebSocket"; } }

        /// <summary>Whether an open socket exists (diagnostics only).</summary>
        public bool IsSocketOpen
        {
            get
            {
                return _socket != null && _socket.State == WebSocketState.Open;
            }
        }

        /// <summary>Begins the async connect; the result is harvested in Pump.</summary>
        public void Connect(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("ClientWebSocketLink.Connect: url must not be empty.", "url");
            }

            DisposeSocket();
            _url = url;
            _closing = false;
            _dead = false;
            _closeReason = null;
            _sends.Clear();

            ClientWebSocket socket = new ClientWebSocket();
            _socket = socket;
            _connectTask = socket.ConnectAsync(new Uri(url), CancellationToken.None);
        }

        /// <summary>Sends on the open socket as a tracked fire-and-forget task; failures surface as close.</summary>
        public void Send(byte[] buffer, int offset, int count)
        {
            ClientWebSocket socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open || _closing)
            {
                // 调用方合约违规（NetClient 只在 Open 时直发），但链接层仍按
                // "失败是值"处理：转成关闭事件而不是抛异常。
                ReportClose("send on a non-open socket");
                return;
            }

            ArraySegment<byte> segment = new ArraySegment<byte>(buffer, offset, count);
            _sends.Add(socket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None));
        }

        /// <summary>Requests a graceful close (fire and forget); the harvest turns it into events.</summary>
        public void Close()
        {
            _closing = true;
            ClientWebSocket socket = _socket;
            if (socket == null || socket.State == WebSocketState.Closed || socket.State == WebSocketState.Aborted)
            {
                return;
            }

            try
            {
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", CancellationToken.None);
            }
            catch (Exception ex)
            {
                // 关闭路径上的异常没有听众：记为理由，等待 Pump 转交。
                _closeReason = "close failed: " + ex.Message;
            }
        }

        /// <summary>Harvests connect/send/receive completion, delivering link events synchronously.</summary>
        public void Pump(INetLinkListener listener)
        {
            _listener = listener;

            if (_connectTask != null && _connectTask.IsCompleted)
            {
                Task finished = _connectTask;
                _connectTask = null;
                if (finished.IsFaulted || finished.IsCanceled)
                {
                    ReportClose("connect failed: " + Describe(finished));
                    return;
                }

                StartReceive();
                listener.OnLinkOpen();
            }

            if (_receiveTask != null && _receiveTask.IsCompleted)
            {
                Task<WebSocketReceiveResult> receive = _receiveTask;
                _receiveTask = null;
                if (receive.IsFaulted || receive.IsCanceled)
                {
                    ReportClose("receive failed: " + Describe(receive));
                    return;
                }

                WebSocketReceiveResult result = receive.Result;
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ReportClose(null); // remote clean close
                    return;
                }

                listener.OnLinkData(_receiveBuffer, 0, result.Count);
                StartReceive();
            }

            for (int i = _sends.Count - 1; i >= 0; i--)
            {
                Task send = _sends[i];
                if (!send.IsCompleted)
                {
                    continue;
                }

                _sends.RemoveAt(i);
                if (send.IsFaulted || send.IsCanceled)
                {
                    ReportClose("send failed: " + Describe(send));
                    return;
                }
            }

            if (_closing && _closeReason != null)
            {
                ReportClose(_closeReason);
            }
        }

        private void StartReceive()
        {
            if (_socket == null || _socket.State != WebSocketState.Open || _closing)
            {
                return;
            }

            _receiveTask = _socket.ReceiveAsync(
                new ArraySegment<byte>(_receiveBuffer), CancellationToken.None);
        }

        private void ReportClose(string reason)
        {
            if (_dead)
            {
                return; // close events fire exactly once per link lifetime
            }

            _dead = true;
            _closing = true;
            DisposeSocket();

            INetLinkListener listener = _listener;
            _listener = null;
            _connectTask = null;
            _receiveTask = null;
            _sends.Clear();
            if (listener != null)
            {
                listener.OnLinkClose(reason);
            }
        }

        private void DisposeSocket()
        {
            ClientWebSocket socket = _socket;
            _socket = null;
            if (socket != null)
            {
                try
                {
                    socket.Abort();
                    socket.Dispose();
                }
                catch (Exception)
                {
                    // 终止路径上没有更多可做的；链接已死，事件层会兜住。
                }
            }
        }

        private static string Describe(Task task)
        {
            Exception ex = task.Exception;
            if (ex == null)
            {
                return task.IsCanceled ? "canceled" : "unknown";
            }

            ex = ex.GetBaseException();
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
