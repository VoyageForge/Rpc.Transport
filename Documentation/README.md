# TCP 帧协议 v4

```text
[SOF: 2B = 0xAA 0x55][Version: 2B BE = 4][CmdOp: 1B][Length: 4B BE][Payload]
```

在 `ConnectionBase` 边界，一个完整 RPC 包的内容为 `CmdOp | Payload`。`TcpConnection` 写入时把包的首字节放到固定头的第 5 字节，读取时按 Length 收齐正文，再交付原包。只有 RPC 分发器解释 CmdOp。会话握手和后续业务包共用一个 TCP 读循环，握手处理期间后续字节留在流中，直到持有者激活会话。

当前系统消息包括 Ping、Pong、RPC Request、RPC Response、RPC Cancel 和可选的会话恢复握手；不包含登录握手或鉴权消息。业务请求 DTO 由 Executor Source Generator 生成，并通过 MemoryPack 序列化。

`CallId` 仅用于将响应和取消通知关联到一次正在进行的调用。Runtime 不保存已完成调用的结果，也不对 Payload 做去重；每次调用都会分配新的 CallId。不要自行复用正在使用的 CallId。

v4 与 v3 帧不兼容，连接两端需同时更新。Length 包含操作前缀与正文的字节数。以下前缀的整数均为大端；业务 DTO 使用 MemoryPack。

| CmdOp | 操作前缀与正文 |
|---|---|
| RpcRequest (0x10) | MethodId (8) + CallId (8) + 参数 DTO；零参数时正文为空 |
| RpcResponse (0x11) | CallId (8) + 状态 (1) + 结果或错误。1 表示成功，0 表示 MemoryPack 错误字符串 |
| RpcCancel (0x12) | CallId (8) |
| Ping / Pong (0x30 / 0x31) | 空 |
| SessionOpen (0x40) | MemoryPack 序列化的 SessionOpenRequest 对象，包含 SessionKind 和可空的 ReconnectKey |
| SessionReady (0x41) | 首次返回 32 字节随机凭据；恢复成功时为空 |
| ResumeRejected (0x42) | 空；未知会话类型、凭据无效或类型不匹配。客户端恢复旧会话时收到此应答会永久结束本地旧会话 |
| ResumeBusy (0x43) | 空；原连接尚活跃或另一握手已占用，允许重试 |

客户端建立 TCP 连接后发送 SessionOpen。服务端与客户端通过同一个操作码分发循环处理会话消息及 Ping/Pong；绑定会话前允许心跳，确认后才开放业务 RPC。握手帧的正文限制为 1024 字节，每个握手阶段最多等待 5 秒。

## 从收到登录请求到执行 AccountController

### 建立会话时注册什么

客户端发送 `SessionOpen` 后，服务端按 `SessionKind` 选择会话类型。示例管理器对 `"game"` 调用 `ServerSession.Create()`。如果是有效的恢复请求，服务端从凭据表取回原 Session，沿用它的执行器和方法路由。[示例管理器](../Samples~/BidirectionalSession/DemoNetworkManager.cs) · [服务端握手](../Runtime/Scripts/Session/RpcServer.cs)

`ServerSession` 的 `[RpcControllerBinding(typeof(AccountController))]` 告诉源生成器要接收哪些 Controller 的调用。生成器把装配逻辑写进 Session 和 Controller 的部分类。下面是 `Create()` 装配登录方法的等价节选，省略了其他方法及描述信息：

```csharp
// ServerSession.Create 内创建注册表，并传入 Controller 工厂。
var registry = new RpcControllerRegistry();
AccountController.RegisterRpc(registry, () => new AccountController());

// AccountController.RegisterRpc 内分别登记实例工厂和入站方法。
registry.Controllers.Register(factory);
route.Handler = (session, packet, ct) =>
    session.Local<AccountController>().DispatchLoginAsync(packet, ct);
registry.Register(route); // route.MethodId 是编译期计算的登录方法编号
```

这里有两次不同的查找。注册表的 `Controllers` 属性在构造时就创建了一个 `LocalExecutor`，它按 **Controller 类型**保存工厂。`registry.Register(route)` 则按 **MethodId** 保存方法描述和强类型委托。生成的 `Create()` 把配置好的注册表传给 Session 构造函数，基类调用 `Seal()` 封存方法路由。`[RpcApi]` 声明的出站接口由生成的构造函数调用 `RegisterRemote` 登记，供本端主动调用对方。[Session 装配声明](../Samples~/BidirectionalSession/Server/ServerSession.cs) · [注册表](../Runtime/Scripts/Session/RpcControllerRegistry.cs) · [Session.Local](../Runtime/Scripts/Session/Session.cs)

### RpcControllerRegistry 和 LocalExecutor 的实例范围

