using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>固定头帧协议的内存流编解码测试（不依赖 socket）。</summary>
    public sealed class TcpFrameProtocolEditModeTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static byte[] RoundTrip(ushort typeId, byte[] payload)
        {
            TcpFrameProtocol protocol = new TcpFrameProtocol(1024 * 1024);
            using (MemoryStream stream = new MemoryStream())
            {
                protocol.WriteFrameAsync(stream, typeId, payload, None).GetAwaiter().GetResult();
                stream.Position = 0;
                TcpFrameProtocol.Frame? frame = protocol.ReadFrameAsync(stream, None).GetAwaiter().GetResult();
                Assert.IsTrue(frame.HasValue);
                Assert.AreEqual(typeId, frame.Value.TypeId);
                return frame.Value.Payload;
            }
        }

        private static void AssertThrowsInvalidData(Action action)
        {
            try
            {
                action();
                Assert.Fail("应抛出 InvalidDataException。");
            }
            catch (InvalidDataException)
            {
            }
        }

        [UnityTest]
        public IEnumerator 写入读取往返_保留类型和内容()
        {
            byte[] payload = { 1, 2, 3, 4, 5 };
            CollectionAssert.AreEqual(payload, RoundTrip(RpcMessageType.RpcRequest, payload));
            yield return null;
        }

        [UnityTest]
        public IEnumerator 空流_返回null表示正常关闭()
        {
            TcpFrameProtocol protocol = new TcpFrameProtocol(1024);
            using (MemoryStream stream = new MemoryStream())
            {
                Assert.IsNull(protocol.ReadFrameAsync(stream, None).GetAwaiter().GetResult());
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator 非法SOF_抛出InvalidDataException()
        {
            TcpFrameProtocol protocol = new TcpFrameProtocol(1024);
            byte[] bytes = { 0xFF, 0xFF, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x01, 0x00 };
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                AssertThrowsInvalidData(() => protocol.ReadFrameAsync(stream, None).GetAwaiter().GetResult());
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator 不支持版本_抛出InvalidDataException()
        {
            TcpFrameProtocol protocol = new TcpFrameProtocol(1024);
            byte[] bytes = { 0xAA, 0x55, 0x00, 0x09, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x01, 0x00 };
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                AssertThrowsInvalidData(() => protocol.ReadFrameAsync(stream, None).GetAwaiter().GetResult());
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator 零长度帧_抛出InvalidDataException()
        {
            TcpFrameProtocol protocol = new TcpFrameProtocol(1024);
            byte[] bytes = { 0xAA, 0x55, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00 };
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                AssertThrowsInvalidData(() => protocol.ReadFrameAsync(stream, None).GetAwaiter().GetResult());
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator 超过最大帧长_抛出InvalidDataException()
        {
            TcpFrameProtocol protocol = new TcpFrameProtocol(4);
            byte[] bytes = { 0xAA, 0x55, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x05, 1, 2, 3, 4, 5 };
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                AssertThrowsInvalidData(() => protocol.ReadFrameAsync(stream, None).GetAwaiter().GetResult());
            }

            yield return null;
        }
    }
}
