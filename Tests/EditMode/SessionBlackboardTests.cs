using System.Collections.Concurrent;
using NUnit.Framework;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class SessionBlackboardTests
    {
        [Test]
        public void Session类型直接位于Transport根命名空间()
        {
            Assert.AreEqual("VoyageForge.Rpc.Transport.Session", typeof(Session).FullName);
            Assert.AreEqual("VoyageForge.Rpc.Transport", typeof(Session).Namespace);
        }

        private sealed class TestSession : Session
        {
            public TestSession(RpcControllerRegistry registry) : base(registry) { }
        }

        [Test]
        public void 每个Session拥有独立Blackboard()
        {
            var registry = new RpcControllerRegistry();
            using (var first = new TestSession(registry))
            using (var second = new TestSession(registry))
            {
                first.Blackboard["User"] = "Alice";
                second.Blackboard["User"] = "Bob";

                Assert.AreEqual("Alice", first.Blackboard["User"]);
                Assert.AreEqual("Bob", second.Blackboard["User"]);
                Assert.AreNotSame(first.Blackboard, second.Blackboard);
                Assert.IsInstanceOf<ConcurrentDictionary<string, object>>(first.Blackboard);
            }
        }
    }
}
