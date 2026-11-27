using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>统一在轮询线程派发事件，跟踪并释放握手中的连接。</summary>
    public abstract class RpcPeer<TSession> : IDisposable where TSession : Session
    {
        private protected readonly HashSet<IDisposable> pendingSockets = new HashSet<IDisposable>();
        private protected readonly List<TSession> sessions = new List<TSession>();
        private readonly Queue<Func<bool>> pending = new Queue<Func<bool>>();

        private protected bool disposed;
        private bool polling;
        protected long Generation { get; private set; }
        // 仅创建此端点的宿主订阅，主动释放时移出轮询集合。
        internal event Action<IDisposable> Released;

        /// <summary>入站或出站连接绑定到 Session 后触发，出站连接随后执行自己的完成回调。</summary>
        public event Action<TSession> Connected;

        /// <summary>轮询发现 Session 已关闭时触发一次，同时给出关闭原因。释放整个端点会丢弃待派发事件。</summary>
        public event Action<TSession, string> Disconnected;

        /// <summary>接收连接失败或操作未指定失败回调时，向订阅方报告异常。</summary>
        public event Action<Exception> Error;

        public event Action<TSession> Suspended;
        public event Action<TSession> Resumed;
        protected void NotifyConnected(TSession session) => Connected?.Invoke(session);
        protected void NotifySuspended(TSession session) => Suspended?.Invoke(session);
        protected void NotifyResumed(TSession session) => Resumed?.Invoke(session);
        protected void ReportError(Exception error) => Error?.Invoke(error);
        protected virtual void AcceptPendingConnections() { }
        protected abstract void PollRecovery(TSession session);
        protected abstract void Forget(TSession session);
        protected abstract void StopCore();
        protected readonly int retentionMs;
        protected RpcPeer(int retentionMilliseconds)
        {
            if (retentionMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(retentionMilliseconds));
            retentionMs = retentionMilliseconds;
        }
        protected static long After(int ms) => Stopwatch.GetTimestamp() + (long)(ms * (Stopwatch.Frequency / 1000.0));

        /// <summary>端点在 Poll 中交付已完成的网络操作。交付前负责 socket 的超时、异常和退出清理。</summary>
        private protected void WaitForSocket(IDisposable socket, Task work, Action success, Action<Exception> failure, int timeoutMs = 5000)
        {
            long deadline = timeoutMs == System.Threading.Timeout.Infinite ? long.MaxValue : After(timeoutMs);
            pendingSockets.Add(socket);
            EnqueueCompletion(work, deadline, () =>
            {
                pendingSockets.Remove(socket);
                success();
            }, error =>
            {
                pendingSockets.Remove(socket);
                socket.Dispose();
                failure?.Invoke(error);
            });
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
            EnqueueCompletion(call, long.MaxValue, completed, error => (failed ?? Error)?.Invoke(error));
        }

        /// <summary>轮询线程统一处理任务完成、失败和截止时间，每项操作只交付一次。</summary>
        private void EnqueueCompletion(Task call, long deadline, Action completed, Action<Exception> failed)
        {
            Observe(call);
            pending.Enqueue(() =>
            {
                if (!call.IsCompleted && (deadline == long.MaxValue || Stopwatch.GetTimestamp() < deadline)) return false;
                try
                {
                    if (!call.IsCompleted) throw new TimeoutException("会话握手超时。");
                    call.GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    failed(error);
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
                long run = Generation;
                AcceptPendingConnections();

                // 只检查本轮开始时已有的回调，回调中新登记的操作留到下一轮。
                int count = pending.Count;
                while (Generation == run && count-- > 0)
                {
                    var deliver = pending.Dequeue();
                    if (!deliver() && Generation == run) pending.Enqueue(deliver);
                }

                for (int i = sessions.Count - 1; Generation == run && i >= 0; i--)
                {
                    var session = sessions[i];
                    if (!session.Closed.IsCompleted)
                    {
                        PollRecovery(session);
                        continue;
                    }
                    Forget(session);
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
        public void Stop()
        {
            if (!disposed) StopRun();
        }

        private void StopRun()
        {
            Generation++;
            StopCore();
            foreach (var socket in pendingSockets) socket.Dispose();
            foreach (var session in sessions) session.Dispose();
            pendingSockets.Clear();
            sessions.Clear();
            pending.Clear();
        }

        /// <summary>永久释放端点，解除宿主和事件订阅。释放后不能再次启动。</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Released?.Invoke(this);
            Released = null;
            StopRun();
            Suspended = null;
            Resumed = null;
            Connected = null;
            Disconnected = null;
            Error = null;
        }

        /// <summary>在端点释放后拒绝新操作。</summary>
        protected void EnsureActive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(RpcPeer<TSession>));
        }
    }
}