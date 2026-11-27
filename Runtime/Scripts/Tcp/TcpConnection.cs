using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport.Tcp
{
    /// <summary>持有一个 TCP socket。负责拆帧、串行写帧和断开通知，每次重连都创建新的实例。</summary>
    public sealed class TcpConnection : ConnectionBase
    {
        private readonly TcpClient client;
        private NetworkStream stream;
        private TcpFrameProtocol protocol;
        private readonly SemaphoreSlim writer = new SemaphoreSlim(1, 1);

        /// <summary>接管一个已经连接的 socket。注册回调后调用 StartReceiving 开始收包。</summary>
        public TcpConnection(TcpClient client, int maxFrameBytes = 1024 * 1024)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            protocol = new TcpFrameProtocol(maxFrameBytes);
            stream = client.GetStream();
            client.NoDelay = true;
        }

        /// <summary>端点在拨号前取得连接所有权，因此组件在握手期间退出也能立即关闭 socket。</summary>
        internal TcpConnection(AddressFamily family, TcpFrameProtocol handshakeProtocol)
        {
            client = new TcpClient(family);
            protocol = handshakeProtocol;
        }

        internal async Task ConnectAsync(IPEndPoint address)
        {
            await client.ConnectAsync(address.Address, address.Port).ConfigureAwait(false);
            stream = client.GetStream();
            client.NoDelay = true;
        }

        // 握手完成且读循环暂停后，切换到该会话配置的业务帧长度上限。
        internal void SetFrameProtocol(TcpFrameProtocol sessionProtocol) => protocol = sessionProtocol;

        /// <summary>每条连接启动一次读循环，每次回调交付一个完整包。</summary>
        protected override void StartReceiveCore() => _ = Task.Run(ReadLoopAsync);

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!Lifetime.IsCancellationRequested)
                {
                    var packet = await protocol.ReadPacketAsync(stream, Lifetime).ConfigureAwait(false);
                    if (packet == null) break;
                    await DeliverPacketAsync(packet).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 半帧、读取错误或收包回调异常都会终止本条连接。
            }
            finally
            {
                Close("CONNECTION_LOST");
            }
        }

        /// <summary>串行写包。取消等待不会截断另一条正在写入的消息。</summary>
        public override async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
        {
            Lifetime.ThrowIfCancellationRequested();
            await writer.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                Lifetime.ThrowIfCancellationRequested();
                await protocol.WritePacketAsync(stream, packet, Lifetime).ConfigureAwait(false);
            }
            catch (IOException)
            {
                Close("CONNECTION_LOST");
                throw;
            }
            finally
            {
                writer.Release();
            }
        }

        /// <summary>关闭 socket，使进行中的读写立即退出。</summary>
        protected override void CloseTransport() => client.Close();
    }
}
