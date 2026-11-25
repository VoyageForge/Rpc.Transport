using System;
using System.Collections.Generic;
using System.Text;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// RPC MethodId v2：FNV-1a 64。
    /// Key = v2 + U+001F + ControllerName + U+001F + MethodName + U+001F +
    /// 参数类型规范名（按声明顺序，以 U+001E 分隔）。
    /// 参数类型名由 Source Generator 使用 Roslyn FullyQualifiedFormat 生成，并忽略可空引用类型注解。
    /// 返回类型不参与 MethodId，但调用方契约与本地 Controller 必须保持一致。
    /// </summary>
    public static class MethodIdHasher
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        /// <summary>按协议约定计算 MethodId。</summary>
        /// <param name="controllerName">RpcContract / RpcController 声明的协议名称。</param>
        /// <param name="methodName">协议方法名称。</param>
        /// <param name="parameterTypeNames">业务参数的规范类型名，按声明顺序传入，不包含 CancellationToken。</param>
        /// <returns>FNV-1a 64 位 MethodId。</returns>
        public static ulong Compute(string controllerName, string methodName, IReadOnlyList<string> parameterTypeNames)
        {
            if (string.IsNullOrWhiteSpace(controllerName)) throw new ArgumentException("ControllerName 不能为空。", nameof(controllerName));
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("MethodName 不能为空。", nameof(methodName));

            StringBuilder key = new StringBuilder("v2\u001F");
            key.Append(controllerName).Append('\u001F').Append(methodName).Append('\u001F');
            if (parameterTypeNames != null)
            {
                for (int i = 0; i < parameterTypeNames.Count; i++)
                {
                    if (i > 0) key.Append('\u001E');
                    key.Append(parameterTypeNames[i] ?? string.Empty);
                }
            }

            byte[] bytes = Encoding.UTF8.GetBytes(key.ToString());
            ulong hash = OffsetBasis;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash = unchecked(hash * Prime);
            }
            return hash;
        }
    }
}
