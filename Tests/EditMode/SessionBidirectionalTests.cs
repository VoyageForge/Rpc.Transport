using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class SessionBidirectionalTests
    {
        private sealed class TestSession : Session
        {
            public TestSession(RpcControllerRegistry registry) : base(registry) { }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator 同一条连接的两端都可以主动发起RPC()
        {
            var run = VerifyBidirectionalSession();
            while (!run.IsCompleted) yield return null;
            run.GetAwaiter().GetResult();
        }

        private static async Task VerifyBidirectionalSession()
        {
            var clientRegistry = new RpcControllerRegistry();
            clientRegistry.Register(new RpcMethodDescriptor
            {
                MethodId = 101,
                ControllerName = "Client",
                MethodName = "Reply",
                Signature = "()",
                Handler = (session, payload, ct) => Task.FromResult(global::MemoryPack.MemoryPackSerializer.Serialize("client-reply"))
            });

            var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int repeatedCallCount = 0;

            var serverRegistry = new RpcControllerRegistry();
            serverRegistry.Register(new RpcMethodDescriptor
            {
                MethodId = 202,
                ControllerName = "Server",
                MethodName = "Reply",
                Signature = "()",
                Handler = (session, payload, ct) => Task.FromResult(global::MemoryPack.MemoryPackSerializer.Serialize("server-reply"))
            });
            serverRegistry.Register(new RpcMethodDescriptor
            {
                MethodId = 404,
                ControllerName = "Server",
                MethodName = "Increment",
                Signature = "()",
                Handler = (session, payload, ct) => Task.FromResult(
                    global::MemoryPack.MemoryPackSerializer.Serialize(Interlocked.Increment(ref repeatedCallCount)))
            });
            serverRegistry.Register(new RpcMethodDescriptor
            {
                MethodId = 303,
                ControllerName = "Server",
                MethodName = "WaitForCancellation",
                Signature = "()",
                Handler = async (session, payload, ct) =>
                {
                    requestStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        requestCancelled.TrySetResult(true);
                        throw;
                    }
                    return global::MemoryPack.MemoryPackSerializer.Serialize("unexpected");
                }
            });

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using (var clientSocket = new TcpClient())
                {
                    await clientSocket.ConnectAsync(IPAddress.Loopback, port);
                    using (TcpClient serverSocket = await listener.AcceptTcpClientAsync())
                    using (var clientSession = new TestSession(clientRegistry))
                    using (var serverSession = new TestSession(serverRegistry))
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        clientSession.Attach(clientSocket);
                        serverSession.Attach(serverSocket);

                        string serverResult = await clientSession.InvokeAsync<string>(202, Array.Empty<byte>(), timeout.Token);
                        string clientResult = await serverSession.InvokeAsync<string>(101, Array.Empty<byte>(), timeout.Token);

                        Assert.AreEqual("server-reply", serverResult);
                        Assert.AreEqual("client-reply", clientResult);

                        // 再次调用同一业务方法时，服务端必须重新执行 Handler。
                        int firstExecution = await clientSession.InvokeAsync<int>(404, Array.Empty<byte>(), timeout.Token);
                        int secondExecution = await clientSession.InvokeAsync<int>(404, Array.Empty<byte>(), timeout.Token);
                        Assert.AreEqual(1, firstExecution);
                        Assert.AreEqual(2, secondExecution);
                        Assert.AreEqual(2, repeatedCallCount);

                        using (var requestCancellation = new CancellationTokenSource())
                        {
                            Task<string> pending = clientSession.InvokeAsync<string>(303, Array.Empty<byte>(), requestCancellation.Token);
                            Task started = await Task.WhenAny(requestStarted.Task, Task.Delay(5000));
                            Assert.AreSame(requestStarted.Task, started, "服务端没有开始执行待取消的请求。");

                            requestCancellation.Cancel();

                            // Unity Test Framework 1.1.33 内置的 NUnit 是 3.5，
                            // 没有 Assert.ThrowsAsync（3.6 才加入）。这里用手动 try/catch 断言，
                            // 行为等价且不依赖 NUnit 版本。
                            bool cancelledAsExpected = false;
                            try
                            {
                                await pending;
                            }
                            catch (TaskCanceledException)
                            {
                                cancelledAsExpected = true;
                            }

                            Assert.IsTrue(cancelledAsExpected, "取消后应当抛出 TaskCanceledException。");

                            Task cancelled = await Task.WhenAny(requestCancelled.Task, Task.Delay(5000));
                            Assert.AreSame(requestCancelled.Task, cancelled, "远端没有收到取消通知。");

                            string afterCancel = await clientSession.InvokeAsync<string>(202, Array.Empty<byte>(), timeout.Token);
                            Assert.AreEqual("server-reply", afterCancel, "取消请求不应破坏同一连接后续的 RPC。");
                        }
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
