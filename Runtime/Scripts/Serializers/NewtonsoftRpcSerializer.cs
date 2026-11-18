using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>
    /// 使用 Unity 官方 Newtonsoft Json Package 实现 IRpcSerializer 的示例。
    /// 序列化器属于 Sample 适配层，不放进 RPC Runtime，避免核心强制依赖某一种 JSON 库。
    /// </summary>
    public sealed class NewtonsoftRpcSerializer : IRpcSerializer
    {
        /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>无 BOM 严格 UTF-8 编码器；静态复用，避免每次 RPC 调用都创建 Encoding 实例。</summary>
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            Culture = System.Globalization.CultureInfo.InvariantCulture
        };

        /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>本示例使用 JSON 文本承载 RPC 消息。</summary>
        public string ContentType { get { return "application/json"; } }

        /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>
        /// 将对象直接写入 UTF-8 MemoryStream，避免 JsonConvert.SerializeObject 先创建完整 JSON 字符串、
        /// 随后再整体编码成 byte[] 的中间字符串分配。JsonSerializer 每次调用单独创建，避免并发共享状态。
        /// </summary>
        public byte[] Serialize<T>(T value)
        {
            // 预估常见 RPC 字符串字段的 UTF-8 大小，降低大字符串写入时 MemoryStream 扩容次数。
            using (MemoryStream stream = new MemoryStream(EstimateCapacity(value)))
            using (StreamWriter textWriter = new StreamWriter(stream, Utf8, 4096, true))
            using (JsonTextWriter jsonWriter = new JsonTextWriter(textWriter))
            {
                jsonWriter.CloseOutput = false;
                JsonSerializer serializer = JsonSerializer.Create(_settings);
                serializer.Serialize(jsonWriter, value);
                jsonWriter.Flush();
                textWriter.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>
        /// 根据消息中已知字符串的 UTF-8 字节数预分配缓冲区。
        /// 这不是序列化长度的精确值；JSON 转义和字段名仍由 MemoryStream 自行扩容处理。
        /// </summary>
        private static int EstimateCapacity<T>(T value)
        {
            long capacity = 256;
            object boxedValue = value;
            RpcRequest request = boxedValue as RpcRequest;
            if (request != null)
            {
                capacity += GetUtf8ByteCount(request.ServiceName) + GetUtf8ByteCount(request.MethodName);
                if (request.Parameters != null)
                {
                    for (int index = 0; index < request.Parameters.Length; index++)
                    {
                        string text = request.Parameters[index] as string;
                        if (text != null) capacity += GetUtf8ByteCount(text) + 8;
                    }
                }
                if (request.Headers != null)
                {
                    foreach (System.Collections.Generic.KeyValuePair<string, string> header in request.Headers)
                        capacity += GetUtf8ByteCount(header.Key) + GetUtf8ByteCount(header.Value) + 16;
                }
            }
            else
            {
                RpcResponse response = boxedValue as RpcResponse;
                if (response != null)
                {
                    string resultText = response.Result as string;
                    if (resultText != null) capacity += GetUtf8ByteCount(resultText) + 16;
                    if (response.Error != null)
                        capacity += GetUtf8ByteCount(response.Error.Code) + GetUtf8ByteCount(response.Error.Message) + 32;
                    capacity += GetUtf8ByteCount(response.Detail);
                }
            }

            if (capacity > int.MaxValue)
                throw new InvalidDataException("RPC 消息中的字符串总长度超过可序列化范围。");
            return (int)capacity;
        }

        /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>复用静态 UTF-8 编码器计算字节数，不创建中间 byte[]。</summary>
        private static int GetUtf8ByteCount(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : Utf8.GetByteCount(value);
        }

        /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>
        /// 将 UTF-8 JSON 反序列化。JSON 中 object 类型的数字/字符串默认可能成为 JValue，
        /// 因此这里把标量 JValue 递归还原成普通 CLR 值，方便 JsonArgumentConverter 继续绑定 int/double/string。
        /// 不启用 TypeNameHandling，复杂 DTO 应由业务方自行定义安全的类型转换规则。
        /// </summary>
        /// <summary>按运行时类型反序列化；供 Session/Dispatcher 使用。</summary>
        public object Deserialize(byte[] data, global::System.Type type)
        {
            if (data == null || data.Length == 0) return null;
            if (type == null) throw new global::System.ArgumentNullException(nameof(type));
            string json = Utf8.GetString(data);
            return JsonConvert.DeserializeObject(json, type, _settings);
        }

        public T Deserialize<T>(ReadOnlySpan<byte> data)
        {
            // Encoding.GetString(ReadOnlySpan<byte>) 直接消费输入 Span，不先调用 ToArray() 复制整帧。
            string json = Utf8.GetString(data);
            T value = JsonConvert.DeserializeObject<T>(json, _settings);

            if (value is RpcRequest request && request.Parameters != null)
            {
                for (int i = 0; i < request.Parameters.Length; i++)
                    request.Parameters[i] = NormalizeToken(request.Parameters[i]);
            }
            else if (value is RpcResponse response)
            {
                response.Result = NormalizeToken(response.Result);
            }

            return value;
        }

        /// <summary>
/// 内置 RPC 序列化器：使用 Unity 官方 Newtonsoft.Json Package。
/// </summary>
/// <remarks>只把 JSON 标量节点转换为 CLR 标量；对象/数组保留 JToken，交给自定义 Converter 处理。</summary>
        private static object NormalizeToken(object value)
        {
            JValue scalar = value as JValue;
            if (scalar != null) return scalar.Value;
            return value;
        }
    }
}
