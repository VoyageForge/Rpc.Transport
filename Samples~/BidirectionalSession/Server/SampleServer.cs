using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Server
{
    public static class SampleServer
    {
        public static TcpListener Listen()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        public static async Task<ServerSession> AcceptAsync(TcpListener listener)
        {
            var socket = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            try
            {
                return ServerSession.Create(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}