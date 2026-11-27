using VoyageForge.Rpc.Transport.Unity;

namespace VoyageForge.Rpc.Transport.Tests
{
    public sealed class TransportTestManager : RpcNetworkManager
    {
        public int Created { get; private set; }

        protected override Session CreateSession(string kind)
        {
            if (kind != "game") return null;
            Created++;
            return new TestSession();
        }

        private sealed class TestSession : Session { public TestSession() : base(null) { } }
    }
}
