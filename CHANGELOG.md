# Changelog

## 0.0.12

- 帧协议改为固定 10 字节头：`SOF(0xAA55) + Version + MessageId + Length`，长度定界、不再使用 EOF。
- 消息统一使用 `Protocol<T>{Version, Data}` 信封；payload 由 `IRpcSerializer` 序列化。
- TCP 客户端改为**长连接**：请求按 `RequestId` 关联、断开后固定间隔重连、`RequestId` 带锁自增回绕。
- 新增心跳 `Ping/Pong`（全双工，客户端默认 15s/45s；服务端可 `enableServerPing` 主动 ping）。
- 新增握手鉴权 `Auth/AuthResult` 与 `QueryToken` 抽查；一次错误即断开。
- 断开时在途请求统一返回 `CONNECTION_LOST`。
- 服务端实现 `IDisposable`；`StopAsync` 释放监听/连接/任务，端口可复用。
- 新增内置 `NewtonsoftRpcSerializer`（Unity 官方 Newtonsoft.Json），客户端/服务端默认使用。
- 新增 `MessageId` 强类型、`RpcMessageAttribute`、`RpcMessageTypeRegistry`、`RpcRequest<T>`/`RpcResponse<T>`。
- 测试拆分为 EditMode（帧协议内存流）与 PlayMode（真实 Loopback TCP），覆盖长连接、关闭、端口复用与协议错误。

## 0.0.11

- 初始版本：消息契约、序列化契约、TCP 长度前缀帧、客户端/服务端参考实现。