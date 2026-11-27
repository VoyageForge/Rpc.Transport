using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>
    /// TCP 消息分帧实例：固定 9 字节头 + payload。
    /// <code>
    /// [SOF: 2B = 0xAA 0x55][Version: 2B BE][CmdOp: 1B][Length: 4B BE][Payload]
    /// </code>
    /// 用长度定界（不使用 EOF），SOF 仅用于协议识别/错位检测。
    /// </summary>
    internal sealed class TcpFrameProtocol
    {
        /// <summary>固定头长度：2 + 2 + 1 + 4。</summary>
        public const int HeaderLength = 9;

        /// <summary>当前协议版本（写入帧头，接收端不匹配直接拒绝）。</summary>
        public const ushort CurrentVersion = 4;

        /// <summary>帧起始魔数 0xAA55。</summary>
        public const ushort Magic = 0xAA55;

        private readonly int _maxFrameBytes;

        /// <summary>创建分帧器。</summary>
        public TcpFrameProtocol(int maxFrameBytes)
        {
            if (maxFrameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
            _maxFrameBytes = maxFrameBytes;
        }

        /// <summary>最大帧长度。</summary>
        public int MaxFrameBytes
        {
            get { return _maxFrameBytes; }
        }

        /// <summary>读取一帧；返回 null 表示对端在帧开始前正常关闭。</summary>
        public async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
        {
            byte[] header = new byte[HeaderLength];
            int headerBytes = await ReadExactlyAsync(stream, header, 0, HeaderLength, true, ct).ConfigureAwait(false);
            if (headerBytes == 0) return null;

            ushort magic = ReadUInt16(header, 0);
            if (magic != Magic)
                throw new InvalidDataException("TCP 帧头 SOF 非法：0x" + magic.ToString("X4"));

            ushort version = ReadUInt16(header, 2);
            if (version != CurrentVersion)
                throw new InvalidDataException("不支持的协议版本：" + version);

            int length = (header[5] << 24) | (header[6] << 16) | (header[7] << 8) | header[8];
            if (length < 0 || length > _maxFrameBytes)
                throw new InvalidDataException("TCP 帧长度非法：" + length + "，允许范围为 0 到 " + _maxFrameBytes + " 字节。");

            byte[] payload = new byte[checked(length + 1)];
            payload[0] = header[4];
            if (length > 0)
                await ReadExactlyAsync(stream, payload, 1, length, false, ct).ConfigureAwait(false);
            return payload;
        }

        /// <summary>写入一帧（固定头 + payload）。</summary>
        public async Task WritePacketAsync(Stream stream, ReadOnlyMemory<byte> packet, CancellationToken ct)
        {
            if (packet.IsEmpty) throw new ArgumentException("不能发送空包。", nameof(packet));
            int length = packet.Length - 1;
            if (length > _maxFrameBytes) throw new InvalidDataException("TCP 帧超过最大长度：" + length);
            byte[] header = new byte[HeaderLength];
            WriteUInt16(header, 0, Magic);
            WriteUInt16(header, 2, CurrentVersion);
            header[4] = packet.Span[0];
            header[5] = (byte)(length >> 24);
            header[6] = (byte)(length >> 16);
            header[7] = (byte)(length >> 8);
            header[8] = (byte)length;

            await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            await stream.WriteAsync(packet.Slice(1), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>按网络字节序读取帧头中的双字节字段。</summary>
        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>按网络字节序写入帧头中的双字节字段。</summary>
        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        /// <summary>循环读取直到凑齐指定字节数，处理一次 TCP 读取只返回部分帧头或正文的情况。</summary>
        /// <remarks>allowCleanEof 用于新帧头：首字节前遇到 EOF 可以返回 0；半帧断开会抛出异常。</remarks>
        private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count,
            bool allowCleanEof, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    if (allowCleanEof && totalRead == 0) return 0;
                    throw new EndOfStreamException("TCP 连接在一帧数据接收完成前关闭。");
                }

                totalRead += read;
            }

            return totalRead;
        }
    }
}
