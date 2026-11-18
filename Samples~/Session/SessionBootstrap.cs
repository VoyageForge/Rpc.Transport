using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>场景引导：把 UI 按钮绑定到 Session 调用。</summary>
    public sealed class SessionBootstrap : MonoBehaviour
    {
        private void Start()
        {
            Button button = FindObjectOfType<Button>();
            if (button != null)
            {
                button.onClick.AddListener(() => _ = RunAsync());
            }
        }

        private async Task RunAsync()
        {
            Debug.Log("[SessionSample] 按钮点击：需要先连接服务器（TcpSession.Attach）");
            await Task.CompletedTask;
        }
    }
}