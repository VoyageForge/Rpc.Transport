using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>
    /// TCP RPC 客户端传输（长连接 + 全双工 + 事件模型）。
    /// <list type="bullet">
    /// <item>固定头帧 + TypeId 分发；</item>
    /// <item>请求/响应通过 RequestId 关联，支持并发在途请求；</item>
    /// <item>心跳 Ping/Pong（客户端定时发起，也响应服务端主动 Ping）；</item>
    /// <item>握手 Auth + 服务端 QueryToken 抽查；</item>
    /// <item>断开后固定间隔重连；在途请求统一返回 CONNECTION_LOST。</item>
    /// </list>
    /// </summary>
    public sealed class TcpRpcClientTransport : IRpcClientTransport, IDisposable
    {
        /// <summary>当前协议版本。</summary>
        public const string ProtocolVersion = "1.0";

        /// <summary>连接断开时在途请求返回的固定错误码。</summary>
        public const string ConnectionLostCode = "CONNECTION_LOST";

        private readonly string _host;
        private readonly int _port;
        private readonly IRpcSerializer _serializer;
        private readonly int _maxFrameBytes;

        /// <summary>固定头帧编解码。</summary>
        private readonly TcpFrameProtocol _frame;

        private readonly string _token;
        private readonly TimeSpan _pingInterval;
        private readonly TimeSpan _pingTimeout;
        private readonly TimeSpan _reconnectDelay;

        private readonly ConcurrentDictionary<uint, TaskCompletionSource<RpcResponse>> _pending =
            new ConcurrentDictionary<uint, TaskCompletionSource<RpcResponse>>();

        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);

        private readonly object _requestIdLock = new object();
        private uint _nextRequestId;

        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _connectionCts;
        private Task _receiveTask;
        private Task _heartbeatTask;
        private volatile bool _connected;
        private long _lastPongTicks;
        private bool _disposed;

        /// <summary>创建 TCP RPC 客户端。</summary>
        public TcpRpcClientTransport(
            string host,
            int port,
            IRpcSerializer serializer = null,
            string token = null,
            int maxFrameBytes = 1024 * 1024,
            TimeSpan? pingInterval = null,
            TimeSpan? pingTimeout = null,
            TimeSpan? reconnectDelay = null)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("主机名不能为空。", nameof(host));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            if (maxFrameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));

            _host = host;
            _port = port;
            _serializer = serializer ?? new NewtonsoftRpcSerializer();
            _token = token;
            _maxFrameBytes = maxFrameBytes;
            _frame = new TcpFrameProtocol(maxFrameBytes);
            _pingInterval = pingInterval ?? TimeSpan.FromSeconds(15);
            _pingTimeout = pingTimeout ?? TimeSpan.FromSeconds(45);
            _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(3);
        }

        /// <summary>目标主机。</summary>
        public string Host
        {
            get { return _host; }
        }

        /// <summary>目标端口。</summary>
        public int Port
        {
            get { return _port; }
        }

        /// <summary>当前是否已连接。</summary>
        public bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>连接建立并完成鉴权后触发。</summary>
        public event Action OnConnected;

        /// <summary>连接断开时触发。</summary>
        public event Action<Exception> OnDisconnected;

        /// <summary>传输层错误通知。</summary>
        public event Action<Exception> OnTransportError;

        /// <summary>申请下一个 RequestId（带锁、循环回绕，0 保留）。</summary>
        public uint NextRequestId()
        {
            lock (_requestIdLock)
            {
                _nextRequestId++;
                if (_nextRequestId == 0)
                {
                    _nextRequestId = 1;
                }

                return _nextRequestId;
            }
        }

        /// <summary>发送请求并等待响应；RequestId 为空时自动分配。</summary>
        public async Task<RpcResponse> SendAsync(RpcRequest request, CancellationToken ct = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_disposed) throw new ObjectDisposedException(nameof(TcpRpcClientTransport));

            if (request.RequestId == 0)
            {
                request.RequestId = NextRequestId();
            }

            // 先序列化并校验长度：超长/序列化异常不应先建立连接。
            byte[] requestPayload = _serializer.Serialize(new Protocol<RpcRequest>
            {
                Version = ProtocolVersion,
                Data = request
            });
            if (requestPayload == null || requestPayload.Length == 0)
            {
                throw new InvalidDataException("序列化后的 RPC 请求为空。");
            }

            if (requestPayload.Length > _maxFrameBytes)
            {
                throw new InvalidDataException("RPC 请求超过最大帧长度：" + requestPayload.Length);
            }

            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            TaskCompletionSource<RpcResponse> completion =
                new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(request.RequestId, completion))
            {
                throw new InvalidOperationException("RequestId 已在途：" + request.RequestId);
            }

            try
            {
                await WriteFrameAsync(RpcMessageType.RpcRequest, requestPayload, ct).ConfigureAwait(false);

                using (ct.Register(() =>
                       {
                           if (_pending.TryRemove(request.RequestId, out TaskCompletionSource<RpcResponse> entry))
                           {
                               entry.TrySetCanceled(ct);
                           }
                       }))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                _pending.TryRemove(request.RequestId, out _);
            }
        }

        /// <summary>确保连接（含鉴权）已就绪；断开后固定间隔重连。</summary>
        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (_connected)
            {
                return;
            }

            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_connected)
                {
                    return;
                }

                // 固定间隔重连：给服务端一点恢复时间。
                if (_client != null)
                {
                    await Task.Delay(_reconnectDelay, ct).ConfigureAwait(false);
                }

                TcpClient client = new TcpClient();
                NetworkStream stream = null;
                try
                {
                    await client.ConnectAsync(_host, _port).ConfigureAwait(false);
                    client.NoDelay = true;
                    stream = client.GetStream();

                    _client = client;
                    _stream = stream;
                    _connectionCts = new CancellationTokenSource();
                    _lastPongTicks = DateTime.UtcNow.Ticks;

                    if (!string.IsNullOrEmpty(_token))
                    {
                        await AuthenticateAsync(ct).ConfigureAwait(false);
                    }

                    _connected = true;
                    _receiveTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token));
                    _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_connectionCts.Token));
                    OnConnected?.Invoke();
                }
                catch (Exception exception)
                {
                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                    }

                    _client = null;
                    _stream = null;
                    OnTransportError?.Invoke(exception);
                    throw;
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>握手鉴权：发送 Auth 并等待 AuthResult。</summary>
        private async Task AuthenticateAsync(CancellationToken ct)
        {
            byte[] tokenPayload = System.Text.Encoding.UTF8.GetBytes(_token);
            await WriteFrameAsync(RpcMessageType.Auth, tokenPayload, ct).ConfigureAwait(false);

            // 握手阶段直接读一帧 AuthResult（此时读循环尚未启动）。
            TcpFrameProtocol.Frame? frame = await _frame.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
            if (frame == null || frame.Value.TypeId != RpcMessageType.AuthResult)
            {
                throw new InvalidDataException("服务端未返回鉴权结果。");
            }

            if (frame.Value.Payload.Length == 0 || frame.Value.Payload[0] == 0)
            {
                throw new UnauthorizedAccessException("鉴权失败。");
            }
        }

        /// <summary>后台读循环：按 TypeId 分发响应、心跳、鉴权抽查。</summary>
        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            Exception failure = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpFrameProtocol.Frame? frame = await _frame.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                    if (frame == null)
                    {
                        break;
                    }

                    switch (frame.Value.TypeId)
                    {
                        case RpcMessageType.Ping:
                            await WriteFrameAsync(RpcMessageType.Pong, Array.Empty<byte>(), ct).ConfigureAwait(false);
                            break;

                        case RpcMessageType.Pong:
                            _lastPongTicks = DateTime.UtcNow.Ticks;
                            break;

                        case RpcMessageType.QueryToken:
                            if (!string.IsNullOrEmpty(_token))
                            {
                                await WriteFrameAsync(RpcMessageType.Token,
                                    System.Text.Encoding.UTF8.GetBytes(_token), ct).ConfigureAwait(false);
                            }

                            break;

                        case RpcMessageType.RpcResponse:
                            CompletePending(frame.Value.Payload);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
                if (!ct.IsCancellationRequested) OnTransportError?.Invoke(exception);
            }
            finally
            {
                HandleDisconnected(failure);
            }
        }

        /// <summary>后台心跳：定时发 Ping，超时未收到 Pong 判定断开。</summary>
        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_pingInterval, ct).ConfigureAwait(false);
                    if (!_connected)
                    {
                        continue;
                    }

                    long lastPong = Interlocked.Read(ref _lastPongTicks);
                    if (DateTime.UtcNow.Ticks - lastPong > _pingTimeout.Ticks)
                    {
                        HandleDisconnected(new TimeoutException("心跳超时，连接判定断开。"));
                        return;
                    }

                    await WriteFrameAsync(RpcMessageType.Ping, Array.Empty<byte>(), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!ct.IsCancellationRequested) OnTransportError?.Invoke(exception);
            }
        }

        /// <summary>完成一个待处理请求。</summary>
        private void CompletePending(byte[] payload)
        {
            Protocol<RpcResponse> envelope = _serializer.Deserialize<Protocol<RpcResponse>>(payload);
            RpcResponse response = envelope != null && envelope.Data != null
                ? envelope.Data
                : _serializer.Deserialize<RpcResponse>(payload);

            if (response == null)
            {
                return;
            }

            if (response.RequestId != 0 &&
                _pending.TryRemove(response.RequestId, out TaskCompletionSource<RpcResponse> completion))
            {
                completion.TrySetResult(response);
            }
        }

        /// <summary>处理断开：通知事件、失败所有在途请求、清理连接。</summary>
        private void HandleDisconnected(Exception reason)
        {
            bool wasConnected = _connected;
            _connected = false;

            TcpClient client = _client;
            _client = null;
            _stream = null;
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            _connectionCts = null;

            if (client != null)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }

            FailAllPending(ConnectionLostCode);

            if (wasConnected)
            {
                OnDisconnected?.Invoke(reason);
            }
        }

        /// <summary>所有在途请求以固定错误结束。</summary>
        private void FailAllPending(string code)
        {
            foreach (uint requestId in _pending.Keys)
            {
                if (_pending.TryRemove(requestId, out TaskCompletionSource<RpcResponse> completion))
                {
                    completion.TrySetResult(RpcResponse.Fail(code, "连接已断开。"));
                }
            }
        }

        private async Task WriteFrameAsync(ushort typeId, byte[] payload, CancellationToken ct)
        {
            NetworkStream stream = _stream;
            if (stream == null) throw new IOException("TCP 连接尚未建立。");
            await _frame.WriteFrameAsync(stream, typeId, payload, ct).ConfigureAwait(false);
        }

        /// <summary>释放连接与资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            HandleDisconnected(null);
            _connectGate.Dispose();
        }
    }
}