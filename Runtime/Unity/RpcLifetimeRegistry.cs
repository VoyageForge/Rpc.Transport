using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoyageForge.Rpc.Transport.Unity
{
    /// <summary>持有单例创建的端点，宿主关闭时统一释放。</summary>
    internal sealed class RpcLifetimeRegistry
    {
        private readonly List<Action> polls = new List<Action>();
        private readonly List<IDisposable> endpoints = new List<IDisposable>();
        private bool stopping;

        /// <summary>登记宿主负责驱动的端点。</summary>
        public void Register(IDisposable endpoint, Action poll)
        {
            if (stopping) throw new InvalidOperationException("TCP lifetime is inactive.");
            endpoints.Add(endpoint);
            polls.Add(poll);
        }

        /// <summary>端点主动释放时同步移除轮询。</summary>
        public void Unregister(IDisposable endpoint)
        {
            int index = endpoints.IndexOf(endpoint);
            if (index < 0) return;
            endpoints.RemoveAt(index);
            polls.RemoveAt(index);
        }

        /// <summary>在主线程轮询已注册的端点，网络完成回调可直接操作场景和 UI。</summary>
        internal void Poll()
        {
            // 事件处理器可能释放端点，删除后要重新检查当前位置，避免跳过被移到这里的端点。
            for (int i = 0; i < polls.Count;)
            {
                var poll = polls[i];
                try { poll(); }
                catch (Exception error) { Debug.LogException(error); }
                if (i < polls.Count && polls[i] == poll) i++;
            }
        }

        internal void Enable() => stopping = false;

        internal void Shutdown()
        {
            stopping = true;
            var owned = endpoints.ToArray();
            endpoints.Clear();
            polls.Clear();
            foreach (var endpoint in owned)
            {
                try { endpoint.Dispose(); }
                catch (Exception error) { Debug.LogException(error); }
            }
        }
    }
}
