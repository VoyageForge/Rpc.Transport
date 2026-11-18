using UnityEngine;
using UnityEngine.UI;
using VoyageForge.Rpc.Transport.Samples.Tcp;

namespace VoyageForge.Rpc.Transport.Samples.Tcp
{
    /// <summary>TCP Echo 示例场景引导：按钮触发一次握手/回显。</summary>
    public sealed class TcpEchoBootstrap : MonoBehaviour
    {
        private void Start()
        {
            Button button = FindObjectOfType<Button>();
            if (button != null) button.onClick.AddListener(Run);
        }

        private async void Run()
        {
            using (TcpEchoSample sample = new TcpEchoSample())
            {
                await sample.StartAsync(System.Threading.CancellationToken.None);
                string echo = await sample.EchoAsync("hello", System.Threading.CancellationToken.None);
                Debug.Log("[TcpEchoSample] " + echo);
            }
        }
    }
}