using System;
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
    public abstract class RpcNetworkManager : MonoBehaviour
    {
        private readonly RpcLifetimeRegistry registry = new RpcLifetimeRegistry();

        private static RpcNetworkManager instance;
        private static bool quitting;

        /// <summary>取得场景中的具体管理器。将派生组件挂到根 GameObject 上。</summary>
        public static RpcNetworkManager Instance
        {
            get
            {
                if (!Application.isPlaying || quitting)
                    throw new InvalidOperationException("RPC 单例只在播放期间可用。");
                if (instance == null)
                    throw new InvalidOperationException("请先在场景中添加 RpcNetworkManager 的派生组件。");
                return instance;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetQuitFlag() => quitting = false;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                enabled = false;
                Destroy(this);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>按客户端请求的类型创建服务端会话，未知类型返回 null。恢复已有会话时跳过此方法。</summary>
        protected abstract Session CreateSession(string sessionKind);

        /// <summary>创建由本单例轮询和释放的服务端，调用 Start 后开始监听。</summary>
        public TcpServer CreateServer(int retentionMilliseconds = 30000) =>
            Own<TcpServer, Session>(new TcpServer(retentionMilliseconds, CreateSession));

        /// <summary>创建由本单例轮询和释放的客户端，调用 Start 后开始连接。</summary>
        public RpcClient<T> CreateClient<T>(string kind, Func<T> create, int retentionMilliseconds = 30000) where T : Session =>
            Own<RpcClient<T>, T>(new RpcClient<T>(kind, create, retentionMilliseconds));

        private TPeer Own<TPeer, T>(TPeer peer) where TPeer : RpcPeer<T> where T : Session
        {
            if (instance != this || !isActiveAndEnabled || quitting)
                throw new InvalidOperationException("RPC 单例已经停用。");
            registry.Register(peer, peer.Poll);
            peer.Released += registry.Unregister;
            return peer;
        }
        private void Update() => registry.Poll();

        /// <summary>开始一次新的组件生命周期，注册退出播放、脚本重载和编辑器退出通知。</summary>
        private void OnEnable()
        {
            registry.Enable();
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
            if (state == PlayModeStateChange.ExitingPlayMode) OnApplicationQuit();
        }
#endif
        /// <summary>应用退出时关闭所有套接字。</summary>
        private void OnApplicationQuit()
        {
            if (instance == this) quitting = true;
            Shutdown();
        }

        /// <summary>组件销毁时兜底清理。</summary>
        private void OnDestroy()
        {
            Shutdown();
            if (instance == this) instance = null;
        }

        /// <summary>组件禁用时结束本次网络生命周期。</summary>
        private void OnDisable() => Shutdown();

        /// <summary>阻止新连接、移除编辑器静态订阅，同步释放端点。多个退出通知可重复进入。</summary>
        private void Shutdown()
        {
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.quitting -= Shutdown;
#endif
            registry.Shutdown();
        }
    }
}
