using NUnit.Framework;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>验证 string 单值 Pack/Unpack 往返（纯内存，不涉及网络）。</summary>
    public sealed class RpcPayloadStringTests
    {
        [Test]
        public void 短字符串往返()
        {
            byte[] payload = RpcPayload.Pack("hi");
            Assert.AreEqual("hi", RpcPayload.Unpack<string>(payload));
        }

        [Test]
        public void Guid字符串往返()
        {
            string sessionId = System.Guid.NewGuid().ToString("N");
            byte[] payload = RpcPayload.Pack(sessionId);
            UnityEngine.Debug.Log("[PayloadTest] sessionId=" + sessionId + " len=" + payload.Length);
            Assert.AreEqual(sessionId, RpcPayload.Unpack<string>(payload));
        }
        [Test]
        public void 字符串元组往返()
        {
            var value = ("sid-admin", "calc.add");
            byte[] payload = RpcPayload.Pack(value);
            UnityEngine.Debug.Log("[PayloadTest] tuple len=" + payload.Length);
            var roundTrip = RpcPayload.Unpack<(string SessionId, string Permissions)>(payload);
            Assert.AreEqual("sid-admin", roundTrip.SessionId);
            Assert.AreEqual("calc.add", roundTrip.Permissions);
        }
    }
}