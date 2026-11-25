using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class RpcTcpEndpointTests
    {
        private sealed class Peer : Session
        {
            public Peer(TcpClient socket) : base(Registry()) => Attach(socket);
            private static RpcControllerRegistry Registry()
            {
                var registry = new RpcControllerRegistry();
                registry.Register(new RpcMethodDescriptor
                {
                    MethodId = 7, ControllerName = "Test", MethodName = "Reply", Signature = "()",
                    Handler = (_, __, ___) => Task.FromResult(MemoryPack.MemoryPackSerializer.Serialize(42))
                });
                return registry;
            }
        }

        private static void Until(Func<bool> done, Action poll)
        {
            var timer = Stopwatch.StartNew();
            while (!done())
            {
                Assert.Less(timer.ElapsedMilliseconds, 5000, "Timed out waiting for TCP");
                poll();
                Thread.Sleep(1);
            }
        }

        /// <summary>同时建立两条回环连接，验证主线程回调、强类型结果和两端各一次断开事件。</summary>
        [Test]
        public void MultipleClientsReceiveTypedRepliesAndDisconnectOnce()
        {
            using var server = new RpcTcpEndpoint<Peer>(socket => new Peer(socket));
            using var client = new RpcTcpEndpoint<Peer>(socket => new Peer(socket));
            int owner = Thread.CurrentThread.ManagedThreadId;
            int accepted = 0, connected = 0, replies = 0, lost = 0, closed = 0;
            server.Connected += _ => accepted++;
            client.Connected += _ => connected++;
            server.Disconnected += (_, reason) => { Assert.AreEqual("CONNECTION_LOST", reason); lost++; };
            client.Disconnected += (_, reason) => { Assert.AreEqual("CONNECTION_CLOSED", reason); closed++; };
            var address = server.Listen(new IPEndPoint(IPAddress.Loopback, 0));
            for (int i = 0; i < 2; i++)
                client.Connect(address, session => client.OnResult(session.InvokeAsync<int>(7, Array.Empty<byte>()), value =>
                {
                    Assert.AreEqual(owner, Thread.CurrentThread.ManagedThreadId);
                    Assert.AreEqual(42, value);
                    replies++;
                    session.Dispose();
                }, error => Assert.Fail(error.ToString())), error => Assert.Fail(error.ToString()));
            Until(() => lost == 2 && closed == 2, () => { server.Poll(); client.Poll(); });
            server.Poll();
            client.Poll();
            Assert.AreEqual((2, 2, 2, 2, 2), (accepted, connected, replies, lost, closed));
        }

        /// <summary>握手失败及调用取消分别触发一次失败回调。</summary>
        [Test]
        public void RefusalAndCancelledCallDeliverOneFailureEach()
        {
            using var client = new RpcTcpEndpoint<Peer>(socket => new Peer(socket));
            var temp = new TcpListener(IPAddress.Loopback, 0);
            temp.Start();
            var address = (IPEndPoint)temp.LocalEndpoint;
            temp.Stop();
            int failures = 0;
            client.Error += error => { Assert.IsInstanceOf<SocketException>(error); failures++; };
            client.Connect(address, _ => Assert.Fail("Unexpected connection"));
            client.OnResult(Task.FromCanceled<int>(new CancellationToken(true)), _ => Assert.Fail(),
                error => { Assert.IsInstanceOf<OperationCanceledException>(error); failures++; });
            Until(() => failures == 2, client.Poll);
            client.Poll();
            Assert.AreEqual(2, failures);
        }

        /// <summary>握手期间释放端点后，旧回调不得执行，监听端口可立即重用。</summary>
        [Test]
        public void DisposeDuringConnectDropsQueuedCallbacksAndReleasesListener()
        {
            var endpoint = new RpcTcpEndpoint<Peer>(socket => new Peer(socket));
            var address = endpoint.Listen(new IPEndPoint(IPAddress.Loopback, 0));
            endpoint.Connect(address, _ => Assert.Fail());
            endpoint.OnResult(Task.CompletedTask, () => Assert.Fail());
            endpoint.Dispose();
            endpoint.Dispose();
            Assert.Throws<ObjectDisposedException>(endpoint.Poll);
            var rebound = new TcpListener(address);
            try { rebound.Start(); } finally { rebound.Stop(); }
        }
    }
}
