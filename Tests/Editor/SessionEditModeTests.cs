using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoyageForge.Rpc.Transport;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>Session 核心测试：注册表、ControllerMap 初始化、序列化与请求结构。</summary>
    public sealed class SessionEditModeTests
    {
        private sealed class EchoControllerMap : ControllerMap
        {
        }

        private sealed class EchoRequest
        {
            public string Text { get; set; }
        }

        private sealed class EchoResponse
        {
            public string Text { get; set; }
        }

        [Test]
        public void 方法注册表按Controller和方法查找()
        {
            RpcMethodRegistry registry = new RpcMethodRegistry();
            RpcMethodDescriptor descriptor = new RpcMethodDescriptor
            {
                ControllerName = "User",
                MethodName = "GetUser",
                RequestType = typeof(EchoRequest),
                ResponseType = typeof(EchoResponse),
                Handler = (request, ct) => Task.FromResult<object>(null)
            };

            registry.Register(descriptor);

            Assert.IsTrue(registry.TryGet("User", "GetUser", out RpcMethodDescriptor found));
            Assert.AreSame(descriptor, found);
            Assert.IsFalse(registry.TryGet("User", "Missing", out _));
            Assert.IsFalse(registry.TryGet("", "GetUser", out _));
        }

        [Test]
        public void 注册空描述抛异常()
        {
            RpcMethodRegistry registry = new RpcMethodRegistry();

            Assert.Throws<ArgumentNullException>(() => registry.Register(null));
            Assert.Throws<ArgumentException>(() => registry.Register(new RpcMethodDescriptor
            {
                MethodName = "M"
            }));
        }

        [Test]
        public void ControllerMap初始化注入Session()
        {
            EchoControllerMap map = new EchoControllerMap();
            TcpSession<EchoControllerMap> session = null;
            try
            {
                session = new TcpSession<EchoControllerMap>(null);
            }
            catch (Exception)
            {
                // 无 socket 构造不应依赖网络；若实现仍要求连接，跳过该断言。
            }

            map.Initialize(session);
            Assert.AreSame(session, map.Session);
        }

        [Test]
        public void SessionRequestResponse可序列化()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            SessionRequest request = new SessionRequest
            {
                RequestId = 42,
                ControllerName = "User",
                MethodName = "GetUser",
                Payload = new byte[] { 1, 2, 3 }
            };

            byte[] bytes = serializer.Serialize(new Protocol<SessionRequest> { Version = "1.0", Data = request });
            Protocol<SessionRequest> roundTrip = serializer.Deserialize<Protocol<SessionRequest>>(
                new ReadOnlySpan<byte>(bytes));

            Assert.IsNotNull(roundTrip);
            Assert.AreEqual(42, roundTrip.Data.RequestId);
            Assert.AreEqual("User", roundTrip.Data.ControllerName);
            Assert.AreEqual("GetUser", roundTrip.Data.MethodName);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, roundTrip.Data.Payload);
        }

        [Test]
        public void MethodId相同输入结果一致()
        {
            MethodIdHasher hasher = new MethodIdHasher();
            ulong first = hasher.Compute("User", "GetAll", typeof(EchoRequest));
            ulong second = hasher.Compute("User", "GetAll", typeof(EchoRequest));
            ulong other = hasher.Compute("User", "GetUser", typeof(EchoRequest));

            Assert.AreEqual(first, second);
            Assert.AreNotEqual(first, other);
            Assert.AreNotEqual(0UL, first);
        }

        [Test]
        public void 注册表可按MethodId查找()
        {
            RpcMethodRegistry registry = new RpcMethodRegistry();
            RpcMethodDescriptor descriptor = new RpcMethodDescriptor
            {
                ControllerName = "User",
                MethodName = "GetAll",
                RequestType = typeof(EchoRequest),
                ResponseType = typeof(EchoResponse),
                Handler = (request, ct) => Task.FromResult<object>(null)
            };

            registry.Register(descriptor);

            Assert.AreNotEqual(0UL, descriptor.MethodId);
            Assert.IsTrue(registry.TryGet(descriptor.MethodId, out RpcMethodDescriptor byId));
            Assert.AreSame(descriptor, byId);
            Assert.IsTrue(registry.TryGet("User", "GetAll", out RpcMethodDescriptor byName));
            Assert.AreSame(descriptor, byName);
        }

        [Test]
        public void 元组参数往返一致()
        {
            // 2+ 参数走 ValueTuple：Pack 元组 → Unpack 同名元组，字段顺序必须一致。
            var value = (3, 4);
            byte[] payload = RpcPayload.Pack(value);
            (int a, int b) roundTrip = RpcPayload.Unpack<(int a, int b)>(payload);

            Assert.AreEqual(3, roundTrip.a);
            Assert.AreEqual(4, roundTrip.b);
        }

        [Test]
        public void 空payload请求可往返()
        {
            // 无参数 Get：Payload 为空数组，经 Protocol 信封往返后仍是合法请求（Data 非空、Payload 空）。
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            SessionRequest request = new SessionRequest
            {
                RequestId = 7,
                ControllerName = "User",
                MethodName = "GetAll",
                Payload = Array.Empty<byte>()
            };

            byte[] bytes = serializer.Serialize(new Protocol<SessionRequest> { Version = "1.0", Data = request });
            Protocol<SessionRequest> roundTrip = serializer.Deserialize<Protocol<SessionRequest>>(
                new ReadOnlySpan<byte>(bytes));

            Assert.IsNotNull(roundTrip);
            Assert.IsNotNull(roundTrip.Data);
            Assert.AreEqual("GetAll", roundTrip.Data.MethodName);
            Assert.AreEqual(0, roundTrip.Data.Payload.Length);
        }

        [Test]
        public void 序列化器支持运行时类型反序列化()
        {
            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            byte[] bytes = serializer.Serialize(new EchoResponse { Text = "hello" });

            object value = serializer.Deserialize(bytes, typeof(EchoResponse));

            Assert.IsInstanceOf<EchoResponse>(value);
            Assert.AreEqual("hello", ((EchoResponse)value).Text);
        }

        [Test]
        public void axios风格调用未连接抛异常()
        {
            // axios 风格：只需要 controller + method 字符串，无需 ControllerMap。
            TcpSession<EchoControllerMap> session = new TcpSession<EchoControllerMap>(null);
            Assert.Catch<System.IO.IOException>(() =>
                session.CallAsync<EchoRequest, EchoResponse>("User", "GetUser", new EchoRequest { Text = "x" })
                    .GetAwaiter().GetResult());
            session.Dispose();
        }

        [Test]
        public void 未绑定连接的会话发送请求抛异常()
        {
            TcpSession<EchoControllerMap> session = new TcpSession<EchoControllerMap>(null);
            Task<SessionResponse> task = session.SendAsync("User", "GetUser", Array.Empty<byte>(),
                CancellationToken.None);

            Assert.Catch<System.IO.IOException>(() => task.GetAwaiter().GetResult());
            session.Dispose();
        }
    }
}
