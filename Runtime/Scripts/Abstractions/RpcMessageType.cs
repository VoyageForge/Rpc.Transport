namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 传输层系统消息类型编号（业务消息由 [RpcMessage] 生成/注册）。
    /// 帧头固定写入 2 字节 TypeId，接收端按编号选择反序列化目标。
    /// </summary>
    public static class RpcMessageType
    {
        /// <summary>心跳请求（客户端/服务端都可发送）。</summary>
        public const ushort Ping = 1;

        /// <summary>心跳响应。</summary>
        public const ushort Pong = 2;

        /// <summary>握手鉴权（客户端 → 服务端，携带 token）。</summary>
        public const ushort Auth = 3;

        /// <summary>握手鉴权结果（服务端 → 客户端）。</summary>
        public const ushort AuthResult = 4;

        /// <summary>服务端抽查 token（服务端 → 客户端）。</summary>
        public const ushort QueryToken = 5;

        /// <summary>客户端回应 token 抽查（客户端 → 服务端）。</summary>
        public const ushort Token = 6;

        /// <summary>RPC 请求。</summary>
        public const ushort RpcRequest = 10;

        /// <summary>RPC 响应。</summary>
        public const ushort RpcResponse = 11;
    }
}