using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 参数负载工具：默认 JSON，可通过 <see cref="Serializer"/> 替换为 MessagePack 等实现。
    /// </summary>
    public static class RpcPayload
    {
        private static IRpcPayloadSerializer _serializer = new MemoryPackPayloadSerializer();

        /// <summary>当前负载序列化实现；替换后 Pack/Unpack 立即生效。</summary>
        public static IRpcPayloadSerializer Serializer
        {
            get { return _serializer; }
            set { _serializer = value ?? throw new ArgumentNullException(nameof(value)); }
        }

        /// <summary>序列化请求对象为 Payload（泛型无装箱）。</summary>
        public static byte[] Pack<T>(T value)
        {
            return _serializer.Pack(value);
        }

        /// <summary>把 Payload 还原为强类型请求对象。</summary>
        public static T Unpack<T>(byte[] payload)
        {
            return _serializer.Unpack<T>(payload);
        }

        /// <summary>按运行时类型还原。</summary>
        public static object Unpack(byte[] payload, Type type)
        {
            return _serializer.Unpack(payload, type);
        }

        /// <summary>按参数索引强类型读取（生成器分发代码使用）。</summary>
        public static T ReadArgument<T>(byte[] payload, int index)
        {
            return _serializer.ReadArgument<T>(payload, index);
        }
    }
}