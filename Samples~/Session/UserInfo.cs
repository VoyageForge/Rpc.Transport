namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>用户信息响应。</summary>
    public sealed class UserInfo
    {
        /// <summary>用户 Id。</summary>
        public string Id { get; set; }

        /// <summary>显示名称。</summary>
        public string Name { get; set; }
    }
}