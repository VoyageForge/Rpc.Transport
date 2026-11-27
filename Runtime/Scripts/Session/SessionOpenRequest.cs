using System;
using MemoryPack;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>创建或恢复会话的握手正文。凭据由服务端签发，首次连接时为空。</summary>
    [MemoryPackable]
    public sealed partial class SessionOpenRequest
    {
        internal const int KeyBytes = 32;
        internal const int MaxBytes = 1024;

        /// <summary>双方约定的会话类型，由服务端据此选择会话工厂。</summary>
        public string SessionKind { get; set; }

        /// <summary>首次连接为 null，恢复时提交服务端签发的 32 字节凭据。</summary>
        public byte[] ReconnectKey { get; set; }

        internal static void ValidateKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind) || kind.Length > 128)
                throw new ArgumentException("会话类型必须为 1 至 128 个字符。", nameof(kind));
        }
        internal byte[] Encode() => MemoryPackSerializer.Serialize(this);
        internal static SessionOpenRequest Decode(byte[] body)
        {
            var request = MemoryPackSerializer.Deserialize<SessionOpenRequest>(body);
            ValidateKind(request?.SessionKind);
            if (request.ReconnectKey != null && request.ReconnectKey.Length != KeyBytes)
                throw new ArgumentException("恢复凭据长度无效。");
            return request;
        }
    }
}
