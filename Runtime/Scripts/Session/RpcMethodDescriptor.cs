using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>由源代码生成器创建的本地 RPC 方法描述，注册到当前 Session 使用的注册表。</summary>
    public sealed class RpcMethodDescriptor
    {
        /// <summary>由协议名称和方法签名计算得到的 FNV-1a 64 位编号。</summary>
        public ulong MethodId { get; set; }

        /// <summary>协议 Controller 名称，与调用方 RpcContract 的名称对应。</summary>
        public string ControllerName { get; set; }

        /// <summary>协议方法名称，与调用方接口方法名或 RpcMethod 自定义名称对应。</summary>
        public string MethodName { get; set; }

        /// <summary>仅用于诊断和日志展示的方法参数签名，不参与运行时查找。</summary>
        public string Signature { get; set; }

        /// <summary>执行本地 Controller 方法；payload 由生成的强类型 Dispatcher 反序列化。</summary>
        public Func<Session, Request<ReadOnlyMemory<byte>>, CancellationToken, Task<byte[]>> Handler { get; set; }
    }
}
