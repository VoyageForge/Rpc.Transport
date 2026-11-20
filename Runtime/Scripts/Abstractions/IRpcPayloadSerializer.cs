using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 参数负载序列化扩展点：默认 JSON（Newtonsoft），可替换为 MessagePack、MemoryPack 等。
    /// 替换方式：RpcPayload.Serializer = new MessagePackPayloadSerializer();
    /// </summary>
    public interface IRpcPayloadSerializer
    {
        /// <summary>把请求对象序列化为 Payload。</summary>
        byte[] Pack<T>(T value);

        /// <summary>把 Payload 还原为强类型请求对象。</summary>
        T Unpack<T>(byte[] payload);

        /// <summary>按运行时类型还原（执行器按描述符类型调用）。</summary>
        object Unpack(byte[] payload, Type type);

        /// <summary>按参数索引强类型读取（生成器分发代码使用；值类型泛型不装箱）。</summary>
        T ReadArgument<T>(byte[] payload, int index);
    }
}