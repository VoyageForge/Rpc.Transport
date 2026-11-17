using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>
    /// 基于 TcpListener 的 RPC 服务端（长连接 + 全双工 + 事件模型）。
    /// <list type="bullet">
    /// <item>固定头帧 + TypeId 分发；</item>
    /// <item>握手 Auth；服务端可 QueryToken 抽查，一次错误即断开；</item>
    /// <item>支持 Ping/Pong（响应客户端心跳；可选服务端主动 Ping）；</item>
    /// <item>一条连接上循环处理多个请求，响应回带 RequestId。</item>
    /// </list>
    /// </summary>
    public sealed class TcpRpcServerTransport : IRpcTransport, IRpcServerTransport, IDisposable
    {
        /// <summary>当前是否持有监听器（诊断用）。</summary>
        internal bool IsListening { get { return _listener != null; } }

        /// <summary>Accept 循环任务（诊断用）。</summary>
        internal Task AcceptTask { get { return _acceptTask; } }

        private readonly int _maxFrameBytes;

        /// <summary>固定头帧编解码。</summary>
        private readonly TcpFrameProtocol _frame;

        private readonly IRpcSerializer _serializer;
        private readonly IPEndPoint _localEndPoint;
        private readonly int _backlog;
        private readonly string _requiredToken;
        private readonly TimeSpan _serverPingIdle;
        private readonly TimeSpan _serverPingTimeout;
        private readonly bool _enableServerPing;

        /// <summary>服务端主动抽查 token 的间隔；null 表示不主动抽查。</summary>
        private readonly TimeSpan? _queryTokenInterval;

        private TcpListener _listener;
        private CancellationTokenSource _lifetime;
        private Task _acceptTask;
        private CancellationTokenRegistration _stopListenerRegistration;

        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new ConcurrentDictionary<TcpClient, byte>();
        private readonly ConcurrentDictionary<int, Task> _connectionTasks = new ConcurrentDictionary<int, Task>();
        private int _nextConnectionId;

        /// <summary>兼容旧接口的请求事件。</summary>
        public event Func<RpcRequest, Task<RpcResponse>> OnRequest;

        /// <summary>推荐使用的请求事件；服务端停止时 ct 会被取消。</summary>
        public event Func<RpcRequest, CancellationToken, Task<RpcResponse>> OnRequestAsync;

        /// <summary>单个连接发生协议、序列化或 Socket 错误时通知 Host。</summary>
        public event Action<Exception> OnTransportError;

        /// <summary>客户端连接建立时触发。</summary>
        public event Action<TcpClient> OnClientConnected;

        /// <summary>客户端连接关闭时触发。</summary>
        public event Action<TcpClient> OnClientDisconnected;

        /// <summary>创建 TCP RPC 服务端。</summary>
        public TcpRpcServerTransport(
            IPAddress address,
            int port,
            IRpcSerializer serializer = null,
            string requiredToken = null,
            int maxFrameBytes = 1024 * 1024,
            int backlog = 128,
            bool enableServerPing = false,
            TimeSpan? serverPingIdle = null,
            TimeSpan? serverPingTimeout = null,
            TimeSpan? queryTokenInterval = null)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            if (maxFrameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
            if (backlog <= 0) throw new ArgumentOutOfRangeException(nameof(backlog));

            _localEndPoint = new IPEndPoint(address, port);
            _serializer = serializer ?? new NewtonsoftRpcSerializer();
            _requiredToken = requiredToken;
            _maxFrameBytes = maxFrameBytes;
            _frame = new TcpFrameProtocol(maxFrameBytes);
            _backlog = backlog;
            _enableServerPing = enableServerPing;
            _serverPingIdle = serverPingIdle ?? TimeSpan.FromSeconds(15);
            _serverPingTimeout = serverPingTimeout ?? TimeSpan.FromSeconds(10);
            _queryTokenInterval = queryTokenInterval;
        }

        /// <summary>当前服务端绑定的本地地址（端口 0 时返回实际分配端口）。</summary>
        public IPEndPoint LocalEndPoint
        {
            get
            {
                TcpListener listener = _listener;
                return listener == null ? _localEndPoint : (IPEndPoint)listener.LocalEndpoint;
            }
        }

        /// <summary>启动 TCP 监听。</summary>
        public Task StartAsync(CancellationToken ct)
        {
            if (_listener != null) throw new InvalidOperationException("TCP RPC 服务端已经启动。");
            ct.ThrowIfCancellationRequested();

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener = new TcpListener(_localEndPoint);
            try
            {
                _listener.Start(_backlog);
                _stopListenerRegistration = _lifetime.Token.Register(() =>
                {
                    try
                    {
                        _listener?.Stop();
                    }
                    catch
                    {
                    }

                    foreach (TcpClient activeClient in _clients.Keys)
                    {
                        try
                        {
                            activeClient.Close();
                        }
                        catch
                        {
                        }
                    }
                });
                _acceptTask = AcceptLoopAsync(_lifetime.Token);
            }
            catch
            {
                _listener.Stop();
                _listener = null;
                _stopListenerRegistration.Dispose();
                _lifetime.Dispose();
                _lifetime = null;
                throw;
            }

            return Task.CompletedTask;
        }

        /// <summary>停止监听并等待连接任务退出。</summary>
        public async Task StopAsync(CancellationToken ct)
        {
            TcpListener listener = _listener;
            CancellationTokenSource lifetime = _lifetime;
            if (listener == null || lifetime == null) return;

            lifetime.Cancel();
            try
            {
                listener.Stop();
            }
            catch
            {
            }

            Task acceptTask = _acceptTask;
            if (acceptTask != null)
            {
                try
                {
                    await acceptTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }
            }

            foreach (TcpClient client in _clients.Keys)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }

            Task[] activeTasks = _connectionTasks.Values.ToArray();
            if (activeTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(activeTasks).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _listener = null;
            _acceptTask = null;
            _stopListenerRegistration.Dispose();
            _lifetime.Dispose();
            _lifetime = null;
            _clients.Clear();
            _connectionTasks.Clear();
        }

        /// <summary>同步释放：停止监听并关闭所有连接（用于宿主 Play 退出/OnDestroy）。</summary>
        public void Dispose()
        {
            try
            {
                StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Dispose 不应抛异常中断宿主清理流程。
            }
        }
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    _clients.TryAdd(client, 0);
                    TrackClientTask(client, ct);
                }
                catch (ObjectDisposedException exception)
                {
                    if (client != null) CloseClient(client);
                    if (ct.IsCancellationRequested) break;
                    ReportTransportError(exception);
                    continue;
                }
                catch (SocketException exception)
                {
                    if (client != null) CloseClient(client);
                    if (ct.IsCancellationRequested) break;
                    ReportTransportError(exception);
                    continue;
                }
                catch (Exception exception)
                {
                    if (client != null) CloseClient(client);
                    if (ct.IsCancellationRequested) break;
                    ReportTransportError(exception);
                    continue;
                }
            }
        }

        private void TrackClientTask(TcpClient client, CancellationToken serverToken)
        {
            int id = Interlocked.Increment(ref _nextConnectionId);
            Task task = HandleClientAsync(client, serverToken);
            _connectionTasks[id] = task;
            task.ContinueWith(
                completed =>
                {
                    _connectionTasks.TryRemove(id, out _);
                    // 观察异常，避免未观察任务异常；连接级错误已由 HandleClientAsync 上报。
                    _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>处理一条长连接：鉴权、心跳、循环处理请求。</summary>
        private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
        {
            OnClientConnected?.Invoke(client);

            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (CancellationTokenSource requestLifetime =
                       CancellationTokenSource.CreateLinkedTokenSource(serverToken))
                {
                    bool authenticated = string.IsNullOrEmpty(_requiredToken);
                    long lastPongTicks = DateTime.UtcNow.Ticks;
                    long lastTokenTicks = DateTime.UtcNow.Ticks;
                    Task pingTask = _enableServerPing
                        ? Task.Run(() => ServerPingLoopAsync(stream, requestLifetime.Token,
                            () => Interlocked.Read(ref lastPongTicks)))
                        : null;

                    while (!requestLifetime.IsCancellationRequested)
                    {
                        TcpFrameProtocol.Frame? frame = await _frame.ReadFrameAsync(stream, requestLifetime.Token)
                            .ConfigureAwait(false);
                        if (frame == null) break;

                        switch (frame.Value.TypeId)
                        {
                            case RpcMessageType.Ping:
                                await _frame.WriteFrameAsync(stream, RpcMessageType.Pong, Array.Empty<byte>(),
                                    requestLifetime.Token).ConfigureAwait(false);
                                break;

                            case RpcMessageType.Pong:
                                Interlocked.Exchange(ref lastPongTicks, DateTime.UtcNow.Ticks);
                                break;

                            case RpcMessageType.Auth:
                            {
                                bool ok = IsTokenValid(frame.Value.Payload);
                                await WriteAuthResultAsync(stream, ok, requestLifetime.Token).ConfigureAwait(false);
                                if (!ok)
                                {
                                    // 一次错误直接断开。
                                    return;
                                }

                                authenticated = true;
                                break;
                            }

                            case RpcMessageType.Token:
                            {
                                if (!IsTokenValid(frame.Value.Payload))
                                {
                                    // 抽查失败：一次错误直接断开。
                                    return;
                                }

                                Interlocked.Exchange(ref lastTokenTicks, DateTime.UtcNow.Ticks);
                                break;
                            }

                            case RpcMessageType.RpcRequest:
                            {
                                if (!authenticated)
                                {
                                    await WriteAuthResultAsync(stream, false, requestLifetime.Token)
                                        .ConfigureAwait(false);
                                    return;
                                }

                                RpcRequest request = DeserializeRequest(frame.Value.Payload);
                                if (request == null)
                                {
                                    // 无法解析的请求体返回稳定错误码，不调用业务处理器。
                                    RpcResponse invalid = RpcResponse.Fail("INVALID_REQUEST", "请求内容无法解析。");
                                    byte[] invalidPayload = _serializer.Serialize(new Protocol<RpcResponse>
                                    {
                                        Version = TcpRpcClientTransport.ProtocolVersion,
                                        Data = invalid
                                    });
                                    await _frame.WriteFrameAsync(stream, RpcMessageType.RpcResponse, invalidPayload, requestLifetime.Token).ConfigureAwait(false);
                                    break;
                                }

                                RpcResponse response;
                                try
                                {
                                    Func<RpcRequest, CancellationToken, Task<RpcResponse>>
                                        asyncHandler = OnRequestAsync;
                                    Func<RpcRequest, Task<RpcResponse>> legacyHandler = OnRequest;
                                    if (asyncHandler != null)
                                        response = await asyncHandler(request, requestLifetime.Token)
                                            .ConfigureAwait(false);
                                    else if (legacyHandler != null)
                                        response = await legacyHandler(request).ConfigureAwait(false);
                                    else
                                        response = RpcResponse.Fail("HANDLER_NOT_REGISTERED", "TCP RPC 服务端尚未绑定请求处理器。");
                                }
                                catch (OperationCanceledException)
                                {
                                    response = RpcResponse.Fail("CANCELLED", "请求已取消。");
                                }
                                catch (Exception exception)
                                {
                                    response = RpcResponse.Fail("SERVER_HANDLER_ERROR", exception.Message);
                                }

                                if (response == null)
                                {
                                    response = RpcResponse.Fail("EMPTY_RESPONSE", "请求处理器返回了空响应。");
                                }

                                response.RequestId = request.RequestId;
                                byte[] responsePayload = _serializer.Serialize(new Protocol<RpcResponse>
                                {
                                    Version = TcpRpcClientTransport.ProtocolVersion,
                                    Data = response
                                });
                                await _frame.WriteFrameAsync(stream, RpcMessageType.RpcResponse, responsePayload,
                                    requestLifetime.Token).ConfigureAwait(false);
                                break;
                            }
                        }
                    }

                    if (pingTask != null)
                    {
                        try
                        {
                            await pingTask.ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException exception)
            {
                if (!serverToken.IsCancellationRequested) ReportTransportError(exception);
            }
            catch (SocketException exception)
            {
                if (!serverToken.IsCancellationRequested) ReportTransportError(exception);
            }
            catch (Exception exception)
            {
                if (!serverToken.IsCancellationRequested) ReportTransportError(exception);
            }
            finally
            {
                OnClientDisconnected?.Invoke(client);
                CloseClient(client);
            }
        }

        /// <summary>服务端主动抽查 token：定时下发 QueryToken，超时未收到 Token 则断开。</summary>
        private async Task TokenQueryLoopAsync(NetworkStream stream, CancellationToken ct, Func<long> getLastTokenTicks)
        {
            TimeSpan interval = _queryTokenInterval.GetValueOrDefault();
            TimeSpan timeout = TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    long lastToken = getLastTokenTicks();
                    if (DateTime.UtcNow.Ticks - lastToken > timeout.Ticks)
                    {
                        ReportTransportError(new TimeoutException("客户端 token 抽查超时。"));
                        return;
                    }

                    await _frame.WriteFrameAsync(stream, RpcMessageType.QueryToken, Array.Empty<byte>(), ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!ct.IsCancellationRequested) ReportTransportError(exception);
            }
        }
        /// <summary>服务端主动心跳：空闲超过阈值发 Ping，等待 Pong 超时断开。</summary>
        private async Task ServerPingLoopAsync(NetworkStream stream, CancellationToken ct, Func<long> getLastPongTicks)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_serverPingIdle, ct).ConfigureAwait(false);
                    long lastPong = getLastPongTicks();
                    if (DateTime.UtcNow.Ticks - lastPong > _serverPingTimeout.Ticks)
                    {
                        ReportTransportError(new TimeoutException("客户端心跳超时。"));
                        return;
                    }

                    await _frame.WriteFrameAsync(stream, RpcMessageType.Ping, Array.Empty<byte>(), ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!ct.IsCancellationRequested) ReportTransportError(exception);
            }
        }

        /// <summary>解析 RpcRequest（Protocol 信封优先，裸消息回退）。</summary>
        private RpcRequest DeserializeRequest(byte[] payload)
        {
            try
            {
                Protocol<RpcRequest> envelope = _serializer.Deserialize<Protocol<RpcRequest>>(payload);
                if (envelope != null && envelope.Data != null)
                {
                    return envelope.Data;
                }

                return _serializer.Deserialize<RpcRequest>(payload);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>校验 token（payload 为 UTF-8 字符串）。</summary>
        private bool IsTokenValid(byte[] payload)
        {
            if (string.IsNullOrEmpty(_requiredToken))
            {
                return true;
            }

            if (payload == null || payload.Length == 0)
            {
                return false;
            }

            return string.Equals(Encoding.UTF8.GetString(payload), _requiredToken, StringComparison.Ordinal);
        }

        /// <summary>写鉴权结果（1 字节：1 成功 / 0 失败）。</summary>
        private async Task WriteAuthResultAsync(NetworkStream stream, bool success, CancellationToken ct)
        {
            await _frame.WriteFrameAsync(stream, RpcMessageType.AuthResult,
                new[] { success ? (byte)1 : (byte)0 }, ct).ConfigureAwait(false);
        }

        private void CloseClient(TcpClient client)
        {
            if (client == null) return;
            _clients.TryRemove(client, out _);
            try
            {
                client.Close();
            }
            catch
            {
            }
        }

        private void ReportTransportError(Exception exception)
        {
            Action<Exception> handler = OnTransportError;
            if (handler == null) return;
            try
            {
                handler(exception);
            }
            catch
            {
            }
        }
    }
}