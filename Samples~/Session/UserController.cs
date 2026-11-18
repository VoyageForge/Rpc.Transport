using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>本地实现：约定实现类名为接口去掉 I 前缀，生成器用它注册 Dispatcher。</summary>
    public sealed class UserController : IUserController
    {
        /// <inheritdoc />
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