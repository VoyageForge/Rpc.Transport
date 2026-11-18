namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 会话 RPC 请求：ControllerName + MethodName 定位方法，Payload 是序列化后的请求对象。
    /// </summary>
    public sealed class SessionRequest
    {
        /// <summary>请求编号，用于把响应匹配回等待任务。</summary>
        public long RequestId { get; set; }

        /// <summary>目标方法 ID（FNV-1a 64，优先用它查表）。</summary>
        public ulong MethodId { get; set; }

        /// <summary>目标 Controller 名称（诊断用）。</summary>
        public string ControllerName { get; set; }

        /// <summary>目标方法名称。</summary>
        public string MethodName { get; set; }

        /// <summary>序列化后的请求数据。</summary>
        public byte[] Payload { get; set; }
    }
}