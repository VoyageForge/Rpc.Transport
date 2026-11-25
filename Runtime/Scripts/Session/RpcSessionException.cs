using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>会话 RPC 失败（错误码来自远端响应）。</summary>
    public sealed class RpcSessionException : Exception
    {
        /// <summary>错误码/错误文本。</summary>
        public string ErrorCode { get; }

        /// <summary>创建会话异常。</summary>
        public RpcSessionException(string errorCode)
            : base(errorCode)
        {
            ErrorCode = errorCode;
        }
    }
}