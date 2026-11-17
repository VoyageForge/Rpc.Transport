namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 强类型响应：在基础 RpcResponse 上附带业务结果。
    /// 传输层仍按基础 RpcResponse 处理；TypeId 用于跨进程确定 T 的具体类型。
    /// </summary>
    /// <typeparam name="T">结果类型。</typeparam>
    public sealed class RpcResponse<T> : RpcResponse
    {
        /// <summary>强类型结果。</summary>
        public T Data { get; set; }
    }
}