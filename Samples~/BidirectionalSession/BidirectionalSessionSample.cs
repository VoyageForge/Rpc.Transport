using System.Net;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Client;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Server;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession
{
    public static class BidirectionalSessionSample
    {
        public static async Task<string[]> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listener = SampleServer.Listen();
            try
            {
                // TCP 监听队列允许客户端先完成握手，再由服务端接收连接。
                using (var client = await SampleClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint)
                           .ConfigureAwait(false))
                using (var server = await SampleServer.AcceptAsync(listener).ConfigureAwait(false))
                    return await SampleClient.RunAsync(client, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}