using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>会话能力集合：手写 Controller 属性 + 代理（生成器版本待恢复后替换）。</summary>
    public partial class GameControllerMap : ControllerMap
    {
        /// <summary>User Controller 代理。</summary>
        public IUserController User { get; private set; }

        /// <summary>初始化代理并注册本地 Dispatcher。</summary>
        public void InitializeControllers()
        {
            User = new UserClient(this);

            UserController local = new UserController();
            Session.Registry.Register(new RpcMethodDescriptor
            {
                ControllerName = "User",
                MethodName = "GetUser",
                RequestType = typeof(GetUserRequest),
                ResponseType = typeof(UserInfo),
                Handler = (request, ct) => Task.FromResult<object>(local.GetUser((GetUserRequest)request))
            });
        }
    }
}