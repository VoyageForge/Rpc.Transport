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
    // ===== 契约与控制器（服务端）=====

    /// <summary>会话控制器（Controller 模式）：首次握手与断线重连。</summary>
    [RpcService("session")]
    public sealed partial class SessionController : RpcControllerBase
    {
        private readonly TcpSessionRegistry _registry;
        private readonly ISessionAuthenticator _authenticator;

        public SessionController(TcpSessionRegistry registry, ISessionAuthenticator authenticator)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        }

        /// <summary>首次握手：userId + token，返回 SessionId 与权限。</summary>
        [RpcMethod]
        public async Task<RpcResponse<(string SessionId, string Permissions)>> Hello(string userId, string token)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token))
                return RpcResponse<(string, string)>.Fail("UNAUTHENTICATED", "缺少 userId 或 token。");

            // 鉴权剥离到 ISessionAuthenticator：控制器不再关心凭据类型与权限来源。
            SessionAuthResult auth = await _authenticator.AuthenticateAsync(userId, token, CancellationToken.None);
            if (!auth.Success)
                return RpcResponse<(string, string)>.Fail(auth.Error ?? "UNAUTHENTICATED", auth.Message ?? "鉴权失败。");

            SessionHandshake handshake = _registry.Create(userId, auth.Permissions);
            return RpcResponse<(string, string)>.Ok((handshake.SessionId, string.Join(",", handshake.Permissions)));
        }

        /// <summary>断线重连：裸 SessionId；活跃会话会被拒绝（禁止顶号）。</summary>
        [RpcMethod]
        public RpcResponse<string> Resume((string SessionId, string Tag) value)
        {
            string sessionId = value.SessionId;
            UnityEngine.Debug.Log("[Session] Resume sessionId=" + (sessionId ?? "null") +
                " len=" + (sessionId?.Length ?? -1));
            bool resumed = _registry.TryResume(sessionId, out SessionHandshake handshake, out string error);
            UnityEngine.Debug.Log("[Session] Resume result=" + resumed + " error=" + (error ?? "none"));
            if (!resumed)
                return RpcResponse<string>.Fail(error, "会话重连被拒绝：" + error);

            return RpcResponse<string>.Ok(handshake.SessionId);
    }

        }

    /// <summary>
    /// TCP 业务控制器（Controller 模式）：
    /// 继承 RpcControllerBase → 拿 Context（UserId/Permissions，不含 SessionId）；
    /// 类必须是 partial，便于生成器生成 Invoke{Method}；服务名显式指定 tcp.calc。
    /// </summary>
    [RpcService("tcp.calc")]
    public sealed partial class TcpCalculatorController : RpcControllerBase
    {
        /// <summary>公开加法。</summary>
        [RpcMethod]
        public RpcResponse<int> Add(int a, int b) { return Ok(a + b); }

        /// <summary>需要 calc.add 权限（[RpcAuthorize] 校验）。</summary>
        [RpcMethod]
        [RpcAuthorize("calc.add")]
        public RpcResponse<int> AuthenticatedAdd(int a, int b)
        {
            if (!IsAuthenticated) return RpcResponse<int>.Fail("UNAUTHENTICATED", "未认证。");
            return Ok(a + b);
        }
    }

    /// <summary>
    /// 示例鉴权实现：admin 拥有 calc.add 权限，其它用户没有。
    /// 生产环境替换成数据库/LDAP/第三方鉴权即可，控制器不需要改动。
    /// </summary>
    public sealed class DemoAuthenticator : ISessionAuthenticator
    {
        public Task<SessionAuthResult> AuthenticateAsync(string userId, string token, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(token))
                return Task.FromResult(SessionAuthResult.Fail("UNAUTHENTICATED", "缺少 token。"));

            return Task.FromResult(userId == "admin"
                ? SessionAuthResult.Ok("calc.add")
                : SessionAuthResult.Ok());
        }
    }

    // ===== 示例编排（客户端）=====

    /// <summary>TCP 示例：双端口 + 会话握手 + 断线重连（禁止顶号）+ 权限调用。</summary>
    public static class TcpDemoSample
    {
        /// <summary>端口 A。</summary>
        public const int PortA = 17860;

        /// <summary>端口 B。</summary>
        public const int PortB = 17861;

        // ===== 数据结构 =====

        /// <summary>一条连接（一个会话）。</summary>
        public sealed class Connection
        {
            /// <summary>展示名。</summary>
            public string Name;
            /// <summary>会话（首次握手后可用）。</summary>
            public RpcSession Session;
            /// <summary>底层客户端（断开/重连时替换）。</summary>
            public TcpRpcClientTransport Client;
            /// <summary>用户 ID（admin 有 calc.add 权限）。</summary>
            public string UserId;
            /// <summary>是否已断开。</summary>
            public bool Disconnected;
        }

        /// <summary>示例运行时状态：服务端 + 客户端连接列表 + 会话表。</summary>
        public sealed class DemoSession
        {
            public TcpRpcServerTransport ServerA;
            public TcpRpcServerTransport ServerB;
            public TcpSessionRegistry Registry;
            public readonly List<Connection> Connections = new List<Connection>();

            /// <summary>连接编号（Interlocked 递增，避免并发点击时读到旧 Count）。</summary>
            private int _nextIndex;

            /// <summary>取下一个连接编号。</summary>
            public int NextIndex()
            {
                return System.Threading.Interlocked.Increment(ref _nextIndex);
            }
        }

        // ===== 服务端装配 =====

        /// <summary>创建执行器：会话服务 + 业务服务 + 生成器分发。</summary>
        public static RpcExecutor CreateExecutor(TcpSessionRegistry registry)
        {
            ServiceRegistry services = new ServiceRegistry();
            services.Register<SessionController>(new SessionController(registry, new DemoAuthenticator()), "session");
            services.Register<TcpCalculatorController>(new TcpCalculatorController(), "tcp.calc");

            IGeneratedRpcDispatcher dispatcher = new RpcGeneratedDispatcher();
            return new RpcExecutor(services, new GeneratedMethodResolver(dispatcher), new GeneratedRpcInvoker(dispatcher));
        }

        /// <summary>启动双端口服务端。</summary>
        public static async Task<DemoSession> StartServersAsync(CancellationToken ct)
        {
            DemoSession session = new DemoSession();
            session.Registry = new TcpSessionRegistry();
            session.ServerA = await StartServerAsync(PortA, session.Registry, ct);
            session.ServerB = await StartServerAsync(PortB, session.Registry, ct);
            return session;
        }

        /// <summary>单端口服务端：请求交给 RPC 执行器。</summary>
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

        // ===== 连接生命周期 =====

        /// <summary>创建一条新连接并完成首次握手；userId 决定权限（admin/guest）。</summary>
        public static async Task<Connection> CreateConnectionAsync(
            DemoSession session, string userId, CancellationToken ct)
        {
            int index = session.NextIndex();
            int port = index % 2 == 1 ? PortA : PortB;
            string userIdForIndex = index % 2 == 1 ? "admin" : "guest";
            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", port, new NewtonsoftRpcSerializer());
            RpcSession rpcSession = new RpcSession(client);
            await rpcSession.ConnectAsync(userId, "demo-token", ct);

            Connection connection = new Connection
            {
                Name = "#" + (session.Connections.Count + 1) + " " + userId + " @" + port,
                Session = rpcSession,
                Client = client,
                UserId = userId
            };
            session.Connections.Add(connection);
            return connection;
        }

        /// <summary>断开连接：释放客户端并标记会话可重连。</summary>
        public static void Disconnect(DemoSession session, Connection connection)
        {
            if (connection == null || connection.Disconnected) return;
            connection.Client?.Dispose();
            session.Registry.MarkDisconnected(connection.Session.SessionId);
            connection.Disconnected = true;
        }

        /// <summary>重连：重开客户端并带裸 SessionId 恢复会话（活跃会话会被拒绝）。</summary>
        public static async Task<string> ReconnectAsync(DemoSession session, Connection connection, CancellationToken ct)
        {
            TcpRpcClientTransport client = new TcpRpcClientTransport("127.0.0.1", PortA, new NewtonsoftRpcSerializer());
            RpcSession rpcSession = new RpcSession(client);
            await rpcSession.ConnectAsync(connection.UserId, "demo-token", ct);
            await rpcSession.ReconnectAsync(ct);

            connection.Client = client;
            connection.Session = rpcSession;
            connection.Disconnected = false;
            return "重连成功：" + connection.Name + " SessionId=" + rpcSession.SessionId;
        }

        // ===== 调用示例 =====

        /// <summary>查询服务端记录的 SessionId。sessionId 是“会话归属”，与连接本身无直接关系。</summary>
        public static async Task<string> GetServerSessionIds(DemoSession session, Connection connection)
        {
            RpcResponse<string> response = await connection.Session.CallAsync<string>(new RpcRequest
            {
                ServiceName = "session",
                MethodName = "GetSessionIds"
            }, default);

            return response.Success ? "服务端记录：" + response.Data
                : "查询失败：[" + response.Error?.Code + "] " + response.Error?.Message;
        }

        /// <summary>调用公开函数 Add(a,b)。</summary>
        public static async Task<string> CallAdd(DemoSession session, Connection connection, int a, int b)
        {
            RpcResponse<int> response = await connection.Session.CallAsync<int>(new RpcRequest
            {
                ServiceName = "tcp.calc",
                MethodName = "Add",
                Payload = RpcPayload.Pack((a, b))
            }, default);

            return response.Success ? connection.Name + " Add(" + a + "," + b + ") = " + response.Data
                : "Add 失败：[" + response.Error?.Code + "] " + response.Error?.Message;
        }

        /// <summary>调用需要 calc.add 权限的函数。</summary>
        public static async Task<string> CallAuthorizedAdd(DemoSession session, Connection connection, int a, int b)
        {
            RpcResponse<int> response = await connection.Session.CallAsync<int>(new RpcRequest
            {
                ServiceName = "tcp.calc",
                MethodName = "AuthenticatedAdd",
                Payload = RpcPayload.Pack((a, b))
            }, default);

            return response.Success ? connection.Name + " 有权调用：Add(" + a + "," + b + ") = " + response.Data
                : "无权调用被拒绝：[" + response.Error?.Code + "] " + response.Error?.Message;
        }
    }
}
