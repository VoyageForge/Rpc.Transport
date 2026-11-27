# 客户端 / 服务端双向 RPC

Unity Package Manager → VoyageForge RPC Transport → Samples → 导入「双向 Session 基础示例」。打开 `Scenes/BidirectionalRpcDemo.unity`，进入 Play Mode。

1. 点击「连接」，在本机临时端口建立 TCP 会话。
2. 使用预填的账号 `demo`、密码 `sample` 点击「登录」。右侧显示服务端黑板中的用户，左侧显示客户端收到的欢迎回调。
3. 点击「查询用户」读取服务端资料。也可以在登录前查询，观察服务端返回的「请先登录」错误。
4. 登录后点击「模拟断线」，待恢复后再次查询，检查服务端仍返回原用户。
5. 点击「断开」释放双方 Session。重新连接后可从空的会话状态再操作一次。

若要自动运行同一示例，可在单独场景中将 `BidirectionalSessionSampleRunner` 挂到空 GameObject，进入 Play Mode 后会在 Console 输出：

```text
[BidirectionalSessionSample] 欢迎 demo
[BidirectionalSessionSample] 当前用户：demo
```

## 按调用顺序阅读

1. **Shared/** 定义 Account 和 Notification 两份协议。两端引用同一份契约。
2. 场景中的 **UI/RpcDemoPanel.cs** 通过 RpcNetworkManager 创建两个端点，分别调用 Start 监听和连接，在 Unity 主线程响应连接和断开事件。两端各自在自己的 Session 中注册本端 Controller。
3. 客户端通过 `session.Remote<IAccountApi>().LoginAsync(...)` 发送登录请求。**Server/AccountController.cs** 检查固定示例账号 demo / sample，并将用户写入服务端这条连接的 Blackboard。
4. LoginAsync 返回前，服务端通过同一连接调用 `Session.Remote<INotificationApi>().WelcomeAsync(...)`。**Client/NotificationController.cs** 将欢迎消息写入客户端自己的 Blackboard 并返回。
5. 客户端收到登录响应，远程调用 GetProfileAsync。服务端从自己的 Blackboard 读取用户。最后客户端通过 `session.Local<NotificationController>().ReadWelcome()` 取出先前收到的通知。

Server 和 Client 各有独立 asmdef，分别生成自己的代理和 Controller 注册表。根目录的 BidirectionalSessionSample 负责在同一进程内连接两端、运行上述流程并释放资源。也可直接 await BidirectionalSessionSample.RunAsync(cancellationToken)。
