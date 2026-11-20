using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoyageForge.Rpc.Transport.Samples.Tcp
{
    /// <summary>
    /// TCP 示例（UI Toolkit）：界面在 TcpDemoScene.uxml / .uss 里，本脚本只绑定按钮与状态。
    /// 需要 UIDocument 组件引用本脚本与 TcpDemoScene.uxml。
    /// </summary>
    public sealed class TcpDemoBootstrap : MonoBehaviour
    {
        private CancellationTokenSource _cts;
        private TcpDemoSample.DemoSession _session;
        private Label _status;
        private string _statusText = "未启动";

        private async void Start()
        {
            _cts = new CancellationTokenSource();
            BindUi();
            try
            {
                _session = await TcpDemoSample.StartAsync(_cts.Token);
                SetStatus("双端口已启动：" + TcpDemoSample.PortA + " / " + TcpDemoSample.PortB);
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

            Bind(root, "call-button", CallAsync);
            Bind(root, "auth-button", AuthenticateAsync);
            Bind(root, "authorized-button", CallAuthorizedAsync);
            Bind(root, "unauthorized-button", CallUnauthorizedAsync);
            Bind(root, "reconnect-button", ReconnectAsync);
            _status = root.Q<Label>("status-label");
            SetStatus(_statusText);
        }

        private void Bind(VisualElement root, string name, Func<Task> action)
        {
            Button button = root.Q<Button>(name);
            if (button != null) button.clicked += () => Run(action);
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

        private async Task CallAsync() { SetStatus(await TcpDemoSample.CallBothAsync(_session)); }
        private async Task AuthenticateAsync() { SetStatus(await TcpDemoSample.AuthenticateAsync(_session)); }
        private async Task CallAuthorizedAsync() { SetStatus(await TcpDemoSample.CallAuthorizedAsync(_session)); }
        private async Task CallUnauthorizedAsync() { SetStatus(await TcpDemoSample.CallUnauthorizedAsync(_session)); }
        private async Task ReconnectAsync() { SetStatus(await TcpDemoSample.ReconnectAsync(_session)); }
    }
}