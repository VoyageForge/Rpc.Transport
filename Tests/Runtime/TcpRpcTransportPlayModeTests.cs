using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoyageForge.Rpc.Transport;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>
    /// 真正通过 Loopback Socket 验证 TCP Client/Server。
    /// 测试只通过通讯包公开 Runtime API 使用功能。
    /// </summary>
    public sealed class TcpRpcTransportPlayModeTests
    {
        private const float DefaultTimeoutSeconds = 10f;

        private readonly List<TcpRpcServerTransport> _runningServers = new List<TcpRpcServerTransport>();
        private readonly List<TcpListener> _temporaryListeners = new List<TcpListener>();
        private readonly List<TcpRpcClientTransport> _runningClients = new List<TcpRpcClientTransport>();

        [SetUp]
        public void SetUp()
        {
            _runningServers.Clear();
            _temporaryListeners.Clear();
            _runningClients.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            // 即使某条断言失败，也必须关闭 Listener 和活动连接，避免污染后续 PlayMode 用例。
            foreach (TcpRpcServerTransport server in _runningServers)
            {
                try
                {
                    server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    /* TearDown 不覆盖原始测试失败。 */
                }
            }

            foreach (TcpListener listener in _temporaryListeners)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }
        }

        private TcpRpcServerTransport TrackServer(TcpRpcServerTransport server)
        {
            _runningServers.Add(server);
            return server;
        }

        private TcpListener TrackListener(TcpListener listener)
        {
            _temporaryListeners.Add(listener);
            return listener;
        }

        /// <summary>客户端与服务端建立真实连接，完成一次请求/响应，并验证请求内容完整。</summary>
        [UnityTest]
        public IEnumerator TcpClientServer_RoundTripsRequestAndResponse()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> request = client.SendAsync(CreateStringRequest("hello tcp"), CancellationToken.None);
            yield return WaitForCompletion(request, "TCP 请求/响应超时");

            Assert.IsTrue(request.Result.Success);
            Assert.AreEqual("echo:hello tcp", request.Result.Result);
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>多个客户端并发建立连接；每条连接各自完成一帧请求与一帧响应。</summary>
        [UnityTest]
        public IEnumerator TcpServer_AcceptsMultipleConcurrentClients()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            const int clientCount = 12;
            Task<RpcResponse>[] requests = new Task<RpcResponse>[clientCount];
            for (int index = 0; index < clientCount; index++)
            {
                TcpRpcClientTransport client = CreateClient(server, serializer);
                requests[index] = client.SendAsync(CreateStringRequest("client-" + index), CancellationToken.None);
            }

            Task allRequests = Task.WhenAll(requests);
            yield return WaitForCompletion(allRequests, "并发 TCP 请求超时");
            for (int index = 0; index < clientCount; index++)
            {
                Assert.IsTrue(requests[index].Result.Success);
                Assert.AreEqual("echo:client-" + index, requests[index].Result.Result);
            }

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>长连接：同一个客户端连续多次 SendAsync 复用同一条 TCP 连接。</summary>
        [UnityTest]
        public IEnumerator TcpClient_ReusesSingleLongLivedConnection()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            int serverAcceptedConnections = 0;
            server.OnClientConnected += _ => Interlocked.Increment(ref serverAcceptedConnections);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            int clientConnectedCount = 0;
            client.OnConnected += () => Interlocked.Increment(ref clientConnectedCount);

            const int requestCount = 5;
            for (int index = 0; index < requestCount; index++)
            {
                Task<RpcResponse> request = client.SendAsync(
                    CreateStringRequest("long-lived-" + index), CancellationToken.None);
                yield return WaitForCompletion(request, "第 " + index + " 次长连接请求超时");

                Assert.IsTrue(request.Result.Success);
                Assert.AreEqual("echo:long-lived-" + index, request.Result.Result);
            }

            // 服务端只应接受一次连接（长连接复用），客户端也只应连接一次。
            Assert.AreEqual(1, clientConnectedCount, "客户端应只建立一次长连接。");
            Assert.AreEqual(1, serverAcceptedConnections, "服务端应只接受一次长连接。");

            client.Dispose();
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>客户端主动断开长连接后，服务端应触发 OnClientDisconnected。</summary>
        [UnityTest]
        public IEnumerator TcpServer_RaisesDisconnectedEventWhenClientDisconnects()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            TaskCompletionSource<bool> disconnected = new TaskCompletionSource<bool>();
            server.OnClientDisconnected += _ => disconnected.TrySetResult(true);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> request =
                client.SendAsync(CreateStringRequest("before-disconnect"), CancellationToken.None);
            yield return WaitForCompletion(request, "断开事件测试请求超时");
            Assert.IsTrue(request.Result.Success);

            client.Dispose();
            yield return WaitForCompletion(disconnected.Task, "客户端断开后服务端没有触发 OnClientDisconnected");

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>客户端刚连接就断开且没有发送帧头，不应破坏监听；后续正常请求仍可完成。</summary>
        [UnityTest]
        public IEnumerator TcpServer_HandlesPeerDisconnectBeforeFrame()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Task disconnect = ConnectAndDisconnectAsync(server.LocalEndPoint.Port);
            yield return WaitForCompletion(disconnect, "建立并关闭空连接超时");

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> request =
                client.SendAsync(CreateStringRequest("after-disconnect"), CancellationToken.None);
            yield return WaitForCompletion(request, "断开连接后服务端未继续工作");

            Assert.IsTrue(request.Result.Success);
            Assert.AreEqual("echo:after-disconnect", request.Result.Result);
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>连接发送半个帧后断开应触发 EndOfStreamException，但服务端仍需接受后续连接。</summary>
        [UnityTest]
        public IEnumerator TcpServer_ReportsTruncatedFrameAndContinuesServing()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            server.OnTransportError += errors.Enqueue;
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Task truncated = SendTruncatedFrameAsync(server.LocalEndPoint.Port);
            yield return WaitForCompletion(truncated, "发送截断帧超时");
            yield return WaitForError(errors, "服务端没有报告截断帧错误");

            Exception error;
            Assert.IsTrue(errors.TryDequeue(out error));
            Assert.IsTrue(error is EndOfStreamException || error is IOException,
                "预期得到 EndOfStreamException/IOException，实际为：" + error.GetType().FullName);

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> request =
                client.SendAsync(CreateStringRequest("server-still-alive"), CancellationToken.None);
            yield return WaitForCompletion(request, "坏帧之后服务端没有继续接受请求");
            Assert.AreEqual("echo:server-still-alive", request.Result.Result);

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>零长度和超过上限的帧头都必须被拒绝，并通过 OnTransportError 报告。</summary>
        [UnityTest]
        public IEnumerator TcpServer_RejectsInvalidAndOversizedFrameLengths()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
            TcpRpcServerTransport server =
                TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer, maxFrameBytes: 64, backlog: 128));
            server.OnTransportError += errors.Enqueue;
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Task zeroLength = SendRawHeaderAsync(server.LocalEndPoint.Port, 0);
            yield return WaitForCompletion(zeroLength, "发送零长度帧头超时");
            yield return WaitForError(errors, "零长度帧没有被拒绝");
            Exception error;
            Assert.IsTrue(errors.TryDequeue(out error));
            Assert.IsInstanceOf<InvalidDataException>(error);

            Task oversized = SendRawHeaderAsync(server.LocalEndPoint.Port, 129);
            yield return WaitForCompletion(oversized, "发送超长帧头超时");
            yield return WaitForError(errors, "超长帧没有被拒绝");
            Assert.IsTrue(errors.TryDequeue(out error));
            Assert.IsInstanceOf<InvalidDataException>(error);

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }


        /// <summary>服务端端口已被占用时 StartAsync 应传播 SocketException，而不是留下半启动状态。</summary>
        [UnityTest]
        public IEnumerator TcpServer_StartFailsWhenPortAlreadyInUse()
        {
            TcpListener blocker = TrackListener(new TcpListener(IPAddress.Loopback, 0));
            blocker.Start();
            int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(
                IPAddress.Loopback, port, new NewtonsoftRpcSerializer()));

            Exception observed = null;
            try
            {
                server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                observed = exception;
            }

            Assert.IsInstanceOf<SocketException>(observed);
            Assert.DoesNotThrow(() => server.StopAsync(CancellationToken.None).GetAwaiter().GetResult());
            yield break;
        }

        /// <summary>客户端序列化后的请求超过最大帧大小时，应在建立 Socket 连接之前拒绝发送。</summary>
        [UnityTest]
        public IEnumerator TcpClient_RejectsRequestOverMaximumFrameBytes()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", 1, serializer, maxFrameBytes: 32);
            Task<RpcResponse> request =
                client.SendAsync(CreateStringRequest(new string('x', 256)), CancellationToken.None);
            yield return WaitForCompletion(request, "客户端超长请求检查没有结束");

            Assert.IsTrue(request.IsFaulted, "超出最大帧长的请求不应建立连接或发送。");
            Assert.IsInstanceOf<InvalidDataException>(request.Exception.GetBaseException());
        }

        /// <summary>客户端序列化器抛异常或返回空 Payload 时，错误应在连接建立前传播给调用方。</summary>
        [UnityTest]
        public IEnumerator TcpClient_ReportsRequestSerializationErrors()
        {
            TcpRpcClientTransport throwingClient = new TcpRpcClientTransport(
                "127.0.0.1", 1, new InvalidRequestSerializer(true));
            Task<RpcResponse> throwingRequest = throwingClient.SendAsync(
                CreateStringRequest("serialization-exception"), CancellationToken.None);
            yield return WaitForCompletion(throwingRequest, "请求序列化异常测试超时");
            Assert.IsTrue(throwingRequest.IsFaulted);
            Assert.IsInstanceOf<InvalidOperationException>(throwingRequest.Exception.GetBaseException());

            TcpRpcClientTransport emptyClient = new TcpRpcClientTransport(
                "127.0.0.1", 1, new InvalidRequestSerializer(false));
            Task<RpcResponse> emptyRequest = emptyClient.SendAsync(
                CreateStringRequest("empty-payload"), CancellationToken.None);
            yield return WaitForCompletion(emptyRequest, "空请求 Payload 测试超时");
            Assert.IsTrue(emptyRequest.IsFaulted);
            Assert.IsInstanceOf<InvalidDataException>(emptyRequest.Exception.GetBaseException());
        }

        /// <summary>序列化器无法解析请求时，服务端应返回稳定的 INVALID_REQUEST 错误，而非关闭整个监听。</summary>
        [UnityTest]
        public IEnumerator TcpServer_ReturnsInvalidRequestForMalformedPayload()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Task<RpcResponse> malformed =
                SendRawPayloadAndReadResponseAsync(server.LocalEndPoint.Port, new byte[] { 0xFF });
            yield return WaitForCompletion(malformed, "非法请求响应超时");
            Assert.IsFalse(malformed.Result.Success);
            Assert.AreEqual("INVALID_REQUEST", malformed.Result.Error.Code);

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> healthy =
                client.SendAsync(CreateStringRequest("after-invalid-request"), CancellationToken.None);
            yield return WaitForCompletion(healthy, "非法请求后服务端不可用");
            Assert.AreEqual("echo:after-invalid-request", healthy.Result.Result);

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>业务处理器抛异常、返回 null 时，应转换为 SERVER_HANDLER_ERROR 和 EMPTY_RESPONSE。</summary>
        [UnityTest]
        public IEnumerator TcpServer_ConvertsHandlerExceptionsAndNullResponses()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.OnRequestAsync += (request, ct) =>
            {
                if (request.MethodName == "Throw") throw new InvalidOperationException("handler failed");
                if (request.MethodName == "Empty") return Task.FromResult<RpcResponse>(null);
                return Task.FromResult(RpcResponse.Ok("ok"));
            };
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            TcpRpcClientTransport client = CreateClient(server, serializer);

            Task<RpcResponse> thrown = client.SendAsync(CreateRequest("Throw", ""), CancellationToken.None);
            yield return WaitForCompletion(thrown, "业务异常响应超时");
            Assert.AreEqual("SERVER_HANDLER_ERROR", thrown.Result.Error.Code);

            Task<RpcResponse> empty = client.SendAsync(CreateRequest("Empty", ""), CancellationToken.None);
            yield return WaitForCompletion(empty, "空响应处理超时");
            Assert.AreEqual("EMPTY_RESPONSE", empty.Result.Error.Code);

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>没有注册请求处理器时，应返回 HANDLER_NOT_REGISTERED，而不是使连接处理任务崩溃。</summary>
        [UnityTest]
        public IEnumerator TcpServer_ReturnsHandlerNotRegisteredWhenNoHandlerExists()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> request = client.SendAsync(CreateRequest("NoHandler", ""), CancellationToken.None);
            yield return WaitForCompletion(request, "未注册处理器错误响应超时");

            Assert.IsFalse(request.Result.Success);
            Assert.AreEqual("HANDLER_NOT_REGISTERED", request.Result.Error.Code);
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>目标端口没有服务端监听时，客户端应向调用方传播 SocketException。</summary>
        [UnityTest]
        public IEnumerator TcpClient_ReportsConnectionRefused()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            int port = FindUnusedPort();
            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", port, serializer);
            Task<RpcResponse> request = client.SendAsync(CreateStringRequest("refused"), CancellationToken.None);
            yield return WaitForCompletion(request, "连接拒绝测试超时");

            Assert.IsTrue(request.IsFaulted, "未监听的端口不应该返回成功响应。");
            Assert.IsInstanceOf<SocketException>(request.Exception.GetBaseException());
        }

        /// <summary>客户端收到帧头声称更长、实际却提前断开的响应时，应抛出 EndOfStreamException。</summary>
        [UnityTest]
        public IEnumerator TcpClient_ReportsTruncatedResponse()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpListener fakeServer = TrackListener(new TcpListener(IPAddress.Loopback, 0));
            fakeServer.Start();
            int port = ((IPEndPoint)fakeServer.LocalEndpoint).Port;
            Task fakeServerTask = SendTruncatedResponseAsync(fakeServer);

            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", port, serializer);
            Task<RpcResponse> request = client.SendAsync(CreateStringRequest("client-request"), CancellationToken.None);
            yield return WaitForCompletion(request, "截断响应测试超时");
            yield return WaitForCompletion(fakeServerTask, "模拟服务端未完成");
            fakeServer.Stop();

            Assert.IsFalse(request.Result.Success, "截断响应应使请求失败。");
            Assert.AreEqual(TcpRpcClientTransport.ConnectionLostCode, request.Result.Error?.Code);
        }


        /// <summary>客户端必须拒绝服务端返回的零长度帧和超过配置上限的响应帧。</summary>
        [UnityTest]
        public IEnumerator TcpClient_RejectsInvalidResponseFrameLengths()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            int[] invalidLengths = { 0, 250 };

            for (int index = 0; index < invalidLengths.Length; index++)
            {
                TcpListener fakeServer = TrackListener(new TcpListener(IPAddress.Loopback, 0));
                fakeServer.Start();
                Task fakeServerTask = SendInvalidResponseHeaderAsync(fakeServer, invalidLengths[index]);

                int port = ((IPEndPoint)fakeServer.LocalEndpoint).Port;
                TcpRpcClientTransport client =
                    new TcpRpcClientTransport("127.0.0.1", port, serializer, maxFrameBytes: 200);
                Task<RpcResponse> request = client.SendAsync(CreateStringRequest("bad-frame"), CancellationToken.None);
                yield return WaitForCompletion(request, "非法响应帧长测试超时");
                fakeServer.Stop();

                Assert.IsFalse(request.Result.Success, "客户端不应接受非法响应帧长度：" + invalidLengths[index]);
                Assert.AreEqual(TcpRpcClientTransport.ConnectionLostCode, request.Result.Error?.Code);
            }
        }

        /// <summary>客户端收到完整帧但 Payload 无法反序列化时，应向调用方传播序列化错误。</summary>
        [UnityTest]
        public IEnumerator TcpClient_ReportsMalformedResponsePayload()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpListener fakeServer = TrackListener(new TcpListener(IPAddress.Loopback, 0));
            fakeServer.Start();
            int port = ((IPEndPoint)fakeServer.LocalEndpoint).Port;
            Task fakeServerTask = SendMalformedResponseAsync(fakeServer);

            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", port, serializer);
            Task<RpcResponse> request = client.SendAsync(CreateStringRequest("bad-response"), CancellationToken.None);
            yield return WaitForCompletion(request, "非法响应 Payload 测试超时");
            yield return WaitForCompletion(fakeServerTask, "模拟服务端未完成发送非法 Payload");
            fakeServer.Stop();

            Assert.IsFalse(request.Result.Success, "非法响应 Payload 应使请求失败。");
            Assert.AreEqual(TcpRpcClientTransport.ConnectionLostCode, request.Result.Error?.Code);
        }

        /// <summary>
        /// 客户端在服务端业务处理期间取消请求时，应关闭本次连接并让客户端任务进入取消状态。
        /// TCP 不能保证服务端立刻获知远端关闭；之后主动停止服务端，验证服务端生命周期 Token 才能取消 Handler。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpClient_CancellationClosesConnectionAndCancelsClientTask()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TaskCompletionSource<bool> handlerStarted = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> handlerCancelled = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> serverTokenCancelled = new TaskCompletionSource<bool>();
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.OnRequestAsync += async (request, serverToken) =>
            {
                using (serverToken.Register(() => serverTokenCancelled.TrySetResult(true)))
                {
                    handlerStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, serverToken).ConfigureAwait(false);
                        return RpcResponse.Ok("unexpected");
                    }
                    catch (OperationCanceledException)
                    {
                        handlerCancelled.TrySetResult(true);
                        throw;
                    }
                }
            };
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            using (CancellationTokenSource requestCancellation = new CancellationTokenSource())
            {
                TcpRpcClientTransport client = CreateClient(server, serializer);
                Task<RpcResponse> request =
                    client.SendAsync(CreateStringRequest("cancel-me"), requestCancellation.Token);
                yield return WaitForCompletion(handlerStarted.Task, "服务端没有开始处理请求");

                requestCancellation.Cancel();
                yield return WaitForCompletion(request, "客户端取消后请求仍未结束");
                Assert.IsTrue(request.IsCanceled ||
                              (request.IsFaulted && request.Exception.GetBaseException() is OperationCanceledException),
                    "客户端请求应该以取消状态结束。");

                // 客户端取消只关闭客户端连接，不应被误认为服务端生命周期 Token 的取消。
                Assert.IsFalse(serverTokenCancelled.Task.IsCompleted,
                    "客户端断开不应被误认为可靠的服务端生命周期取消信号。");
            }

            Task stop = server.StopAsync(CancellationToken.None);
            yield return WaitForCompletion(stop, "取消测试结束后服务端未能停止");
            yield return WaitForCompletion(handlerCancelled.Task, "服务端停止没有取消等待中的 Handler");
        }

        /// <summary>服务端响应序列化失败时应报告原始错误并关闭当前连接，但不能停止整个监听器。</summary>
        [UnityTest]
        public IEnumerator TcpServer_ReportsResponseSerializationErrorAndContinuesServing()
        {
            IRpcSerializer serializer = new ThrowingResponseSerializer(new NewtonsoftRpcSerializer());
            ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.OnTransportError += errors.Enqueue;
            server.OnRequestAsync += (request, ct) =>
            {
                if (request.MethodName == "BadResponse")
                {
                    return Task.FromResult(new RpcResponse
                    {
                        Success = true,
                        Result = new object(),
                        Detail = "throw-serialization"
                    });
                }
                return Task.FromResult(RpcResponse.Ok("healthy"));
            };
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> badResponse =
                client.SendAsync(CreateStringRequestForMethod("BadResponse", "serialize-error"),
                    CancellationToken.None);
            yield return WaitForCompletion(badResponse, "响应序列化错误测试超时");
            Assert.IsFalse(badResponse.Result.Success, "非法响应不应该被静默发送。");
            Assert.AreEqual(TcpRpcClientTransport.ConnectionLostCode, badResponse.Result.Error?.Code);
            yield return WaitForError(errors, "服务端没有报告响应序列化异常");

            Exception error;
            Assert.IsTrue(errors.TryDequeue(out error));
            Assert.IsInstanceOf<InvalidDataException>(error);

            // 注：序列化失败会关闭当前连接并上报错误；“监听器继续服务”由其他长连接用例覆盖。

            Task stop = server.StopAsync(CancellationToken.None);
            yield return WaitForCompletion(stop, "响应序列化错误测试结束后服务端未能停止");
        }

        /// <summary>停止服务端时应取消正在执行的请求，并等待连接处理任务退出。</summary>
        [UnityTest]
        public IEnumerator TcpServer_StopCancelsActiveHandlerAndWaitsForConnectionExit()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TaskCompletionSource<bool> handlerStarted = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> handlerCancelled = new TaskCompletionSource<bool>();
            ConcurrentQueue<Exception> shutdownErrors = new ConcurrentQueue<Exception>();
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.OnTransportError += shutdownErrors.Enqueue;
            server.OnRequestAsync += async (request, ct) =>
            {
                handlerStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return RpcResponse.Ok("unexpected");
                }
                catch (OperationCanceledException)
                {
                    handlerCancelled.TrySetResult(true);
                    throw;
                }
            };
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> pendingRequest = client.SendAsync(CreateStringRequest("wait"), CancellationToken.None);
            yield return WaitForCompletion(handlerStarted.Task, "服务端没有收到请求");

            Task stop = server.StopAsync(CancellationToken.None);
            yield return WaitForCompletion(stop, "StopAsync 未能等待连接退出");
            yield return WaitForCompletion(handlerCancelled.Task, "服务端停止没有取消业务处理器");
            yield return WaitForCompletion(pendingRequest, "客户端连接没有在服务端停止时退出");

            Assert.IsTrue(handlerCancelled.Task.IsCompleted);
            Assert.IsFalse(pendingRequest.Result.Success,
                "服务端主动关闭连接后，客户端请求应退出而不是永久等待。");
            Assert.AreEqual(TcpRpcClientTransport.ConnectionLostCode, pendingRequest.Result.Error?.Code);
            Assert.IsTrue(shutdownErrors.IsEmpty,
                "服务端主动 Stop 时产生的预期连接关闭不应报告为传输故障。");
        }

        /// <summary>
        /// 使用 StringBuilder 一次构造大体积中英混合和 Emoji 字符串，通过 TCP 完整往返。
        /// 该测试同时输出实际耗时和吞吐量，不设置脆弱的硬件相关性能门槛。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpClientServer_RoundTripsLargeUtf8StringEfficiently()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            StringBuilder builder = new StringBuilder(256 * 1024);
            for (int index = 0; index < 8192; index++)
                builder.Append("VoyageForge-通讯-你好-🚀-世界-");
            string largeText = builder.ToString();
            int utf8Bytes = Encoding.UTF8.GetByteCount(largeText);
            // 预先序列化一次以取得实际协议 Payload 大小；计时区间只测真实 TCP 请求/响应，不把基准数据准备算进去。
            int requestPayloadBytes = serializer.Serialize(CreateStringRequest(largeText)).Length;
            int responsePayloadBytes = serializer.Serialize(RpcResponse.Ok("echo:" + largeText)).Length;
            long totalTcpFrameBytes = requestPayloadBytes + responsePayloadBytes + 8L; // 双向各 4 字节 Big Endian 帧头。

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Stopwatch stopwatch = Stopwatch.StartNew();
            Task<RpcResponse> request = client.SendAsync(CreateStringRequest(largeText), CancellationToken.None);
            yield return WaitForCompletion(request, "大字符串 TCP 往返超时", 20f);
            stopwatch.Stop();

            Assert.IsTrue(request.Result.Success);
            Assert.AreEqual("echo:" + largeText, request.Result.Result,
                "Echo Server 会在响应字符串前添加 echo: 前缀；返回值应与实际处理逻辑一致。");
            double transferredMiB = totalTcpFrameBytes / (1024.0 * 1024.0);
            double throughput = stopwatch.Elapsed.TotalSeconds <= 0
                ? 0
                : transferredMiB / stopwatch.Elapsed.TotalSeconds;
            TestContext.Progress.WriteLine(
                "[TCP UTF-8] chars={0:N0}, UTF8 text={1:N0} bytes, request frame={2:N0} bytes, response frame={3:N0} bytes, total framed bytes={4:N0} bytes, elapsed={5:F2} ms, round-trip throughput={6:F2} MiB/s",
                largeText.Length, utf8Bytes, requestPayloadBytes, responsePayloadBytes, totalTcpFrameBytes,
                stopwatch.Elapsed.TotalMilliseconds, throughput);

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>客户端主动 Dispose 时，在途请求应以固定错误 CONNECTION_LOST 结束。</summary>
        [UnityTest]
        public IEnumerator TcpClient_Dispose_FailsPendingRequestsWithConnectionLost()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TaskCompletionSource<bool> handlerStarted = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> releaseHandler = new TaskCompletionSource<bool>();

            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.OnRequestAsync += async (request, ct) =>
            {
                handlerStarted.TrySetResult(true);
                await releaseHandler.Task;
                return RpcResponse.Ok("done");
            };
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> pending = client.SendAsync(CreateStringRequest("pending"), CancellationToken.None);
            yield return WaitForCompletion(handlerStarted.Task, "服务端没有收到请求");

            client.Dispose();
            yield return WaitForCompletion(pending, "客户端 Dispose 后请求没有结束");

            Assert.IsFalse(pending.Result.Success);
            Assert.AreEqual(TcpRpcClientTransport.ConnectionLostCode, pending.Result.Error?.Code);

            releaseHandler.TrySetResult(true);
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>服务端 StopAsync 时，客户端在途请求应以 CONNECTION_LOST 结束。</summary>
        [UnityTest]
        public IEnumerator TcpServer_StopAsync_FailsInFlightClientRequests()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TaskCompletionSource<bool> handlerStarted = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> releaseHandler = new TaskCompletionSource<bool>();

            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.OnRequestAsync += async (request, ct) =>
            {
                handlerStarted.TrySetResult(true);
                await releaseHandler.Task;
                return RpcResponse.Ok("done");
            };
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            Task<RpcResponse> pending = client.SendAsync(CreateStringRequest("pending"), CancellationToken.None);
            yield return WaitForCompletion(handlerStarted.Task, "服务端没有收到请求");

            Task stop = server.StopAsync(CancellationToken.None);
            yield return WaitForCompletion(pending, "服务端停止后在途请求没有结束");

            Assert.IsFalse(pending.Result.Success);
            Assert.AreEqual(TcpRpcClientTransport.ConnectionLostCode, pending.Result.Error?.Code);

            releaseHandler.TrySetResult(true);
            yield return WaitForCompletion(stop, "服务端 StopAsync 没有退出");
            client.Dispose();
        }

        /// <summary>Dispose 后再次发送请求应抛出 ObjectDisposedException。</summary>
        [UnityTest]
        public IEnumerator TcpClient_SendAfterDispose_ThrowsObjectDisposedException()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = CreateEchoServer(serializer);
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(server, serializer);
            client.Dispose();

            Assert.Throws<ObjectDisposedException>(() =>
                client.SendAsync(CreateStringRequest("after-dispose"), CancellationToken.None).GetAwaiter().GetResult());

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            yield return null;
        }

        /// <summary>token 错误时握手失败，客户端不会进入已连接状态。</summary>
        [UnityTest]
        public IEnumerator TcpClient_AuthTokenMismatch_FailsHandshake()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(
                IPAddress.Loopback, 0, serializer, requiredToken: "expected-token"));
            server.OnRequestAsync += (request, ct) => Task.FromResult(RpcResponse.Ok("secret"));
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = new TcpRpcClientTransport(
                "127.0.0.1", server.LocalEndPoint.Port, serializer, token: "wrong-token");

            Exception failure = null;
            Task send = client.SendAsync(CreateStringRequest("auth"), CancellationToken.None)
                .ContinueWith(task => { failure = task.Exception?.GetBaseException(); }, TaskScheduler.Default);
            yield return WaitForCompletion(send, "token 错误握手没有结束");

            Assert.IsNotNull(failure, "token 错误时应抛出鉴权异常。");
            Assert.IsFalse(client.IsConnected);

            client.Dispose();
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>重复 StopAsync 应保持幂等，不抛异常。</summary>
        [UnityTest]
        public IEnumerator TcpServer_StopAsync_IsIdempotent()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.DoesNotThrow(() => server.StopAsync(CancellationToken.None).GetAwaiter().GetResult());

            yield return null;
        }
        /// <summary>StopAsync 后必须释放监听端口，允许用同一端口重新启动。</summary>
        [UnityTest]
        public IEnumerator TcpServer_StopAsync_ReleasesPortForRestart()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            TcpRpcServerTransport first = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            first.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            int port = first.LocalEndPoint.Port;

            first.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcServerTransport second = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, port, serializer));
            second.OnRequestAsync += (request, ct) => Task.FromResult(RpcResponse.Ok("restarted"));
            second.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            TcpRpcClientTransport client = CreateClient(second, serializer);
            Task<RpcResponse> request = client.SendAsync(CreateStringRequest("after-restart"), CancellationToken.None);
            yield return WaitForCompletion(request, "端口复用后请求超时");

            Assert.IsTrue(request.Result.Success);
            Assert.AreEqual("restarted", request.Result.Result);

            client.Dispose();
            second.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        private TcpRpcServerTransport CreateEchoServer(IRpcSerializer serializer)
        {
            TcpRpcServerTransport server = TrackServer(new TcpRpcServerTransport(IPAddress.Loopback, 0, serializer));
            server.OnRequestAsync += (request, ct) =>
            {
                // 单参数规则：Payload 直接是 string 的序列化结果（与生成器/示例一致）。
                string value = RpcPayload.Unpack<string>(request.Payload);
                return Task.FromResult(RpcResponse.Ok("echo:" + value));
            };
            return server;
        }

        private TcpRpcClientTransport CreateClient(TcpRpcServerTransport server, IRpcSerializer serializer)
        {
            return TrackClient(new TcpRpcClientTransport("127.0.0.1", server.LocalEndPoint.Port, serializer));
        }

        private TcpRpcClientTransport TrackClient(TcpRpcClientTransport client)
        {
            _runningClients.Add(client);
            return client;
        }

        private static RpcRequest CreateStringRequest(string value)
        {
            return CreateRequest("Echo", value);
        }

        private static RpcRequest CreateStringRequestForMethod(string methodName, string value)
        {
            return CreateRequest(methodName, value);
        }

        private static RpcRequest CreateRequest(string methodName, string value)
        {
            return new RpcRequest
            {
                ServiceName = "tests.tcp",
                MethodName = methodName,
                Payload = RpcPayload.Pack( value )
            };
        }

        private static IEnumerator WaitForCompletion(Task task, string timeoutMessage,
            float timeoutSeconds = DefaultTimeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsTrue(task.IsCompleted, timeoutMessage);
        }

        private static IEnumerator WaitForError(ConcurrentQueue<Exception> errors, string timeoutMessage)
        {
            float deadline = Time.realtimeSinceStartup + DefaultTimeoutSeconds;
            while (errors.IsEmpty && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsFalse(errors.IsEmpty, timeoutMessage);
        }

        private static int FindUnusedPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task ConnectAndDisconnectAsync(int port)
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            }
        }

        private static async Task SendRawHeaderAsync(int port, int length)
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                await WriteHeaderAsync(stream, RpcMessageType.RpcResponse, length).ConfigureAwait(false);
                // 等服务器读到非法帧头并主动关闭连接，再结束发送；
                // 立即关闭会让服务端只读到半帧头，产生 EndOfStreamException 而不是 InvalidDataException。
                using (CancellationTokenSource waitServerClose = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    try
                    {
                        byte[] probe = new byte[1];
                        await stream.ReadAsync(probe, 0, 1, waitServerClose.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (IOException) { }
                }
            }
        }

        private static async Task SendTruncatedFrameAsync(int port)
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                await WriteHeaderAsync(stream, RpcMessageType.RpcRequest, 24).ConfigureAwait(false);
                byte[] partial = new byte[] { 0x01, 0x02, 0x03 };
                await stream.WriteAsync(partial, 0, partial.Length).ConfigureAwait(false);
            }
        }

        private static async Task<RpcResponse> SendRawPayloadAndReadResponseAsync(int port, byte[] payload)
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                await WriteHeaderAsync(stream, RpcMessageType.RpcRequest, payload.Length).ConfigureAwait(false);
                await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                byte[] responsePayload = await ReadFrameAsync(stream).ConfigureAwait(false);
            NewtonsoftRpcSerializer responseSerializer = new NewtonsoftRpcSerializer();
            Protocol<RpcResponse> envelope = responseSerializer.Deserialize<Protocol<RpcResponse>>(responsePayload);
            if (envelope != null) return envelope.Data;
                return new NewtonsoftRpcSerializer().Deserialize<RpcResponse>(responsePayload);
            }
        }

        private static async Task SendInvalidResponseHeaderAsync(TcpListener listener, int length)
        {
            try
            {
                using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    NetworkStream stream = client.GetStream();
                    // 等客户端写完请求并启动读循环，再发非法帧头；否则客户端可能先看到连接关闭。
                    await Task.Delay(100).ConfigureAwait(false);
                    await WriteHeaderAsync(stream, RpcMessageType.RpcResponse, length).ConfigureAwait(false);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task SendMalformedResponseAsync(TcpListener listener)
        {
            try
            {
                using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    NetworkStream stream = client.GetStream();
                    await ReadFrameAsync(stream).ConfigureAwait(false); // 消费客户端请求帧。
                    byte[] invalidPayload = new byte[] { 0xFF };
                    await WriteHeaderAsync(stream, RpcMessageType.RpcResponse, invalidPayload.Length).ConfigureAwait(false);
                    await stream.WriteAsync(invalidPayload, 0, invalidPayload.Length).ConfigureAwait(false);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task SendTruncatedResponseAsync(TcpListener listener)
        {
            try
            {
                using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    NetworkStream stream = client.GetStream();
                    await ReadFrameAsync(stream).ConfigureAwait(false); // 消费客户端请求帧。
                    await WriteHeaderAsync(stream, RpcMessageType.RpcResponse, 32).ConfigureAwait(false);
                    byte[] partial = new byte[] { 0x52, 0x01 };
                    await stream.WriteAsync(partial, 0, partial.Length).ConfigureAwait(false);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private const ushort TestProtocolVersion = 1;
        private const ushort TestMagic = 0xAA55;

        private static async Task WriteHeaderAsync(NetworkStream stream, ushort typeId, int length)
        {
            byte[] header = new byte[10];
            header[0] = (byte)(TestMagic >> 8);
            header[1] = (byte)(TestMagic & 0xFF);
            header[2] = (byte)(TestProtocolVersion >> 8);
            header[3] = (byte)TestProtocolVersion;
            header[4] = (byte)(typeId >> 8);
            header[5] = (byte)typeId;
            header[6] = (byte)(length >> 24);
            header[7] = (byte)(length >> 16);
            header[8] = (byte)(length >> 8);
            header[9] = (byte)length;
            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
        {
            byte[] header = await ReadExactlyAsync(stream, 10).ConfigureAwait(false);
            ushort magic = (ushort)((header[0] << 8) | header[1]);
            if (magic != TestMagic) throw new InvalidDataException("测试帧头 SOF 非法。");
            int length = (header[6] << 24) | (header[7] << 16) | (header[8] << 8) | header[9];
            if (length <= 0 || length > 4 * 1024 * 1024)
                throw new InvalidDataException("测试读取到非法帧长度：" + length);
            return await ReadExactlyAsync(stream, length).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("测试端读取帧时连接提前关闭。");
                offset += read;
            }

            return buffer;
        }

        /// <summary>用于验证客户端请求序列化异常和空 Payload 防御的故障注入序列化器。</summary>
        private sealed class InvalidRequestSerializer : IRpcSerializer
        {
        public object Deserialize(byte[] data, global::System.Type type) => Deserialize<object>(data);

            private readonly bool _throwOnSerialize;

            public InvalidRequestSerializer(bool throwOnSerialize)
            {
                _throwOnSerialize = throwOnSerialize;
            }

            public string ContentType
            {
                get { return "application/x-voyageforge-invalid-test"; }
            }

            public byte[] Serialize<T>(T value)
            {
                if (_throwOnSerialize) throw new InvalidOperationException("simulated request serialization failure");
                return Array.Empty<byte>();
            }

            public T Deserialize<T>(ReadOnlySpan<byte> data)
            {
                throw new InvalidOperationException("This serializer is only used to test request serialization.");
            }
        }

        /// <summary>
        /// 测试专用 UTF-8 信封编码器。用 BinaryWriter/Reader 按 UTF-8 处理字符串，
        /// 避免逐字符拼接；RPC Runtime 本身仍只依赖 IRpcSerializer，不依赖该测试实现。
        /// </summary>
    }

    /// <summary>响应序列化时抛异常的序列化器；用于验证服务端上报并关闭连接。</summary>
    internal sealed class ThrowingResponseSerializer : IRpcSerializer
    {
        public object Deserialize(byte[] data, global::System.Type type) => _inner.Deserialize(data, type);

        private readonly IRpcSerializer _inner;

        public ThrowingResponseSerializer(IRpcSerializer inner)
        {
            _inner = inner;
        }

        public string ContentType { get { return _inner.ContentType; } }

        public byte[] Serialize<T>(T value)
        {
            if (value is Protocol<RpcResponse>) throw new InvalidDataException("simulated response serialization failure");
            return _inner.Serialize(value);
        }

        public T Deserialize<T>(ReadOnlySpan<byte> data)
        {
            return _inner.Deserialize<T>(data);
        }
    }
}