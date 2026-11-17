namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 强类型请求：在基础 RpcRequest 上附带业务 DTO。
    /// 传输层仍按基础 RpcRequest 处理；TypeId 用于跨进程确定 T 的具体类型。
    /// </summary>
    /// <typeparam name="T">参数/请求负载类型。</typeparam>
    public sealed class RpcRequest<T> : RpcRequest
    {
        /// <summary>强类型负载。</summary>
        public T Data { get; set; }
    }
}