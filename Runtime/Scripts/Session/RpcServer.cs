using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>按类型创建会话，管理恢复凭据及服务端持有的逻辑会话。</summary>
    public abstract class RpcServer : RpcPeer<Session>
    {
        private readonly Func<string, Session> createByKind;
        private readonly Dictionary<string, Func<Session>> factories = new();
        private readonly Dictionary<string, SavedSession> tickets = new();
        private readonly Dictionary<Session, SavedSession> saved = new();
        // 握手确认写完前由服务端持有候选会话，退出时也能找到并释放它。
        private readonly HashSet<Session> unpublishedSessions = new();
        /// <summary>已发布的会话，包含恢复窗口内挂起的会话。由轮询线程访问。</summary>
        public IReadOnlyList<Session> Sessions { get; }
        protected RpcServer(int retentionMilliseconds, Func<string, Session> createSession = null) : base(retentionMilliseconds)
        {
            createByKind = createSession;
            Sessions = sessions.AsReadOnly();
        }

        /// <summary>登记双方约定的会话类型，工厂仅在首次接入时调用。</summary>
        public void RegisterSession(string kind, Func<Session> create)
        {
            EnsureActive();
            SessionOpenRequest.ValidateKind(kind);
            if (createByKind != null) throw new InvalidOperationException("请在管理器的 CreateSession 中处理会话类型。");
            factories.Add(kind, create ?? throw new ArgumentNullException(nameof(create)));
        }

        /// <summary>同一逻辑会话在首次连接及以后各次恢复之间共享的服务端记录。</summary>
        private sealed class SavedSession
        {
            public string Kind;
            public byte[] Ticket;
            public Session Session;
            // 正在绑定的物理连接；握手完成或失败后清空，避免并发接管。
            public ConnectionBase Attempt;
            public bool Announced;
        }
        protected abstract void StopListening();
        protected virtual void ConfigureConnection(ConnectionBase connection, int maxBytes) { }
        protected override void StopCore()
        {
            StopListening();
            foreach (var session in unpublishedSessions) session.Dispose();
            unpublishedSessions.Clear();
            tickets.Clear();
            saved.Clear();
        }
        protected override void Forget(Session session)
        {
            if (!saved.TryGetValue(session, out var state)) return;
            saved.Remove(session);
            tickets.Remove(Convert.ToBase64String(state.Ticket));
            state.Attempt?.Dispose();
        }
        protected override void PollRecovery(Session session)
        {
            if (session.IsSuspended) Announce(saved[session]);
        }
        private void Announce(SavedSession state)
        {
            if (state.Announced) return;
            state.Announced = true;
            NotifySuspended(state.Session);
        }

        /// <summary>服务端接收 SessionOpen，在创建会话和查找旧会话之间选择一条路径。</summary>
        protected void AcceptConnection(ConnectionBase socket)
        {
            EnsureActive();
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            if (pendingSockets.Count >= 64)
            {
                socket.Dispose();
                return;
            }
            var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = Session.SessionBinding.ReceiveHandshakeAsync(socket, activated.Task, expectOpen: true);
            WaitForSocket(socket, request, () => HandleSessionOpen(socket, request.Result, activated), null);
        }

        /// <summary>服务端按 SessionOpen 正文选择握手路径：按类型创建会话，携带凭据则查找旧会话并检查能否接回。</summary>
        private void HandleSessionOpen(ConnectionBase socket, RpcWire.Frame frame, TaskCompletionSource<bool> activated)
        {
            SessionOpenRequest request;
            try { request = SessionOpenRequest.Decode(frame.Payload); }
            catch (Exception) { Reject(socket, CmdOp.ResumeRejected); return; }
            if (request.ReconnectKey == null)
            {
                ConfirmServerSession(socket, new SavedSession { Kind = request.SessionKind, Ticket = NewTicket() }, activated, false);
                return;
            }
            if (!tickets.TryGetValue(Convert.ToBase64String(request.ReconnectKey), out var state) ||
                state.Kind != request.SessionKind || state.Session.Closed.IsCompleted)
            {
                Reject(socket, CmdOp.ResumeRejected);
                return;
            }
            if (!state.Session.IsSuspended || state.Attempt != null)
            {
                Reject(socket, CmdOp.ResumeBusy);
                return;
            }

            state.Attempt = socket;
            try { Announce(state); }
            catch
            {
                state.Attempt = null;
                socket.Dispose();
                throw;
            }
            // Suspended 回调允许主动结束会话或释放端点。
            if (disposed || state.Session.Closed.IsCompleted)
            {
                state.Attempt = null;
                socket.Dispose();
                return;
            }
            ConfirmServerSession(socket, state, activated, resume: true);
        }

        /// <summary>先绑定 socket，再写握手确认。写完确认后才启动收发并通知业务。</summary>
        private void ConfirmServerSession(ConnectionBase socket, SavedSession state, TaskCompletionSource<bool> activated, bool resume)
        {
            state.Attempt = socket;
            try
            {
                long run = Generation;
                if (!resume)
                {
                    if (createByKind != null) state.Session = createByKind(state.Kind);
                    else if (factories.TryGetValue(state.Kind, out var create)) state.Session = create();
                }
                if (run != Generation)
                {
                    state.Session?.Dispose();
                    socket.Dispose();
                    return;
                }
                if (state.Session == null)
                {
                    Reject(socket, CmdOp.ResumeRejected);
                    return;
                }
                ConfigureConnection(socket, state.Session.MaxFrameBytes);
                state.Session.Bind(socket, retentionMs, resume, start: false);
            }
            catch (Exception error)
            {
                state.Attempt = null;
                if (!resume) state.Session?.Dispose();
                Reject(socket, CmdOp.ResumeRejected);
                ReportError(error);
                return;
            }

            if (!resume) unpublishedSessions.Add(state.Session);
            var reply = socket.SendAsync(CmdOp.SessionReady, resume ? null : state.Ticket);
            WaitForSocket(socket, reply, () =>
            {
                try { state.Session.Activate(socket); }
                catch (IOException)
                {
                    AbortServerHandshake(state, resume);
                    return;
                }
                activated.TrySetResult(true);
                unpublishedSessions.Remove(state.Session);
                state.Attempt = null;
                state.Announced = false;
                if (resume) NotifyResumed(state.Session);
                else
                {
                    sessions.Add(state.Session);
                    saved.Add(state.Session, state);
                    tickets.Add(Convert.ToBase64String(state.Ticket), state);
                    NotifyConnected(state.Session);
                }
            }, _ => AbortServerHandshake(state, resume));
        }

        /// <summary>新建但未交付的会话直接释放；恢复握手失败的旧会话重新进入保留窗口。</summary>
        private void AbortServerHandshake(SavedSession state, bool resume)
        {
            state.Attempt = null;
            if (resume)
            {
                if (!state.Session.Closed.IsCompleted) state.Session.Suspend();
                return;
            }
            unpublishedSessions.Remove(state.Session);
            state.Session.Dispose();
        }

        /// <summary>向客户端写回拒绝原因，写完即关闭这条握手连接；写失败或超时也会释放套接字。</summary>
        private void Reject(ConnectionBase socket, CmdOp reason) =>
            WaitForSocket(socket, socket.SendAsync(reason, null), socket.Dispose, null);

        /// <summary>服务端为新会话生成 32 字节随机凭据，客户端重连时原样提交。</summary>
        private static byte[] NewTicket()
        {
            var bytes = new byte[SessionOpenRequest.KeyBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return bytes;
        }
    }
}
