using System;
using MemoryPack;
using System.Linq;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// MemoryPack 负载实现（内置格式）：源生成器强类型序列化，支持数组/List/DTO。
    /// object[] 视为参数列表：逐参数序列化 + 4 字节长度前缀。
    /// </summary>
    public sealed class MemoryPackPayloadSerializer : IRpcPayloadSerializer
    {
        /// <inheritdoc />
        public byte[] Pack<T>(T value)
        {
            if (value == null) return Array.Empty<byte>();
            if (typeof(T) == typeof(object) && value.GetType() != typeof(object))
            {
                // 单参数/元组以 object 传入时，按运行时类型选择 formatter（避免 object 未注册）。
                return SerializeDynamic(value);
            }

            if (value is object[] values)
            {
                using (var stream = new System.IO.MemoryStream())
                using (var writer = new System.IO.BinaryWriter(stream))
                {
                    writer.Write(values.Length);
                    for (int index = 0; index < values.Length; index++)
                    {
                        // object 元素无法走泛型 Serialize<T>，用反射调用按运行时类型序列化（结果缓存）。
                        byte[] item = SerializeDynamic(values[index]);
                        writer.Write(item.Length);
                        writer.Write(item);
                    }

                    writer.Flush();
                    return stream.ToArray();
                }
            }

            return MemoryPackSerializer.Serialize(value);
        }

        /// <inheritdoc />
        public T Unpack<T>(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return default(T);
            return MemoryPackSerializer.Deserialize<T>(payload);
        }

        /// <inheritdoc />
        public object Unpack(byte[] payload, Type type)
        {
            if (payload == null || payload.Length == 0) return null;
            if (type == null) throw new ArgumentNullException(nameof(type));
            return MemoryPackSerializer.Deserialize(type, payload);
        }

        /// <summary>按运行时类型调用 MemoryPackSerializer.Serialize&lt;T&gt;。</summary>
        private static byte[] SerializeDynamic(object value)
        {
            System.Reflection.MethodInfo method = typeof(MemoryPackSerializer).GetMethods()
                .FirstOrDefault(m => m.Name == "Serialize" && m.IsGenericMethodDefinition &&
                                     m.GetParameters().Length == 2 &&
                                     m.GetParameters()[1].ParameterType == typeof(MemoryPackSerializerOptions));
            if (method == null) throw new NotSupportedException("当前 MemoryPack 版本不支持运行时类型序列化。");
            System.Reflection.MethodInfo closed = method.MakeGenericMethod(value.GetType());
            object result = closed.Invoke(null, new object[] { value, null });
            return (byte[])result;
        }

        /// <inheritdoc />
        public T ReadArgument<T>(byte[] payload, int index)
        {
            if (payload == null || payload.Length == 0) return default(T);
            using (System.IO.MemoryStream stream = new System.IO.MemoryStream(payload))
            using (System.IO.BinaryReader reader = new System.IO.BinaryReader(stream))
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int length = reader.ReadInt32();
                    byte[] item = reader.ReadBytes(length);
                    if (i == index)
                    {
                        return MemoryPackSerializer.Deserialize<T>(item);
                    }
                }

                return default(T);
            }
        }
    }
}