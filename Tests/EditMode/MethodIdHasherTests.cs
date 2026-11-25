using NUnit.Framework;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class MethodIdHasherTests
    {
        [Test]
        public void 相同协议签名生成相同MethodId()
        {
            ulong first = MethodIdHasher.Compute("Session", "Connect", new[] { "string", "string" });
            ulong second = MethodIdHasher.Compute("Session", "Connect", new[] { "string", "string" });
            Assert.AreEqual(first, second);
            Assert.AreNotEqual(first, MethodIdHasher.Compute("Session", "Connect", new[] { "string" }));
            Assert.AreNotEqual(first, MethodIdHasher.Compute("Session", "Connect", new[] { "string", "int" }));
        }
    }
}
