using System;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>手动组织 Controller 注册的基类。使用 RpcControllerBinding 的 Session 由生成器直接装配。</summary>
    public abstract class RpcControllerCatalog
    {
        public RpcControllerRegistry CreateRegistry(Func<Type, RpcControllerBase> controllerFactory)
        {
            var registry = new RpcControllerRegistry();
            Register(registry, controllerFactory);
            return registry;
        }

        /// <summary>多个程序集可依次将本端 Controller 加入同一个注册表。</summary>
        public abstract void Register(RpcControllerRegistry registry, Func<Type, RpcControllerBase> controllerFactory);
    }
}
