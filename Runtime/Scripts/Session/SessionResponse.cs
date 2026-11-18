namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 会话 RPC 响应：RequestId 关联请求，Payload 是序列化后的响应对象。
    /// </summary>
    public sealed class SessionResponse
    {
        /// <summary>对应请求的编号。</summary>
        public long RequestId { get; set; }

        /// <summary>序列化后的响应数据；失败时为空。</summary>
        public byte[] Payload { get; set; }

        /// <summary>错误信息；成功时为 null。</summary>
        public string Error { get; set; }

        /// <summary>是否成功。</summary>
        public bool Success { get; set; }
    }
}