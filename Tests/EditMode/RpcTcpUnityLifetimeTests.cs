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
            public Peer(TcpClient socket) : base(null) => Attach(socket);
        }

        /// <summary>进入真实播放模式后再创建网络组件。</summary>
        [UnitySetUp]
        public IEnumerator EnterPlay() { yield return new EnterPlayMode(); }

        /// <summary>组件禁用后对端应读到 EOF，重新启用和销毁后同一端口可再次绑定。</summary>
        [UnityTest]
        public IEnumerator DisableClosesPeerAndReenableCreatesFreshEndpoint()
        {
            var go = new GameObject("TCP lifetime test");
            var lifetime = go.AddComponent<RpcTcpLifetime>();
            var host = lifetime.Open(socket => new Peer(socket));
            var address = host.Listen(new IPEndPoint(IPAddress.Loopback, 0));
            Peer accepted = null;
            host.Connected += session => accepted = session;
            using var remote = new TcpClient();
            remote.Connect(address);
            float deadline = Time.realtimeSinceStartup + 5;
            while (accepted == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.NotNull(accepted);
            go.SetActive(false);
            remote.ReceiveTimeout = 2000;
            Assert.AreEqual(-1, remote.GetStream().ReadByte(), "Peer must observe EOF after disable");
            Assert.IsTrue(accepted.Closed.IsCompleted);
            go.SetActive(true);
            var next = lifetime.Open(socket => new Peer(socket));
            Assert.AreEqual(address, next.Listen(address));
            UnityEngine.Object.Destroy(go);
            yield return null;
            AssertReusable(address);
        }

        /// <summary>将监听端口保存在编辑器状态中，跨域重载验证资源清理。</summary>
        [UnityTest]
        public IEnumerator ExitPlayModeReleasesLiveListener()
        {
            var lifetime = new GameObject("Exit Play Mode TCP test").AddComponent<RpcTcpLifetime>();
            var host = lifetime.Open(socket => new Peer(socket));
            var address = host.Listen(new IPEndPoint(IPAddress.Loopback, 0));
            SessionState.SetInt(PortKey, address.Port);
            yield return null;
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
