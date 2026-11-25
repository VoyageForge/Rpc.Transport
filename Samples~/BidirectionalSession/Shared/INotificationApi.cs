using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Shared
{
    [RpcContract("Notification")]
    public partial interface INotificationApi
    {
        Task WelcomeAsync(string message, CancellationToken ct);
    }
}
