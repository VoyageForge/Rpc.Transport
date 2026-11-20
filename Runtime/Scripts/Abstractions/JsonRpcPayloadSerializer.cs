using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>默认实现：Newtonsoft JSON。</summary>
    public sealed class JsonRpcPayloadSerializer : IRpcPayloadSerializer
    {
        private readonly JsonSerializerSettings _settings;

        /// <summary>创建 JSON 序列化器；默认忽略空值、不写类型名。</summary>
        public JsonRpcPayloadSerializer(JsonSerializerSettings settings = null)
        {
            _settings = settings ?? new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                TypeNameHandling = TypeNameHandling.None
            };
        }

        /// <inheritdoc />
        public byte[] Pack<T>(T value)
        {
            if (value == null) return Array.Empty<byte>();
            return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, _settings));
        }

        /// <inheritdoc />
        public T Unpack<T>(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return default(T);
            return JsonConvert.DeserializeObject<T>(System.Text.Encoding.UTF8.GetString(payload), _settings);
        }

        /// <inheritdoc />
        public T ReadArgument<T>(byte[] payload, int index)
        {
            if (payload == null || payload.Length == 0) return default(T);
            JArray array = JsonConvert.DeserializeObject<JArray>(System.Text.Encoding.UTF8.GetString(payload), _settings);
            if (array == null || index < 0 || index >= array.Count) return default(T);
            return array[index].ToObject<T>(JsonSerializer.Create(_settings));
        }

        /// <inheritdoc />
        public object Unpack(byte[] payload, Type type)
        {
            if (payload == null || payload.Length == 0) return null;
            if (type == null) throw new ArgumentNullException(nameof(type));
            return JsonConvert.DeserializeObject(System.Text.Encoding.UTF8.GetString(payload), type, _settings);
        }
    }
}