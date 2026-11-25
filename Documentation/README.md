# TCP 帧协议 v3

```text
[SOF: 2B = 0xAA 0x55][Version: 2B BE = 3][CmdOp: 1B][Length: 4B BE][Payload]
```

当前系统消息包括 Ping、Pong、RPC Request、RPC Response 和 RPC Cancel；不包含登录握手或鉴权消息。业务请求 DTO 由 Executor Source Generator 生成，并通过 MemoryPack 序列化。

`CallId` 仅用于将响应和取消通知关联到一次正在进行的调用。Runtime 不保存已完成调用的结果，也不对 Payload 做去重；每次调用都会分配新的 CallId。不要自行复用正在使用的 CallId。

v3 与 v2 帧不兼容，连接两端需同时更新。Length 包含操作前缀与正文的字节数。以下前缀的整数均为大端；业务 DTO 使用 MemoryPack。

| CmdOp | 操作前缀与正文 |
|---|---|
| RpcRequest (0x10) | MethodId (8) + CallId (8) + 参数 DTO；零参数时正文为空 |
| RpcResponse (0x11) | CallId (8) + 状态 (1) + 结果或错误。1 表示成功，0 表示 MemoryPack 错误字符串 |
| RpcCancel (0x12) | CallId (8) |
| Ping / Pong (0x30 / 0x31) | 空 |

## 待实现

Channel 用于单向更新，例如同步坐标。计划按通道和方向分配序号，按数量或时间回报接收统计（如 50 条或 10 秒先到）。TCP 上统计的是应用消息接收情况。广播的接收端选择、转发语义与操作编码仍待确定。
