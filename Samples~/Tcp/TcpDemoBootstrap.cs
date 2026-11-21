using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoyageForge.Rpc.Transport.Samples.Tcp
{
    /// <summary>
    /// TCP 会话列表（UI Toolkit）：
    /// “创建链接”按钮新增一条会话；列表中右击条目弹出函数菜单（Add/有权 Add/断开/重连）。
    /// </summary>
    public sealed class TcpDemoBootstrap : MonoBehaviour
    {
        private CancellationTokenSource _cts;
        private TcpDemoSample.DemoSession _session;
        private ScrollView _list;
        private Label _status;
        private string _statusText = "未启动";

        private async void Start()
        {
            _cts = new CancellationTokenSource();
            BindUi();
            try
            {
                _session = await TcpDemoSample.StartServersAsync(_cts.Token);
                SetStatus("服务端已启动：" + TcpDemoSample.PortA + " / " + TcpDemoSample.PortB);
            }
            catch (Exception exception)
            {
                SetStatus("启动失败：" + exception.Message);
                Debug.LogException(exception);
            }
        }

        private void BindUi()
        {
            UIDocument document = GetComponent<UIDocument>();
            VisualElement root = document != null ? document.rootVisualElement : null;
            if (root == null)
            {
                Debug.LogError("[TCP Demo] 缺少 UIDocument 或 UXML 未加载。");
                return;
            }

            Button create = root.Q<Button>("create-button");
            if (create != null) create.clicked += () => Run(CreateConnectionAsync);

            _list = root.Q<ScrollView>("connection-list");
            _status = root.Q<Label>("status-label");
            SetStatus(_statusText);
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private void SetStatus(string status)
        {
            _statusText = status;
            if (_status != null) _status.text = status;
            Debug.Log("[TCP Demo] " + status);
        }

        private async void Run(Func<Task> action)
        {
            if (_session == null) return;
            try { await action(); }
            catch (Exception exception)
            {
                SetStatus("操作失败：" + exception.Message);
                Debug.LogException(exception);
            }
        }

        private async Task CreateConnectionAsync()
        {
            // 交替 admin / guest，便于演示有权与无权调用。
            string userId = _session.Connections.Count % 2 == 0 ? "admin" : "guest";
            TcpDemoSample.Connection connection = await TcpDemoSample.CreateConnectionAsync(_session, userId, _cts.Token);
            AddItem(connection);
            SetStatus("已创建：" + connection.Name + " SessionId=" + connection.Session.SessionId);
        }

        private void AddItem(TcpDemoSample.Connection connection)
        {
            VisualElement item = new VisualElement();
            item.AddToClassList("connection-item");

            Label name = new Label(connection.Name);
            name.AddToClassList("connection-name");
            item.Add(name);

            Label state = new Label("在线");
            state.AddToClassList("connection-state");
            item.Add(state);

            // 右击菜单：函数 + 断开/重连
            item.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("函数/Add(3,4)", _ => Run(() => CallAddAsync(connection, 3, 4)));
                evt.menu.AppendAction("函数/有权 Add(1,2)", _ => Run(() => CallAuthorizedAsync(connection, 1, 2)));
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("连接/断开", _ =>
                {
                    TcpDemoSample.Disconnect(_session, connection);
                    state.text = "已断开";
                    SetStatus("已断开：" + connection.Name);
                });
                evt.menu.AppendAction("连接/重连", _ => Run(async () =>
                {
                    SetStatus(await TcpDemoSample.ReconnectAsync(_session, connection, _cts.Token));
                    state.text = "在线";
                }));
            }));

            _list?.Add(item);
        }

        private async Task CallAddAsync(TcpDemoSample.Connection connection, int a, int b)
        {
            SetStatus(await TcpDemoSample.CallAdd(_session, connection, a, b));
        }

        private async Task CallAuthorizedAsync(TcpDemoSample.Connection connection, int a, int b)
        {
            SetStatus(await TcpDemoSample.CallAuthorizedAdd(_session, connection, a, b));
        }
    }
}