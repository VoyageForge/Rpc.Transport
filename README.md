# VoyageForge RPC Transport

独立的 RPC 通讯 UPM Package（`com.voyageforge.rpc-transport`），目标 Unity 2022.3。

## 职责

- 消息模型：`RpcRequest` / `RpcResponse` / `RpcError` / `Protocol<T>` / `RpcRequest<T>` / `RpcResponse<T>`；
- 消息编号：`MessageId` 强类型 + `RpcMessageAttribute` + `RpcMessageTypeRegistry`；
- 序列化契约 `IRpcSerializer`，内置 `NewtonsoftRpcSerializer`（Unity 官方 Newtonsoft.Json）；
- 客户端/服务端/数据报能力：`IRpcClientTransport`、`IRpcServerTransport`、`IRpcDatagramTransport`；
- TCP 长连接实现：固定头帧、心跳、鉴权、RequestId、重连、断开固定错误；
- 不引用 `Rpc.Executor`，不承担服务注册/参数绑定/业务调用。

## 帧协议

```
[SOF: 2B = 0xAA 0x55][Version: 2B BE][TypeId: 2B BE][Length: 4B BE][Payload]
```

- 长度定界，不使用 EOF；`SOF` 仅用于协议识别/错位检测；
- `Version` 固定为 `1`，不匹配直接拒绝；
- `TypeId` 为系统消息编号：`Ping=1`、`Pong=2`、`Auth=3`、`AuthResult=4`、`QueryToken=5`、`Token=6`、`RpcRequest=10`、`RpcResponse=11`；
- `Payload` 是 `IRpcSerializer` 序列化的 `Protocol<T>`（JSON 时形如 `{"Version":"1.0","Data":{...}}`）。

## 长连接能力

- 客户端首次 `SendAsync` 建连，后续复用；请求按 `RequestId`（`uint`，带锁自增、回绕）关联响应；
- 心跳：独立 `Ping/Pong` 帧，全双工；默认客户端 15s 发送、45s 超时；服务端可 `enableServerPing` 主动 ping；
- 鉴权：握手 `Auth` → `AuthResult`；服务端可 `QueryToken` 抽查，一次错误即断开；
- 重连：客户端断开后固定间隔（默认 3s）重连；
- 断开时在途请求统一返回 `CONNECTION_LOST`；
- 服务端 `IDisposable`（`Dispose` → `StopAsync`），`StopAsync` 释放监听/连接/任务，端口可复用。

事件：

- 客户端：`OnConnected`、`OnDisconnected(Exception)`、`OnTransportError(Exception)`；
- 服务端：`OnRequestAsync`、`OnTransportError`、`OnClientConnected(TcpClient)`、`OnClientDisconnected(TcpClient)`。

## 内置序列化

`VoyageForge.Rpc.Transport.NewtonsoftRpcSerializer` 使用 Unity 官方 `com.unity.nuget.newtonsoft-json`：

- 直接写 UTF-8 `MemoryStream`，避免中间字符串；
- 不启用 `TypeNameHandling`；
- `Deserialize` 会把 `JValue` 标量还原成 CLR 值，便于 `JsonArgumentConverter` 绑定基础类型。

客户端/服务端构造时 `serializer` 参数可省略，默认使用内置 Newtonsoft 序列化器。

## 源码布局

- `Runtime/Scripts/Abstractions`：消息、编号、信封、契约、注册表；
- `Runtime/Scripts/Transports/Tcp`：`TcpFrameProtocol`（实例分帧器）、`TcpRpcClientTransport`、`TcpRpcServerTransport`；
- `Runtime/Scripts/Serializers`：`NewtonsoftRpcSerializer`；
- `Tests/Editor`：帧协议内存流测试（EditMode）；
- `Tests/Runtime`：真实 Loopback TCP 测试（PlayMode）。

## 测试

- EditMode：固定头帧编解码（往返、空流、非法 SOF/版本/长度、超长）；
- PlayMode：长连接复用、并发客户端、断开事件、客户端 Dispose/服务端 Stop 的在途请求 `CONNECTION_LOST`、Dispose 后发送、token 错误握手失败、端口复用重启、截断帧/非法帧长/非法 payload、处理器异常/空响应、取消、大字符串往返。

Unity 2022.3 的 Test Framework 1.1.33 不支持 `async Task` 作为测试入口：

- EditMode 用例使用 `[UnityTest] IEnumerator`，内部同步等待内存流 Task；
- PlayMode 用例使用 `[UnityTest] IEnumerator` + 真实 Socket。

`testables` 中启用本包：

```json
{
  "testables": [
    "com.voyageforge.rpc-transport"
  ]
}
```

## 安装

通过 Package Manager 的 Add package from disk 选择本包 `package.json`；`Rpc.Executor` 的 `package.json` 已声明对本包的依赖。
