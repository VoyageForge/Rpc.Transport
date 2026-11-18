using System;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Session
{
    /// <summary>
    /// 泛型会话：持有本端 ControllerMap（Ability），并提供强类型 SendAsync。
    /// 进程内只有一个实例（Instance）；连接由外部 Attach/Connect 注入，业务代码不接触 Socket。
    /// </summary>
    public sealed class TcpSession<TControllerMap> : TcpSessionBase
        where TControllerMap : ControllerMap, new()
    {
        /// <summary>创建一条连接的会话；每个 TcpClient 对应一个独立实例。</summary>
        public TcpSession(TcpClient client, IRpcSerializer serializer = null)
            : base(serializer ?? new NewtonsoftRpcSerializer())
        {
            Ability = new TControllerMap();
            Ability.Initialize(this);
            if (client != null)
            {
                Attach(client);
            }
        }

        /// <summary>本端的 Controller 能力集合。</summary>
        public TControllerMap Ability { get; }

        /// <inheritdoc />
        protected override async Task<SessionResponse> HandleRemoteRequestAsync(SessionRequest request,
            CancellationToken ct)
        {
            // 主路径：MethodId 查表；MethodId 为 0（旧客端）时回退名称查找。
            RpcMethodDescriptor descriptor = null;
            bool found = request.MethodId != 0
                ? Registry.TryGet(request.MethodId, out descriptor)
                : Registry.TryGet(request.ControllerName, request.MethodName, out descriptor);
            if (!found)
            {
                return new SessionResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Error = "METHOD_NOT_FOUND:" + request.ControllerName + "." + request.MethodName
                };
            }

            object requestObject = request.Payload == null || request.Payload.Length == 0
                ? null
                : Serializer.Deserialize(request.Payload, descriptor.RequestType);
            object result = await descriptor.Handler(requestObject, ct).ConfigureAwait(false);

            return new SessionResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Payload = Serializer.Serialize(result)
            };
        }
    }
}
