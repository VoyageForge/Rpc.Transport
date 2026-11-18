using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>序列化扩展点。RPC 执行器本身不依赖具体 JSON、MessagePack 或 Protobuf 实现。</summary>
    public interface IRpcSerializer
    {
        /// <summary>序列化内容类型，例如 application/json。</summary>
        string ContentType { get; }

        /// <summary>将对象编码为字节数组。</summary>
        byte[] Serialize<T>(T value);

        /// <summary>将字节数组解码为目标类型。</summary>
        T Deserialize<T>(ReadOnlySpan<byte> data);

        /// <summary>按运行时类型解码（Session/Dispatcher 使用，避免反射构造泛型调用）。</summary>
        object Deserialize(byte[] data, global::System.Type type);
    }

    /// <summary>
    /// 旧版 RPC 服务端传输接口，保留给已有 Host 代码兼容使用。
    /// 新传输实现应优先实现 IRpcServerTransport，获得带 CancellationToken 的请求回调。
    /// </summary>
    public interface IRpcTransport
    {
        /// <summary>启动监听。实现应在此方法返回前完成本地监听器初始化。</summary>
        Task StartAsync(CancellationToken ct);

        /// <summary>停止监听并释放连接、Socket 等传输资源。</summary>
        Task StopAsync(CancellationToken ct);

        /// <summary>兼容简单 Host 的请求事件。该事件不携带连接生命周期取消令牌。</summary>
        event Func<RpcRequest, Task<RpcResponse>> OnRequest;
    }

    /// <summary>
    /// 新版 RPC 服务端传输接口。它与旧 IRpcTransport 独立，避免新的 UDP/WebSocket 实现被迫提供旧事件。
    /// 新实现只需实现 OnRequestAsync，即可把服务端生命周期 Token 传递给 RpcExecutor。
    /// </summary>
    public interface IRpcServerTransport
    {
        /// <summary>
        /// 启动监听器；返回时应保证本地监听资源已经就绪。
        /// ct 是服务端生命周期令牌：它取消时，实现应停止监听，并取消正在处理的请求。
        /// </summary>
        Task StartAsync(CancellationToken ct);

        /// <summary>停止监听并释放传输资源。</summary>
        Task StopAsync(CancellationToken ct);

        /// <summary>
        /// 收到完整请求时触发。ct 在服务端停止时取消，Host 应将它传给 RpcExecutor.ExecuteAsync。
        /// 注意：TCP 对端在业务处理期间断开连接时，服务端未必能立即获知；不能把它当作可靠的客户端取消通知。
        /// </summary>
        event Func<RpcRequest, CancellationToken, Task<RpcResponse>> OnRequestAsync;
    }

    /// <summary>
    /// RPC 客户端传输接口。业务层只提交 RpcRequest 并等待 RpcResponse，不需要知道 Socket 或连接细节。
    /// 每种传输都可以提供自己的超时、连接复用和重试策略，但这些策略不应混入 RpcExecutor。
    /// </summary>
    public interface IRpcClientTransport
    {
        /// <summary>发送一次 RPC 请求并等待对应响应。取消令牌应终止连接/等待操作。</summary>
        Task<RpcResponse> SendAsync(RpcRequest request, CancellationToken ct = default(CancellationToken));
    }

    /// <summary>
    /// 同时支持请求发送和请求接收的数据报传输能力标记，供 UDP 等双向消息传输实现。
    /// 该接口不代表 UDP 自动可靠；可靠性、去重、超时和重传必须由具体协议另外实现。
    /// </summary>
    public interface IRpcDatagramTransport : IRpcClientTransport, IRpcServerTransport
    {
    }
}