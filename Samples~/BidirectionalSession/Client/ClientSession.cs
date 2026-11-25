using VoyageForge.Rpc.Transport;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Shared;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Client
{
    [RpcApi(typeof(IAccountApi))]
    [RpcControllerBinding(typeof(NotificationController))]
    public sealed partial class ClientSession : Session
    {
        
    }
}
