using System;
using System.Collections.Generic;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 应用级 RPC 方法注册表。各程序集生成静态 Catalog，应用启动时将 Catalog 汇总到此注册表。
    /// 注册完成后 Seal；多个 Session 可以安全共享只读注册表。
    /// </summary>
    public sealed class RpcControllerRegistry
    {
        private readonly Dictionary<ulong, RpcMethodDescriptor> _methods = new Dictionary<ulong, RpcMethodDescriptor>();
        private readonly object _gate = new object();
        private bool _sealed;

        public int Count { get { lock (_gate) return _methods.Count; } }
        public bool IsSealed { get { lock (_gate) return _sealed; } }

        public void Register(RpcMethodDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (descriptor.MethodId == 0) throw new ArgumentException("MethodId 不能为 0。", nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.ControllerName)) throw new ArgumentException("ControllerName 不能为空。");
            if (string.IsNullOrWhiteSpace(descriptor.MethodName)) throw new ArgumentException("MethodName 不能为空。");
            if (descriptor.Handler == null) throw new ArgumentException("Handler 不能为空。", nameof(descriptor));

            lock (_gate)
            {
                if (_sealed) throw new InvalidOperationException("RPC 注册表已 Seal，不能继续注册方法。");
                if (_methods.TryGetValue(descriptor.MethodId, out RpcMethodDescriptor existing))
                {
                    throw new InvalidOperationException(
                        "RPC MethodId 冲突：" + existing.ControllerName + "." + existing.MethodName +
                        " 与 " + descriptor.ControllerName + "." + descriptor.MethodName +
                        "，MethodId=0x" + descriptor.MethodId.ToString("X16"));
                }
                _methods.Add(descriptor.MethodId, descriptor);
            }
        }

        /// <summary>结束注册阶段。可重复调用。</summary>
        public void Seal()
        {
            lock (_gate) _sealed = true;
        }

        public bool TryGet(ulong methodId, out RpcMethodDescriptor descriptor)
        {
            lock (_gate) return _methods.TryGetValue(methodId, out descriptor);
        }
    }
}
