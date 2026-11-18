using System;
using System.Collections.Generic;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 两级方法注册表：ControllerName → MethodName → 描述。
    /// 每个 Session 可注册自己这一端提供的 Controller 方法。
    /// </summary>
    public sealed class RpcMethodRegistry
    {
        private readonly Dictionary<string, Dictionary<string, RpcMethodDescriptor>> _methods =
            new Dictionary<string, Dictionary<string, RpcMethodDescriptor>>(StringComparer.Ordinal);

        /// <summary>注册一个方法描述；同名方法覆盖旧描述。</summary>
        public void Register(RpcMethodDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.ControllerName)) throw new ArgumentException("ControllerName 不能为空。");
            if (string.IsNullOrWhiteSpace(descriptor.MethodName)) throw new ArgumentException("MethodName 不能为空。");

            if (!_methods.TryGetValue(descriptor.ControllerName, out Dictionary<string, RpcMethodDescriptor> controller))
            {
                controller = new Dictionary<string, RpcMethodDescriptor>(StringComparer.Ordinal);
                _methods[descriptor.ControllerName] = controller;
            }

            controller[descriptor.MethodName] = descriptor;
        }

        /// <summary>按 ControllerName + MethodName 查找描述。</summary>
        public bool TryGet(string controllerName, string methodName, out RpcMethodDescriptor descriptor)
        {
            descriptor = null;
            if (string.IsNullOrWhiteSpace(controllerName) || string.IsNullOrWhiteSpace(methodName)) return false;
            return _methods.TryGetValue(controllerName, out Dictionary<string, RpcMethodDescriptor> controller)
                   && controller.TryGetValue(methodName, out descriptor);
        }
    }
}