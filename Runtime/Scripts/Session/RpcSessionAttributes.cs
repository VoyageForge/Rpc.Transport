using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>选择本端接受 RPC 调用的 Controller，可重复声明。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class RpcControllerBindingAttribute : Attribute
    {
        public Type ControllerType { get; }
        public RpcControllerBindingAttribute(Type controllerType) => ControllerType = controllerType;
    }

    /// <summary>声明一个 RPC API 契约及其协议名称。调用端根据此名称生成代理。</summary>
    [AttributeUsage(AttributeTargets.Interface, Inherited = false)]
    public sealed class RpcContractAttribute : Attribute
    {
        /// <summary>协议控制器名称；未指定时使用接口名去掉 I 前缀及末尾 Api。</summary>
        public string Name { get; }

        /// <summary>创建 RPC 契约标记。</summary>
        /// <param name="name">可选协议名称；为空时由生成器根据接口名推导。</param>
        public RpcContractAttribute(string name = null) { Name = name; }
    }

    /// <summary>标记由源代码生成器扩展的 Session 子类。</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class RpcSessionAttribute : Attribute { }

    /// <summary>声明当前 Session 拥有的一个远端 API。可重复使用以组合不同 API。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class RpcApiAttribute : Attribute
    {
        /// <summary>当前 Session 要暴露为远端调用入口的契约接口。</summary>
        public Type ContractType { get; }

        /// <summary>声明一个 Session API。</summary>
        /// <param name="contractType">带 RpcContract 标记的接口类型。</param>
        public RpcApiAttribute(Type contractType)
        {
            ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
        }
    }
}
