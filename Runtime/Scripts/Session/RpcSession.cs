using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport.Tcp;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 会话级客户端：一个连接一个会话。
    /// 首次连接握手拿 SessionId 与权限；之后请求复用连接；断线重连带裸 SessionId（服务端禁止顶号）。
    /// </summary>
    public sealed class RpcSession : IDisposable
    {
        private readonly TcpRpcClientTransport _client;

        /// <summary>会话 ID（服务端下发）；未握手时为 null。</summary>
        public string SessionId { get; private set; }

        /// <summary>握手时下发的权限集合。</summary>
        public string[] Permissions { get; private set; } = Array.Empty<string>();

        /// <summary>当前用户 ID。</summary>
        public string UserId { get; private set; }

        /// <summary>用已建立的 TCP 客户端构造（客户端连接由外部管理）。</summary>
        public RpcSession(TcpRpcClientTransport client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>首次握手：提交 userId/token，拿 SessionId 与权限。</summary>
        public async Task ConnectAsync(string userId, string token, CancellationToken ct)
        {
            // 握手返回元组 (sessionId, permissions)：MemoryPack 原生支持，无需额外 formatter。
            RpcResponse<(string SessionId, string Permissions)> response = await SendTypedAsync<(string SessionId, string Permissions)>(
                new RpcRequest
                {
                    ServiceName = "session",
                    MethodName = "Hello",
                    Payload = RpcPayload.Pack((userId, token))
                }, ct);

            if (!response.Success)
                throw new InvalidOperationException("会话握手失败 [" + response.Error?.Code + "]：" + response.Error?.Message);

            UserId = userId;
            SessionId = response.Data.SessionId;
        }

        /// <summary>断线重连：带裸 SessionId；服务端已有活跃连接时会拒绝（禁止顶号）。</summary>
        public Task ReconnectAsync(CancellationToken ct)
        {
            return ReconnectAsync(SessionId, ct);
        }

        /// <summary>断线重连（显式传入旧 SessionId）：重建 RpcSession 时不必先握手。</summary>
        public async Task ReconnectAsync(string sessionId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidOperationException("没有 SessionId 可重连。");

            RpcResponse<string> response = await SendTypedAsync<string>(
                new RpcRequest
                {
                    ServiceName = "session",
                    MethodName = "Resume",
                    Payload = RpcPayload.Pack((sessionId, string.Empty))
                }, ct);

            if (!response.Success)
                throw new InvalidOperationException("会话重连失败 [" + response.Error?.Code + "]：" + response.Error?.Message);

        }

        /// <summary>会话级调用：自动把 user-id / permissions 写进请求头（执行器层 RpcContext 使用）。</summary>
        public Task<RpcResponse<T>> CallAsync<T>(RpcRequest request, CancellationToken ct)
        {
            if (request.Headers == null) request.Headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(UserId)) request.Headers["user-id"] = UserId;
            request.Headers["permissions"] = string.Join(",", Permissions);
            return SendTypedAsync<T>(request, ct);
        }

        private async Task<RpcResponse<T>> SendTypedAsync<T>(RpcRequest request, CancellationToken ct)
        {
            RpcResponse baseResponse = await _client.SendAsync(request, ct).ConfigureAwait(false);
            return ToTyped<T>(baseResponse);
        }

        /// <summary>权限字符串（逗号分隔）转集合。</summary>
        private static string[] ParsePermissions(string permissions)
        {
            if (string.IsNullOrEmpty(permissions)) return Array.Empty<string>();
            return permissions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>把基础响应转成强类型响应（Payload 是服务端 dispatch 写出的 Data）。</summary>
        private static RpcResponse<T> ToTyped<T>(RpcResponse response)
        {
            if (!response.Success)
            {
                return new RpcResponse<T> { Success = false, Error = response.Error };
            }

            if (response.Payload != null && response.Payload.Length > 0)
            {
                return new RpcResponse<T> { Success = true, Data = RpcPayload.Unpack<T>(response.Payload) };
            }

            return new RpcResponse<T> { Success = true };
        }

        /// <summary>关闭连接。</summary>
        public void Dispose()
        {
            _client.Dispose();
        }
    }
}