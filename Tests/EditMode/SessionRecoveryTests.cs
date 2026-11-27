using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>用真实回环 TCP 和两个独立端点验证恢复，全程不注册登录接口。</summary>
    public sealed class SessionRecoveryTests
    {
        private sealed class Peer : Session
        {
            public Peer(RpcControllerRegistry routes = null) : base(routes)
            {
                RegisterRemote<Func<Task<int>>>(() => InvokeAsync<int>(7, Array.Empty<byte>()));
            }
        }

        private static byte[] OpenBody(byte[] ticket = null, string kind = "game") =>
            new SessionOpenRequest { SessionKind = kind, ReconnectKey = ticket }.Encode();

        private static readonly IPEndPoint Loopback = new IPEndPoint(IPAddress.Loopback, 0);
        private static void Until(Func<bool> done, Action poll)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                Assert.Less(watch.ElapsedMilliseconds, 6000, "等待网络状态超时");
                poll();
                Thread.Sleep(1);
            }
        }

        [Test]
        public void 连续写入握手和下一包时先确认再交付()
        {
            using var server = new TcpServer();
            server.RegisterSession("game", () => new Peer());
            using var socket = new TcpClient();
            socket.Connect(server.Start(Loopback));
            var wire = socket.GetStream();
            var protocol = new TcpFrameProtocol(1024);
            using var batch = new MemoryStream();
            protocol.WriteFrameAsync(batch, CmdOp.SessionOpen, OpenBody(), CancellationToken.None).GetAwaiter().GetResult();
            protocol.WriteFrameAsync(batch, CmdOp.Ping, null, CancellationToken.None).GetAwaiter().GetResult();
            byte[] bytes = batch.ToArray();
            wire.Write(bytes, 0, bytes.Length);

            foreach (var expected in new[] { CmdOp.SessionReady, CmdOp.Pong })
            {
                var response = protocol.ReadFrameAsync(wire, CancellationToken.None);
                Until(() => response.IsCompleted, server.Poll);
                Assert.AreEqual(expected, response.Result.Value.Op);
            }
        }

        [Test]
        public void 建立会话前可先Ping再发送SessionOpen()
        {
            using var server = new TcpServer();
            server.RegisterSession("game", () => new Peer());
            using var socket = new TcpClient();
            socket.Connect(server.Start(Loopback));
            var wire = socket.GetStream();
            var protocol = new TcpFrameProtocol(1024);
            RpcWire.Frame Send(CmdOp op)
            {
                protocol.WriteFrameAsync(wire, op, op == CmdOp.SessionOpen ? OpenBody() : null, CancellationToken.None).GetAwaiter().GetResult();
                var read = protocol.ReadFrameAsync(wire, CancellationToken.None);
                Until(() => read.IsCompleted, server.Poll);
                return read.Result.Value;
            }
            Assert.AreEqual(CmdOp.Pong, Send(CmdOp.Ping).Op);
            var ready = Send(CmdOp.SessionOpen);
            Assert.AreEqual(CmdOp.SessionReady, ready.Op);
            Assert.AreEqual(32, ready.Payload.Length);
        }

        [Test]
        public void 通过IP端口和旧凭据在新客户端接回服务端会话()
        {
            using var server = new TcpServer();
            server.RegisterSession("game", () => new Peer());
            var address = server.Start(Loopback);
            Session original = null, resumed = null, clientSession = null;
            server.Connected += peer => original = peer;
            server.Resumed += peer => resumed = peer;
            string key;
            using (var first = new RpcClient<Peer>("game", () => new Peer()))
            {
                first.Start("127.0.0.1", address.Port, completed: peer => clientSession = peer);
                Until(() => original != null && clientSession != null, () => { server.Poll(); first.Poll(); });
                key = clientSession.ReconnectKey;
                Assert.IsNotEmpty(key);
            }
            Until(() => original.IsSuspended, server.Poll);
            using var next = new RpcClient<Peer>("game", () => new Peer());
            Session newClient = null;
            next.Start("127.0.0.1", address.Port, key, peer => newClient = peer);
            Until(() => resumed != null && newClient != null, () => { server.Poll(); next.Poll(); });
            Assert.AreSame(original, resumed);
            Assert.AreEqual(key, newClient.ReconnectKey);
            Assert.AreNotSame(clientSession, newClient);
        }

        [Test]
        public void 恢复同一会话和代理且旧调用不会重发()
        {
            var routes = new RpcControllerRegistry();
            int calls = 0;
            var finishOld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            routes.Register(new RpcMethodDescriptor
            {
                MethodId = 7, ControllerName = "Echo", MethodName = "Echo", Signature = "()",
                Handler = async (session, _, ct) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) await finishOld.Task.ConfigureAwait(false);
                    return MemoryPack.MemoryPackSerializer.Serialize((int)session.Blackboard["value"]);
                }
            });
            using var server = new TcpServer();
            server.RegisterSession("game", () => new Peer(routes));
            using var client = new RpcClient<Peer>("game", () => new Peer());
            Session inbound = null, outbound = null;
            int opens = 0, resumes = 0, suspends = 0;
            server.Connected += peer => { inbound = peer; opens++; };
            client.Connected += peer => { outbound = peer; opens++; };
            server.Resumed += peer => { Assert.AreSame(inbound, peer); resumes++; };
            client.Resumed += peer => { Assert.AreSame(outbound, peer); resumes++; };
            client.Suspended += _ => suspends++;
            server.Suspended += _ => suspends++;
            void Poll() { server.Poll(); client.Poll(); }
            client.Start(server.Start(Loopback));
            Until(() => opens == 2, Poll);
            var proxy = outbound.Remote<Func<Task<int>>>();
            inbound.Blackboard["value"] = 42;
            var old = proxy();
            try
            {
                Until(() => Volatile.Read(ref calls) == 1, Poll);
                outbound.Suspend();
                Assert.IsTrue(outbound.IsSuspended);
                Assert.Throws<IOException>(() => proxy().GetAwaiter().GetResult());
                Assert.Throws<RpcSessionException>(() => old.GetAwaiter().GetResult());
                Until(() => resumes == 2, Poll);
                Assert.AreSame(proxy, outbound.Remote<Func<Task<int>>>());
                Assert.IsFalse(outbound.Closed.IsCompleted);
                finishOld.TrySetResult(true);
                var current = proxy();
                Until(() => current.IsCompleted, Poll);
                Assert.AreEqual(42, current.GetAwaiter().GetResult());
                Assert.AreEqual(2, calls);
                Assert.AreEqual(2, opens);
                Assert.AreEqual(2, suspends);
            }
            finally { finishOld.TrySetResult(true); }
        }

        [Test]
        public void 客户端放弃后服务端到期释放且只通知一次()
        {
            using var server = new TcpServer(180);
            server.RegisterSession("game", () => new Peer());
            using var client = new RpcClient<Peer>("game", () => new Peer());
            Session accepted = null, connected = null;
            int terminated = 0;
            server.Connected += peer => accepted = peer;
            server.Disconnected += (_, reason) => { Assert.AreEqual("RESUME_EXPIRED", reason); terminated++; };
            client.Start(server.Start(Loopback), peer => connected = peer);
            Until(() => accepted != null && connected != null, () => { client.Poll(); server.Poll(); });
            accepted.Blackboard["value"] = new object();
            client.Dispose();
            Assert.AreEqual("CONNECTION_CLOSED", connected.Closed.Result);
            // 即使应用暂时停止轮询，保留期限也由单次定时器独立执行。
            Assert.IsTrue(accepted.Closed.Wait(3000));
            Assert.IsFalse(accepted.IsSuspended);
            Assert.IsTrue(accepted.Blackboard.IsEmpty);
            server.Poll();
            server.Poll();
            Assert.AreEqual(1, terminated);
        }

        [Test]
        public void 服务端放弃会话后客户端收到拒绝并永久释放()
        {
            using var server = new TcpServer();
            server.RegisterSession("game", () => new Peer());
            using var client = new RpcClient<Peer>("game", () => new Peer());
            Session inbound = null, outbound = null;
            int opens = 0;
            server.Connected += peer => { inbound = peer; opens++; };
            client.Start(server.Start(Loopback), peer => outbound = peer);
            void Poll() { server.Poll(); client.Poll(); }
            Until(() => inbound != null && outbound != null, Poll);
            inbound.Dispose();
            Until(() => outbound.Closed.IsCompleted, Poll);
            Assert.AreEqual("RESUME_REJECTED", outbound.Closed.Result);
            Assert.AreEqual(1, opens);
        }

        [Test]
        public void 凭据不能接管活跃连接且清理后不可复用()
        {
            using var server = new TcpServer();
            server.RegisterSession("game", () => new Peer());
            var address = server.Start(Loopback);
            var frames = new TcpFrameProtocol(1024);
            Session accepted = null;
            int opens = 0;
            server.Connected += peer => { accepted = peer; opens++; };
            RpcWire.Frame Open(TcpClient socket, byte[] ticket, string kind = "game")
            {
                socket.Connect(address);
                var wire = socket.GetStream();
                frames.WriteFrameAsync(wire, CmdOp.SessionOpen, OpenBody(ticket, kind), CancellationToken.None).GetAwaiter().GetResult();
                var response = frames.ReadFrameAsync(wire, CancellationToken.None);
                Until(() => response.IsCompleted, server.Poll);
                return response.Result.Value;
            }

            using var first = new TcpClient();
            var hello = Open(first, null);
            Assert.AreEqual(CmdOp.SessionReady, hello.Op);
            Assert.AreEqual(32, hello.Payload.Length);
            Until(() => accepted != null, server.Poll);
            CollectionAssert.AreEqual(new[] { accepted }, server.Sessions);
            int lobbyCreated = 0;
            server.RegisterSession("lobby", () => { lobbyCreated++; return new Peer(); });
            using var wrongKind = new TcpClient();
            Assert.AreEqual(CmdOp.ResumeRejected, Open(wrongKind, hello.Payload, "lobby").Op);
            using var unregistered = new TcpClient();
            Assert.AreEqual(CmdOp.ResumeRejected, Open(unregistered, null, "unknown").Op);
            Assert.AreEqual(0, lobbyCreated);
            using var duplicate = new TcpClient();
            Assert.AreEqual(CmdOp.ResumeBusy, Open(duplicate, hello.Payload).Op);
            using var unknown = new TcpClient();
            Assert.AreEqual(CmdOp.ResumeRejected, Open(unknown, new byte[32]).Op);
            accepted.Dispose();
            using var abandoned = new TcpClient();
            Assert.AreEqual(CmdOp.ResumeRejected, Open(abandoned, hello.Payload).Op);
            Assert.IsEmpty(server.Sessions);
            using var lobby = new TcpClient();
            Assert.AreEqual(CmdOp.SessionReady, Open(lobby, null, "lobby").Op);
            Assert.AreEqual(1, lobbyCreated);
            Until(() => server.Sessions.Count == 1, server.Poll);
            Assert.AreEqual(2, opens);
        }

        [Test]
        public void 成功应答尚未交付时释放端点也会释放候选会话()
        {
            Session candidate = null;
            using var server = new TcpServer();
            server.RegisterSession("game", () => candidate = new Peer());
            using var socket = new TcpClient();
            socket.Connect(server.Start(Loopback));
            bool published = false;
            server.Connected += _ => published = true;
            var wire = socket.GetStream();
            new TcpFrameProtocol(1024).WriteFrameAsync(wire, CmdOp.SessionOpen, OpenBody(), CancellationToken.None)
                .GetAwaiter().GetResult();
            Until(() => candidate != null, server.Poll);
            Assert.IsFalse(published);
            server.Dispose();
            Assert.AreEqual("CONNECTION_CLOSED", candidate.Closed.Result);
            Assert.IsFalse(candidate.IsConnected);
            Assert.IsFalse(published);
        }

        [Test]
        public void 端点退出时关闭等待握手的套接字且不会创建会话()
        {
            int created = 0;
            using var server = new TcpServer();
            server.RegisterSession("game", () => { created++; return new Peer(); });
            using var socket = new TcpClient();
            socket.Connect(server.Start(Loopback));
            // 先收到 Pong，确认端点已接管物理连接且仍在等待 SessionOpen。
            var wire = socket.GetStream();
            var protocol = new TcpFrameProtocol(1024);
            protocol.WriteFrameAsync(wire, CmdOp.Ping, null, CancellationToken.None).GetAwaiter().GetResult();
            var pong = protocol.ReadFrameAsync(wire, CancellationToken.None);
            Until(() => pong.IsCompleted, server.Poll);
            Assert.AreEqual(CmdOp.Pong, pong.Result.Value.Op);

            server.Dispose();
            socket.ReceiveTimeout = 2000;
            try { Assert.AreEqual(-1, wire.ReadByte()); }
            catch (IOException error) when (error.InnerException is SocketException socketError &&
                                            socketError.SocketErrorCode == SocketError.ConnectionReset)
            {
                // 对端退出可表现为 EOF 或 TCP reset；读取超时仍会使测试失败。
            }
            Assert.AreEqual(0, created);
        }
    }
}
