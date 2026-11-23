using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>
    /// 演示 Session 连接与服务端 Catalog 注册。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么这里没有直接写 <c>session.Ability.User.GetUser(...)</c></b>：
    /// 那个写法依赖 <c>ControllerRpcGenerator</c> 为 <c>GameControllerMap</c> 生成的代理属性。
    /// 但本目录是通过软链接挂到 Unity 工程的，Unity 的增量编译不会把 analyzer 的生成产物
    /// 纳入该程序集的编译，于是形成"源码缺 User → 程序集编译失败 → 生成产物被丢弃 → 仍然缺 User"
    /// 的死循环（已用临时文件探针确认：生成器本身执行正常、AddSource 成功，是产物没进编译）。
    /// </para>
    /// <para>
    /// 要恢复代理调用写法，需要让本目录变成实体目录（而不是软链接），
    /// 或在改动 analyzer 后删除 <c>Library/ScriptAssemblies</c> 下对应程序集并重启 Unity。
    /// </para>
    /// </remarks>
    public sealed class SessionSample : MonoBehaviour
    {
        private async void Start()
        {
            TcpClient client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 17860);

            RpcControllerRegistry registry = new RpcControllerRegistry();
            TcpSession<GameControllerMap> session = new TcpSession<GameControllerMap>(client, registry);

            // 代理调用示例（需要上述生成产物可用后取消注释）：
            //   UserInfo user = await session.Ability.User.GetUser(new GetUserRequest { Id = "123" });
            UserInfo user = new UserInfo { Id = "123", Name = "User-123" };
            Debug.Log("[SessionSample] " + user.Name);
            session.Dispose();
        }
    }
}
