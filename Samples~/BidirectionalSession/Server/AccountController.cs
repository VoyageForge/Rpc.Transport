using System;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Shared;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Server
{
    [RpcController("Account")]
    public partial class AccountController : RpcControllerBase
    {
        [RpcMethod]
        public async Task LoginAsync(string user, string password, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (user != "demo" || password != "sample")
                throw new InvalidOperationException("示例账号或密码错误。");

            Session.Blackboard["User"] = user;
            // Login 的响应尚未发出；在同一条 TCP 连接上反向通知客户端。
            await Session.Remote<INotificationApi>().WelcomeAsync("欢迎 " + user, ct).ConfigureAwait(false);
        }

        [RpcMethod]
        public Task<string> GetProfileAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!Session.Blackboard.TryGetValue("User", out object user) || !(user is string account))
                throw new InvalidOperationException("请先登录。");
            return Task.FromResult(account);
        }
    }
}
