namespace VoyageForge.Rpc.Transport
{
    /// <summary>固定帧头中的单字节操作码，决定后续前缀和正文的读取方式。</summary>
    public enum CmdOp : byte
    {
        /// <summary>请求执行对端方法，前缀中携带 MethodId 和 CallId。</summary>
        RpcRequest = 0x10,
        /// <summary>返回执行结果，按 CallId 匹配出站等待项。</summary>
        RpcResponse = 0x11,
        /// <summary>请求取消指定 CallId 的入站执行。</summary>
        RpcCancel = 0x12,
        /// <summary>探测对端是否仍能收发协议帧，正文为空。</summary>
        Ping = 0x30,
        /// <summary>回复收到的 Ping，正文为空。</summary>
        Pong = 0x31,
        /// <summary>请求建立会话：空正文表示首次连接，32 字节私密凭据表示请求恢复。</summary>
        SessionOpen = 0x40,
        /// <summary>建立成功：首次下发凭据，恢复时正文为空。</summary>
        SessionReady = 0x41,
        /// <summary>恢复凭据无效或会话已经结束。</summary>
        ResumeRejected = 0x42,
        /// <summary>旧连接仍活跃或已有恢复请求在途，允许稍后再试。</summary>
        ResumeBusy = 0x43
    }
}
