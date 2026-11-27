using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VoyageForge.Rpc.Transport.Unity;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class RpcTcpUnityLifetimeTests
    {
        private const string PortKey = "VoyageForge.Rpc.Tests.ExitPlayModePort";

        private sealed class Peer : Session
        {
            public Peer() : base(null) { }
            public Peer(TcpClient socket) : this() => Attach(socket);
        }

        /// <summary>进入真实播放模式后再创建网络组件。</summary>
        [UnitySetUp]
        public IEnumerator EnterPlay()
        {
            yield return new EnterPlayMode();
            // 当前打开的示例场景可能已有业务管理器。仅在播放实例中移除，隔离测试宿主。
            foreach (var existing in UnityEngine.Object.FindObjectsOfType<RpcNetworkManager>())
                UnityEngine.Object.Destroy(existing.gameObject);
            yield return null;
        }

        /// <summary>组件禁用后对端应读到 EOF，重新启用和销毁后同一端口可再次绑定。</summary>
        [UnityTest]
        public IEnumerator DisableClosesPeerAndReenableCreatesFreshEndpoint()
        {
            var go = new GameObject("TCP lifetime test");
            var lifetime = go.AddComponent<TransportTestManager>();
            var host = lifetime.CreateServer();

            var address = host.Start(new IPEndPoint(IPAddress.Loopback, 0));
            Session accepted = null;
            host.Connected += session => accepted = session;
            using var remote = new TcpClient();
            remote.Connect(address);
            var frames = new Tcp.TcpFrameProtocol(1024);
            var stream = remote.GetStream();
            frames.WriteFrameAsync(stream, CmdOp.SessionOpen, new SessionOpenRequest { SessionKind = "game" }.Encode(), default).GetAwaiter().GetResult();
            var reply = frames.ReadFrameAsync(stream, default);
            float deadline = Time.realtimeSinceStartup + 5;
            while (accepted == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.NotNull(accepted);
            Assert.AreEqual(CmdOp.SessionReady, reply.GetAwaiter().GetResult().Value.Op);
            go.SetActive(false);
            remote.ReceiveTimeout = 2000;
            Assert.AreEqual(-1, remote.GetStream().ReadByte(), "Peer must observe EOF after disable");
            Assert.IsTrue(accepted.Closed.IsCompleted);
            go.SetActive(true);
            var next = lifetime.CreateServer();

            Assert.AreEqual(address, next.Start(address));
            UnityEngine.Object.Destroy(go);
            yield return null;
            AssertReusable(address);
        }

        /// <summary>两端已挂起、客户端准备重连时禁用组件，应永久释放会话并取消在途握手。</summary>
        [UnityTest]
        public IEnumerator 禁用时释放等待恢复的会话()
        {
            var go = new GameObject("可恢复会话生命周期测试");
            var lifetime = go.AddComponent<TransportTestManager>();
            var server = lifetime.CreateServer();

            var client = lifetime.CreateClient("game", () => new Peer());
            Session inbound = null, outbound = null;
            int resumed = 0;
            server.Connected += peer => inbound = peer;
            client.Resumed += _ => resumed++;
            var address = server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            client.Start(address, peer => outbound = peer);
            float deadline = Time.realtimeSinceStartup + 5;
            while ((inbound == null || outbound == null) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.NotNull(inbound);
            Assert.NotNull(outbound);
            outbound.Suspend();
            client.Poll();
            go.SetActive(false);
            Assert.AreEqual("CONNECTION_CLOSED", inbound.Closed.Result);
            Assert.AreEqual("CONNECTION_CLOSED", outbound.Closed.Result);
            yield return null;
            Assert.AreEqual(0, resumed);
            AssertReusable(address);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator 单例释放不会关闭独立服务端()
        {
            using var independent = new Tcp.TcpServer();
            var address = independent.Start(new IPEndPoint(IPAddress.Loopback, 0));
            var mono = new GameObject("RPC manager").AddComponent<TransportTestManager>();
            var managed = mono.CreateServer();
            var managedAddress = managed.Start(new IPEndPoint(IPAddress.Loopback, 0));
            managed.Stop();
            managed.Start(managedAddress);
            UnityEngine.Object.Destroy(mono.gameObject);
            yield return null;
            AssertReusable(managedAddress);
            Assert.Throws<ObjectDisposedException>(managed.Poll);
            using var socket = new TcpClient();
            socket.Connect(address);
            independent.Poll();
            independent.Dispose();
            AssertReusable(address);
        }

        /// <summary>将监听端口保存在编辑器状态中，跨域重载验证资源清理。</summary>
        [UnityTest]
        public IEnumerator ExitPlayModeReleasesLiveListener()
        {
            var manager = new GameObject("RPC manager").AddComponent<TransportTestManager>();
            var host = RpcNetworkManager.Instance.CreateServer();

            var address = host.Start(new IPEndPoint(IPAddress.Loopback, 0));
            SessionState.SetInt(PortKey, address.Port);
            var client = RpcNetworkManager.Instance.CreateClient("game", () => new Peer());
            client.Start(address);
            float deadline = Time.realtimeSinceStartup + 5;
            while ((client.CurrentSession == null || host.Sessions.Count == 0) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.NotNull(client.CurrentSession, "默认宿主应自动轮询并交付会话");
            var original = host.Sessions[0];
            client.CurrentSession.Suspend();
            deadline = Time.realtimeSinceStartup + 5;
            while (!client.CurrentSession.IsConnected && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(client.CurrentSession.IsConnected);
            Assert.AreSame(original, host.Sessions[0]);
            Assert.AreEqual(1, manager.Created);
            // 刻意遗漏 Stop 和 Dispose，LeavePlay 会在退出后验证端口可立即重绑。
        }

        /// <summary>退出播放后实际重绑监听端口，检查退出路径。</summary>
        [UnityTearDown]
        public IEnumerator LeavePlay()
        {
            yield return new ExitPlayMode();
            int port = SessionState.GetInt(PortKey, 0);
            SessionState.EraseInt(PortKey);
            if (port != 0) AssertReusable(new IPEndPoint(IPAddress.Loopback, port));
        }

        private static void AssertReusable(IPEndPoint address)
        {
            var listener = new TcpListener(address);
            try { listener.Start(); } finally { listener.Stop(); }
        }
    }
}
