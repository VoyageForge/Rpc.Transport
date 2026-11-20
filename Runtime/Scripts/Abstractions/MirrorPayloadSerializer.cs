using System;
using System.IO;
using System.Text;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 内置 Mirror 风格二进制负载格式：每个参数 = 1 字节类型标签 + 二进制值。
    /// 不经过 JSON、不产生 object 装箱（值类型走 BinaryWriter 强类型写入）。
    /// </summary>
    public sealed class MirrorPayloadSerializer : IRpcPayloadSerializer
    {
        /// <summary>写入单个值（类型标签 + 二进制）。</summary>
        public byte[] Pack<T>(T value)
        {
            if (value == null) return Array.Empty<byte>();
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                // object[] 视为参数列表：按顺序平铺写入（Mirror RPC 语义），不额外包数组长度。
                if (value is object[] values)
                {
                    for (int index = 0; index < values.Length; index++)
                    {
                        WriteValue(writer, values[index]);
                    }
                }
                else
                {
                    WriteValue(writer, value);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>读取单个强类型值。</summary>
        public T Unpack<T>(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return default(T);
            using (MemoryStream stream = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                return (T)ReadValue(reader, typeof(T));
            }
        }

        /// <summary>按运行时类型读取。</summary>
        public object Unpack(byte[] payload, Type type)
        {
            if (payload == null || payload.Length == 0) return null;
            if (type == null) throw new ArgumentNullException(nameof(type));
            using (MemoryStream stream = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                return ReadValue(reader, type);
            }
        }

        /// <summary>按参数索引读取（先跳过 index 个带标签的值）。</summary>
        public T ReadArgument<T>(byte[] payload, int index)
        {
            if (payload == null || payload.Length == 0) return default(T);
            using (MemoryStream stream = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                for (int i = 0; i < index; i++)
                {
                    SkipValue(reader);
                }

                return (T)ReadValue(reader, typeof(T));
            }
        }

        private static void WriteValue(BinaryWriter writer, object value)
        {
            if (value == null)
            {
                writer.Write((byte)0);
                return;
            }

            switch (value)
            {
                case bool v: writer.Write((byte)1); writer.Write(v); break;
                case byte v: writer.Write((byte)2); writer.Write(v); break;
                case sbyte v: writer.Write((byte)3); writer.Write(v); break;
                case short v: writer.Write((byte)4); writer.Write(v); break;
                case ushort v: writer.Write((byte)5); writer.Write(v); break;
                case int v: writer.Write((byte)6); writer.Write(v); break;
                case uint v: writer.Write((byte)7); writer.Write(v); break;
                case long v: writer.Write((byte)8); writer.Write(v); break;
                case ulong v: writer.Write((byte)9); writer.Write(v); break;
                case float v: writer.Write((byte)10); writer.Write(v); break;
                case double v: writer.Write((byte)11); writer.Write(v); break;
                case char v: writer.Write((byte)12); writer.Write(v); break;
                case string v: writer.Write((byte)13); WriteString(writer, v); break;
                case byte[] v: writer.Write((byte)14); writer.Write(v.Length); writer.Write(v); break;
                case DateTime v: writer.Write((byte)15); writer.Write(v.Ticks); break;
                case Guid v: writer.Write((byte)16); writer.Write(v.ToByteArray()); break;
                case Array v:
                {
                    writer.Write((byte)17);
                    writer.Write(v.Length);
                    for (int index = 0; index < v.Length; index++)
                    {
                        WriteValue(writer, v.GetValue(index));
                    }

                    break;
                }
                case System.Collections.IList v:
                {
                    writer.Write((byte)18);
                    writer.Write(v.Count);
                    for (int index = 0; index < v.Count; index++)
                    {
                        WriteValue(writer, v[index]);
                    }

                    break;
                }
                default: throw new NotSupportedException("MirrorPayloadSerializer 不支持类型：" + value.GetType().FullName);
            }
        }

        private static object ReadValue(BinaryReader reader, Type type)
        {
            byte tag = reader.ReadByte();
            switch (tag)
            {
                case 0: return null;
                case 1: return reader.ReadBoolean();
                case 2: return reader.ReadByte();
                case 3: return reader.ReadSByte();
                case 4: return reader.ReadInt16();
                case 5: return reader.ReadUInt16();
                case 6: return reader.ReadInt32();
                case 7: return reader.ReadUInt32();
                case 8: return reader.ReadInt64();
                case 9: return reader.ReadUInt64();
                case 10: return reader.ReadSingle();
                case 11: return reader.ReadDouble();
                case 12: return reader.ReadChar();
                case 13: return ReadString(reader);
                case 14: return reader.ReadBytes(reader.ReadInt32());
                case 15: return new DateTime(reader.ReadInt64());
                case 16: return new Guid(reader.ReadBytes(16));
                case 17:
                {
                    int count = reader.ReadInt32();
                    object[] result = new object[count];
                    for (int index = 0; index < count; index++)
                    {
                        result[index] = ReadValue(reader, typeof(object));
                    }

                    return result;
                }
                case 18:
                {
                    int count = reader.ReadInt32();
                    System.Collections.Generic.List<object> result =
                        new System.Collections.Generic.List<object>(count);
                    for (int index = 0; index < count; index++)
                    {
                        result.Add(ReadValue(reader, typeof(object)));
                    }

                    return result;
                }
                default: throw new NotSupportedException("未知的类型标签：" + tag);
            }
        }

        private static void SkipValue(BinaryReader reader)
        {
            ReadValue(reader, typeof(object));
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
    }
}