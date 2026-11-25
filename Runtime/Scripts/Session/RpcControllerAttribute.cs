using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>标记本端要接收并执行的 RPC Controller。无需引用调用端的 API 接口。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class RpcControllerAttribute : Attribute
    {
        /// <summary>协议控制器名称，必须与调用端 RpcContract 的名称一致。</summary>
        /// <summary>与调用方 RpcContract 对应的协议 Controller 名称。</summary>
        public string Name { get; }

        /// <summary>创建本地 Controller 标记。</summary>
        /// <param name="name">可选协议名称；为空时由生成器根据类名推导。</param>
        public RpcControllerAttribute(string name = null) { Name = name; }
    }
}
