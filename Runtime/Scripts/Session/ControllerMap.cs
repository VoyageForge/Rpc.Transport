using System;
using System.Reflection;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 会话能力集合：每个 Controller 属性既是远端调用代理，也是本地请求执行入口。
    /// 框架提供内置 ControllerMap，用户继承扩展；第一阶段只支持单继承。
    /// </summary>
    public abstract class ControllerMap
    {
        /// <summary>当前 ControllerMap 所属的会话；由会话初始化时注入。</summary>
        public TcpSessionBase Session { get; internal set; }

        /// <summary>
        /// 初始化本层 Controller：注入 Session，并调用生成器生成的 InitializeControllers（只反射一次）。
        /// </summary>
        public virtual void Initialize(TcpSessionBase session)
        {
            Session = session;

            MethodInfo method = GetType().GetMethod("InitializeControllers",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null && method.GetParameters().Length == 0)
            {
                method.Invoke(this, null);
            }
        }
    }
}