using System;
using System.Net;
using System.Net.Sockets;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>监听 TCP 并将已接入的物理连接交给会话服务端。</summary>
    public sealed class TcpServer : RpcServer
    {
        private TcpListener listener;
        public TcpServer(int retentionMilliseconds = 30000) : base(retentionMilliseconds) { }
        internal TcpServer(int retentionMilliseconds, Func<string, Session> createSession)
            : base(retentionMilliseconds, createSession) { }

        protected override void StopListening()
        {
            listener?.Stop();
            listener = null;
        }
        protected override void ConfigureConnection(ConnectionBase connection, int maxBytes) =>
            ((TcpConnection)connection).SetFrameProtocol(new TcpFrameProtocol(maxBytes));

        /// <summary>开始监听并返回实际绑定地址。端口传 0 时，由操作系统选择可用端口。</summary>
        public IPEndPoint Start(IPEndPoint address)
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

        /// <summary>每帧最多接受 16 个连接，接管连接后等待握手帧。</summary>
        protected override void AcceptPendingConnections()
        {
            for (int i = 0; i < 16 && !disposed && listener != null; i++)
            {
                TcpClient socket = null;
                try
                {
                    if (!listener.Pending()) break;
                    socket = listener.AcceptTcpClient();
                    AcceptConnection(new TcpConnection(socket, SessionOpenRequest.MaxBytes));
                }
                catch (Exception error)
                {
                    socket?.Dispose();
                    ReportError(error);
                    break;
                }
            }
        }
    }
}
