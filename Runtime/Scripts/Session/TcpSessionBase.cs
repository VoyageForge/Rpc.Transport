using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>一条 TCP 连接上的双向 RPC Session。网络路由只使用 MethodId，业务 Payload 使用 MemoryPack。</summary>
    public abstract class TcpSessionBase : IDisposable
    {
        private const int DefaultMaxFrameBytes = 1024 * 1024;
        private readonly TcpFrameProtocol _frame;
        private readonly RpcControllerRegistry _registry;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<SessionResponse>> _pending =
            new ConcurrentDictionary<long, TaskCompletionSource<SessionResponse>>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private long _nextRequestId;
        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _lifetime;
        private Task _readTask;
        private bool _disposed;

        protected TcpSessionBase(RpcControllerRegistry registry, int maxFrameBytes = DefaultMaxFrameBytes)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _registry.Seal();
            _frame = new TcpFrameProtocol(maxFrameBytes);
        }

        public bool IsConnected { get { return _client != null && _client.Connected; } }

        public void Attach(TcpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (_disposed) throw new ObjectDisposedException(GetType().Name);
            if (_client != null) throw new InvalidOperationException("Session 已绑定连接。");
            client.NoDelay = true;
            _client = client;
            _stream = client.GetStream();
            _lifetime = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_lifetime.Token));
        }

        /// <summary>由生成的调用代理调用；TResponse 在编译期确定，避免运行时 Type 反射。</summary>
        public async Task<TResponse> InvokeAsync<TResponse>(ulong methodId, byte[] payload,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            SessionResponse response = await SendAsync(methodId, payload, cancellationToken).ConfigureAwait(false);
            if (!response.Success) throw new RpcSessionException(response.Error ?? "RPC_FAILED");
            if (response.Payload == null || response.Payload.Length == 0)
                throw new InvalidDataException("RPC 返回 Payload 为空，但调用方期待 " + typeof(TResponse).FullName + "。");
            return MemoryPackSerializer.Deserialize<TResponse>(response.Payload);
        }

        /// <summary>无返回值的 RPC 调用。</summary>
        public async Task InvokeAsync(ulong methodId, byte[] payload,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            SessionResponse response = await SendAsync(methodId, payload, cancellationToken).ConfigureAwait(false);
            if (!response.Success) throw new RpcSessionException(response.Error ?? "RPC_FAILED");
        }

        private async Task<SessionResponse> SendAsync(ulong methodId, byte[] payload, CancellationToken ct)
        {
            NetworkStream stream = _stream;
            if (stream == null) throw new IOException("Session 尚未绑定 TCP 连接。");
            if (methodId == 0) throw new ArgumentOutOfRangeException(nameof(methodId), "MethodId 不能为 0。");
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            long requestId = Interlocked.Increment(ref _nextRequestId);
            TaskCompletionSource<SessionResponse> completion =
                new TaskCompletionSource<SessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("RequestId 冲突。");

            try
            {
                byte[] framePayload = MemoryPackSerializer.Serialize(new SessionRequest
                {
                    RequestId = requestId,
                    MethodId = methodId,
                    Payload = payload ?? Array.Empty<byte>()
                });

                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try { await _frame.WriteFrameAsync(stream, RpcMessageType.RpcRequest, framePayload, ct).ConfigureAwait(false); }
                finally { _writeLock.Release(); }

                using (ct.Register(() => completion.TrySetCanceled(ct)))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }

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
                            // 不阻塞读循环：Controller 可以在处理请求时反向调用对端，否则会卡住本连接的响应读取。
                            _ = HandleRequestAsync(frame.Value.Payload, ct);
                            break;
                        case RpcMessageType.Ping:
                            await WriteFrameAsync(RpcMessageType.Pong, Array.Empty<byte>(), ct).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception) { }
            finally { FailAllPending("CONNECTION_LOST"); }
        }

        private void HandleResponse(byte[] payload)
        {
            SessionResponse response = MemoryPackSerializer.Deserialize<SessionResponse>(payload);
            if (response == null) return;
            if (_pending.TryRemove(response.RequestId, out TaskCompletionSource<SessionResponse> completion))
                completion.TrySetResult(response);
        }

        private async Task HandleRequestAsync(byte[] payload, CancellationToken ct)
        {
            SessionRequest request = null;
            SessionResponse response;
            try
            {
                request = MemoryPackSerializer.Deserialize<SessionRequest>(payload);
                if (request == null) throw new InvalidDataException("RPC Request 为空。");
                if (!_registry.TryGet(request.MethodId, out RpcMethodDescriptor descriptor))
                {
                    response = new SessionResponse { Success = false, Error = "METHOD_NOT_FOUND:0x" + request.MethodId.ToString("X16") };
                }
                else
                {
                    byte[] result = await descriptor.Handler(request.Payload ?? Array.Empty<byte>(), ct).ConfigureAwait(false);
                    response = new SessionResponse { Success = true, Payload = result ?? Array.Empty<byte>() };
                }
            }
            catch (OperationCanceledException)
            {
                response = new SessionResponse { Success = false, Error = "CANCELLED" };
            }
            catch (Exception exception)
            {
                response = new SessionResponse { Success = false, Error = "RPC_EXECUTION_FAILED: " + exception.Message };
            }

            response.RequestId = request != null ? request.RequestId : 0;
            try
            {
                await WriteFrameAsync(RpcMessageType.RpcResponse, MemoryPackSerializer.Serialize(response), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task WriteFrameAsync(ushort typeId, byte[] payload, CancellationToken ct)
        {
            NetworkStream stream = _stream;
            if (stream == null) throw new IOException("Session 连接已关闭。");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await _frame.WriteFrameAsync(stream, typeId, payload, ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        private void FailAllPending(string error)
        {
            foreach (long requestId in _pending.Keys)
            {
                if (_pending.TryRemove(requestId, out TaskCompletionSource<SessionResponse> completion))
                    completion.TrySetResult(new SessionResponse { RequestId = requestId, Success = false, Error = error });
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime?.Cancel();
            try { _client?.Close(); } catch { }
            FailAllPending("CONNECTION_CLOSED");
            _lifetime?.Dispose();
            _client = null;
            _stream = null;
        }
    }
}
