using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Executor;
using VoyageForge.Rpc.Generated;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Samples.Tcp
{
    /// <summary>TCP 示例契约：同时依赖传输层（TCP）与执行器（RpcExecutor/ServicesMap）。</summary>
    [RpcService("tcp.calc")]
    public interface ITcpCalculator
    {
        /// <summary>公开加法。</summary>
        RpcResponse<int> Add(int a, int b);

        /// <summary>有权调用。</summary>
        RpcResponse<int> AuthenticatedAdd(int a, int b);

        /// <summary>无权调用（服务端抛 UnauthorizedAccessException）。</summary>
        RpcResponse<int> UnauthorizedAdd(int a, int b);
    }

    /// <summary>契约实现：业务只填 Data。</summary>
    public sealed class TcpCalculator : ITcpCalculator
    {
        public RpcResponse<int> Add(int a, int b) { return new RpcResponse<int> { Data = a + b }; }

        public RpcResponse<int> AuthenticatedAdd(int a, int b) { return new RpcResponse<int> { Data = a + b }; }

        public RpcResponse<int> UnauthorizedAdd(int a, int b)
        {
            // 执行器会把异常转成 Error.Code（System.UnauthorizedAccessException）。
            throw new UnauthorizedAccessException("无权限调用 UnauthorizedAdd。");
        }
    }

    /// <summary>
    /// TCP 示例：双端口（17860/17861）+ RpcExecutor + 生成器代理。
    /// 依赖 Rpc.Executor（执行器）+ Rpc.Transport（通讯）。
    /// </summary>
    public static class TcpDemoSample
    {
        public const int PortA = 17860;
        public const int PortB = 17861;

        /// <summary>双端口会话。</summary>
        public sealed class DemoSession
        {
            public TcpRpcServerTransport ServerA;
            public TcpRpcServerTransport ServerB;
            public TcpRpcClientTransport ClientA;
            public TcpRpcClientTransport ClientB;
            public ServicesMap ApiA;
            public ServicesMap ApiB;
        }

        /// <summary>创建执行器：注册契约实现 + 生成器分发。</summary>
        public static RpcExecutor CreateExecutor()
        {
            ServiceRegistry registry = new ServiceRegistry();
            registry.Register<ITcpCalculator>(new TcpCalculator());

            IGeneratedRpcDispatcher dispatcher = new RpcGeneratedDispatcher();
            return new RpcExecutor(
                registry,
                new GeneratedMethodResolver(dispatcher),
                new GeneratedRpcInvoker(dispatcher));
        }

        /// <summary>两个端口各启动一个服务端，并建立两个客户端。</summary>
        public static async Task<DemoSession> StartAsync(CancellationToken ct)
        {
            DemoSession session = new DemoSession();
            session.ServerA = await StartServerAsync(PortA, ct);
            session.ServerB = await StartServerAsync(PortB, ct);

            NewtonsoftRpcSerializer serializer = new NewtonsoftRpcSerializer();
            session.ClientA = new TcpRpcClientTransport("127.0.0.1", PortA, serializer);
            session.ClientB = new TcpRpcClientTransport("127.0.0.1", PortB, serializer);
            session.ApiA = new ServicesMap((request, token) => session.ClientA.SendAsync(request, token));
            session.ApiB = new ServicesMap((request, token) => session.ClientB.SendAsync(request, token));
            return session;
        }

        /// <summary>单端口服务端：请求交给 RpcExecutor。</summary>
        public static async Task<TcpRpcServerTransport> StartServerAsync(int port, CancellationToken ct)
        {
            RpcExecutor executor = CreateExecutor();
            TcpRpcServerTransport server = new TcpRpcServerTransport(
                IPAddress.Loopback, port, new NewtonsoftRpcSerializer());
            server.OnRequestAsync += (request, token) => executor.ExecuteAsync(request, token);
            server.OnTransportError += exception => UnityEngine.Debug.LogException(exception);
            await server.StartAsync(ct);
            return server;
        }

        /// <summary>客户端调用服务端函数（两个端口各一次）。</summary>
        public static async Task<string> CallBothAsync(DemoSession session)
        {
            RpcResponse<int> a = await session.ApiA.Add(3, 4);
            RpcResponse<int> b = await session.ApiB.Add(10, 20);
            if (!a.Success || !b.Success)
                return "调用失败：" + (a.Error?.Code ?? b.Error?.Code);
            return "端口 " + PortA + " Add(3,4)=" + a.Data + "；端口 " + PortB + " Add(10,20)=" + b.Data;
        }

        /// <summary>服务端主动鉴权：用 Add 通道模拟 token 校验。</summary>
        public static async Task<string> AuthenticateAsync(DemoSession session)
        {
            RpcResponse<int> ping = await session.ApiA.Add(1, 1);
            return ping.Success
                ? "服务端主动鉴权通过（token 校验成功）"
                : "服务端主动鉴权失败：" + ping.Error?.Message;
        }

        /// <summary>有权调用。</summary>
        public static async Task<string> CallAuthorizedAsync(DemoSession session)
        {
            RpcResponse<int> response = await session.ApiA.AuthenticatedAdd(1, 2);
            return response.Success ? "有权调用成功：1 + 2 = " + response.Data
                : "有权调用失败：[" + response.Error?.Code + "] " + response.Error?.Message;
        }

        /// <summary>无权调用。</summary>
        public static async Task<string> CallUnauthorizedAsync(DemoSession session)
        {
            RpcResponse<int> response = await session.ApiA.UnauthorizedAdd(1, 2);
            return response.Success ? "无权调用成功（不应发生）"
                : "无权调用被拒绝：[" + response.Error?.Code + "] " + response.Error?.Message;
        }

        /// <summary>模拟断线重连：释放并重建客户端 A。</summary>
        public static async Task<string> ReconnectAsync(DemoSession session)
        {
            session.ClientA?.Dispose();
            session.ClientA = new TcpRpcClientTransport("127.0.0.1", PortA, new NewtonsoftRpcSerializer());
            session.ApiA = new ServicesMap((request, token) => session.ClientA.SendAsync(request, token));

            RpcResponse<int> response = await session.ApiA.Add(5, 6);
            return response.Success ? "断线重连成功，Add(5,6)=" + response.Data
                : "断线重连后调用失败：" + response.Error?.Message;
        }
    }
}