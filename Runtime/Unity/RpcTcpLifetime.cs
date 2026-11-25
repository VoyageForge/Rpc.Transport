using System;
using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine;
using VoyageForge.Rpc.Transport.Tcp;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VoyageForge.Rpc.Transport.Unity
{
    /// <summary>管理组件本次启用期间的 TCP 端点，在 Unity 主线程派发网络事件。</summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class RpcTcpLifetime : MonoBehaviour
    {
        private readonly List<Action> polls = new List<Action>();
        private readonly List<IDisposable> endpoints = new List<IDisposable>();
        private bool stopping;

        /// <summary>创建由此组件驱动和清理的端点；每次重新启用后可以建立新的会话。</summary>
        public RpcTcpEndpoint<T> Open<T>(Func<TcpClient, T> attach) where T : Session
        {
            if (stopping || !isActiveAndEnabled) throw new InvalidOperationException("TCP lifetime is inactive.");
            var endpoint = new RpcTcpEndpoint<T>(attach);
            endpoints.Add(endpoint);
            polls.Add(endpoint.Poll);
            return endpoint;
        }

        /// <summary>提前移除并关闭本组件创建的端点，可在事件回调或业务组件禁用时调用。</summary>
        public void Release<T>(RpcTcpEndpoint<T> endpoint) where T : Session
        {
            if (endpoint == null || !endpoints.Remove(endpoint)) return;
            polls.Remove(endpoint.Poll);
            endpoint.Dispose();
        }

        /// <summary>在主线程轮询已注册的端点，网络完成回调可直接操作场景和 UI。</summary>
        private void Update()
        {
            // 事件处理器可能释放端点，删除后要重新检查当前位置，避免跳过被移到这里的端点。
            for (int i = 0; i < polls.Count;)
            {
                var poll = polls[i];
                poll();
                if (i < polls.Count && polls[i] == poll) i++;
            }
        }

        /// <summary>开始一次新的组件生命周期，注册退出播放、脚本重载和编辑器退出通知。</summary>
        private void OnEnable()
        {
            stopping = false;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.quitting += Shutdown;
#endif
        }

#if UNITY_EDITOR
        /// <summary>在编辑器离开播放模式时立即释放端口。</summary>
        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) Shutdown();
        }
#endif
        /// <summary>应用退出时关闭所有套接字。</summary>
        private void OnApplicationQuit() => Shutdown();

        /// <summary>组件销毁时兜底清理。</summary>
        private void OnDestroy() => Shutdown();

        /// <summary>组件禁用时结束本次网络生命周期。</summary>
        private void OnDisable() => Shutdown();

        /// <summary>阻止新连接、移除编辑器静态订阅，同步释放端点。多个退出通知可重复进入。</summary>
        private void Shutdown()
        {
            stopping = true;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.quitting -= Shutdown;
#endif
            foreach (var endpoint in endpoints) endpoint.Dispose();
            endpoints.Clear();
            polls.Clear();
        }
    }
}