using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// Controller 方法的静态描述：名称、请求/响应类型和本地执行入口。
    /// 由生成器在编译期生成，运行时只查表，不扫描/反射业务方法。
    /// </summary>
    public sealed class RpcMethodDescriptor
    {
        /// <summary>方法 ID（FNV-1a 64，ControllerName + MethodName + 请求类型）；0 表示尚未计算。</summary>
        public ulong MethodId { get; set; }

        /// <summary>Controller 名称。</summary>
        public string ControllerName { get; set; }

        /// <summary>方法名称。</summary>
        public string MethodName { get; set; }

        /// <summary>请求类型。</summary>
        public Type RequestType { get; set; }

        /// <summary>响应类型。</summary>
        public Type ResponseType { get; set; }

        /// <summary>本地执行入口：参数为反序列化后的请求对象，返回响应对象。</summary>
        public Func<object, CancellationToken, Task<object>> Handler { get; set; }
    }
}