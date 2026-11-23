using MemoryPack;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>网络请求包。协议只传 RequestId、MethodId 和业务 Payload，不传 ControllerName/MethodName。</summary>
    [MemoryPackable]
    public sealed partial class SessionRequest
    {
        public long RequestId { get; set; }
        public ulong MethodId { get; set; }
        public byte[] Payload { get; set; }
    }
}
