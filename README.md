# VoyageForge RPC Transport

Unity 2022.3 的双向 TCP RPC 包。每条活跃连接在两端各绑定一个 Session，两端都能发起请求。

## 从示例开始

通过 Package Manager 导入「双向 Session 基础示例」，打开 `Scenes/BidirectionalRpcDemo.unity`。依次连接、登录、查询用户、断开连接，可以看到服务端黑板和客户端收到的欢迎回调。[示例代码和场景说明](Samples~/BidirectionalSession/README.md)

## Session 与 Controller

| 调用 | 执行位置 |
|---|---|
| `session.Local<UserController>()` | 本端。用注册工厂取得 Controller 并绑定当前连接。 |
| `session.Remote<IAccountApi>()` | 对端。调用代理的方法时发送请求。 |

Session 用 `[RpcApi]` 声明出站契约，用 `[RpcControllerBinding]` 选择入站 Controller。托管客户端向端点传入 `() => ClientSession.Create()` 工厂，端点在握手完成时绑定连接。两端按协议名、方法名、业务参数类型及顺序匹配，返回类型也须一致。每次调用的取消令牌可通过取消帧通知对端。

登录状态由业务代码写入当前 Session 的 `Blackboard`，后续 Controller 从同一连接读取。字典支持并发访问，字典中对象的线程安全由业务代码负责。

