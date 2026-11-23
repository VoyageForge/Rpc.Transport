using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>由源代码生成器创建的本地 RPC 方法描述。</summary>
    public sealed class RpcMethodDescriptor
    {
        public ulong MethodId { get; set; }
        public string ControllerName { get; set; }
        public string MethodName { get; set; }
        public string Signature { get; set; }

        /// <summary>执行本地 Controller 方法；payload 已由生成的强类型 Dispatcher 反序列化。</summary>
        public Func<byte[], CancellationToken, Task<byte[]>> Handler { get; set; }
    }
}
