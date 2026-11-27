using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class TcpFrameProtocolEditModeTests
    {
        [Test]
        public void 普通帧可以完整往返()
        {
            var protocol = new TcpFrameProtocol(1024);
            byte[] payload = { 1, 2, 3, 4, 5 };
            using (var stream = new MemoryStream())
            {
                protocol.WriteFrameAsync(stream, CmdOp.RpcRequest, payload, CancellationToken.None).GetAwaiter().GetResult();
                stream.Position = 0;
                var frame = protocol.ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(frame.HasValue);
                Assert.AreEqual(CmdOp.RpcRequest, frame.Value.Op);
                CollectionAssert.AreEqual(payload, frame.Value.Payload);
            }
        }

        [Test]
        public void PingPong允许空Payload()
        {
            var protocol = new TcpFrameProtocol(1024);
            using (var stream = new MemoryStream())
            {
                protocol.WriteFrameAsync(stream, CmdOp.Ping, Array.Empty<byte>(), CancellationToken.None).GetAwaiter().GetResult();
                stream.Position = 0;
                var frame = protocol.ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(frame.HasValue);
                Assert.AreEqual(CmdOp.Ping, frame.Value.Op);
                Assert.AreEqual(0, frame.Value.Payload.Length);
            }
        }
    }
    /// <summary>供线协议测试读写指定操作码，生产分帧器只收发字节包。</summary>
    internal static class TestWireExtensions
    {
        internal static System.Threading.Tasks.Task WriteFrameAsync(this TcpFrameProtocol protocol, Stream stream,
            CmdOp op, byte[] payload, CancellationToken ct) => protocol.WritePacketAsync(stream, RpcWire.Packet(op, payload), ct);

        internal static async System.Threading.Tasks.Task<RpcWire.Frame?> ReadFrameAsync(this TcpFrameProtocol protocol,
            Stream stream, CancellationToken ct)
        {
            var packet = await protocol.ReadPacketAsync(stream, ct).ConfigureAwait(false);
            if (packet == null) return null;
            return new RpcWire.Frame((CmdOp)packet[0], packet.AsSpan(1).ToArray());
        }
    }
}
