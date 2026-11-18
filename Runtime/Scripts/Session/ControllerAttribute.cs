using System;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 标记一个 RPC Controller 契约接口；Name 用于 ControllerName（缺省用接口名去掉 I 前缀）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, Inherited = false)]
    public sealed class ControllerAttribute : Attribute
    {
        /// <summary>Controller 名称。</summary>
        public string Name { get; }

        /// <summary>声明 Controller 契约。</summary>
        public ControllerAttribute(string name = null)
        {
            Name = name;
        }
    }

    /// <summary>
    /// 标记一个 ControllerMap 子类，并声明它包含哪些 Controller 契约（生成器生成属性与代理）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ControllerMapAttribute : Attribute
    {
        /// <summary>Controller 契约接口类型。</summary>
        public Type[] Controllers { get; }

        /// <summary>声明 Controller 列表。</summary>
        public ControllerMapAttribute(params Type[] controllers)
        {
            Controllers = controllers ?? Type.EmptyTypes;
        }
    }
}