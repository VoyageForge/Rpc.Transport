using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>由单个线程驱动的 TCP 端点，可监听多个客户端，也可主动连接。Unity 中由生命周期组件逐帧轮询。</summary>
    public sealed class RpcTcpEndpoint<TSession> : IDisposable where TSession : Session
    {
        private readonly Func<TcpClient, TSession> attach;
        private readonly HashSet<TcpClient> connecting = new HashSet<TcpClient>();
        private readonly List<TSession> sessions = new List<TSession>();
        private readonly Queue<Func<bool>> pending = new Queue<Func<bool>>();
        private TcpListener listener;
        private bool disposed;
        private bool polling;

        /// <summary>入站或出站连接绑定到 Session 后触发，出站连接随后执行自己的完成回调。</summary>
        public event Action<TSession> Connected;

        /// <summary>轮询发现 Session 已关闭时触发一次，同时给出关闭原因。释放整个端点会丢弃待派发事件。</summary>
        public event Action<TSession, string> Disconnected;

        /// <summary>接收连接失败或操作未指定失败回调时，向订阅方报告异常。</summary>
        public event Action<Exception> Error;

        /// <summary>创建端点。工厂接收已连接的套接字，并返回接管该套接字的 Session。</summary>
        public RpcTcpEndpoint(Func<TcpClient, TSession> attach)
        {
            this.attach = attach ?? throw new ArgumentNullException(nameof(attach));
        }

        /// <summary>开始监听并返回实际绑定地址。端口传 0 时，由操作系统选择可用端口。</summary>
        public IPEndPoint Listen(IPEndPoint address)
        {
            EnsureActive();
            if (listener != null) throw new InvalidOperationException("Already listening.");
            var candidate = new TcpListener(address);
            try
            {
                candidate.Start();
            }
            catch
            {
                candidate.Stop();
                throw;
            }

            listener = candidate;
            return (IPEndPoint)listener.LocalEndpoint;
        }

        /// <summary>异步发起 TCP 握手。轮询线程完成 Session 装配后调用 completed；失败交给 failed 或 Error。</summary>
        public void Connect(IPEndPoint address, Action<TSession> completed = null, Action<Exception> failed = null)
        {
            EnsureActive();
            if (address == null) throw new ArgumentNullException(nameof(address));
            var socket = new TcpClient(address.AddressFamily);
            connecting.Add(socket);
            Task handshake;
            try
            {
                handshake = socket.ConnectAsync(address.Address, address.Port);
            }
            catch (Exception error)
            {
                handshake = Task.FromException(error);
            }

            Observe(handshake);
            pending.Enqueue(() =>
            {
                if (!handshake.IsCompleted) return false;
                connecting.Remove(socket);
                TSession session;
                try
                {
                    handshake.GetAwaiter().GetResult();
                    session = Attach(socket);
                }
                catch (Exception error)
                {
                    socket.Dispose();
                    (failed ?? Error)?.Invoke(error);
                    return true;
                }

                Connected?.Invoke(session);
                if (!disposed) completed?.Invoke(session);
                return true;
            });
        }

        /// <summary>将工厂创建的会话纳入端点管理，之后由端点负责断开通知和资源释放。</summary>
        private TSession Attach(TcpClient socket)
        {
            var session = attach(socket) ?? throw new InvalidOperationException("Session factory returned null.");
            sessions.Add(session);
            return session;
        }

        /// <summary>登记强类型调用结果。任务完成后，由轮询线程派发一次成功或失败回调。</summary>
        public void OnResult<TResult>(Task<TResult> call, Action<TResult> completed, Action<Exception> failed = null)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            Watch(call, () => completed(call.GetAwaiter().GetResult()), failed);
        }

        /// <summary>登记无返回值调用；取消与异常均交给失败回调或 Error。</summary>
        public void OnResult(Task call, Action completed, Action<Exception> failed = null) =>
            Watch(call, completed, failed);

        /// <summary>将任务加入待派发队列，仅在任务已完成时读取结果。</summary>
        private void Watch(Task call, Action completed, Action<Exception> failed)
        {
            EnsureActive();
            if (call == null || completed == null)
                throw new ArgumentNullException(call == null ? nameof(call) : nameof(completed));
            Observe(call);
            pending.Enqueue(() =>
            {
                if (!call.IsCompleted) return false;
                try
                {
                    call.GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    (failed ?? Error)?.Invoke(error);
                    return true;
                }

                completed();
                return true;
            });
        }

        // 端点销毁后会丢弃回调，仍需观察后台任务的异常。
        private static void Observe(Task task) =>
            task.ContinueWith(t => { _ = t.Exception; }, System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        /// <summary>接受新连接、派发操作回调并报告关闭的会话。请由同一线程调用，回调内不可重入 Poll。</summary>
        public void Poll()
        {
            EnsureActive();
            if (polling) throw new InvalidOperationException("Poll is already running.");
            polling = true;
            try
            {
                // 每帧最多接受 16 条连接，将余下的握手留给后续帧。
                for (int i = 0; i < 16 && !disposed && listener != null; i++)
                {
                    TSession accepted;
                    TcpClient socket = null;
                    try
                    {
                        if (!listener.Pending()) break;
                        socket = listener.AcceptTcpClient();
                        accepted = Attach(socket);
                    }
                    catch (Exception error)
                    {
                        socket?.Dispose();
                        Error?.Invoke(error);
                        break;
                    }

                    Connected?.Invoke(accepted);
                }

                // 只检查本轮开始时已有的回调，回调中新登记的操作留到下一轮。
                int count = pending.Count;
                while (!disposed && count-- > 0)
                {
                    var deliver = pending.Dequeue();
                    if (!deliver() && !disposed) pending.Enqueue(deliver);
                }

                for (int i = sessions.Count - 1; !disposed && i >= 0; i--)
                {
                    var session = sessions[i];
                    if (!session.Closed.IsCompleted) continue;
                    sessions.RemoveAt(i);
                    Disconnected?.Invoke(session, session.Closed.Result);
                }
            }
            finally
            {
                polling = false;
            }
        }

        /// <summary>同步停止监听，关闭握手中的套接字及所有会话，并丢弃待执行的回调。可重复调用。</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            listener?.Stop();
            foreach (var socket in connecting) socket.Dispose();
            foreach (var session in sessions) session.Dispose();
            connecting.Clear();
            sessions.Clear();
            pending.Clear();
            Connected = null;
            Disconnected = null;
            Error = null;
        }

        /// <summary>在端点释放后拒绝新操作。</summary>
        private void EnsureActive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(RpcTcpEndpoint<TSession>));
        }
    }
}