从登录示例跟踪 Controller 工厂注册、MethodId 分发、MemoryPack 参数解码和响应匹配，见[从收到登录请求到执行 AccountController](Documentation/README.md#从收到登录请求到执行-accountcontroller)。

## MemoryPack 依赖

通过 NuGetForUnity 安装 MemoryPack 1.21.4，项目中应有 `MemoryPack.Core.dll`。开启 `overrideReferences` 的 asmdef 需要在 `precompiledReferences` 中列出该 DLL，Transport 的 EditMode 测试程序集使用这种配置。使用默认预编译引用的程序集可直接访问它。

## Unity 场景中的 TCP 生命周期

在场景根对象上挂载具体的管理器。首次接入时，管理器根据客户端请求的 `SessionKind` 创建服务端 Session；返回 `null` 会拒绝该请求。

```csharp
public sealed class GameNetworkManager : RpcNetworkManager
{
    protected override Session CreateSession(string sessionKind) => sessionKind switch
    {
        "game" => ServerSession.Create(),
        _ => null
    };
}
```

通过管理器创建端点，在 Unity 主线程启动：

```csharp
var server = RpcNetworkManager.Instance.CreateServer();

server.Connected += session => Debug.Log("收到客户端连接");
var address = server.Start(new IPEndPoint(IPAddress.Loopback, 0));

var client = RpcNetworkManager.Instance.CreateClient("game", () => ClientSession.Create());
client.Error += error => Debug.LogException(error);
client.Disconnected += (session, reason) => Debug.Log(reason);
client.Start(address, session =>
    client.OnResult(session.Remote<IAccountApi>().GetProfileAsync(ct),
        profile => Debug.Log(profile), error => Debug.LogException(error)));
```

`RpcNetworkManager.Update` 派发连接事件和完成回调。每次操作可以提供失败回调，省略时走端点的 `Error` 事件。Session 的入站 Controller 使用原有的异步分发。`Stop()` 关闭监听、握手和全部会话，保留事件订阅及已注册的工厂，允许再次 `Start`。`Dispose()` 永久释放端点。组件禁用、销毁、退出播放、退出应用或编辑器脚本重载时，组件同步停止监听，关闭连接中和已连接的 socket，丢弃尚未派发的回调。再次启用后可创建新端点。直接构造 `TcpServer` 时，通过 `RegisterSession("game", () => ServerSession.Create())` 注册工厂。直接构造的服务端和客户端由调用者驱动 `Poll()` 并负责 `Dispose()`。

## 单条 TCP 连接

连接持有者通过 `ConnectionBase` 收发完整字节包。`TcpConnection` 接管已连接的 socket，负责拆包、串行写入和释放。接收事件只携带包，断开事件只携带原因，每条连接只通知一次。回调在接收或关闭线程执行，Unity 端点的业务事件由 Poll 派发。

```csharp
using var connection = new TcpConnection(connectedSocket);
connection.PacketReceived += packet => HandlePacket(packet);
connection.Disconnected += reason => RecordDisconnect(reason);
connection.StartReceiving();
await connection.SendAsync(packetToSend);
```

每次重连都创建新的 `TcpConnection`。同一个逻辑 Session 可绑定多代连接，每代的待响应调用和心跳由 `SessionBinding` 管理。[物理连接实现](Runtime/Scripts/Tcp/TcpConnection.cs)

## 断线恢复

`server.Sessions` 包含已交付和恢复窗口内的会话。每个客户端实例连接一个目标。首次握手签发凭据，恢复时校验凭据和类型并接回原会话。恢复过程不会再次调用管理器的 `CreateSession`。

- `Connected` 交付新会话。意外断线或心跳超时时触发 `Suspended`，在途调用结束，挂起期间的新调用返回连接错误。业务请求不会自动重发。
- 主动连接端尝试恢复，失败后从 250 毫秒退避到最多 4 秒。握手成功后触发 `Resumed`。每次绑定有独立的流、心跳、请求取消源和写锁。
- 默认保留 30 秒，可通过 `retentionMilliseconds` 配置。失效凭据返回 `RESUME_REJECTED`；挂起超时返回 `RESUME_EXPIRED`。永久结束由 `Closed` 和 `Disconnected` 报告。端点在轮询时移除记录。释放端点会立即清理握手中的套接字、挂起会话、定时器和重试。
- `Session.Suspend()` 可用于演练临时断线。`Dispose()` 永久释放本端；对端收到 TCP 关闭后按自己的保留窗口回收。示例面板可依次点击「登录」「模拟断线」「查询用户」验证状态保留。

### 从哪里读代码

- **客户端**：`Start(ip, port, key)` 在 TCP 连上后发送 SessionOpen。空 key 申请新会话；传入此前的 `session.ReconnectKey` 可接回服务端会话。自动重连沿用原客户端对象；换一个客户端端点时会创建新的本地对象。[客户端代码](Runtime/Scripts/Tcp/RpcClient.cs)
- **收帧**：`TcpConnection` 收齐一帧后，`SessionBinding.DispatchAsync` 在同一个 `switch` 分发 SessionOpen、会话应答、Ping/Pong 和业务帧。建立会话前也能响应 Ping；业务帧需要已绑定的 Session。[操作码分发](Runtime/Scripts/Session/Session.Connection.cs)
- **服务端**：SessionOpen 分支选中 `HandleSessionOpen`，在轮询线程创建或恢复会话并回复 SessionReady。客户端收到确认后才交付可用的 Session。[服务端代码](Runtime/Scripts/Session/RpcServer.cs)
- **socket 释放**：端点在 `WaitForSocket` 中跟踪连接或握手中的 socket，超时、失败或端点退出时负责关闭。绑定后由 TcpConnection 串行读写并通知 SessionBinding 清理该代请求。服务端握手确认期间，端点还保留未交付的新 Session，确认失败时一并释放。[端点轮询与释放](Runtime/Scripts/Session/RpcPeer.cs)
- **请求编号**：`Session.InvokeAsync` 给出站请求分配 CallId。当前 SessionBinding 负责等待它的响应。断线恢复后 Session 继续编号，旧绑定的等待项随断线结束。[Session](Runtime/Scripts/Session/Session.cs) · [单条连接的收发](Runtime/Scripts/Session/Session.Connection.cs)

恢复凭据允许持有者接回原会话。现有 TCP 传输是明文，请在可信网络或加密隧道中使用；凭据仅存于内存，服务端重启后失效。

## Session 心跳

每个已连接的 Session 单独记录上一次收到完整帧的单调时钟时间。默认连续 15 秒未收包时主动发送一个空正文 Ping；对端收到后回复空正文 Pong。探测期间收到任何完整的合法帧均视为连接活跃。两端可同时发起探测。

探测窗口默认为 10 秒，从 Ping 开始排队写入时计时。窗口结束时仍未收到帧，或写 Ping 超时，Session 会关闭套接字。普通会话以 `HEARTBEAT_TIMEOUT` 结束，可恢复会话进入挂起状态。业务会话可覆写 `HeartbeatIdleMilliseconds` 和 `HeartbeatTimeoutMilliseconds`，在 Attach 时读取配置。计时独立于 Unity 的 Update 和 timeScale；事件仍在端点下一次 Poll 时派发。释放 Session 会同时取消心跳等待。

有关固定帧头、操作码和待实现的 Channel、广播模式，见 [TCP 帧协议 v4](Documentation/README.md)。
