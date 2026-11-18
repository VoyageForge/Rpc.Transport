using System;
using System.Text;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// Controller 方法 ID 计算器（FNV-1a 64），与 CodeGen 的 key 规范保持一致：
    /// v1 + U+001F + ControllerName + U+001F + MethodName + U+001F + 请求类型全名。
    /// 一旦发布，key 规范即协议的一部分，不可随意修改。
    /// </summary>
    public sealed class MethodIdHasher
    {
        private const string KeyVersion = "v1";
        private const char UnitSeparator = '\u001F';
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        /// <summary>计算方法 ID；参数类型必须使用完整身份名（Type.FullName）。</summary>
        public ulong Compute(string controllerName, string methodName, Type requestType)
        {
            if (string.IsNullOrWhiteSpace(controllerName)) throw new ArgumentException("ControllerName 不能为空。", nameof(controllerName));
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("MethodName 不能为空。", nameof(methodName));

            StringBuilder key = new StringBuilder();
            key.Append(KeyVersion).Append(UnitSeparator)
                .Append(controllerName).Append(UnitSeparator)
                .Append(methodName).Append(UnitSeparator)
                .Append(requestType != null ? requestType.FullName ?? requestType.Name : string.Empty);

            byte[] bytes = Encoding.UTF8.GetBytes(key.ToString());
            ulong hash = OffsetBasis;
            for (int index = 0; index < bytes.Length; index++)
            {
                hash ^= bytes[index];
                hash *= Prime;
            }

            return hash;
        }
    }
}