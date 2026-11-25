using System;
using System.IO;
using System.Threading.Tasks;
using MemoryPack;

namespace VoyageForge.Rpc.Transport
{
    public readonly struct Response<T>
    {
        public readonly ulong MethodId;
        public readonly long CallId;
        public readonly T Data;
        public readonly string Error;

        internal Response(ulong methodId, long callId, T data, string error)
        {
            MethodId = methodId;
            CallId = callId;
            Data = data;
            Error = error;
        }
    }

    internal interface IPendingCall
    {
        void Complete(byte[] frame);
        void Fail(string reason);
    }

    internal sealed class PendingCall<T> : IPendingCall
    {
        internal readonly TaskCompletionSource<Response<T>> Source =
            new TaskCompletionSource<Response<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ulong _method;
        private readonly long _id;

        internal PendingCall(ulong method, long id) { _method = method; _id = id; }
        public void Fail(string reason) => Source.TrySetException(new RpcSessionException(reason));
        public void Complete(byte[] frame)
        {
            try
            {
                if (frame[8] == 0)
                {
                    string error = MemoryPackSerializer.Deserialize<string>(frame.AsSpan(9));
                    Source.TrySetResult(new Response<T>(_method, _id, default, error ?? "RPC_FAILED"));
                    return;
                }
                if (frame[8] != 1) throw new InvalidDataException("Unknown response status.");
                T value = typeof(T) == typeof(RpcUnit) ? default : MemoryPackSerializer.Deserialize<T>(frame.AsSpan(9));
                Source.TrySetResult(new Response<T>(_method, _id, value, null));
            }
            catch (Exception error) { Source.TrySetException(error); }
        }
    }
}
