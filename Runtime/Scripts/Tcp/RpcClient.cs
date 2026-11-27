using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>主动建立一条 TCP 会话，断线后使用原会话类型和凭据重试。</summary>
    public sealed class RpcClient<TSession> : RpcPeer<TSession> where TSession : Session
    {
        private readonly string kind;
        private readonly Func<TSession> createSession;
        private SavedSession current;
        /// <summary>已经建立的本地会话，挂起期间仍可读取黑板。</summary>
        public TSession CurrentSession => current?.Session;

        public RpcClient(string sessionKind, Func<TSession> create, int retentionMilliseconds = 30000) : base(retentionMilliseconds)
        {
            SessionOpenRequest.ValidateKind(sessionKind);
            kind = sessionKind;
            createSession = create ?? throw new ArgumentNullException(nameof(create));
        }

        /// <summary>首次连接后保留的客户端状态，断线期间用于定位原服务端并安排下一次握手。</summary>
        private sealed class SavedSession
        {
            public TSession Session;
            public byte[] Ticket;
            public IPEndPoint Address;
            public TcpConnection Attempt;
            public bool Announced;
            public long RetryAt;
            public int RetryDelayMs = 250;
        }

        protected override void Forget(TSession session)
        {
            current?.Attempt?.Dispose();
            current = null;
        }

        protected override void StopCore() => current = null;
        protected override void PollRecovery(TSession session)
        {
            if (!session.IsSuspended) return;
            var state = current;
            if (!state.Announced)
            {
                state.Announced = true;
                NotifySuspended(session);
            }

            // 回调可能已经停止端点或关闭会话，重试前重新检查状态。
            if (!disposed && session.IsSuspended && state.Attempt == null && System.Diagnostics.Stopwatch.GetTimestamp() >= state.RetryAt)
                OpenSession(state.Address, state.Ticket, state, null, null);
        }

        /// <summary>连接指定 IP 和端口。可恢复端点首次连接时 key 留空，恢复时传入服务端此前返回的 ReconnectKey。</summary>
        public void Start(string ip, int port, string key = null, Action<TSession> completed = null, Action<Exception> failed = null)
        {
            EnsureActive();
            var address = new IPEndPoint(IPAddress.Parse(ip), port);
            if (string.IsNullOrEmpty(key))
            {
                Start(address, completed, failed);
                return;
            }
            byte[] ticket = Convert.FromBase64String(key);
            key = Convert.ToBase64String(ticket);
            if (ticket.Length != SessionOpenRequest.KeyBytes) throw new ArgumentException("重连凭据长度无效。", nameof(key));

            // 本端还持有旧会话时沿用其代理和黑板；新的客户端只恢复服务端状态。
            var state = current;
            if (state != null)
            {
                if (!state.Address.Equals(address) || state.Session.ReconnectKey != key || !state.Session.IsSuspended || state.Attempt != null)
                    throw new InvalidOperationException("客户端已有会话或正在握手。");
                OpenSession(address, ticket, state, completed, failed);
                return;
            }
            OpenSession(address, ticket, null, completed, failed);
        }

        /// <summary>异步发起 TCP 握手。轮询线程完成 Session 装配后调用 completed；失败交给 failed 或 Error。</summary>
        public void Start(IPEndPoint address, Action<TSession> completed = null, Action<Exception> failed = null)
        {
            EnsureActive();
            if (address == null) throw new ArgumentNullException(nameof(address));
            OpenSession(address, null, null, completed, failed);
        }

        /// <summary>TCP 建连后发送 SessionOpen。首次握手携带类型，恢复握手同时携带原凭据。</summary>
        private void OpenSession(IPEndPoint address, byte[] ticket, SavedSession previous,
            Action<TSession> completed, Action<Exception> failed)
        {
            if (pendingSockets.Count != 0 || (previous == null && current != null)) throw new InvalidOperationException("客户端已有会话或正在握手。");
            var socket = new TcpConnection(address.AddressFamily, new TcpFrameProtocol(SessionOpenRequest.MaxBytes));
            if (previous != null) previous.Attempt = socket;
            void OnFailure(Exception error)
            {
                if (previous == null) (failed ?? ReportError)(error);
                else
                {
                    ScheduleRetry(previous);
                    failed?.Invoke(error);
                }
            }

            var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnReply(RpcWire.Frame response)
            {
                if (response.Op == CmdOp.ResumeRejected && previous != null)
                {
                    socket.Dispose();
                    previous.Attempt = null;
                    previous.Session.Terminate("RESUME_REJECTED");
                    failed?.Invoke(new IOException("服务端已拒绝恢复会话。"));
                    return;
                }
                int expectedSize = ticket == null ? SessionOpenRequest.KeyBytes : 0;
                if (response.Op != CmdOp.SessionReady || response.Payload.Length != expectedSize)
                {
                    socket.Dispose();
                    OnFailure(new IOException("服务端未接受会话：" + response.Op));
                    return;
                }
                var state = previous ?? new SavedSession { Ticket = ticket ?? response.Payload, Address = address };
                BindClientSession(socket, state, previous != null, activated, completed, OnFailure);
            }
            var reply = Exchange(socket, address, ticket, activated.Task);
            WaitForSocket(socket, reply, () => OnReply(reply.Result), OnFailure);
        }

        /// <summary>清除本次连接尝试，按当前退避间隔安排下次重试，再将后续间隔翻倍，最长 4 秒。</summary>
        private static void ScheduleRetry(SavedSession state)
        {
            state.Attempt = null;
            state.RetryAt = After(state.RetryDelayMs);
            state.RetryDelayMs = Math.Min(state.RetryDelayMs * 2, 4000);
        }

        /// <summary>客户端建立 TCP 连接，发送 SessionOpen，然后等待服务端的会话应答。</summary>
        /// <param name="socket">本次握手使用的新套接字；等待期间由端点持有。</param>
        /// <param name="address">服务端监听地址。</param>
        /// <param name="ticket">首次连接传 null；恢复时传入原凭据。两种握手都序列化 SessionOpenRequest。</param>
        /// <param name="activation">会话激活后完成，允许读循环继续处理下一包。</param>
        /// <returns>收到的握手应答帧，交给 Poll 处理。</returns>
        /// <remarks>连接、写入和读取异常会传给 WaitForSocket，由轮询线程关闭失败的套接字并派发失败回调。</remarks>
        private async Task<RpcWire.Frame> Exchange(TcpConnection socket, IPEndPoint address, byte[] ticket,
            Task activation)
        {
            await socket.ConnectAsync(address).ConfigureAwait(false);
            await socket.SendAsync(CmdOp.SessionOpen, new SessionOpenRequest { SessionKind = kind, ReconnectKey = ticket }.Encode()).ConfigureAwait(false);
            return await Session.SessionBinding.ReceiveHandshakeAsync(socket, activation, expectOpen: false).ConfigureAwait(false);
        }

        /// <summary>仅在轮询线程创建、注册会话和派发事件。恢复路径不会再次调用业务工厂。</summary>
        private void BindClientSession(TcpConnection socket, SavedSession state, bool resume, TaskCompletionSource<bool> activated, Action<TSession> completed, Action<Exception> failed)
        {
            state.Attempt = null;
            try
            {
                long run = Generation;
                if (!resume) state.Session = createSession() ?? throw new InvalidOperationException("会话工厂返回空值。");
                if (run != Generation)
                {
                    state.Session.Dispose();
                    socket.Dispose();
                    return;
                }
                socket.SetFrameProtocol(new TcpFrameProtocol(state.Session.MaxFrameBytes));
                state.Session.Bind(socket, retentionMs, resume);
                state.Session.ReconnectKey = Convert.ToBase64String(state.Ticket);
            }
            catch (Exception error)
            {
                socket.Dispose();
                if (!resume) state.Session?.Dispose();
                failed?.Invoke(error);
                return;
            }
            activated.TrySetResult(true);
            current = state;
            state.Announced = false;
            state.RetryAt = 0;
            state.RetryDelayMs = 250;
            if (resume) NotifyResumed(state.Session);
            else { sessions.Add(state.Session); NotifyConnected(state.Session); }
            if (current == state && !state.Session.Closed.IsCompleted) completed?.Invoke(state.Session);
        }
    }
}
