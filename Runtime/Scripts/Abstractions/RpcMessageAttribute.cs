using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 标注消息类型及其 MessageId；未标注的类型由源生成器/注册表自动编号。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class RpcMessageAttribute : Attribute
    {
        /// <summary>显式消息编号。</summary>
        public MessageId MessageId { get; }

        /// <summary>指定消息编号；特性参数使用 ushort，内部转为 MessageId。</summary>
        public RpcMessageAttribute(ushort messageId)
        {
            MessageId = new MessageId(messageId);
        }
    }
}