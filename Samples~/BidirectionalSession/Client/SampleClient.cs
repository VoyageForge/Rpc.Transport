using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Shared;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Client
{
    public static class SampleClient
    {
        public static async Task<ClientSession> ConnectAsync(IPEndPoint endpoint)
        {
            var socket = new TcpClient(endpoint.AddressFamily);
            try
            {
                await socket.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);
                return ClientSession.Create(socket);
            }
            catch { socket.Dispose(); throw; }
        }

        public static async Task<string[]> RunAsync(ClientSession session, CancellationToken ct)
        {
            // 两次出站请求由 Server/AccountController 处理。
            await session.Remote<IAccountApi>().LoginAsync("demo", "sample", ct).ConfigureAwait(false);
            string profile = await session.Remote<IAccountApi>().GetProfileAsync(ct).ConfigureAwait(false);

            // 欢迎消息由服务端回调写入客户端黑板，这次读取只在本地执行。
            string welcome = session.Local<NotificationController>().ReadWelcome();
            return new[] { welcome, "当前用户：" + profile };
        }
    }
}
