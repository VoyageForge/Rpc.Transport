using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 方法级 RPC 标记：显式声明方法参与 RPC，并可自定义方法名。
    /// 继承 RpcControllerBase 的类，其业务方法必须标注本特性才会被源生成器识别。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class RpcMethodAttribute : Attribute
    {
        /// <summary>自定义方法名；为空时使用方法名。</summary>
        public string Name { get; }

        /// <summary>创建方法标记。</summary>
        public RpcMethodAttribute(string name = null)
        {
            Name = name;
        }
    }
}