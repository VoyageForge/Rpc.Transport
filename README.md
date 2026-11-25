# VoyageForge RPC Transport

Unity 2022.3 的双向 TCP RPC 包。每条连接在两端各有一个 Session，两端都能发起请求。

## 从示例开始

通过 Package Manager 导入「双向 Session 基础示例」，打开 `Scenes/BidirectionalRpcDemo.unity`。依次连接、登录、查询用户、断开连接，可以看到服务端黑板和客户端收到的欢迎回调。[示例代码和场景说明](Samples~/BidirectionalSession/README.md)

## Session 与 Controller

| 调用 | 执行位置 |
|---|---|
| `session.Local<UserController>()` | 本端。用注册工厂取得 Controller 并绑定当前连接。 |
| `session.Remote<IAccountApi>()` | 对端。调用代理的方法时发送请求。 |

Session 用 `[RpcApi]` 声明出站契约，用 `[RpcControllerBinding]` 选择入站 Controller。业务代码通过生成的 `ClientSession.Create(socket)` 创建会话。两端按协议名、方法名、业务参数类型及顺序匹配，返回类型也须一致。每次调用的取消令牌可通过取消帧通知对端。

登录状态由业务代码写入当前 Session 的 `Blackboard`，后续 Controller 从同一连接读取。字典支持并发访问，字典中对象的线程安全由业务代码负责。

## MemoryPack 依赖

通过 NuGetForUnity 安装 MemoryPack 1.21.4，项目中应有 `MemoryPack.Core.dll`。开启 `overrideReferences` 的 asmdef 需要在 `precompiledReferences` 中列出该 DLL，Transport 的 EditMode 测试程序集使用这种配置。使用默认预编译引用的程序集可直接访问它。

## Unity 场景中的 TCP 生命周期

在 GameObject 上添加 `RpcTcpLifetime`，业务程序集引用 `VoyageForge.Rpc.Transport.Unity`。通过组件创建端点，在主线程调用其 API。监听端可同时接受多个连接，每条连接使用独立的 Session。

```csharp
var server = lifetime.Open(socket => ServerSession.Create(socket));
server.Connected += session => Debug.Log("收到客户端连接");
var address = server.Listen(new IPEndPoint(IPAddress.Loopback, 0));

var client = lifetime.Open(socket => ClientSession.Create(socket));
client.Error += error => Debug.LogException(error);
client.Disconnected += (session, reason) => Debug.Log(reason);
client.Connect(address, session =>
    client.OnResult(session.Remote<IAccountApi>().GetProfileAsync(ct),
        profile => Debug.Log(profile), error => Debug.LogException(error)));
```

`RpcTcpLifetime.Update` 派发连接事件和完成回调。每次操作可以提供失败回调，省略时走端点的 `Error` 事件。Session 的入站 Controller 使用原有的异步分发。要在业务 UI 提前断开，调用 `lifetime.Release(endpoint)`。组件禁用、销毁、退出播放、退出应用或编辑器脚本重载时，组件同步停止监听，关闭连接中和已连接的 socket，丢弃尚未派发的回调。再次启用后可创建新端点。独立 .NET 调用端可自行驱动 `Poll()` 并负责 `Dispose()`。

## Session 心跳

每个已连接的 Session 单独记录上一次收到完整帧的单调时钟时间。默认连续 15 秒未收包时主动发送一个空正文 Ping；对端收到后回复空正文 Pong。探测期间收到任何完整的合法帧均视为连接活跃。两端可同时发起探测。

探测窗口默认为 10 秒，从 Ping 开始排队写入时计时。窗口结束时仍未收到帧，或写 Ping 超时，Session 会关闭套接字，并通过 `Closed` 和端点的 `Disconnected` 报告 `HEARTBEAT_TIMEOUT`。业务会话可覆写 `HeartbeatIdleMilliseconds` 和 `HeartbeatTimeoutMilliseconds`，在 Attach 时读取配置。计时独立于 Unity 的 Update 和 timeScale；事件仍在端点下一次 Poll 时派发。释放 Session 会同时取消心跳等待。

有关固定帧头、操作码和待实现的 Channel、广播模式，见 [TCP 帧协议 v3](Documentation/README.md)。
