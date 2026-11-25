using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>一条 TCP 连接上的双向 RPC Session。网络路由只使用 MethodId，业务 Payload 使用 MemoryPack。</summary>
    public abstract partial class Session : IDisposable
    {
        /// <summary>默认允许的单帧最大负载长度：1 MiB。</summary>
        public const int DefaultMaxFrameBytes = 1024 * 1024;

        private readonly TcpFrameProtocol _frame;
        private readonly RpcControllerRegistry _registry;

        private readonly ConcurrentDictionary<long, IPendingCall> _pending =
            new ConcurrentDictionary<long, IPendingCall>();

        // CallId 仅用于关联一次调用、响应和取消，不参与业务 Payload 的处理。
        // 每次本地调用都会分配新的 CallId。
        private readonly object _activeCallsGate = new object();

        private readonly
            System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<CancellationTokenSource>>
            _activeCalls =
                new System.Collections.Generic.Dictionary<long,
                    System.Collections.Generic.List<CancellationTokenSource>>();

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private long _nextCallId;
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly object _connectionGate = new object();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CancellationToken _connectionToken;
        private volatile bool _disposed;
        private readonly TaskCompletionSource<string> _closed =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>完成套接字清理后返回关闭原因：主动关闭、连接丢失或心跳超时。</summary>
        public Task<string> Closed => _closed.Task;

        private readonly System.Collections.Generic.Dictionary<Type, object> _remoteApis =
            new System.Collections.Generic.Dictionary<Type, object>();

        /// <summary>取得当前连接为指定远端契约创建的代理。</summary>
        public TApi Remote<TApi>() where TApi : class
        {
            if (_remoteApis.TryGetValue(typeof(TApi), out object proxy)) return (TApi)proxy;
            throw new InvalidOperationException(GetType().Name + " 未声明远端契约 " + typeof(TApi).FullName);
        }

        /// <summary>由生成的 Session 构造函数在连接启动前注册一次。</summary>
        protected void RegisterRemote<TApi>(TApi proxy) where TApi : class
        {
            _remoteApis.Add(typeof(TApi), proxy ?? throw new ArgumentNullException(nameof(proxy)));
        }

        /// <summary>获取本端 Controller；RPC Controller 在交付前绑定当前连接。</summary>
        public T Local<T>() where T : VoyageForge.Rpc.Executor.ControllerBase
        {
            T controller = _registry.Controllers.Local<T>();
            if (controller is RpcControllerBase rpc) rpc.SetSession(this);
            return controller;
        }

        /// <summary>固定本连接的分发表、帧大小上限和取消令牌。调用 Attach 后开始收包和心跳。</summary>
        protected Session(RpcControllerRegistry registry, int maxFrameBytes = DefaultMaxFrameBytes)
        {
            _connectionToken = _lifetime.Token;
            _registry = registry ?? new RpcControllerRegistry();
            _registry.Seal();
            _frame = new TcpFrameProtocol(maxFrameBytes);
        }

        /// <summary>当前 Session 独有的应用上下文存储。RPC 框架不解释其中的键和值，也不内置鉴权。</summary>
        public ConcurrentDictionary<string, object> Blackboard { get; } =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        /// <summary>底层 TCP 连接当前报告的连接状态。</summary>
        public bool IsConnected
        {
            get { return _client?.Connected == true; }
        }

        /// <summary>将一个已经建立连接的 TcpClient 绑定到当前 Session 并启动接收循环。</summary>
        /// <param name="client">已连接的 TCP 客户端或服务端已接受连接。</param>
        public void Attach(TcpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            lock (_connectionGate)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                if (_client != null) throw new InvalidOperationException("Session 已绑定连接。");
                int idle = HeartbeatIdleMilliseconds;
                int timeout = HeartbeatTimeoutMilliseconds;
                if (idle <= 0 || timeout <= 0) throw new InvalidOperationException("心跳间隔和超时必须为正毫秒数。");
                NetworkStream stream = client.GetStream();
                client.NoDelay = true;
                _client = client;
                _stream = stream;
                MarkFrameReceived();
                _ = MonitorHeartbeatAsync(idle, timeout, _connectionToken);
                _ = Task.Run(() => ReadLoopAsync(stream, _connectionToken));
            }
        }

        /// <summary>由生成的调用代理调用；TResponse 在编译期确定，避免运行时 Type 反射。</summary>
        public Task<TResult> InvokeAsync<TArgs, TResult>(ulong methodId, TArgs args, CancellationToken ct = default)
        {
            long id = Interlocked.Increment(ref _nextCallId);
            var request = new Request<TArgs>(methodId, id, args);
            return SendAsync<TResult>(request.MethodId, request.CallId, RpcWire.Encode(request.Data), ct);
        }

        /// <summary>发送已序列化的请求正文，以强类型结果完成对应的等待项。</summary>
        public Task<TResult> InvokeAsync<TResult>(ulong methodId, byte[] payload, CancellationToken ct = default)
            => SendAsync<TResult>(methodId, Interlocked.Increment(ref _nextCallId), payload, ct);

        /// <summary>为本次请求登记等待项，串行写入整帧，并用 CallId 匹配响应、取消或关闭通知。</summary>
        private async Task<TResult> SendAsync<TResult>(ulong methodId, long callId, byte[] body, CancellationToken ct)
        {
            NetworkStream stream = _stream ?? throw new IOException("Session is not connected.");
            if (methodId == 0) throw new ArgumentOutOfRangeException(nameof(methodId));
            ct.ThrowIfCancellationRequested();
            var completion = new PendingCall<TResult>(methodId, callId);
            if (!_pending.TryAdd(callId, completion)) throw new InvalidOperationException("Duplicate CallId.");
            try
            {
                _connectionToken.ThrowIfCancellationRequested();
                // 等待写锁时响应本地取消；一旦开始写帧则使用 Session 生命周期 Token，避免取消造成半帧。
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _frame.WriteFrameAsync(stream, CmdOp.RpcRequest, body, _connectionToken, RpcWire.RequestPrefix(methodId, callId))
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }

                using (ct.Register(() =>
                       {
                           // 本地取消等待的同时通知对端取消正在执行的同一个 CallId。
                           _ = SendCancelAsync(callId);
                           completion.Source.TrySetCanceled(ct);
                       }))
                {
                    var response = await completion.Source.Task.ConfigureAwait(false);
                    if (response.Error != null) throw new RpcSessionException(response.Error);
                    return response.Data;
                }
            }
            finally
            {
                _pending.TryRemove(callId, out _);
            }
        }

        /// <summary>逐帧读取并按 CmdOp 分发；读到 EOF 或协议错误时关闭连接。</summary>
        private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpFrameProtocol.Frame? frame = await _frame.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                    if (!frame.HasValue) break;
                    MarkFrameReceived();
                    switch (frame.Value.Op)
                    {
                        case CmdOp.RpcResponse:
                            HandleResponse(frame.Value.Payload);
                            break;
                        case CmdOp.RpcRequest:
                            // 不阻塞读循环：Controller 可以在处理请求时反向调用对端，否则会卡住本连接的响应读取。
                            _ = HandleRequestAsync(RpcWire.ReadRequest(frame.Value.Payload), ct);
                            break;
                        case CmdOp.RpcCancel:
                            HandleCancel(frame.Value.Payload);
                            break;
                        case CmdOp.Pong:
                            break;
                        case CmdOp.Ping:
                            await WriteFrameAsync(CmdOp.Pong, Array.Empty<byte>(), ct).ConfigureAwait(false);
                            break;
                        default:
                            throw new InvalidDataException("Unknown CmdOp: " + frame.Value.Op);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception)
            {
            }
            finally
            {
                Close("CONNECTION_LOST");
            }
        }

        /// <summary>从响应前缀读取 CallId，取出等待项并按该调用声明的返回类型解码。</summary>
        private void HandleResponse(byte[] payload)
        {
            if (payload.Length < 9) throw new InvalidDataException("Incomplete response prefix.");
            long id = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(payload);
            if (_pending.TryRemove(id, out var pending)) pending.Complete(payload);
        }

        /// <summary>为远端调用建立独立取消作用域，执行 Controller 并返回带相同 CallId 的响应。</summary>
        private async Task HandleRequestAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken sessionToken)
        {
            CancellationTokenSource requestLifetime = null;
            byte[] body;
            bool success = false;
            try
            {
                requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                RegisterActiveCall(request.CallId, requestLifetime);

                if (!_registry.TryGet(request.MethodId, out RpcMethodDescriptor descriptor))
                {
                    body = RpcWire.Encode("METHOD_NOT_FOUND:0x" + request.MethodId.ToString("X16"));
                }
                else
                {
                    body = await descriptor.Handler(this, request, requestLifetime.Token).ConfigureAwait(false);
                    success = true;
                }
            }
            catch (OperationCanceledException) { body = RpcWire.Encode("CANCELLED"); }
            catch (Exception error) { body = RpcWire.Encode("RPC_EXECUTION_FAILED: " + error.Message); }
            finally
            {
                if (requestLifetime != null)
                {
                    UnregisterActiveCall(request.CallId, requestLifetime);
                    requestLifetime.Dispose();
                }
            }

            try
            {
                await WriteFrameAsync(CmdOp.RpcResponse, body, sessionToken,
                    RpcWire.CallPrefix(request.CallId, success)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>校验取消帧长度，通知对应的入站请求停止执行。</summary>
        private void HandleCancel(byte[] payload)
        {
            if (payload.Length != 8) throw new InvalidDataException("Invalid cancel prefix.");
            CancelActiveCalls(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(payload));
        }

        /// <summary>尽力通知对端取消请求；连接关闭时允许取消帧发送失败。</summary>
        private async Task SendCancelAsync(long callId)
        {
            try
            {
                CancellationToken token = _connectionToken;
                byte[] payload =
                    RpcWire.CallPrefix(callId);
                await WriteFrameAsync(CmdOp.RpcCancel, payload, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>让心跳、响应与业务请求共享写锁，避免并发写入时交错帧字节。</summary>
        private async Task WriteFrameAsync(CmdOp op, byte[] payload, CancellationToken ct, byte[] prefix = null)
        {
            NetworkStream stream = _stream;
            if (stream == null) throw new IOException("Session 连接已关闭。");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _frame.WriteFrameAsync(stream, op, payload, ct, prefix).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>记录入站请求的取消源，同一 CallId 下的执行可一起取消。</summary>
        private void RegisterActiveCall(long callId, CancellationTokenSource requestLifetime)
        {
            lock (_activeCallsGate)
            {
                if (!_activeCalls.TryGetValue(callId,
                        out System.Collections.Generic.List<CancellationTokenSource> requests))
                {
                    requests = new System.Collections.Generic.List<CancellationTokenSource>();
                    _activeCalls.Add(callId, requests);
                }

                requests.Add(requestLifetime);
            }
        }

        /// <summary>移除已结束的入站执行，并清理空的取消源列表。</summary>
        private void UnregisterActiveCall(long callId, CancellationTokenSource requestLifetime)
        {
            lock (_activeCallsGate)
            {
                if (!_activeCalls.TryGetValue(callId,
                        out System.Collections.Generic.List<CancellationTokenSource> requests))
                    return;

                requests.Remove(requestLifetime);
                if (requests.Count == 0)
                    _activeCalls.Remove(callId);
            }
        }

        /// <summary>在锁内取得取消源快照，再在锁外运行可能重入的取消回调。</summary>
        private void CancelActiveCalls(long callId)
        {
            CancellationTokenSource[] requests;
            lock (_activeCallsGate)
            {
                if (!_activeCalls.TryGetValue(callId,
                        out System.Collections.Generic.List<CancellationTokenSource> active))
                    return;
                requests = active.ToArray();
            }

            // 取消回调可能重入 Session，因此在锁外调用 Cancel。
            foreach (CancellationTokenSource request in requests)
            {
                try
                {
                    request.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>以同一个关闭原因结束仍在等待网络响应的出站请求。</summary>
        private void FailAllPending(string error)
        {
            foreach (long callId in _pending.Keys)
            {
                if (_pending.TryRemove(callId, out IPendingCall completion))
                    completion.Fail(error);
            }
        }

        /// <summary>关闭 TCP 连接、取消正在执行的请求并完成所有等待中的调用。</summary>
        public void Dispose() => Close("CONNECTION_CLOSED");

        /// <summary>第一个关闭路径负责释放套接字、取消后台工作并公布原因；后续关闭调用直接返回。</summary>
        private void Close(string reason)
        {
            lock (_connectionGate)
            {
                if (_disposed) return;
                _disposed = true;
                _client?.Close();
                _client = null;
                _stream = null;
            }
            try { _lifetime.Cancel(); } catch (AggregateException) { }
            FailAllPending(reason);
            _lifetime.Dispose();
            _closed.TrySetResult(reason);
        }
    }
}
