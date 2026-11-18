using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 会话基类：一条 TCP 连接上的双向 RPC。
    /// 负责帧收发、RequestId 分配、pending 响应匹配和把远端请求交给子类分发；
    /// 子类只关心“收到请求后怎么执行本地 Controller”。
    /// </summary>
    public abstract class TcpSessionBase : IDisposable
    {
        /// <summary>默认最大帧长度。</summary>
        private const int DefaultMaxFrameBytes = 1024 * 1024;

        private readonly TcpFrameProtocol _frame;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<SessionResponse>> _pending =
            new ConcurrentDictionary<long, TaskCompletionSource<SessionResponse>>();

        private readonly object _requestIdLock = new object();
        private long _nextRequestId;

        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _lifetime;
        private Task _readTask;
        private bool _disposed;

        /// <summary>创建会话。</summary>
        protected TcpSessionBase(IRpcSerializer serializer, int maxFrameBytes = DefaultMaxFrameBytes)
        {
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _frame = new TcpFrameProtocol(maxFrameBytes);
            Registry = new RpcMethodRegistry();
        }

        /// <summary>本会话使用的序列化器。</summary>
        public IRpcSerializer Serializer { get; }

        /// <summary>本端提供的 Controller 方法注册表。</summary>
        public RpcMethodRegistry Registry { get; }

        /// <summary>是否已连接。</summary>
        public bool IsConnected { get { return _client != null; } }

        /// <summary>绑定一条已经建立的 TCP 连接并启动读循环。</summary>
        public void Attach(TcpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (_client != null) throw new InvalidOperationException("会话已经绑定连接。");

            _client = client;
            _stream = client.GetStream();
            _lifetime = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_lifetime.Token));
        }

        /// <summary>分配下一个请求编号。</summary>
        public long NextRequestId()
        {
            lock (_requestIdLock)
            {
                return ++_nextRequestId;
            }
        }

        /// <summary>发送请求并等待对应响应。</summary>
        public async Task<SessionResponse> SendAsync(string controllerName, string methodName, byte[] payload,
            CancellationToken ct = default(CancellationToken))
        {
            NetworkStream stream = _stream;
            if (stream == null) throw new IOException("会话尚未绑定连接。");

            SessionRequest request = new SessionRequest
            {
                RequestId = NextRequestId(),
                ControllerName = controllerName,
                MethodName = methodName,
                Payload = payload ?? Array.Empty<byte>()
            };

            TaskCompletionSource<SessionResponse> completion =
                new TaskCompletionSource<SessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[request.RequestId] = completion;

            try
            {
                byte[] framePayload = Serializer.Serialize(new Protocol<SessionRequest>
                {
                    Version = "1.0",
                    Data = request
                });
                await _frame.WriteFrameAsync(stream, RpcMessageType.RpcRequest, framePayload, ct).ConfigureAwait(false);

                using (ct.Register(() => completion.TrySetCanceled(ct)))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                _pending.TryRemove(request.RequestId, out _);
            }
        }

        /// <summary>发送强类型请求并反序列化响应（代理生成代码使用）。</summary>
        public async Task<TResponse> SendAsync<TRequest, TResponse>(string controllerName, string methodName,
            TRequest request, CancellationToken ct = default(CancellationToken))
        {
            byte[] payload = Serializer.Serialize(request);
            SessionResponse response = await SendAsync(controllerName, methodName, payload, ct).ConfigureAwait(false);
            if (response == null || !response.Success)
            {
                throw new RpcSessionException(response != null ? response.Error : "EMPTY_RESPONSE");
            }

            return Serializer.Deserialize<TResponse>(response.Payload);
        }

        /// <summary>读循环：响应交给 pending，远端请求交给子类执行。</summary>
        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpFrameProtocol.Frame? frame = await _frame.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                    if (!frame.HasValue) break;

                    switch (frame.Value.TypeId)
                    {
                        case RpcMessageType.RpcResponse:
                            HandleResponse(frame.Value.Payload);
                            break;

                        case RpcMessageType.RpcRequest:
                            await HandleRequestAsync(frame.Value.Payload, ct).ConfigureAwait(false);
                            break;

                        case RpcMessageType.Ping:
                            await _frame.WriteFrameAsync(_stream, RpcMessageType.Pong, Array.Empty<byte>(), ct)
                                .ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                FailAllPending();
            }
        }

        private void HandleResponse(byte[] payload)
        {
            Protocol<SessionResponse> envelope = Serializer.Deserialize<Protocol<SessionResponse>>(payload);
            SessionResponse response = envelope != null ? envelope.Data : null;
            if (response == null) return;

            if (_pending.TryRemove(response.RequestId, out TaskCompletionSource<SessionResponse> completion))
            {
                completion.TrySetResult(response);
            }
        }

        private async Task HandleRequestAsync(byte[] payload, CancellationToken ct)
        {
            SessionResponse response;
            try
            {
                Protocol<SessionRequest> envelope = Serializer.Deserialize<Protocol<SessionRequest>>(payload);
                SessionRequest request = envelope != null ? envelope.Data : null;
                if (request == null)
                {
                    response = new SessionResponse { Success = false, Error = "INVALID_REQUEST" };
                }
                else
                {
                    response = await HandleRemoteRequestAsync(request, ct).ConfigureAwait(false);
                    response.RequestId = request.RequestId;
                }
            }
            catch (OperationCanceledException)
            {
                response = new SessionResponse { Success = false, Error = "CANCELLED" };
            }
            catch (Exception exception)
            {
                response = new SessionResponse { Success = false, Error = exception.Message };
            }

            byte[] framePayload = Serializer.Serialize(new Protocol<SessionResponse>
            {
                Version = "1.0",
                Data = response
            });
            await _frame.WriteFrameAsync(_stream, RpcMessageType.RpcResponse, framePayload, ct).ConfigureAwait(false);
        }

        /// <summary>处理远端请求：子类按 Registry 查描述、反序列化并调用本地 Controller。</summary>
        protected abstract Task<SessionResponse> HandleRemoteRequestAsync(SessionRequest request,
            CancellationToken ct);

        private void FailAllPending()
        {
            foreach (long requestId in _pending.Keys)
            {
                if (_pending.TryRemove(requestId, out TaskCompletionSource<SessionResponse> completion))
                {
                    completion.TrySetResult(new SessionResponse
                    {
                        RequestId = requestId,
                        Success = false,
                        Error = "CONNECTION_LOST"
                    });
                }
            }
        }

        /// <summary>释放连接与 pending。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _lifetime?.Cancel();
            try { _client?.Close(); }
            catch { }

            FailAllPending();
            _lifetime?.Dispose();
            _client = null;
            _stream = null;
        }
    }
}
