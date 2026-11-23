using System;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>调用方的远程 Controller 代理集合。代理属性及初始化代码由 Source Generator 生成。</summary>
    public abstract class ControllerMap
    {
        public TcpSessionBase Session { get; private set; }

        /// <summary>由 TcpSession 初始化；不使用反射查找生成方法。</summary>
        public void Initialize(TcpSessionBase session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (Session != null) throw new InvalidOperationException("ControllerMap 已初始化。");
            Session = session;
            InitializeGenerated();
        }

        /// <summary>由生成的部分类重写，创建当前 Session 专属的代理对象。</summary>
        protected virtual void InitializeGenerated() { }
    }
}
