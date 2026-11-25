using System;
using System.Buffers.Binary;
using System.IO;
using MemoryPack;

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
        internal static byte[] RequestPrefix(ulong method, long call)
        {
            var prefix = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(prefix, method);
            BinaryPrimitives.WriteInt64BigEndian(prefix.AsSpan(8), call);
            return prefix;
        }

        internal static Request<ReadOnlyMemory<byte>> ReadRequest(byte[] frame)
        {
            if (frame.Length < 16) throw new InvalidDataException("Incomplete request prefix.");
            return new Request<ReadOnlyMemory<byte>>(BinaryPrimitives.ReadUInt64BigEndian(frame),
                BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(8)), frame.AsMemory(16));
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
