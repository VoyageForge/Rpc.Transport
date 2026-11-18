# Changelog

## 0.2.0

- 版本统一为 0.2.0。
- 帧协议：固定 10 字节头（SOF + Version + MessageId + Length），`Protocol<T>` 信封。
- 长连接 TCP：心跳、鉴权、RequestId、固定间隔重连、断开 `CONNECTION_LOST`。
- 内置 `NewtonsoftRpcSerializer`。
- 新增 `TcpSession<TControllerMap>` 会话（`Rpc.Transport/Session`）：请求/响应、双向分发、`ControllerMap`、`RpcMethodRegistry`。
- 命名空间/程序集：`VoyageForge.Rpc.Transport`。

## 0.0.12

- 帧协议改为固定 10 字节头，长连接 TCP + 心跳/鉴权/RequestId/重连。