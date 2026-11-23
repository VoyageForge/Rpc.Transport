using MemoryPack;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>网络响应包。</summary>
    [MemoryPackable]
    public sealed partial class SessionResponse
    {
        public long RequestId { get; set; }
        public bool Success { get; set; }
        public byte[] Payload { get; set; }
        public string Error { get; set; }
    }
}
