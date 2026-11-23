using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 会话鉴权接口：把“谁能登录、登录后有哪些权限”从传输层剥离。
    /// 默认实现见示例；生产环境可换成数据库/LDAP/第三方鉴权。
    /// </summary>
    public interface ISessionAuthenticator
    {
        /// <summary>校验凭据；成功时返回权限集合。</summary>
        Task<SessionAuthResult> AuthenticateAsync(string userId, string token, CancellationToken ct);
    }
}