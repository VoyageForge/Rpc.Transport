using System;
using System.Collections.Concurrent;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 服务端会话表：SessionId → 会话状态。
    /// 首次 Hello 分配 SessionId；重连 Resume 校验存在性与活跃状态（禁止顶号）。
    /// 注意：裸 SessionId 仅过渡方案，后续应补一次性 ResumeToken/加密。
    /// </summary>
    public sealed class TcpSessionRegistry
    {
        /// <summary>会话状态。</summary>
        public enum SessionState
        {
            /// <summary>已断开（可重连）。</summary>
            Disconnected = 0,
            /// <summary>活跃连接（禁止顶号）。</summary>
            Active = 1
        }

        private sealed class Entry
        {
            public string UserId;
            public string[] Permissions;
            public SessionState State;
        }

        private readonly ConcurrentDictionary<string, Entry> _sessions =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>进程内自增序号：避免 Mono 的 Guid.NewGuid 快速调用重复。</summary>
        private int _counter;

        /// <summary>创建会话：分配 SessionId 并返回权限快照。</summary>
        public SessionHandshake Create(string userId, string[] permissions)
        {
            string sessionId = Guid.NewGuid().ToString("N") + "-" + System.Threading.Interlocked.Increment(ref _counter);
            _sessions[sessionId] = new Entry
            {
                UserId = userId,
                Permissions = permissions ?? Array.Empty<string>(),
                State = SessionState.Active
            };

            return new SessionHandshake { SessionId = sessionId, Permissions = permissions ?? Array.Empty<string>() };
        }

        /// <summary>重连：存在且当前未活跃时恢复；活跃则拒绝（禁止顶号）。</summary>
        public bool TryResume(string sessionId, out SessionHandshake handshake, out string error)
        {
            handshake = null;
            error = null;
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out Entry entry))
            {
                error = "SESSION_INVALID";
                return false;
            }

            if (entry.State == SessionState.Active)
            {
                error = "SESSION_ALREADY_ACTIVE";
                return false;
            }

            entry.State = SessionState.Active;
            handshake = new SessionHandshake { SessionId = sessionId, Permissions = entry.Permissions };
            return true;
        }

        /// <summary>连接断开时标记会话为可重连。</summary>
        public void MarkDisconnected(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (_sessions.TryGetValue(sessionId, out Entry entry)) entry.State = SessionState.Disconnected;
        }

        /// <summary>服务端当前记录的 SessionId（用于示例/诊断）。</summary>
        public string[] GetSessionIds()
        {
            System.Collections.Generic.List<string> ids = new System.Collections.Generic.List<string>();
            foreach (System.Collections.Generic.KeyValuePair<string, Entry> pair in _sessions)
            {
                ids.Add(pair.Key + "|" + pair.Value.UserId + "|" + pair.Value.State);
            }

            return ids.ToArray();
        }

        /// <summary>移除会话（过期/登出）。</summary>
        public bool Remove(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && _sessions.TryRemove(sessionId, out _);
        }
    }
}