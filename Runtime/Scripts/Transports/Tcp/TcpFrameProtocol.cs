using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>
    /// TCP 消息分帧实例：固定 10 字节头 + payload。
    /// <code>
    /// [SOF: 2B = 0xAA 0x55][Version: 2B BE][TypeId: 2B BE][Length: 4B BE][Payload]
    /// </code>
    /// 用长度定界（不使用 EOF），SOF 仅用于协议识别/错位检测。
    /// </summary>
    internal sealed class TcpFrameProtocol
    {
        /// <summary>固定头长度：2 + 2 + 2 + 4。</summary>
        public const int HeaderLength = 10;

        /// <summary>当前协议版本（写入帧头，接收端不匹配直接拒绝）。</summary>
        public const ushort CurrentVersion = 1;

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
        public async Task<Frame?> ReadFrameAsync(Stream stream, CancellationToken ct)
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

            ushort typeId = ReadUInt16(header, 4);
            int length = (header[6] << 24) | (header[7] << 16) | (header[8] << 8) | header[9];
            if (length <= 0 || length > _maxFrameBytes)
                throw new InvalidDataException("TCP 帧长度非法：" + length + "，允许范围为 1 到 " + _maxFrameBytes + " 字节。");

            byte[] payload = new byte[length];
            await ReadExactlyAsync(stream, payload, 0, length, false, ct).ConfigureAwait(false);
            return new Frame(typeId, payload);
        }

        /// <summary>写入一帧（固定头 + payload）。</summary>
        public async Task WriteFrameAsync(Stream stream, ushort typeId, byte[] payload, CancellationToken ct)
        {
            if (payload == null || payload.Length == 0)
                throw new InvalidDataException("不能发送空 TCP 帧。");
            if (payload.Length > _maxFrameBytes)
                throw new InvalidDataException("TCP 帧超过最大长度：" + payload.Length);

            byte[] header = new byte[HeaderLength];
            WriteUInt16(header, 0, Magic);
            WriteUInt16(header, 2, CurrentVersion);
            WriteUInt16(header, 4, typeId);
            int length = payload.Length;
            header[6] = (byte)(length >> 24);
            header[7] = (byte)(length >> 16);
            header[8] = (byte)(length >> 8);
            header[9] = (byte)length;

            await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

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

        /// <summary>读到的帧：类型编号 + payload。</summary>
        public readonly struct Frame
        {
            public Frame(ushort typeId, byte[] payload)
            {
                TypeId = typeId;
                Payload = payload;
            }

            /// <summary>消息类型编号（RpcMessageType 或生成器分配）。</summary>
            public ushort TypeId { get; }

            /// <summary>消息体字节。</summary>
            public byte[] Payload { get; }
        }
    }
}