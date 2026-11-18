using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>
    /// TcpSession 示例：连接一条 TCP 连接后，像本地方法一样调用 Controller。
    /// </summary>
    public sealed class SessionSample : MonoBehaviour
    {
        private async void Start()
        {
            // 1. 连接（演示用；正式项目由网络层管理连接）。
            TcpClient client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 17860);

            // 2. 每条连接一个 TcpSession 实例；socket 在构造时注入，业务代码不再接触它。
            TcpSession<GameControllerMap> session = new TcpSession<GameControllerMap>(client);

            // 3. axios 风格：只需要 controller 名 + 方法名，无需 ControllerMap。
            UserInfo user = await session.CallAsync<GetUserRequest, UserInfo>("User", "GetUser",
                new GetUserRequest { Id = "123" });
            Debug.Log("[SessionSample] " + user.Name);

            // 4. 关闭。
            session.Dispose();
        }
    }
}