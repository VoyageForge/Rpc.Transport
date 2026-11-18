using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>User Controller 的远端代理（手写版）。</summary>
    public sealed class UserClient : IUserController
    {
        private readonly ControllerMap _map;

        /// <summary>创建代理。</summary>
        public UserClient(ControllerMap map)
        {
            _map = map;
        }

        /// <inheritdoc />
        public Task<UserInfo> GetUser(GetUserRequest request)
        {
            return _map.Session.SendAsync<GetUserRequest, UserInfo>("User", "GetUser", request);
        }
    }
}