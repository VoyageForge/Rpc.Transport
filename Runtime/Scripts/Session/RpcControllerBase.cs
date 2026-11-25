using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>本地 RPC Controller 基类。每次调用由生成的 Dispatcher 关联当前连接的 Session。</summary>
    public abstract class RpcControllerBase : VoyageForge.Rpc.Executor.ControllerBase
    {
        /// <summary>当前请求所属的 Session，可访问该 Session 独有的 Blackboard。</summary>
        private Session _owner;
        protected Session Session => _owner;

        /// <summary>由生成的 Catalog 在调用前注入；业务代码不应主动调用。</summary>
        internal void SetSession(Session session)
        {
            Session previous = System.Threading.Interlocked.CompareExchange(ref _owner, session, null);
            if (previous != null && previous != session) throw new InvalidOperationException("Controller belongs to another session.");
        }
    }
}
