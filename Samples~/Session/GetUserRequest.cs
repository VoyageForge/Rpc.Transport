using MemoryPack;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>获取用户的请求。</summary>
    [MemoryPackable]
    public sealed partial class GetUserRequest
    {
        /// <summary>用户 Id。</summary>
        public string Id { get; set; }
    }
}