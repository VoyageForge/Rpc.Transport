using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Executor;
using VoyageForge.Rpc.Generated;
using VoyageForge.Rpc.Transport.Session;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Samples.Tcp
{
    /// <summary>会话契约：首次握手与断线重连。</summary>
    [RpcService("session")]
    public interface ISessionService
    {
        /// <summary>首次握手：userId + token，返回 SessionId 与权限。</summary>
        RpcResponse<SessionHandshake> Hello(string userId, string token);

        /// <summary>断线重连：裸 SessionId；活跃会话会被拒绝（禁止顶号）。</summary>
        RpcResponse<SessionHandshake> Resume(string sessionId);
    }

    /// <summary>会话实现：分配 SessionId、下发权限、重连校验。</summary>
    public sealed class SessionService : ISessionService
    {
        private readonly TcpSessionRegistry _registry;

        public SessionService(TcpSessionRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public RpcResponse<SessionHandshake> Hello(string userId, string token)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token))
                return RpcResponse<SessionHandshake>.Fail("UNAUTHENTICATED", "缺少 userId 或 token。");

            // 示例鉴权：admin 拥有 calc.add 权限，其它用户没有。
            string[] permissions = userId == "admin" ? new[] { "calc.add" } : Array.Empty<string>();
            return RpcResponse<SessionHandshake>.Ok(_registry.Create(userId, permissions));
        }

        public RpcResponse<SessionHandshake> Resume(string sessionId)
        {
            if (!_registry.TryResume(sessionId, out SessionHandshake handshake, out string error))
                return RpcResponse<SessionHandshake>.Fail(error, "会话重连被拒绝：" + error);

            return RpcResponse<SessionHandshake>.Ok(handshake);
        }
    }

    /// <summary>TCP 业务契约（鉴权后调用）。</summary>
    [RpcService("tcp.calc")]
    public interface ITcpCalculator
    {
        /// <summary>公开加法。</summary>
        RpcResponse<int> Add(int a, int b);

        /// <summary>需要 calc.add 权限（[RpcAuthorize] 校验）。</summary>
        [RpcAuthorize("calc.add")]
        RpcResponse<int> AuthenticatedAdd(int a, int b);
    }

    /// <summary>业务实现：继承 RpcControllerBase，拿 Context（UserId/Permissions，不含 SessionId）。</summary>
    public sealed class TcpCalculator : RpcControllerBase, ITcpCalculator
    {
        public RpcResponse<int> Add(int a, int b) { return Ok(a + b); }

        [RpcAuthorize("calc.add")]
        public RpcResponse<int> AuthenticatedAdd(int a, int b)
        {
            // Context 由执行器注入：可以看出调用方身份与权限（不暴露 SessionId）。
            if (!IsAuthenticated) return RpcResponse<int>.Fail("UNAUTHENTICATED", "未认证。");
            return Ok(a + b);
        }
    }

    /// <summary>TCP 示例：双端口 + 会话握手 + 断线重连（禁止顶号）+ 权限调用。</summary>
    public static class TcpDemoSample
    {
        /// <summary>端口 A。</summary>
        public const int PortA = 17860;

        /// <summary>端口 B。</summary>
        public const int PortB = 17861;

        /// <summary>双端口会话。</summary>
        public sealed class DemoSession
        {
            public TcpRpcServerTransport ServerA;
            public TcpRpcServerTransport ServerB;
            public TcpRpcClientTransport ClientA;
            public TcpRpcClientTransport ClientB;
            public RpcSession SessionA;
            public RpcSession SessionB;
            public TcpSessionRegistry Registry;
            public string LastUserId = "admin";
            public string LastToken = "demo-token";
        }

        /// <summary>创建执行器：会话服务 + 业务服务 + 生成器分发。</summary>
        public static RpcExecutor CreateExecutor(TcpSessionRegistry registry)
        {
            ServiceRegistry services = new ServiceRegistry();
            services.Register<ISessionService>(new SessionService(registry), "session");
            services.Register<ITcpCalculator>(new TcpCalculator(), "tcp.calc");

            IGeneratedRpcDispatcher dispatcher = new RpcGeneratedDispatcher();
            return new RpcExecutor(services, new GeneratedMethodResolver(dispatcher), new GeneratedRpcInvoker(dispatcher));
        }

        /// <summary>启动双端口服务端与两个客户端，各自完成首次握手。</summary>
        public static async Task<DemoSession> StartAsync(CancellationToken ct)
        {
            DemoSession session = new DemoSession();
            session.Registry = new TcpSessionRegistry();

            session.ServerA = await StartServerAsync(PortA, session.Registry, ct);
            session.ServerB = await StartServerAsync(PortB, session.Registry, ct);

            session.ClientA = new TcpRpcClientTransport("127.0.0.1", PortA, new NewtonsoftRpcSerializer());
            session.ClientB = new TcpRpcClientTransport("127.0.0.1", PortB, new NewtonsoftRpcSerializer());
            session.SessionA = new RpcSession(session.ClientA);
            session.SessionB = new RpcSession(session.ClientB);

            await session.SessionA.ConnectAsync("admin", "demo-token", ct);
            await session.SessionB.ConnectAsync("guest", "demo-token", ct);
            return session;
        }

        /// <summary>单端口服务端：请求交给 RPC 执行器；断开时标记会话可重连。</summary>
        public static async Task<TcpRpcServerTransport> StartServerAsync(
            int port, TcpSessionRegistry registry, CancellationToken ct)
        {
            RpcExecutor executor = CreateExecutor(registry);
            TcpRpcServerTransport server = new TcpRpcServerTransport(
                IPAddress.Loopback, port, new NewtonsoftRpcSerializer());
            server.OnRequestAsync += (request, token) => executor.ExecuteAsync(request, token);
            server.OnTransportError += exception => UnityEngine.Debug.LogException(exception);
            await server.StartAsync(ct);
            return server;
        }

        /// <summary>公开调用（无需权限）。</summary>
        public static async Task<string> CallAddAsync(DemoSession session)
        {
            RpcResponse<int> response = await session.SessionA.CallAsync<int>(new RpcRequest
            {
                ServiceName = "tcp.calc",
                MethodName = "Add",
                Payload = RpcPayload.Pack((3, 4))
            }, default);

            return response.Success ? "Add(3,4) = " + response.Data
                : "Add 失败：[" + response.Error?.Code + "] " + response.Error?.Message;
        }

        /// <summary>有权调用（admin 带 calc.add 权限）。</summary>
        public static async Task<string> CallAuthorizedAsync(DemoSession session)
        {
            RpcResponse<int> response = await session.SessionA.CallAsync<int>(new RpcRequest
            {
                ServiceName = "tcp.calc",
                MethodName = "AuthenticatedAdd",
                Payload = RpcPayload.Pack((1, 2))
            }, default);

            return response.Success ? "有权调用成功：1 + 2 = " + response.Data
                : "有权调用失败：[" + response.Error?.Code + "] " + response.Error?.Message;
        }

        /// <summary>无权调用（guest 没有 calc.add 权限 → UNAUTHORIZED）。</summary>
        public static async Task<string> CallUnauthorizedAsync(DemoSession session)
        {
            RpcResponse<int> response = await session.SessionB.CallAsync<int>(new RpcRequest
            {
                ServiceName = "tcp.calc",
                MethodName = "AuthenticatedAdd",
                Payload = RpcPayload.Pack((1, 2))
            }, default);

            return response.Success ? "无权调用成功（不应发生）"
                : "无权调用被拒绝：[" + response.Error?.Code + "] " + response.Error?.Message;
        }

        /// <summary>断线重连：断开客户端 A → 服务端标记会话断开 → 带裸 SessionId 重连。</summary>
        public static async Task<string> ReconnectAsync(DemoSession session, CancellationToken ct)
        {
            session.ClientA.Dispose();
            session.Registry.MarkDisconnected(session.SessionA.SessionId);

            session.ClientA = new TcpRpcClientTransport("127.0.0.1", PortA, new NewtonsoftRpcSerializer());
            RpcSession reconnect = new RpcSession(session.ClientA);
            await reconnect.ConnectAsync("admin", "demo-token", ct);
            await reconnect.ReconnectAsync(ct);
            session.SessionA = reconnect;

            return "断线重连成功：SessionId=" + reconnect.SessionId + "，权限=" + string.Join(",", reconnect.Permissions);
        }

        /// <summary>伪造 SessionId：不存在的 SessionId 重连 → SESSION_INVALID。</summary>
        public static async Task<string> FakeSessionAsync(DemoSession session, CancellationToken ct)
        {
            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", PortA, new NewtonsoftRpcSerializer());
            try
            {
                RpcSession fake = new RpcSession(client);
                await fake.ConnectAsync("admin", "demo-token", ct);

                RpcResponse<SessionHandshake> response = await fake.CallAsync<SessionHandshake>(new RpcRequest
                {
                    ServiceName = "session",
                    MethodName = "Resume",
                    Payload = RpcPayload.Pack("forged-session-id")
                }, ct);

                return response.Success ? "伪造会话成功（不应发生）"
                    : "伪造会话被拒绝：[" + response.Error?.Code + "] " + response.Error?.Message;
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>顶号：用已有 SessionId 另起连接重连 → SESSION_ALREADY_ACTIVE（禁止顶号）。</summary>
        public static async Task<string> HijackAsync(DemoSession session, CancellationToken ct)
        {
            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", PortB, new NewtonsoftRpcSerializer());
            try
            {
                RpcSession hijack = new RpcSession(client);
                await hijack.ConnectAsync("guest", "demo-token", ct);

                RpcResponse<SessionHandshake> response = await hijack.CallAsync<SessionHandshake>(new RpcRequest
                {
                    ServiceName = "session",
                    MethodName = "Resume",
                    Payload = RpcPayload.Pack(session.SessionA.SessionId)
                }, ct);

                return response.Success ? "顶号成功（不应发生）"
                    : "顶号被拒绝：[" + response.Error?.Code + "] " + response.Error?.Message;
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}