using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>可接管消息连接的双向 RPC Session。网络路由只使用 MethodId，业务 Payload 使用 MemoryPack。</summary>
    public abstract partial class Session : IDisposable
    {
        /// <summary>默认允许的单帧最大负载长度：1 MiB。</summary>
        public const int DefaultMaxFrameBytes = 1024 * 1024;

        internal int MaxFrameBytes { get; }
        private readonly RpcControllerRegistry _registry;

        private readonly object _connectionGate = new object();

        // 只给本端发出的请求编号。恢复时替换的是 SessionBinding，这个计数器随 Session 保留。
        private long _nextCallId;
        private SessionBinding _connection;
        private Timer _expiry;
        private long _expiresAt;
        private int _retentionMs;
        private bool _disposed;

        private readonly TaskCompletionSource<string> _closed =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>逻辑会话永久结束时返回原因。可恢复会话的临时断线由端点的 Suspended 事件通知。</summary>
        public Task<string> Closed => _closed.Task;

        private readonly System.Collections.Generic.Dictionary<Type, object> _remoteApis =
            new System.Collections.Generic.Dictionary<Type, object>();

        /// <summary>客户端持有的重连凭据，供 Connect(ip, port, key) 接回服务端原会话。请勿记录到日志。</summary>
        public string ReconnectKey { get; internal set; }

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

        /// <summary>固定本会话的分发表和帧大小上限。调用 Attach 后开始收包和心跳。</summary>
        protected Session(RpcControllerRegistry registry, int maxFrameBytes = DefaultMaxFrameBytes)
        {
            _registry = registry ?? new RpcControllerRegistry();
            _registry.Seal();
            if (maxFrameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
            MaxFrameBytes = maxFrameBytes;
        }

        /// <summary>当前 Session 独有的应用上下文存储。RPC 框架不解释其中的键和值，也不内置鉴权。</summary>
        public ConcurrentDictionary<string, object> Blackboard { get; } =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        /// <summary>当前代次是否已绑定并允许收发业务帧。</summary>
        public bool IsConnected
        {
            get
            {
                lock (_connectionGate) return _connection?.Started == true;
            }
        }

        /// <summary>当前套接字已释放，但逻辑会话仍在恢复窗口内。</summary>
        public bool IsSuspended
        {
            get
            {
                lock (_connectionGate) return _expiry != null;
            }
        }

        /// <summary>直接绑定一条普通连接；需要恢复握手时由端点负责绑定。</summary>
        public void Attach(TcpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            Attach(new TcpConnection(client, MaxFrameBytes));
        }

        /// <summary>接管一条消息连接，启动 RPC 收发与心跳。</summary>
        public void Attach(ConnectionBase connection) => Bind(connection, 0, false);

        internal void Bind(ConnectionBase client, int retentionMs, bool resume, bool start = true)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            lock (_connectionGate)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                if (_connection != null || resume != (_expiry != null))
                    throw new InvalidOperationException("会话状态不允许绑定该连接。");
                if (resume && System.Diagnostics.Stopwatch.GetTimestamp() >= _expiresAt)
                {
                    Terminate("RESUME_EXPIRED");
                    throw new InvalidOperationException("会话恢复窗口已结束。");
                }

                int idle = HeartbeatIdleMilliseconds;
                int timeout = HeartbeatTimeoutMilliseconds;
                if (idle <= 0 || timeout <= 0) throw new InvalidOperationException("心跳配置必须为正数。");
                if (client.IsClosed) throw new IOException("连接已关闭。");
                var connection = new SessionBinding(this, client, idle, timeout);
                _expiry?.Dispose();
                _expiry = null;
                _retentionMs = retentionMs;
                _connection = connection;
                if (start) connection.Start();
            }
        }

        /// <summary>服务端写完握手确认帧后才允许发送 RPC，避免业务帧抢占握手响应。</summary>
        internal void Activate(ConnectionBase socket)
        {
            lock (_connectionGate)
            {
                if (_connection == null || !_connection.Owns(socket)) throw new IOException("握手连接已释放。");
                _connection.Start();
            }
        }

        /// <summary>主动丢弃当前传输，保留逻辑会话供恢复；永久退出请调用 Dispose。</summary>
        public void Suspend()
        {
            SessionBinding connection;
            lock (_connectionGate)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                if (_retentionMs == 0) throw new InvalidOperationException("当前会话未启用恢复。");
                connection = _connection;
            }

            connection?.Close("CONNECTION_LOST");
        }

        /// <summary>回收旧连接。只有当前代次可以挂起会话；已排队的旧定时器不能终止新连接。</summary>
        private void Lost(SessionBinding connection, string reason)
        {
            lock (_connectionGate)
            {
                if (_connection != connection) return;
                _connection = null;
                if (_retentionMs == 0)
                {
                    _disposed = true;
                    _closed.TrySetResult(reason);
                }
                else
                {
                    Timer timer = null;
                    timer = new Timer(_ =>
                    {
                        lock (_connectionGate)
                        {
                            if (_expiry == timer) Terminate("RESUME_EXPIRED");
                        }
                    }, null, Timeout.Infinite, Timeout.Infinite);
                    _expiresAt = System.Diagnostics.Stopwatch.GetTimestamp() +
                                 (long)(_retentionMs * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
                    _expiry = timer;
                    timer.Change(_retentionMs, Timeout.Infinite);
                }
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

        private Task<TResult> SendAsync<TResult>(ulong methodId, long callId, byte[] body, CancellationToken ct)
        {
            SessionBinding connection;
            lock (_connectionGate) connection = _connection?.Started == true ? _connection : null;
            if (connection == null) return Task.FromException<TResult>(new IOException("Session 当前没有可用连接。"));
            return connection.SendAsync<TResult>(methodId, callId, body, ct);
        }

        /// <summary>永久释放会话与恢复定时器。主动释放后不能再接入新套接字。</summary>
        public void Dispose() => Terminate("CONNECTION_CLOSED");

        internal void Terminate(string reason)
        {
            SessionBinding connection;
            lock (_connectionGate)
            {
                if (_disposed) return;
                _disposed = true;
                ReconnectKey = null;
                _expiry?.Dispose();
                _expiry = null;
                connection = _connection;
                _connection = null;
            }

            connection?.Close(reason);
            // 外部可能仍持有旧代理；永久结束后解除可恢复会话对业务状态的引用。
            if (_retentionMs > 0) Blackboard.Clear();
            _closed.TrySetResult(reason);
        }
    }
}