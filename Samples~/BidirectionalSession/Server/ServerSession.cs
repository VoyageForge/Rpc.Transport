using VoyageForge.Rpc.Transport;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Shared;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Server
{
    [RpcApi(typeof(INotificationApi))]
    [RpcControllerBinding(typeof(AccountController))]
    public sealed partial class ServerSession : Session
    {
    }
}