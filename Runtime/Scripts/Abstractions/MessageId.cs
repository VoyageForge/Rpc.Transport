using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 消息编号强类型包装：帧头 TypeId 与 [RpcMessage] 统一使用。
    /// 与 ushort 可隐式转换，便于协议写入和特性参数。
    /// </summary>
    public readonly struct MessageId : IEquatable<MessageId>
    {
        /// <summary>编号值。</summary>
        public ushort Value { get; }

        /// <summary>用原始编号创建。</summary>
        public MessageId(ushort value)
        {
            Value = value;
        }

        /// <summary>隐式转回原始编号。</summary>
        public static implicit operator ushort(MessageId id)
        {
            return id.Value;
        }

        /// <summary>从原始编号隐式创建。</summary>
        public static implicit operator MessageId(ushort value)
        {
            return new MessageId(value);
        }

        /// <inheritdoc />
        public bool Equals(MessageId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is MessageId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Value.ToString();
        }

        /// <summary>相等比较。</summary>
        public static bool operator ==(MessageId left, MessageId right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较。</summary>
        public static bool operator !=(MessageId left, MessageId right)
        {
            return !left.Equals(right);
        }
    }
}