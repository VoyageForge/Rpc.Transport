using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class RpcControllerRegistryTests
    {
        [Test]
        public void 注册表拒绝重复MethodId()
        {
            var registry = new RpcControllerRegistry();
            registry.Register(new RpcMethodDescriptor
            {
                MethodId = 42,
                ControllerName = "Session",
                MethodName = "Connect",
                Signature = "(string)",
                Handler = (session, payload, ct) => Task.FromResult(Array.Empty<byte>())
            });

            Assert.Throws<InvalidOperationException>(() => registry.Register(new RpcMethodDescriptor
            {
                MethodId = 42,
                ControllerName = "Other",
                MethodName = "Call",
                Signature = "()",
                Handler = (session, payload, ct) => Task.FromResult(Array.Empty<byte>())
            }));
        }

        [Test]
        public void Session创建后注册表进入只读状态()
        {
            var registry = new RpcControllerRegistry();
            using (var session = new TestSession(registry))
            {
                Assert.IsTrue(registry.IsSealed);
                Assert.Throws<InvalidOperationException>(() => registry.Register(new RpcMethodDescriptor
                {
                    MethodId = 7,
                    ControllerName = "Session",
                    MethodName = "Ping",
                    Signature = "()",
                    Handler = (owner, payload, ct) => Task.FromResult(Array.Empty<byte>())
                }));
            }
        }

        private sealed class TestSession : Session
        {
            public TestSession(RpcControllerRegistry registry) : base(registry) { }
        }
    }
}
