using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>Controller 契约：只声明方法，代理由生成器生成。</summary>
    [Controller("User")]
    public interface IUserController
    {
        /// <summary>获取用户。</summary>
        Task<UserInfo> GetUser(GetUserRequest request);
    }
}