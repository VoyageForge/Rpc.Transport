using System;
using System.Collections.Generic;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 方法注册表：以 MethodId（FNV-1a 64）为主键查表，同时保留名称索引用于诊断/兼容。
    /// 注册时若 MethodId 为 0，会用 <see cref="MethodIdHasher"/> 自动计算。
    /// </summary>
    public sealed class RpcMethodRegistry
    {
        private readonly Dictionary<ulong, RpcMethodDescriptor> _byId = new Dictionary<ulong, RpcMethodDescriptor>();
        private readonly Dictionary<string, RpcMethodDescriptor> _byName =
            new Dictionary<string, RpcMethodDescriptor>(StringComparer.Ordinal);
        private readonly MethodIdHasher _hasher = new MethodIdHasher();

        /// <summary>注册一个方法描述；同 ID/同名方法覆盖旧描述。</summary>
        public void Register(RpcMethodDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.ControllerName)) throw new ArgumentException("ControllerName 不能为空。");
            if (string.IsNullOrWhiteSpace(descriptor.MethodName)) throw new ArgumentException("MethodName 不能为空。");

            if (descriptor.MethodId == 0)
            {
                descriptor.MethodId = _hasher.Compute(descriptor.ControllerName, descriptor.MethodName,
                    descriptor.RequestType);
            }

            ulong methodId = descriptor.MethodId;
            _byId[methodId] = descriptor;
            _byName[NameKey(descriptor.ControllerName, descriptor.MethodName)] = descriptor;
        }

        /// <summary>按 MethodId 查找（远端请求的主路径）。</summary>
        public bool TryGet(ulong methodId, out RpcMethodDescriptor descriptor)
        {
            return _byId.TryGetValue(methodId, out descriptor);
        }

        /// <summary>按 ControllerName + MethodName 查找（诊断/旧协议回退）。</summary>
        public bool TryGet(string controllerName, string methodName, out RpcMethodDescriptor descriptor)
        {
            descriptor = null;
            if (string.IsNullOrWhiteSpace(controllerName) || string.IsNullOrWhiteSpace(methodName)) return false;
            return _byName.TryGetValue(NameKey(controllerName, methodName), out descriptor);
        }

        private static string NameKey(string controllerName, string methodName)
        {
            return controllerName + "\u001F" + methodName;
        }
    }
}