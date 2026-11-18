using System;
using System.Collections.Generic;

namespace VoyageForge.Rpc.Transport
{
    /// <summary>
    /// 一次 RPC 调用的输入描述。它只说明“要调用什么”，不包含 TCP、HTTP 或其他传输信息。
    /// </summary>
    /// <remarks>
    /// Console、TCP、NamedPipe、单元测试或游戏内部代码都可以创建 RpcRequest。
    /// Parameters 保存的是原始值，类型转换会延迟到 RpcExecutor 的参数绑定阶段。
    /// 例如网络 JSON 层可以把一个 JsonElement 原样放入 Parameters，再由 IArgumentConverter 转成 int。
    /// </remarks>
    public class RpcRequest
    {
        /// <summary>目标服务名。默认按 StringComparison.Ordinal 进行大小写敏感比较。</summary>
        public string ServiceName { get; set; }

        /// <summary>目标方法名。重载通过参数数量和参数可转换性进一步区分。</summary>
        public string MethodName { get; set; }

        /// <summary>
        /// 原始位置参数。这里不包含 CancellationToken；取消令牌属于本地执行控制信息，由 ExecuteAsync 注入。
        /// </summary>
        public object[] Parameters { get; set; } = Array.Empty<object>();

        /// <summary>可选请求头。认证、追踪、审计等拦截器可以读取或写入这些键值。</summary>
        public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 请求唯一编号（uint，客户端带锁自增、循环回绕；0 表示未分配）。
        /// 用于长连接响应关联与幂等去重。
        /// </summary>
        public uint RequestId { get; set; }

        /// <summary>
        /// 方法 ID（FNV-1a 64）；优先用它查表（Mirror 式），为 0 时回退 ServiceName + MethodName。
        /// </summary>
        public ulong MethodId { get; set; }
    }

    /// <summary>RPC 执行结果。业务异常由执行器转换成 Error，不应直接穿透到传输层。</summary>
    public class RpcResponse
    {
        /// <summary>成功标记。调用方应先判断它，再读取 Result 或 Error。</summary>
        public bool Success { get; set; }

        /// <summary>成功时的方法返回值；void 和无返回值 Task 对应 null。</summary>
        public object Result { get; set; }

        /// <summary>失败时的结构化错误；成功时为 null。</summary>
        public RpcError Error { get; set; }

        /// <summary>回带请求编号，用于长连接响应关联。</summary>
        public uint RequestId { get; set; }

        /// <summary>
        /// 方法 ID（FNV-1a 64）；优先用它查表（Mirror 式），为 0 时回退 ServiceName + MethodName。
        /// </summary>
        public ulong MethodId { get; set; }

        /// <summary>内部诊断信息（通常为堆栈）。默认不向外部响应暴露，避免泄露服务器路径和实现细节。</summary>
        public string Detail { get; set; }

        /// <summary>创建成功响应，同时把 Error 明确设置为 null。</summary>
        public static RpcResponse Ok(object result)
        {
            return new RpcResponse { Success = true, Result = result, Error = null };
        }

        /// <summary>创建失败响应。Code 用于程序判断，Message 用于人阅读，Detail 是可选内部诊断。</summary>
        public static RpcResponse Fail(string code, string message, string detail = null)
        {
            return new RpcResponse
            {
                Success = false,
                Result = null,
                Error = new RpcError { Code = code, Message = message },
                Detail = detail
            };
        }
    }

    /// <summary>稳定错误模型，调用方应依赖 Code 而不是解析 Message 文本。</summary>
    public sealed class RpcError
    {
        /// <summary>稳定错误码，例如 SERVICE_NOT_FOUND、ARGUMENT_BIND_ERROR 或业务异常全名。</summary>
        public string Code { get; set; }

        /// <summary>供开发者或用户查看的错误说明。</summary>
        public string Message { get; set; }
    }
}