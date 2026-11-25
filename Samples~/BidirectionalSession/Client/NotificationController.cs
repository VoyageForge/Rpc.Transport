using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Client
{
    [RpcController("Notification")]
    public partial class NotificationController : RpcControllerBase
    {
        [RpcMethod]
        public Task WelcomeAsync(string message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Session.Blackboard["Welcome"] = message;
            return Task.CompletedTask;
        }

        // 本地读取，不暴露为远端方法。
        public string ReadWelcome() => (string)Session.Blackboard["Welcome"];
    }
}
