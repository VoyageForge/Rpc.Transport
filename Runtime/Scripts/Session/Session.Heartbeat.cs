using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport
{
    public abstract partial class Session
    {
        /// <summary>连接建立或最近一次收到完整帧时的单调时钟时间戳，供心跳监视器计算空闲时长。</summary>
        private long _lastReceiveTimestamp;

        /// <summary>连续多久没有收到完整帧时发出 Ping。默认 15 秒，派生会话可在 Attach 前覆写。</summary>
        protected virtual int HeartbeatIdleMilliseconds => 15000;

        /// <summary>从开始发送 Ping 起，最多等待对端活动的时间。默认 10 秒，包含等待写锁的时间。</summary>
        protected virtual int HeartbeatTimeoutMilliseconds => 10000;

        /// <summary>用当前单调时钟值原子更新收包时间，供心跳监视器跨线程读取。</summary>
        private void MarkFrameReceived() =>
            Interlocked.Exchange(ref _lastReceiveTimestamp, Stopwatch.GetTimestamp());

        /// <summary>每条连接最多有一个未决探测。收到业务帧或心跳帧都说明对端仍在发送数据。</summary>
        private async Task MonitorHeartbeatAsync(int idle, int timeout, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    long last = Interlocked.Read(ref _lastReceiveTimestamp);
                    double elapsed = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
                    if (elapsed < idle)
                    {
                        await Task.Delay((int)Math.Ceiling(idle - elapsed), ct).ConfigureAwait(false);
                        continue;
                    }

                    // 在排队写 Ping 前启动截止计时，避免堵塞的写操作使探测永久悬挂。
                    var deadline = Task.Delay(timeout, ct);
                    var send = WriteFrameAsync(CmdOp.Ping, Array.Empty<byte>(), ct);
                    if (await Task.WhenAny(send, deadline).ConfigureAwait(false) == deadline && !send.IsCompleted)
                        Close("HEARTBEAT_TIMEOUT");

                    await send.ConfigureAwait(false);
                    await deadline.ConfigureAwait(false);
                    if (Interlocked.Read(ref _lastReceiveTimestamp) == last)
                    {
                        Close("HEARTBEAT_TIMEOUT");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
            }
            catch (System.IO.IOException)
            {
                Close("CONNECTION_LOST");
            }
            catch (System.Net.Sockets.SocketException)
            {
                Close("CONNECTION_LOST");
            }
        }
    }
}