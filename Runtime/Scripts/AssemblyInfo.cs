using System.Runtime.CompilerServices;

// 测试程序集需要访问 internal 的 TcpFrameProtocol 做内存流编解码测试。
[assembly: InternalsVisibleTo("VoyageForge.Rpc.Transport.Editor.Tests")]
[assembly: InternalsVisibleTo("VoyageForge.Rpc.Transport.PlayMode.Tests")]