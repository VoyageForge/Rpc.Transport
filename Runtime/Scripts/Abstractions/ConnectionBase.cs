using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>一条有序消息连接。每次发送一个完整的非空包，接收方获得同样的字节。</summary>
    public abstract class ConnectionBase : IDisposable
    {
        private Func<ReadOnlyMemory<byte>, Task> consumer;
        private int started;
        private int closed;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        protected ConnectionBase()
        {
            Lifetime = lifetime.Token;
        }

        /// <summary>按接收顺序触发，包内存可在回调返回后继续读取。</summary>
        public event Action<ReadOnlyMemory<byte>> PacketReceived;

        /// <summary>物理连接永久结束时触发一次。回调运行于传输或关闭线程。</summary>
        public event Action<string> Disconnected;

        public bool IsClosed => Volatile.Read(ref closed) != 0;
        protected internal CancellationToken Lifetime { get; }

        /// <summary>订阅事件后启动接收。握手、恢复和业务阶段共用一次启动。</summary>
        public void StartReceiving()
        {
            if (IsClosed) throw new ObjectDisposedException(GetType().Name);
            if (Interlocked.Exchange(ref started, 1) == 0) StartReceiveCore();
        }

        /// <summary>启动派生传输的接收循环，每条物理连接只调用一次。</summary>
        protected abstract void StartReceiveCore();

        /// <summary>安装顺序消费回调。持有者可交接，底层只启动一次接收。</summary>
        internal void ReceiveWith(Func<ReadOnlyMemory<byte>, Task> handler)
        {
            Volatile.Write(ref consumer, handler);
            StartReceiving();
        }

        /// <summary>交付完整包。派生类须等待此任务完成再读取下一包。</summary>
        protected Task DeliverPacketAsync(ReadOnlyMemory<byte> packet)
        {
            PacketReceived?.Invoke(packet);
            return Volatile.Read(ref consumer)?.Invoke(packet) ?? Task.CompletedTask;
        }

        /// <summary>发送一个非空包。发送完成前调用方须保留其内容。</summary>
        public abstract Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default);

        /// <summary>释放底层连接并唤醒挂起的读写，每条物理连接只调用一次。</summary>
        protected abstract void CloseTransport();

        public void Dispose() => Close("CONNECTION_CLOSED");

        protected internal void Close(string reason)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try
            {
                CloseTransport();
            }
            finally
            {
                try
                {
                    lifetime.Cancel();
                }
                catch (AggregateException)
                {
                }

                lifetime.Dispose();
                var notify = Disconnected;
                Disconnected = null;
                PacketReceived = null;
                Volatile.Write(ref consumer, null);
                notify?.Invoke(reason);
            }
        }
    }
}