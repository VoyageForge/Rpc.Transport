using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>直接使用回环 socket 验证物理传输；测试不创建 Session 或 RPC Controller。</summary>
    public sealed class TcpConnectionTests
    {
        /// <summary>内存连接直接交付消息，用来验证会话绑定只依赖消息包契约。</summary>
        private sealed class MemoryConnection : ConnectionBase
        {
            internal byte[] LastSent;
            internal int Starts;
            internal int Closes;

            internal Task InjectAsync(byte[] packet) => DeliverPacketAsync(packet);
            protected override void StartReceiveCore() => Starts++;
            protected override void CloseTransport() => Closes++;

            public override Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
            {
                LastSent = packet.ToArray();
                return Task.CompletedTask;
            }
        }

        private sealed class Peer : Session
        {
            internal Peer() : base(null) { }
        }

        [Test]
        public void 会话可以接管非TCP连接并释放一次()
        {
            using var connection = new MemoryConnection();
            using var session = new Peer();
            session.Attach(connection);
            connection.StartReceiving();
            Assert.AreEqual(1, connection.Starts);
            connection.InjectAsync(new byte[] { (byte)CmdOp.Ping }).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new byte[] { (byte)CmdOp.Pong }, connection.LastSent);
            session.Dispose();
            connection.Dispose();
            Assert.AreEqual(1, connection.Closes);
            Assert.AreEqual("CONNECTION_CLOSED", session.Closed.Result);
        }

        private static void WithConnection(Action<TcpConnection, NetworkStream> test)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var peer = new TcpClient();
                peer.Connect((IPEndPoint)listener.LocalEndpoint);
                using var connection = new TcpConnection(listener.AcceptTcpClient(), 1024);
                test(connection, peer.GetStream());
            }
            finally { listener.Stop(); }
        }

        [Test]
        public void 分段收包只回调完整包()
        {
            WithConnection((connection, wire) =>
            {
                var protocol = new TcpFrameProtocol(1024);
                using var encoded = new MemoryStream();
                byte[] body = { 1, 2, 3 };
                protocol.WriteFrameAsync(encoded, CmdOp.RpcRequest, body, CancellationToken.None).GetAwaiter().GetResult();
                var bytes = encoded.ToArray();
                var received = new TaskCompletionSource<byte[]>();
                connection.PacketReceived += packet => received.TrySetResult(packet.ToArray());
                connection.StartReceiving();
                wire.Write(bytes, 0, bytes.Length - 1);
                Assert.IsFalse(received.Task.Wait(50));
                wire.WriteByte(bytes[bytes.Length - 1]);
                Assert.IsTrue(received.Task.Wait(3000));
                CollectionAssert.AreEqual(new byte[] { (byte)CmdOp.RpcRequest, 1, 2, 3 }, received.Task.Result);
            });
        }

        [Test]
        public void 半帧断开不交付数据且断开通知只触发一次()
        {
            WithConnection((connection, wire) =>
            {
                int disconnects = 0;
                int packets = 0;
                string closeReason = null;
                connection.PacketReceived += _ => Interlocked.Increment(ref packets);
                connection.Disconnected += reason =>
                {
                    closeReason = reason;
                    Interlocked.Increment(ref disconnects);
                };
                connection.StartReceiving();
                wire.WriteByte(0xaa);
                wire.Dispose();
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref disconnects) > 0, 3000));
                Assert.AreEqual("CONNECTION_LOST", closeReason);
                Assert.AreEqual(0, packets);
                connection.Dispose();
                Assert.AreEqual(1, disconnects);
            });
        }

        [Test]
        public void 并发发送的帧互不穿插()
        {
            WithConnection((connection, wire) =>
            {
                var writes = new Task[24];
                for (int i = 0; i < writes.Length; i++)
                {
                    byte id = (byte)i;
                    writes[i] = Task.Run(() => connection.SendAsync(new[] { (byte)CmdOp.RpcResponse, id, id }));
                }
                var seen = new HashSet<byte>();
                var protocol = new TcpFrameProtocol(1024);
                for (int i = 0; i < writes.Length; i++)
                {
                    var read = protocol.ReadFrameAsync(wire, CancellationToken.None);
                    Assert.IsTrue(read.Wait(3000));
                    var frame = read.Result.Value;
                    Assert.AreEqual(CmdOp.RpcResponse, frame.Op);
                    Assert.AreEqual(2, frame.Payload.Length);
                    Assert.AreEqual(frame.Payload[0], frame.Payload[1]);
                    Assert.IsTrue(seen.Add(frame.Payload[0]));
                }
                Assert.IsTrue(Task.WaitAll(writes, 3000));
            });
        }
    }
}
