using System.Threading.Tasks;
using VoyageForge.Rpc.Executor;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>服务端业务实现；Dispatcher 由 ControllerRpcGenerator 生成。</summary>
    [RpcController(typeof(IUserController))]
    public sealed partial class UserController : RpcControllerBase, IUserController
    {
        public Task<UserInfo> GetUser(GetUserRequest request)
        {
            return Task.FromResult(new UserInfo
            {
                Id = request != null ? request.Id : null,
                Name = "User-" + (request != null ? request.Id : "?")
            });
        }
    }
}
