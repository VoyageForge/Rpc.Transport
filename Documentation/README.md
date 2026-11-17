# VoyageForge RPC Transport 文档

- `通讯架构.md`：解释独立通讯包的职责、接口关系、TCP 帧协议和扩展边界。
- `源码阅读指南.md`：按接口、消息模型、TCP Client、TCP Server、帧协议和测试的顺序阅读。
- `../Tests/Editor`：Edit Mode 接口契约测试。
- `../Tests/Runtime`：Play Mode 异步请求与取消令牌测试。

若本包通过本地路径或 Git 作为外部依赖安装，请在 Unity 项目的 `Packages/manifest.json` 中将 `com.voyageforge.rpc-transport` 加入 `testables` 数组，再通过 Test Runner 分别运行 EditMode 和 PlayMode。若本包直接位于项目 `Packages/` 下作为 Embedded Package，则不需要添加 `testables`。若已有 `testables`，请追加而不是覆盖现有包名。

本包不依赖 `Rpc.Executor`，也不依赖 UnityEngine。`Rpc.Executor` 通过 UPM 依赖本包，并消费这里的消息模型和传输抽象。