**每个注册表持有一个固定的 LocalExecutor，同一进程可以有多个执行器。** 生成的 `ServerSession.Create()` 每调用一次，就创建一个注册表并交给新 Session。因此，两个客户端分别建立新会话时，服务端通常持有两套注册表和执行器。一个客户端先登录、再查询资料，这两次请求使用同一个执行器。恢复原会话也沿用这个执行器。[注册表中的 Controllers 属性](../Runtime/Scripts/Session/RpcControllerRegistry.cs) · [Session 构造与本地调用](../Runtime/Scripts/Session/Session.cs)

| 对象或成员 | 登录示例中的内容 | 何时创建或使用 |
|---|---|---|
| `RpcControllerRegistry._methods` | 登录和查询资料各有一个 MethodId，对应各自的分发委托 | 建立新 Session 时登记，每个入站请求按 MethodId 查询。`Count` 统计方法数。 |
| `RpcControllerRegistry.Controllers` | 一个 LocalExecutor，其中登记了一份 AccountController 工厂 | 注册表构造时创建执行器。两个方法共用这个执行器和工厂。 |
| `AccountController` 实例 | 处理当前请求，并通过 Session 访问黑板 | 每次 `Local<AccountController>()` 都调用工厂。默认工厂创建新实例，自定义工厂可以在同一 Session 内复用实例。 |

手写 Session 构造函数时，可以显式传入同一个注册表，让多个 Session 共享方法路由和执行器。工厂返回的 RPC Controller 会在首次使用时绑定当前 Session；若同一个 Controller 实例随后被另一个 Session 取得，`SetSession` 会抛出异常。共享注册表时，应为不同 Session 提供独立的 Controller 实例。[Controller 的会话绑定](../Runtime/Scripts/Session/RpcControllerBase.cs)

`Seal()` 的作用范围是方法路由：调用后再用 `registry.Register(descriptor)` 新增方法会抛出异常，重复的 MethodId 也会在注册阶段被拒绝。执行器有自己的注册规则，同一执行器内每种 Controller 类型只允许登记一次工厂。`Seal()` 不会冻结这张工厂表。[注册与封存实现](../Runtime/Scripts/Session/RpcControllerRegistry.cs)

### 每次请求如何执行

1. **收齐一包。** `TcpConnection` 按帧头长度收齐数据，交付 `CmdOp | Payload`。握手完成后，这条物理连接的接收处理器已绑定到一个 `SessionBinding`，它的 `_owner` 就是本次调用的 Session。[TCP 接收循环](../Runtime/Scripts/Tcp/TcpConnection.cs)
2. **读取请求前缀。** `DispatchAsync` 遇到 `RpcRequest` 时调用 `RpcWire.ReadRequest`。前 8 字节是 MethodId，接下来的 8 字节是 CallId，二者按大端读取。剩余字节作为 `Request<ReadOnlyMemory<byte>>.Data` 的切片交给下游，此时业务参数仍是序列化数据。[请求解码](../Runtime/Scripts/Session/Request.cs)
3. **按方法取委托，按类型取实例。** `HandleRequestAsync` 用 MethodId 查当前 Session 的注册表，再执行 `descriptor.Handler(_owner, request, ct)`。登录对应的委托调用 `session.Local<AccountController>()`，后者通过 `LocalExecutor.Local<AccountController>()` 调用工厂，再用 `SetSession(this)` 将当前 Session 绑定给 RPC Controller。默认工厂每次创建新实例，所以示例将登录状态保存在会话黑板中。[入站分发](../Runtime/Scripts/Session/Session.Connection.cs) · [登录实现](../Samples~/BidirectionalSession/Server/AccountController.cs)
4. **还原参数并执行。** 生成的 `AccountController.DispatchLoginAsync` 用 MemoryPack 将 `packet.Data.Span` 反序列化为 Controller 自己的 `LoginRequest`，取出用户名和密码，再调用 `LoginAsync(user, password, ct)`。取消令牌来自接收端为这次调用创建的取消源。登录过程中可以反向调用客户端的 `WelcomeAsync`，接收循环会继续处理它的响应。
5. **完成等待中的调用。** 服务端发送 `RpcResponse`，带回原 CallId、成功标志和结果或错误正文。发起端用 CallId 找到当前连接的等待项，按声明的返回类型解码并完成任务。若入站 MethodId 未注册，服务端返回 `METHOD_NOT_FOUND` 错误，流程不会进入 Controller。[响应处理](../Runtime/Scripts/Session/Response.cs)

## 待实现

Channel 用于单向更新，例如同步坐标。计划按通道和方向分配序号，按数量或时间回报接收统计（如 50 条或 10 秒先到）。TCP 上统计的是应用消息接收情况。广播的接收端选择、转发语义与操作编码仍待确定。
