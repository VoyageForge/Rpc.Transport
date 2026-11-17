using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>
    /// Edit Mode 下验证传输契约的最小语义：客户端发送、服务端回调、生命周期以及 Datagram 组合能力。
    /// 使用内存实现避免单元测试依赖本机端口或网络权限。
    /// </summary>
    public sealed class TransportContractsEditModeTests
    {
        /// <summary>客户端发送必须把同一个请求和 CancellationToken 交给服务端请求处理器。</summary>
        [Test]
        public void DatagramTransport_ForwardsRequestAndCancellationToken()
        {
            InMemoryDatagramTransport transport = new InMemoryDatagramTransport();
            RpcRequest expectedRequest = new RpcRequest
            {
                ServiceName = "test.echo",
                MethodName = "Echo",
                Parameters = new object[] { "hello" }
            };
            CancellationTokenSource lifetime = new CancellationTokenSource();
            RpcRequest receivedRequest = null;
            CancellationToken receivedToken = default(CancellationToken);

            transport.OnRequestAsync += (request, token) =>
            {
                receivedRequest = request;
                receivedToken = token;
                return Task.FromResult(RpcResponse.Ok("world"));
            };

            // Unity 2022.3 默认使用 Test Framework 1.1.33，不支持 async Task 测试入口。
            // 此测试替身只返回已完成的 Task，因此同步取结果不会等待 Unity PlayerLoop。
            transport.StartAsync(lifetime.Token).GetAwaiter().GetResult();
            RpcResponse response = ((IRpcClientTransport)transport)
                .SendAsync(expectedRequest, lifetime.Token).GetAwaiter().GetResult();

            Assert.AreSame(expectedRequest, receivedRequest);
            Assert.AreEqual(lifetime.Token, receivedToken);
            Assert.IsTrue(response.Success);
            Assert.AreEqual("world", response.Result);
            Assert.IsTrue(transport.IsStarted);

            transport.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsFalse(transport.IsStarted);
            lifetime.Dispose();
        }


        /// <summary>独立客户端接口应原样传递请求和调用方取消令牌，并返回底层响应。</summary>
        [Test]
        public void ClientTransport_ForwardsRequestAndCancellationToken()
        {
            InMemoryClientTransport transport = new InMemoryClientTransport();
            RpcRequest request = new RpcRequest { ServiceName = "test.client", MethodName = "Call" };
            CancellationTokenSource cancellation = new CancellationTokenSource();

            RpcResponse response = transport.SendAsync(request, cancellation.Token).GetAwaiter().GetResult();

            Assert.AreSame(request, transport.LastRequest);
            Assert.AreEqual(cancellation.Token, transport.LastToken);
            Assert.IsTrue(response.Success);
            Assert.AreEqual("client-ok", response.Result);
            cancellation.Dispose();
        }

        /// <summary>独立服务端接口应在启动后接收请求，并把服务端生命周期 Token 交给处理器。</summary>
        [Test]
        public void ServerTransport_ForwardsRequestAndLifecycleToken()
        {
            InMemoryServerTransport transport = new InMemoryServerTransport();
            CancellationTokenSource lifetime = new CancellationTokenSource();
            RpcRequest request = new RpcRequest { ServiceName = "test.server", MethodName = "Handle" };
            RpcRequest received = null;
            CancellationToken receivedToken = default(CancellationToken);
            transport.OnRequestAsync += (value, token) =>
            {
                received = value;
                receivedToken = token;
                return Task.FromResult(RpcResponse.Ok("server-ok"));
            };

            transport.StartAsync(lifetime.Token).GetAwaiter().GetResult();
            RpcResponse response = transport.DispatchAsync(request).GetAwaiter().GetResult();

            Assert.AreSame(request, received);
            Assert.AreEqual(lifetime.Token, receivedToken);
            Assert.IsTrue(response.Success);
            Assert.AreEqual("server-ok", response.Result);
            transport.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            lifetime.Dispose();
        }

        /// <summary>组合接口必须能分别作为 Client 和 Server 使用。</summary>
        [Test]
        public void DatagramTransport_ExposesBothCapabilities()
        {
            IRpcDatagramTransport datagram = new InMemoryDatagramTransport();
            Assert.IsInstanceOf<IRpcClientTransport>(datagram);
            Assert.IsInstanceOf<IRpcServerTransport>(datagram);
        }


        /// <summary>序列化接口只负责字节转换，不依赖具体 TCP 或 RPC 执行器。</summary>
        [Test]
        public void Serializer_ImplementsByteConversionContract()
        {
            IRpcSerializer serializer = new Utf8TextSerializer();
            byte[] bytes = serializer.Serialize("transport");
            string text = serializer.Deserialize<string>(bytes);

            Assert.AreEqual("text/plain; charset=utf-8", serializer.ContentType);
            Assert.AreEqual("transport", text);
        }

        /// <summary>旧版 IRpcTransport 的事件签名仍可由既有 Host 实现。</summary>
        [Test]
        public void LegacyServerTransport_InvokesCompatibilityEvent()
        {
            InMemoryLegacyServerTransport transport = new InMemoryLegacyServerTransport();
            transport.OnRequest += request => Task.FromResult(RpcResponse.Ok(request.MethodName));
            transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            RpcResponse response = transport.DispatchAsync(new RpcRequest
            {
                ServiceName = "legacy",
                MethodName = "Ping"
            }).GetAwaiter().GetResult();

            Assert.IsTrue(response.Success);
            Assert.AreEqual("Ping", response.Result);
            transport.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>请求/响应模型的工厂方法应保留成功结果和结构化错误。</summary>
        [Test]
        public void RpcResponse_FactoryMethodsCreateExpectedEnvelope()
        {
            RpcResponse success = RpcResponse.Ok(42);
            RpcResponse failure = RpcResponse.Fail("TEST_ERROR", "测试错误");

            Assert.IsTrue(success.Success);
            Assert.AreEqual(42, success.Result);
            Assert.IsNull(success.Error);

            Assert.IsFalse(failure.Success);
            Assert.AreEqual("TEST_ERROR", failure.Error.Code);
            Assert.AreEqual("测试错误", failure.Error.Message);
        }


        /// <summary>最小 UTF-8 文本序列化器，仅用于验证 IRpcSerializer 的通用方法契约。</summary>
        private sealed class Utf8TextSerializer : IRpcSerializer
        {
            public string ContentType
            {
                get { return "text/plain; charset=utf-8"; }
            }

            public byte[] Serialize<T>(T value)
            {
                return System.Text.Encoding.UTF8.GetBytes(Convert.ToString(value,
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            public T Deserialize<T>(ReadOnlySpan<byte> data)
            {
                object value = System.Text.Encoding.UTF8.GetString(data.ToArray());
                return (T)value;
            }
        }

        /// <summary>只实现客户端能力的测试替身，记录请求与 Token 以便断言契约行为。</summary>
        private sealed class InMemoryClientTransport : IRpcClientTransport
        {
            public RpcRequest LastRequest { get; private set; }
            public CancellationToken LastToken { get; private set; }

            public Task<RpcResponse> SendAsync(RpcRequest request, CancellationToken ct = default(CancellationToken))
            {
                LastRequest = request;
                LastToken = ct;
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(RpcResponse.Ok("client-ok"));
            }
        }

        /// <summary>只实现服务端能力的测试替身，用于验证生命周期 Token 和请求回调。</summary>
        private sealed class InMemoryServerTransport : IRpcServerTransport
        {
            private CancellationToken _lifetimeToken;
            public event Func<RpcRequest, CancellationToken, Task<RpcResponse>> OnRequestAsync;

            public Task StartAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                _lifetimeToken = ct;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                _lifetimeToken = default(CancellationToken);
                return Task.CompletedTask;
            }

            public Task<RpcResponse> DispatchAsync(RpcRequest request)
            {
                Func<RpcRequest, CancellationToken, Task<RpcResponse>> handler = OnRequestAsync;
                if (handler == null) throw new InvalidOperationException("未注册服务端请求处理器。");
                return handler(request, _lifetimeToken);
            }
        }

        /// <summary>旧接口的内存实现，用来验证兼容事件仍能正常订阅和调用。</summary>
        private sealed class InMemoryLegacyServerTransport : IRpcTransport
        {
            public event Func<RpcRequest, Task<RpcResponse>> OnRequest;

            public Task StartAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<RpcResponse> DispatchAsync(RpcRequest request)
            {
                Func<RpcRequest, Task<RpcResponse>> handler = OnRequest;
                return handler == null
                    ? Task.FromResult(RpcResponse.Fail("HANDLER_NOT_REGISTERED", "未注册请求处理器。"))
                    : handler(request);
            }
        }

        /// <summary>仅用于测试的内存数据报传输，完整实现组合接口而不依赖 Socket。</summary>
        private sealed class InMemoryDatagramTransport : IRpcDatagramTransport
        {
            /// <summary>当前是否已启动。</summary>
            public bool IsStarted { get; private set; }

            /// <summary>收到请求时由服务端 Host 订阅。</summary>
            public event Func<RpcRequest, CancellationToken, Task<RpcResponse>> OnRequestAsync;

            /// <summary>模拟启动监听。</summary>
            public Task StartAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                IsStarted = true;
                return Task.CompletedTask;
            }

            /// <summary>模拟停止监听。</summary>
            public Task StopAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                IsStarted = false;
                return Task.CompletedTask;
            }

            /// <summary>模拟发送：把请求路由到服务端事件，返回处理器生成的响应。</summary>
            public Task<RpcResponse> SendAsync(RpcRequest request, CancellationToken ct = default(CancellationToken))
            {
                if (!IsStarted) throw new InvalidOperationException("传输尚未启动。");
                ct.ThrowIfCancellationRequested();
                Func<RpcRequest, CancellationToken, Task<RpcResponse>> handler = OnRequestAsync;
                if (handler == null) return Task.FromResult(RpcResponse.Fail("HANDLER_NOT_REGISTERED", "未注册请求处理器。"));
                return handler(request, ct);
            }
        }
    }
}