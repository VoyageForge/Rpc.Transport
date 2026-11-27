using System;
using System.Buffers.Binary;
using System.IO;
using MemoryPack;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport
{
    public readonly struct RpcUnit { }

    public readonly struct Request<T>
    {
        public readonly ulong MethodId;
        public readonly long CallId;
        public readonly T Data;

        public Request(ulong methodId, long callId, T data)
        {
            MethodId = methodId;
            CallId = callId;
            Data = data;
        }
    }

    internal static class RpcWire
    {
        /// <summary>读到的帧：类型编号 + payload。</summary>
        public readonly struct Frame
        {
            public Frame(CmdOp op, byte[] payload)
            {
                Op = op;
                Payload = payload;
            }

            /// <summary>单字节操作码。</summary>
            public CmdOp Op { get; }

            /// <summary>消息体字节。</summary>
            public byte[] Payload { get; }
        }
        /// <summary>把操作码、操作专属前缀和正文组装为一个完整消息包。</summary>
        internal static byte[] Packet(CmdOp op, byte[] payload, byte[] prefix = null)
        {
            int prefixLength = prefix?.Length ?? 0;
            var packet = new byte[checked(1 + prefixLength + (payload?.Length ?? 0))];
            packet[0] = (byte)op;
            prefix?.CopyTo(packet, 1);
            payload?.CopyTo(packet, 1 + prefixLength);
            return packet;
        }

        internal static Task SendAsync(this ConnectionBase connection, CmdOp op, byte[] payload,
            CancellationToken ct = default, byte[] prefix = null) => connection.SendAsync(Packet(op, payload, prefix), ct);

        internal static byte[] RequestPrefix(ulong method, long call)
        {
            var prefix = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(prefix, method);
            BinaryPrimitives.WriteInt64BigEndian(prefix.AsSpan(8), call);
            return prefix;
        }

        internal static Request<ReadOnlyMemory<byte>> ReadRequest(ReadOnlyMemory<byte> frame)
        {
            if (frame.Length < 16) throw new InvalidDataException("Incomplete request prefix.");
            
            return new Request<ReadOnlyMemory<byte>>
            (
                BinaryPrimitives.ReadUInt64BigEndian(frame.Span),
                BinaryPrimitives.ReadInt64BigEndian(frame.Span.Slice(8)), 
                frame.Slice(16)
                );
        }

        internal static byte[] CallPrefix(long id, bool? success = null)
        {
            var prefix = new byte[success.HasValue ? 9 : 8];
            BinaryPrimitives.WriteInt64BigEndian(prefix, id);
            if (success.HasValue) prefix[8] = success.Value ? (byte)1 : (byte)0;
            return prefix;
        }

        internal static byte[] Encode<T>(T value) =>
            typeof(T) == typeof(RpcUnit) ? Array.Empty<byte>() : MemoryPackSerializer.Serialize(value);
    }
}
