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
        private bool _creating;

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
            if (_creating) return;
            _creating = true;
            try
            {
                // 交替 admin / guest，便于演示有权与无权调用。
                string userId = _session.Connections.Count % 2 == 0 ? "admin" : "guest";
                TcpDemoSample.Connection connection = await TcpDemoSample.CreateConnectionAsync(_session, userId, _cts.Token);
                AddItem(connection);
                SetStatus("已创建：" + connection.Name + " SessionId=" + connection.Session.SessionId);
            }
            finally
            {
                _creating = false;
            }
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

            // 右击菜单：GenericDropdownMenu 在运行时手动弹出（ScrollView 内 ContextualMenuManipulator 会被吞）。
            item.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 1)
                {
                    return;
                }

                GenericDropdownMenu menu = new GenericDropdownMenu();
                menu.AddItem("函数/Add(3,4)", false, () => Run(() => CallAddAsync(connection, 3, 4)));
                menu.AddItem("函数/有权 Add(1,2)", false, () => Run(() => CallAuthorizedAsync(connection, 1, 2)));
                menu.AddSeparator("");
                menu.AddItem("连接/断开", false, () =>
                {
                    TcpDemoSample.Disconnect(_session, connection);
                    state.text = "已断开";
                    SetStatus("已断开：" + connection.Name);
                });
                menu.AddItem("连接/重连", false, () => Run(async () =>
                {
                    SetStatus(await TcpDemoSample.ReconnectAsync(_session, connection, _cts.Token));
                    state.text = "在线";
                }));

                // 弹层出现在鼠标位置；深色底 + 白字（默认主题的深色文字在深色场景看不清）。
                menu.DropDown(new Rect(evt.mousePosition, Vector2.zero), item);
                menu.contentContainer.style.backgroundColor = new Color(0.13f, 0.17f, 0.24f, 1f);
                menu.contentContainer.style.minWidth = 220f;
                menu.contentContainer.style.paddingTop = 4f;
                menu.contentContainer.style.paddingBottom = 4f;
                menu.contentContainer.Query<TextElement>().ForEach(label =>
                {
                    label.style.color = new Color(0.93f, 0.95f, 1f, 1f);
                    label.style.fontSize = 18f;
                    label.style.paddingTop = 4f;
                    label.style.paddingBottom = 4f;
                    label.style.paddingLeft = 8f;
                    label.style.paddingRight = 8f;
                });
                evt.StopPropagation();
            });

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