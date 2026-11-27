using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using VoyageForge.Rpc.Transport.Tcp;
using VoyageForge.Rpc.Transport.Unity;
using UnityEngine;
using UnityEngine.UIElements;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Client;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Server;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Shared;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class RpcDemoPanel : MonoBehaviour
    {
        private VisualElement root;
        private ClientSession client;
        private Session server;
        private RpcClient<ClientSession> outgoing;
        private TcpServer incoming;
        private CancellationTokenSource request;
        private readonly Queue<string> entries = new Queue<string>();
        private bool busy;

        /// <summary>绑定界面按钮并取得场景中的网络生命周期组件。</summary>
        private void OnEnable()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            entries.Clear();
            root.Q<Button>("connect").clicked += Connect;
            root.Q<Button>("disconnect").clicked += Disconnect;
            root.Q<Button>("interrupt").clicked += Interrupt;
            root.Q<Button>("login").clicked += Login;
            root.Q<Button>("profile").clicked += Profile;
            RefreshControls();
        }

        /// <summary>解除按钮订阅并释放连接，避免关闭后的回调访问旧界面。</summary>
        private void OnDisable()
        {
            root.Q<Button>("connect").clicked -= Connect;
            root.Q<Button>("disconnect").clicked -= Disconnect;
            root.Q<Button>("interrupt").clicked -= Interrupt;
            root.Q<Button>("login").clicked -= Login;
            root.Q<Button>("profile").clicked -= Profile;
            ReleaseSessions();
            busy = false;
        }

        /// <summary>启动本机服务端并连接其实际端口，分别接收两端的会话。</summary>
        private void Connect()
        {
            if (outgoing != null) return;
            busy = true;
            try
            {
                incoming = RpcNetworkManager.Instance.CreateServer();

                outgoing = RpcNetworkManager.Instance.CreateClient("game", () => ClientSession.Create());
                incoming.Connected += session =>
                {
                    server = session;
                    Ready();
                };
                outgoing.Suspended += _ =>
                {
                    Log("连接中断，正在恢复原会话");
                    RefreshControls();
                };
                outgoing.Resumed += _ =>
                {
                    Log("已恢复原会话");
                    RefreshControls();
                };
                outgoing.Disconnected += (_, reason) =>
                {
                    Disconnect();
                    Log(reason);
                };
                incoming.Error += error => Log(error.Message);
                outgoing.Error += ConnectionFailed;
                var address = incoming.Start(new IPEndPoint(IPAddress.Loopback, 0));
                Set("endpoint", address + " · TCP");
                outgoing.Start(address.Address.ToString(), address.Port, completed: session =>
                {
                    client = session;
                    Ready();
                });
            }
            catch (Exception error)
            {
                ConnectionFailed(error);
            }

            RefreshControls();
        }

        /// <summary>关闭当前 TCP 套接字，验证重新接入后仍可查询原黑板中的用户。</summary>
        private void Interrupt() => client?.Suspend();

        /// <summary>两端均完成绑定后才允许登录和查询。</summary>
        private void Ready()
        {
            if (client == null || server == null) return;
            Log("客户端 ↔ 服务端：已建立连接");
            busy = false;
            RefreshControls();
        }

        /// <summary>释放连接资源并显示连接异常。</summary>
        private void ConnectionFailed(Exception error)
        {
            Disconnect();
            Log(error.Message);
        }

        /// <summary>进入请求状态，为当前操作设置独立的十秒超时。</summary>
        private CancellationToken BeginRequest()
        {
            busy = true;
            RefreshControls();
            request = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return request.Token;
        }

        /// <summary>结束当前请求并恢复界面操作。</summary>
        private void EndRequest()
        {
            request?.Dispose();
            request = null;
            busy = false;
            RefreshControls();
        }

        /// <summary>显示 RPC 失败原因，允许再次操作。</summary>
        private void Failed(Exception error)
        {
            EndRequest();
            Log(error.Message);
        }

        /// <summary>向服务端登录，在主线程完成回调中显示双向调用结果。</summary>
        /// <summary>保留最近十条示例日志并刷新日志区域。</summary>
        private void Login()
        {
            var user = root.Q<TextField>("user").value;
            var password = root.Q<TextField>("password").value;
            try
            {
                var call = client.Remote<IAccountApi>().LoginAsync(user, password, BeginRequest());
                outgoing.OnResult(call, () =>
                {
                    Set("notice", client.Local<NotificationController>().ReadWelcome());
                    Set("identity", (string)server.Blackboard["User"]);
                    Log("服务端 → 客户端：WelcomeAsync 回调完成，登录成功");
                    EndRequest();
                }, Failed);
            }
            catch (Exception error)
            {
                Failed(error);
            }
        }

        /// <summary>发起强类型查询，在完成回调中显示结果。</summary>
        private void Profile()
        {
            try
            {
                var call = client.Remote<IAccountApi>().GetProfileAsync(BeginRequest());
                outgoing.OnResult(call, user =>
                {
                    Set("result", user);
                    Log("服务端 → 客户端：返回用户 " + user);
                    EndRequest();
                }, Failed);
            }
            catch (Exception error)
            {
                Failed(error);
            }
        }

        /// <summary>关闭本次连接并清空与旧 Session 相关的界面状态。</summary>
        private void Disconnect()
        {
            ReleaseSessions();
            Set("endpoint", "本机 TCP · 等待连接");
            Set("identity", "—");
            Set("result", "—");
            Set("notice", "尚未收到欢迎消息");
            Log("本次会话已关闭");
            RefreshControls();
        }

        /// <summary>释放两端及其待派发回调，清理当前请求资源。</summary>
        private void ReleaseSessions()
        {
            outgoing?.Dispose();
            incoming?.Dispose();
            outgoing = null;
            incoming = null;
            request?.Dispose();
            request = null;
            busy = false;
            client = null;
            server = null;
        }

        /// <summary>按连接状态和请求状态设置按钮可用性。</summary>
        private void RefreshControls()
        {
            bool connected = client?.IsConnected == true;
            Set("status", client?.IsSuspended == true ? "重连中" : busy ? "处理中" : connected ? "已连接" : "未连接");
            root.Q<Button>("connect").SetEnabled(outgoing == null && !busy);
            root.Q<Button>("disconnect").SetEnabled(outgoing != null);
            foreach (var name in new[] { "login", "profile", "interrupt" })
                root.Q<Button>(name).SetEnabled(!busy && connected);
        }

        /// <summary>更新指定名称的界面标签。</summary>
        private void Set(string name, string value) => root.Q<Label>(name).text = value;

        private void Log(string entry)
        {
            if (entries.Count == 10) entries.Dequeue();
            entries.Enqueue(entry);
            Set("log", string.Join("\n", entries));
        }
    }
}