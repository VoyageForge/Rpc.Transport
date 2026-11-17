using System;
using System.Collections.Generic;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 消息编号注册表：Type ↔ MessageId。
    /// 显式 [RpcMessage] 优先；未标注类型从 AutoStart 开始顺序分配。
    /// </summary>
    public sealed class RpcMessageTypeRegistry
    {
        /// <summary>自动编号起始值，避开系统帧 MessageId（1-11）。</summary>
        public const ushort AutoStart = 1000;

        private readonly Dictionary<Type, MessageId> _typeToId = new Dictionary<Type, MessageId>();
        private readonly Dictionary<MessageId, Type> _idToType = new Dictionary<MessageId, Type>();
        private readonly object _sync = new object();
        private ushort _nextAutoId = AutoStart;

        /// <summary>注册一个消息类型；返回其 MessageId。</summary>
        public MessageId Register(Type messageType, MessageId? explicitMessageId = null)
        {
            if (messageType == null) throw new ArgumentNullException(nameof(messageType));

            lock (_sync)
            {
                if (_typeToId.TryGetValue(messageType, out MessageId existing))
                {
                    return existing;
                }

                ushort value = explicitMessageId.HasValue
                    ? explicitMessageId.Value.Value
                    : _nextAutoId++;

                if (explicitMessageId == null && value < AutoStart)
                {
                    throw new InvalidOperationException("自动 MessageId 不能低于 " + AutoStart + "。");
                }

                MessageId messageId = new MessageId(value);
                if (_idToType.TryGetValue(messageId, out Type occupied) && occupied != messageType)
                {
                    throw new InvalidOperationException("MessageId " + value + " 已被 " + occupied.FullName + " 占用。");
                }

                _typeToId[messageType] = messageId;
                _idToType[messageId] = messageType;
                return messageId;
            }
        }

        /// <summary>按类型查 MessageId。</summary>
        public bool TryGetMessageId(Type messageType, out MessageId messageId)
        {
            lock (_sync)
            {
                return _typeToId.TryGetValue(messageType, out messageId);
            }
        }

        /// <summary>按 MessageId 查类型。</summary>
        public bool TryGetType(MessageId messageId, out Type messageType)
        {
            lock (_sync)
            {
                return _idToType.TryGetValue(messageId, out messageType);
            }
        }
    }
}