using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Samples.Tcp
{
    /// <summary>
    /// Rpc.Transport 的 TCP 示例：长连接 + 心跳 + 握手鉴权 + 断线重连。
    /// <para>服务端与客户端都只在传输层，不依赖 Rpc.Executor。</para>
    /// </summary>
    public sealed class TcpEchoSample : IDisposable
    {
        /// <summary>示例握手 token；正式项目应从配置读取，不要硬编码。</summary>
        public const string DemoToken = "demo-token";

        /// <summary>最大帧长度（示例给 1MB）。</summary>
        private const int MaxFrameBytes = 1024 * 1024;

        private TcpRpcServerTransport _server;
        private TcpRpcClientTransport _client;

        /// <summary>服务端监听的端口（端口 0 时由系统分配）。</summary>
        public int Port { get; private set; }

        /// <summary>启动服务端并创建客户端（同一进程 Loopback 演示）。</summary>
        public async Task StartAsync(CancellationToken ct)
        {
            if (_server != null) throw new InvalidOperationException("示例已经启动。");

            // 1. 服务端：Loopback + 系统分配端口 + 握手 token。
            TcpRpcServerTransport server = new TcpRpcServerTransport(
                IPAddress.Loopback,
                0,
                new NewtonsoftRpcSerializer(),
                requiredToken: DemoToken,
                maxFrameBytes: MaxFrameBytes);
            server.OnRequestAsync += (request, requestToken) =>
            {
                // 2. 传输层示例直接返回响应；正式项目这里交给 RpcExecutor.ExecuteAsync。
                // 单一参数：直接序列化参数类型（与生成器规则一致，无 DTO）。
                string text = RpcPayload.Unpack<string>(request.Payload) ?? string.Empty;
                return Task.FromResult(RpcResponse.Ok("echo:" + text));
            };
            server.OnTransportError += exception => Console.WriteLine("[server] " + exception.Message);

            await server.StartAsync(ct);
            _server = server;
            Port = server.LocalEndPoint.Port;

            // 3. 客户端：长连接 + 握手 token；心跳与断线重连由传输层自动处理。
            _client = new TcpRpcClientTransport(
                "127.0.0.1",
                Port,
                new NewtonsoftRpcSerializer(),
                token: DemoToken,
                maxFrameBytes: MaxFrameBytes);
            _client.OnConnected += () => Console.WriteLine("[client] connected");
            _client.OnDisconnected += exception => Console.WriteLine("[client] disconnected: " + (exception?.Message ?? "closed"));
        }

        /// <summary>发送一条回显请求；演示 RequestId 自动分配与长连接复用。</summary>
        public async Task<string> EchoAsync(string text, CancellationToken ct)
        {
            if (_client == null) throw new InvalidOperationException("请先调用 StartAsync。");

            RpcResponse response = await _client.SendAsync(new RpcRequest
            {
                ServiceName = "demo.echo",
                MethodName = "Echo",
                Payload = RpcPayload.Pack(text)
            }, ct);

            if (!response.Success)
                throw new InvalidOperationException("RPC 失败 [" + response.Error.Code + "]：" + response.Error.Message);

            return Convert.ToString(response.Result, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>释放客户端与服务端（服务端会释放监听端口）。</summary>
        public void Dispose()
        {
            _client?.Dispose();
            _client = null;

            _server?.Dispose();
            _server = null;
        }
    }
}
