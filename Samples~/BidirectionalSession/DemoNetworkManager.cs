using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Server;
using VoyageForge.Rpc.Transport.Unity;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession
{
    /// <summary>在示例服务端接入时选择游戏会话。</summary>
    public sealed class DemoNetworkManager : RpcNetworkManager
    {
        
        
        protected override Session CreateSession(string sessionKind) => sessionKind switch
        {
            "game" => ServerSession.Create(),
            _ => null
        };
    }
}
