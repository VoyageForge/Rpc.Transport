using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Shared
{
    [RpcContract("Account")]
    public partial interface IAccountApi
    {
        Task LoginAsync(string userName, string password, CancellationToken ct);
        Task<string> GetProfileAsync(CancellationToken ct);
    }
}
