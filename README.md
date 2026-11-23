# VoyageForge RPC Transport

独立 Unity UPM Package：`com.voyageforge.rpc-transport`，目标 Unity 2022.3。

## 职责

- TCP 固定头分帧、长连接、心跳、鉴权、重连；
- 双向 `TcpSession<TControllerMap>`；
- 应用级 `RpcControllerRegistry` 与程序集生成的 `GeneratedRpcControllerCatalog`；
- Session 业务 Payload 统一使用 MemoryPack；
- 传输层通用 `IRpcSerializer` 仍用于独立的 `TcpRpcClientTransport` / `TcpRpcServerTransport` 消息适配，不参与新的 Controller Session 协议。

## Controller Session

调用方声明 `partial ControllerMap` 并通过 `[ControllerMap(typeof(IUserController))]` 选择共享契约。Source Generator 生成属性与强类型代理。被调用方通过 `[RpcController(typeof(IUserController))]` 标记 `partial class XxxController : RpcControllerBase, IUserController`，生成 Dispatcher。

每个服务程序集生成一个静态 `GeneratedRpcControllerCatalog`。应用启动时先创建 `RpcControllerRegistry`，依次注册各程序集 Catalog，然后把同一个 Registry 传给每条 `TcpSession<TControllerMap>`。Session 创建时会 Seal Registry，因此必须先完成注册。Catalog 的 factory 每次调用都应返回新实例或请求作用域实例。

```csharp
var registry = new RpcControllerRegistry();
ServerAccount.GeneratedRpcControllerCatalog.Register(registry, type => new UserController());
var session = new TcpSession<GameControllerMap>(tcpClient, registry);
var user = await session.Ability.User.GetUser(request, ct);
```

### MethodId

MethodId 是 FNV-1a 64，输入包括 `ControllerName`、`MethodName` 和按声明顺序排列的所有业务参数类型规范名。`CancellationToken` 不参与签名。类型规范名由 Source Generator 的 Roslyn `FullyQualifiedFormat` 产生，协议 Key 版本为 `v2`。

网络请求只包含 `RequestId`、`MethodId`、`Payload`；Controller/Method 名称仅存在于本地生成 Catalog 和 Descriptor 中。重复 MethodId 注册会直接抛出异常，不再回退到字符串查找。

### Payload

- 0 个业务参数：空 Payload；
- 1 个业务参数：直接序列化该参数的静态类型；
- 2–7 个业务参数：序列化对应的 `ValueTuple`；
- 返回值 `Task<T>`：MemoryPack 序列化 `T`；
- 返回值 `Task`：空响应 Payload。

DTO 必须由用户源码声明为 `[MemoryPackable] partial`。RPC Generator 不会向 MemoryPack Generator 注入 DTO 源码。单个 `null` 参数仍会按其静态类型序列化，不与无参数调用混淆。

## TCP 帧协议

```text
[SOF: 2B = 0xAA 0x55][Version: 2B BE][TypeId: 2B BE][Length: 4B BE][Payload]
```

Session 控制包使用轻量二进制编码；业务 Payload 使用 MemoryPack。帧版本目前为 `1`。

## 通用 TCP Transport

`TcpRpcClientTransport` / `TcpRpcServerTransport` 保留独立的 `IRpcSerializer` 适配层，当前仍提供 Newtonsoft 实现。这条通用消息通道与新的 Controller Session 是两种不同 API，不要把它的 Serializer 参数传给 `TcpSession`。

## 安装与依赖

通过 Package Manager 的 Add package from disk 选择本包 `package.json`。使用 MemoryPack 的业务 asmdef 需要引用 `MemoryPack.Core`，并安装 MemoryPack Source Generator Analyzer。
