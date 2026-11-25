using System;
using System.Collections.Generic;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 应用级 RPC 方法注册表。Session 工厂将绑定 Controller 的路由汇总到此注册表。
    /// 注册完成后 Seal；多个 Session 可以安全共享只读注册表。
    /// </summary>
    public sealed class RpcControllerRegistry
    {
        public VoyageForge.Rpc.Executor.LocalExecutor Controllers { get; } = new VoyageForge.Rpc.Executor.LocalExecutor();

        private readonly Dictionary<ulong, RpcMethodDescriptor> _methods = new Dictionary<ulong, RpcMethodDescriptor>();
        private readonly object _gate = new object();
        private bool _sealed;

        /// <summary>当前已注册的方法数量。</summary>
        public int Count { get { lock (_gate) return _methods.Count; } }

        /// <summary>注册表是否已经结束注册并进入只读状态。</summary>
        public bool IsSealed { get { lock (_gate) return _sealed; } }

        /// <summary>注册一个本地 RPC 方法描述；Seal 之后不允许继续注册。</summary>
        /// <param name="descriptor">由源代码生成器创建的方法描述。</param>
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

        /// <summary>根据 MethodId 查找本地方法描述。</summary>
        /// <param name="methodId">远端请求携带的稳定方法编号。</param>
        /// <param name="descriptor">找到时返回的方法描述。</param>
        /// <returns>找到对应方法时返回 true。</returns>
        public bool TryGet(ulong methodId, out RpcMethodDescriptor descriptor)
        {
            lock (_gate) return _methods.TryGetValue(methodId, out descriptor);
        }
    }
}
