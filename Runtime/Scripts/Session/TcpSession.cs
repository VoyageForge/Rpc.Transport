using System;
using System.Net.Sockets;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>泛型 Session：持有当前连接的远程代理集合 Ability。</summary>
    public sealed class TcpSession<TControllerMap> : TcpSessionBase
        where TControllerMap : ControllerMap, new()
    {
        public TcpSession(TcpClient client, RpcControllerRegistry registry, int maxFrameBytes = 1024 * 1024)
            : base(registry, maxFrameBytes)
        {
            Ability = new TControllerMap();
            Ability.Initialize(this);
            if (client != null) Attach(client);
        }

        public TcpSession(RpcControllerRegistry registry, int maxFrameBytes = 1024 * 1024)
            : this(null, registry, maxFrameBytes) { }

        public TControllerMap Ability { get; }
    }
}
