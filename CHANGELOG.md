# Changelog

## 0.3.0

- 重构双向 Session：网络请求只传 RequestId、MethodId 和 Payload，不再发送 ControllerName / MethodName。
- 新增应用级 RpcControllerRegistry；每条 Session 不再创建独立方法注册表。
- ControllerMap 改为生成式代理初始化，不再通过反射查找 InitializeControllers。
- Session 控制消息和业务 Payload 固定使用 MemoryPack；移除 Session 对 Newtonsoft IRpcSerializer 的依赖。
- 删除可替换的 IRpcPayloadSerializer、JSON/Mirror Payload 实现和运行时类型反射序列化路径；RpcPayload 固定调用 MemoryPack。
- MethodId 升级为 v2：ControllerName + MethodName + 按顺序排列的参数类型规范名。
- 注册冲突改为显式异常，Registry Seal 后只读。
- Controller Dispatcher 每次调用通过 Catalog factory 创建 Controller 实例，避免跨请求共享实例状态。

## 0.2.0

- 版本统一为 0.2.0。
- 帧协议：固定 10 字节头（SOF + Version + MessageId + Length），`Protocol<T>` 信封。
- 长连接 TCP：心跳、鉴权、RequestId、固定间隔重连、断开 `CONNECTION_LOST`。
- 内置 `NewtonsoftRpcSerializer`。
- 新增 `TcpSession<TControllerMap>` 会话（`Rpc.Transport/Session`）：请求/响应、双向分发、`ControllerMap`、`RpcMethodRegistry`。
- 命名空间/程序集：`VoyageForge.Rpc.Transport`。

## 0.0.12

- 帧协议改为固定 10 字节头，长连接 TCP + 心跳/鉴权/RequestId/重连。