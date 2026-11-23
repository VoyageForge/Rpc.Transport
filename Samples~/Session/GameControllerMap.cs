using VoyageForge.Rpc.Transport.Session;

namespace VoyageForge.Rpc.Transport.Samples.Session
{
    /// <summary>调用方的代理集合；属性和代理实现由 ControllerRpcGenerator 生成。</summary>
    [ControllerMap(typeof(IUserController))]
    public partial class GameControllerMap : ControllerMap
    {
    }
}
