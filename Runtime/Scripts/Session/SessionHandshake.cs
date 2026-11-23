using System.Collections.Generic;
using MemoryPack;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>会话握手结果：SessionId + 权限集合。</summary>
    [MemoryPackable]
    public sealed partial class SessionHandshake
    {
        /// <summary>服务端分配的会话 ID。</summary>
        public string SessionId { get; set; }

        /// <summary>握手时下发的权限。</summary>
        public string[] Permissions { get; set; } = System.Array.Empty<string>();
    }
}