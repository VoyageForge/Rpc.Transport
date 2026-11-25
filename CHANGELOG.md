# Changelog

## 0.4.2

- 将 Session、RpcControllerRegistry、RpcMethodDescriptor、RpcContractAttribute、RpcSessionAttribute、RpcApiAttribute、SessionRequest、SessionResponse、SessionCancelRequest、RpcSessionException、MethodIdHasher 等公开类型统一移动到 `VoyageForge.Rpc.Transport` 根命名空间。
- 补回并重写双向 Session Sample，移除旧 ControllerMap / TcpRpcTransport / 内置鉴权示例。
- 在 UPM Samples 列表中注册 `BidirectionalSession` 示例；该示例使用 Rpc.Executor 生成 Session 工厂和代理。

## 0.4.1

- 将传输关联字段 `RequestId` 更名为 `CallId`，明确它只用于请求/响应关联与取消。
- 移除接收端对重复 CallId 的拒绝逻辑；运行时不缓存调用结果，也不执行请求去重。
- 修正 MemoryPackSerializer 全限定名，并修正 asmdef 对 MemoryPack.Core.dll 的引用配置。

## 0.4.0

- 移除旧通用 TCP Client/Server Transport 与 Serializer 通道。
- 帧协议版本提升到 2，拒绝与旧版握手/传输协议混用。
- 统一双端 Session，支持双向 RPC。
- 移除握手鉴权、权限下发及 SessionId 管理实现。
- Session 增加实例级 Blackboard。
- 新增 RpcCancel 帧，将调用方 CancellationToken 取消通知传递到远端请求处理器。
- 移除 ControllerMap 和 TcpSession<TControllerMap>。
