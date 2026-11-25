using System;
using System.Net;
using System.Threading;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Client;
using VoyageForge.Rpc.Transport.Samples.BidirectionalSession.Server;
using VoyageForge.Rpc.Transport.Tcp;
using VoyageForge.Rpc.Transport.Unity;
using UnityEngine;

namespace VoyageForge.Rpc.Transport.Samples.BidirectionalSession
{
    /// <summary>将双向 Session 示例挂到场景 GameObject 上即可运行，并在 Console 输出双方结果。</summary>
    public sealed class BidirectionalSessionSampleRunner : MonoBehaviour
    {
        private RpcTcpLifetime lifetime;
        private RpcTcpEndpoint<ServerSession> server;
        private RpcTcpEndpoint<ClientSession> client;
        private CancellationTokenSource timeout;

        /// <summary>创建监听端和连接端，在真实 TCP 会话上运行登录和查询示例。</summary>
        private void OnEnable()
        {
            lifetime = GetComponent<RpcTcpLifetime>() ?? gameObject.AddComponent<RpcTcpLifetime>();
            timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                server = lifetime.Open(socket => ServerSession.Create(socket));
                client = lifetime.Open(socket => ClientSession.Create(socket));
                client.Error += Debug.LogException;
                server.Error += Debug.LogException;
                var address = server.Listen(new IPEndPoint(IPAddress.Loopback, 0));
                client.Connect(address, session => client.OnResult(SampleClient.RunAsync(session, timeout.Token),
                    results => { foreach (var result in results) Debug.Log("[BidirectionalSessionSample] " + result); }));
            }
            catch (Exception error) { OnDisable(); Debug.LogException(error); }
        }

        /// <summary>移除网络端点及回调，释放本轮示例的超时资源。</summary>
        private void OnDisable()
        {
            lifetime.Release(client);
            lifetime.Release(server);
            timeout?.Dispose();
            timeout = null;
        }
    }
}
