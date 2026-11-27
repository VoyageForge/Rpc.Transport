using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


namespace VoyageForge.Rpc.Transport
{
    public abstract partial class Session
    {
        /// <summary>一次 TCP 绑定独享的资源，旧请求和心跳始终使用同一条流。</summary>
        internal sealed partial class SessionBinding
        {
            private readonly Session _owner;
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

            private readonly ConnectionBase _transport;
            private CancellationToken _connectionToken => _transport.Lifetime;

            private readonly int _heartbeatIdle;
            private readonly int _heartbeatTimeout;

            internal SessionBinding(Session owner, ConnectionBase transport, int heartbeatIdle, int heartbeatTimeout)
            {
                _heartbeatIdle = heartbeatIdle;
                _heartbeatTimeout = heartbeatTimeout;
                _owner = owner;
                _transport = transport;
                transport.Disconnected += OnDisconnected;
            }

            internal bool Started { get; private set; }
            internal bool Owns(ConnectionBase transport) => _transport == transport;

            internal void Start()
            {
                Started = true;
                MarkFrameReceived();
                _ = MonitorHeartbeatAsync(_heartbeatIdle, _heartbeatTimeout, _connectionToken);
                _transport.ReceiveWith(packet => DispatchAsync(_transport, packet, this, null, null));
            }

            /// <summary>为本次请求登记等待项，串行写入整帧，并用 CallId 匹配响应、取消或关闭通知。</summary>
            internal async Task<TResult> SendAsync<TResult>(ulong methodId, long callId, byte[] body, CancellationToken ct)
            {
                if (methodId == 0) throw new ArgumentOutOfRangeException(nameof(methodId));
                ct.ThrowIfCancellationRequested();
                var completion = new PendingCall<TResult>(methodId, callId);
                if (!_pending.TryAdd(callId, completion)) throw new InvalidOperationException("Duplicate CallId.");
                try
                {
                    _connectionToken.ThrowIfCancellationRequested();
                    await _transport.SendAsync(CmdOp.RpcRequest, body, ct,
                        RpcWire.RequestPrefix(methodId, callId)).ConfigureAwait(false);

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

            /// <summary>握手应答交给 Poll。当前包的处理等到会话激活或连接释放才结束，下一包始终由同一个读循环接收。</summary>
            internal static Task<RpcWire.Frame> ReceiveHandshakeAsync(ConnectionBase transport, Task activation, bool expectOpen)
            {
                var received = new TaskCompletionSource<RpcWire.Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
                var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var registration = transport.Lifetime.Register(() =>
                {
                    received.TrySetException(new EndOfStreamException("握手连接已关闭。"));
                    closed.TrySetResult(true);
                });

                async Task Deliver(RpcWire.Frame frame)
                {
                    received.TrySetResult(frame);
                    await Task.WhenAny(activation, closed.Task).ConfigureAwait(false);
                    registration.Dispose();
                }

                Func<RpcWire.Frame, Task> open = null, reply = null;
                if (expectOpen) open = Deliver;
                else reply = Deliver;
                transport.ReceiveWith(packet => DispatchAsync(transport, packet, null, open, reply));
                return received.Task;
            }

            /// <summary>握手与会话共用操作码分发，TCP 只负责交付完整的消息包。</summary>
            private static async Task DispatchAsync(ConnectionBase source, ReadOnlyMemory<byte> packet,
                SessionBinding connection, Func<RpcWire.Frame, Task> onOpen, Func<RpcWire.Frame, Task> onReply)
            {
                if (packet.IsEmpty) throw new InvalidDataException("RPC 消息包缺少操作码。");
                var op = (CmdOp)packet.Span[0];
                var payload = packet.Slice(1);
                connection?.MarkFrameReceived();
                switch (op)
                {
                    case CmdOp.Ping:
                        await source.SendAsync(CmdOp.Pong, Array.Empty<byte>()).ConfigureAwait(false);
                        break;
                    case CmdOp.Pong:
                        break;
                    case CmdOp.SessionOpen when onOpen != null:
                        // 服务端选择 Session 的操作在 Poll 线程执行，确认完成后才交付 socket。
                        await onOpen(new RpcWire.Frame(op, payload.ToArray())).ConfigureAwait(false);
                        break;
                    case CmdOp.SessionReady when onReply != null:
                    case CmdOp.ResumeBusy when onReply != null:
                    case CmdOp.ResumeRejected when onReply != null:
                        await onReply(new RpcWire.Frame(op, payload.ToArray())).ConfigureAwait(false);
                        break;
                    case CmdOp.RpcResponse when connection != null:
                        connection.HandleResponse(payload);
                        break;
                    case CmdOp.RpcRequest when connection != null:
                        // Controller 可以反向调用对端；读循环继续接收该调用的响应。
                        _ = connection.HandleRequestAsync(RpcWire.ReadRequest(payload), connection._connectionToken);
                        break;
                    case CmdOp.RpcCancel when connection != null:
                        connection.HandleCancel(payload);
                        break;
                    default:
                        throw new InvalidDataException("当前连接状态不能处理操作码：" + op);
                }
            }

            /// <summary>从响应前缀读取 CallId，取出等待项并按该调用声明的返回类型解码。</summary>
            private void HandleResponse(ReadOnlyMemory<byte> payload)
            {
                if (payload.Length < 9) throw new InvalidDataException("Incomplete response prefix.");
                long id = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(payload.Span);
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

                    if (!_owner._registry.TryGet(request.MethodId, out RpcMethodDescriptor descriptor))
                    {
                        body = RpcWire.Encode("METHOD_NOT_FOUND:0x" + request.MethodId.ToString("X16"));
                    }
                    else
                    {
                        body = await descriptor.Handler(_owner, request, requestLifetime.Token).ConfigureAwait(false);
                        success = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    body = RpcWire.Encode("CANCELLED");
                }
                catch (Exception error)
                {
                    body = RpcWire.Encode("RPC_EXECUTION_FAILED: " + error.Message);
                }
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
                    await _transport.SendAsync(CmdOp.RpcResponse, body, sessionToken,
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
            private void HandleCancel(ReadOnlyMemory<byte> payload)
            {
                if (payload.Length != 8) throw new InvalidDataException("Invalid cancel prefix.");
                CancelActiveCalls(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(payload.Span));
            }

            /// <summary>尽力通知对端取消请求；连接关闭时允许取消帧发送失败。</summary>
            private async Task SendCancelAsync(long callId)
            {
                try
                {
                    CancellationToken token = _connectionToken;
                    byte[] payload =
                        RpcWire.CallPrefix(callId);
                    await _transport.SendAsync(CmdOp.RpcCancel, payload, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
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

            internal void Close(string reason) => _transport.Close(reason);

            private void OnDisconnected(string reason)
            {
                FailAllPending(reason);
                _owner.Lost(this, reason);
            }
        }
    }
}
