using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoyageForge.Rpc.Transport;

namespace VoyageForge.Rpc.Transport.Tests
{
    /// <summary>Play Mode 下验证异步请求回调和取消令牌在 Unity Player Loop 环境中可正常传递。</summary>
    public sealed class TransportContractsPlayModeTests
    {
        /// <summary>通过组合接口发送请求，服务端回调可等待异步工作并返回 RPC 响应。</summary>
        [UnityTest]
        public IEnumerator DatagramTransport_CompletesAsyncRequestInPlayMode()
        {
            Task<RpcResponse> scenario = RunScenarioAsync();
            yield return new WaitUntil(() => scenario.IsCompleted);

            if (scenario.IsFaulted)
                Assert.Fail(scenario.Exception == null ? "Play Mode 测试任务失败。" : scenario.Exception.ToString());
            if (scenario.IsCanceled)
                Assert.Fail("Play Mode 测试任务意外取消。");

            Assert.IsTrue(scenario.Result.Success);
            Assert.AreEqual("pong", scenario.Result.Result);
        }

        /// <summary>验证 Server 生命周期取消后传入请求处理器的 Token 会转为取消状态。</summary>
        [UnityTest]
        public IEnumerator ServerLifetimeCancellation_IsVisibleToRequestHandler()
        {
            Task<bool> scenario = RunCancellationScenarioAsync();
            yield return new WaitUntil(() => scenario.IsCompleted);

            if (scenario.IsFaulted)
                Assert.Fail(scenario.Exception == null ? "取消测试任务失败。" : scenario.Exception.ToString());
            Assert.IsTrue(scenario.Result, "服务端生命周期取消必须传递给请求处理器。");
        }

        /// <summary>执行正常的启动、请求、停止流程。</summary>
        private static async Task<RpcResponse> RunScenarioAsync()
        {
            InMemoryDatagramTransport transport = new InMemoryDatagramTransport();
            transport.OnRequestAsync += async (request, ct) =>
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return RpcResponse.Ok(request.MethodName == "Ping" ? "pong" : "unknown");
            };

            await transport.StartAsync(CancellationToken.None);
            RpcResponse response = await ((IRpcClientTransport)transport).SendAsync(new RpcRequest
            {
                ServiceName = "test.health",
                MethodName = "Ping",
                Parameters = Array.Empty<object>()
            }, CancellationToken.None);
            await transport.StopAsync(CancellationToken.None);
            return response;
        }

        /// <summary>服务端 StartAsync 收到的生命周期 Token 应传给 OnRequestAsync。</summary>
        private static async Task<bool> RunCancellationScenarioAsync()
        {
            CancellationTokenSource lifetime = new CancellationTokenSource();
            InMemoryDatagramTransport transport = new InMemoryDatagramTransport();
            bool observedCancellation = false;
            transport.OnRequestAsync += (request, ct) =>
            {
                // 此处检查的是服务端生命周期令牌，而不是客户端传入的取消令牌。
                observedCancellation = ct.IsCancellationRequested;
                return Task.FromResult(RpcResponse.Ok(null));
            };

            await transport.StartAsync(lifetime.Token);
            lifetime.Cancel();
            // 客户端请求本身不取消；服务端必须仍把已取消的服务端 Token 传给请求处理器。
            await ((IRpcClientTransport)transport).SendAsync(new RpcRequest
            {
                ServiceName = "test.health",
                MethodName = "Ping",
                Parameters = Array.Empty<object>()
            }, CancellationToken.None);
            await transport.StopAsync(CancellationToken.None);
            lifetime.Dispose();
            return observedCancellation;
        }

        /// <summary>Play Mode 测试用的最小内存传输实现。</summary>
        private sealed class InMemoryDatagramTransport : IRpcDatagramTransport
        {
            private CancellationToken _lifetimeToken;

            /// <summary>收到完整请求时由宿主订阅。</summary>
            public event Func<RpcRequest, CancellationToken, Task<RpcResponse>> OnRequestAsync;

            /// <summary>保存服务端生命周期 Token。</summary>
            public Task StartAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                _lifetimeToken = ct;
                return Task.CompletedTask;
            }

            /// <summary>清理传输状态。</summary>
            public Task StopAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                _lifetimeToken = default(CancellationToken);
                return Task.CompletedTask;
            }

            /// <summary>将客户端请求交给服务端回调，模拟一个双向数据报通道。</summary>
            public Task<RpcResponse> SendAsync(RpcRequest request, CancellationToken ct = default(CancellationToken))
            {
                ct.ThrowIfCancellationRequested();
                Func<RpcRequest, CancellationToken, Task<RpcResponse>> handler = OnRequestAsync;
                if (handler == null) throw new InvalidOperationException("未注册请求处理器。");
                return handler(request, _lifetimeToken);
            }
        }
    }
}