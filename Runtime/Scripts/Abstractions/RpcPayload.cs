using System;
using MemoryPack;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>RPC 业务 Payload 的唯一序列化入口，固定使用 MemoryPack，不支持运行时替换 Serializer。</summary>
    public static class RpcPayload
    {
        /// <summary>按编译期静态类型序列化 Payload。</summary>
        public static byte[] Pack<T>(T value)
        {
            return MemoryPackSerializer.Serialize(value);
        }

        /// <summary>按编译期静态类型反序列化 Payload。</summary>
        public static T Unpack<T>(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return default(T);
            return MemoryPackSerializer.Deserialize<T>(payload);
        }
    }
}
