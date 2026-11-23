namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>鉴权结果：是否通过 + 权限集合 + 失败原因。</summary>
    public sealed class SessionAuthResult
    {
        /// <summary>是否通过。</summary>
        public bool Success { get; set; }

        /// <summary>通过时下发的权限集合。</summary>
        public string[] Permissions { get; set; } = System.Array.Empty<string>();

        /// <summary>失败时的错误码（如 UNAUTHENTICATED / FORBIDDEN）。</summary>
        public string Error { get; set; }

        /// <summary>失败原因。</summary>
        public string Message { get; set; }

        /// <summary>构造成功结果。</summary>
        public static SessionAuthResult Ok(params string[] permissions)
        {
            return new SessionAuthResult { Success = true, Permissions = permissions ?? System.Array.Empty<string>() };
        }

        /// <summary>构造失败结果。</summary>
        public static SessionAuthResult Fail(string error, string message)
        {
            return new SessionAuthResult { Success = false, Error = error, Message = message };
        }
    }
}