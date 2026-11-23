using System;
using NUnit.Framework;
using MemoryPack;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class SessionEditModeTests
    {
        private sealed class EmptyControllerMap : ControllerMap { }

        [Test]
        public void Registry按MethodId查找并拒绝重复注册()
        {
            RpcControllerRegistry registry = new RpcControllerRegistry();
            RpcMethodDescriptor descriptor = new RpcMethodDescriptor
            {
                MethodId = 123UL,
                ControllerName = "User",
                MethodName = "GetUser",
                Signature = "(global::System.String)",
                Handler = (payload, ct) => System.Threading.Tasks.Task.FromResult(payload)
            };
            registry.Register(descriptor);
            Assert.IsTrue(registry.TryGet(123UL, out RpcMethodDescriptor found));
            Assert.AreSame(descriptor, found);
            Assert.Throws<InvalidOperationException>(() => registry.Register(new RpcMethodDescriptor
            {
                MethodId = 123UL, ControllerName = "Other", MethodName = "Run",
                Handler = (payload, ct) => System.Threading.Tasks.Task.FromResult(payload)
            }));
        }

        [Test]
        public void RegistrySeal后不可继续注册()
        {
            RpcControllerRegistry registry = new RpcControllerRegistry();
            registry.Seal();
            Assert.Throws<InvalidOperationException>(() => registry.Register(new RpcMethodDescriptor
            {
                MethodId = 1UL, ControllerName = "A", MethodName = "B",
                Handler = (payload, ct) => System.Threading.Tasks.Task.FromResult(payload)
            }));
        }

        [Test]
        public void MethodId对参数顺序和类型敏感()
        {
            ulong first = MethodIdHasher.Compute("User", "Update", new[] { "global::System.String", "global::System.Int32" });
            ulong same = MethodIdHasher.Compute("User", "Update", new[] { "global::System.String", "global::System.Int32" });
            ulong reversed = MethodIdHasher.Compute("User", "Update", new[] { "global::System.Int32", "global::System.String" });
            ulong otherType = MethodIdHasher.Compute("User", "Update", new[] { "global::System.String", "global::System.Int64" });
            Assert.AreEqual(first, same);
            Assert.AreNotEqual(first, reversed);
            Assert.AreNotEqual(first, otherType);
            Assert.AreNotEqual(0UL, first);
        }

        [Test]
        public void Session控制包只包含MethodId不包含名称()
        {
            SessionRequest request = new SessionRequest { RequestId = 42, MethodId = 0x1234UL, Payload = new byte[] { 1, 2, 3 } };
            SessionRequest copy = MemoryPackSerializer.Deserialize<SessionRequest>(MemoryPackSerializer.Serialize(request));
            Assert.AreEqual(42, copy.RequestId);
            Assert.AreEqual(0x1234UL, copy.MethodId);
            CollectionAssert.AreEqual(request.Payload, copy.Payload);
        }

        [Test]
        public void ControllerMap由Session初始化且不依赖反射()
        {
            RpcControllerRegistry registry = new RpcControllerRegistry();
            TcpSession<EmptyControllerMap> session = new TcpSession<EmptyControllerMap>(registry);
            Assert.AreSame(session, session.Ability.Session);
            session.Dispose();
        }
    }
}
