namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// RPC 消息信封：版本号 + 业务数据。
    /// 传输层只关心字节，版本号用于协议演进的兼容判断（例如服务端拒绝不支持的版本）。
    /// </summary>
    /// <typeparam name="T">消息体类型（RpcRequest / RpcResponse）。</typeparam>
    public sealed class Protocol<T>
    {
        /// <summary>协议版本号，例如 "1.0"。</summary>
        public string Version { get; set; }

        /// <summary>消息体。</summary>
        public T Data { get; set; }
    }
}