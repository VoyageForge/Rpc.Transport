using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>以原始 TCP 对端控制何时回包，独立验证 Session 的探测和关闭原因。</summary>
    public sealed class SessionHeartbeatTests
    {
        private sealed class Peer : Session
        {
            public Peer() : base(null)
            {
            }

            protected override int HeartbeatIdleMilliseconds => 75;
            protected override int HeartbeatTimeoutMilliseconds => 600;
        }

        /// <summary>用真实回环连接运行断言；测试结束或失败时两个套接字和监听端口都会释放。</summary>
        private static void WithPeer(Action<Peer, NetworkStream> verify)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var socket = new TcpClient();
                socket.Connect((IPEndPoint)listener.LocalEndpoint);
                using var accepted = listener.AcceptTcpClient();
                using var session = new Peer();
                session.Attach(accepted);
                verify(session, socket.GetStream());
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>等待一帧并同时检查操作码及正文长度，设置上限以防测试挂起。</summary>
        private static void ExpectPing(NetworkStream stream)
        {
            var frame = new TcpFrameProtocol(1024).ReadFrameAsync(stream, CancellationToken.None);
            Assert.IsTrue(frame.Wait(5000), "等待 Ping 超时");
            Assert.IsTrue(frame.Result.HasValue, "读到 EOF，连接提前关闭");
            Assert.AreEqual(CmdOp.Ping, frame.Result.Value.Op);
            Assert.IsEmpty(frame.Result.Value.Payload);
        }

        [Test]
        public void 空闲连接收到Pong后继续探测而静默对端触发心跳超时()
        {
            WithPeer((session, wire) =>
            {
                ExpectPing(wire);
                new TcpFrameProtocol(1024).WriteFrameAsync(wire, CmdOp.Pong, Array.Empty<byte>(),
                    CancellationToken.None).GetAwaiter().GetResult();
                ExpectPing(wire);
                Assert.IsFalse(session.Closed.IsCompleted);
                // 对端收下请求但永不回复；超时必须同时结束这笔等待中的 RPC。
                var pending = session.InvokeAsync<int>(1, Array.Empty<byte>());
                var request = new TcpFrameProtocol(1024).ReadFrameAsync(wire, CancellationToken.None);
                Assert.IsTrue(request.Wait(2000));
                Assert.AreEqual(CmdOp.RpcRequest, request.Result.Value.Op);
                Assert.IsTrue(session.Closed.Wait(5000));
                Assert.AreEqual("HEARTBEAT_TIMEOUT", session.Closed.Result);
                var failure = Assert.Throws<RpcSessionException>(() => pending.GetAwaiter().GetResult());
                StringAssert.Contains("HEARTBEAT_TIMEOUT", failure.Message);
                wire.ReadTimeout = 2000;
                Assert.AreEqual(-1, wire.ReadByte(), "超时后对端应读到 EOF");
            });
        }

        [Test]
        public void 等待心跳时主动释放保持本地主动关闭原因()
        {
            WithPeer((session, wire) =>
            {
                ExpectPing(wire);
                session.Dispose();
                Assert.IsTrue(session.Closed.IsCompleted);
                Assert.AreEqual("CONNECTION_CLOSED", session.Closed.Result);
                wire.ReadTimeout = 2000;
                Assert.AreEqual(-1, wire.ReadByte());
            });
        }
    }
}