using System;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class HostShutdownRequest
    {
        public string Id, Actor, Authority, Channel;
        public uint SenderId;
        public DateTime RequestedUtc;
        internal HostShutdownRequest Copy() => (HostShutdownRequest)MemberwiseClone();
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _hostControlSync = new object();
        private HostShutdownRequest _shutdown;
        private bool _managerRestart;
        public HostShutdownRequest ShutdownRequest()
        { lock (_hostControlSync) return _shutdown?.Copy(); }
        public void RequestShutdown(string id, string actor, uint senderId, string authority, string channel, DateTime requestedUtc)
        {
            lock (_hostControlSync)
            {
                if (_shutdown != null) throw new InvalidOperationException("Shutdown already requested.");
                _shutdown = new HostShutdownRequest { Id = id, Actor = actor, SenderId = senderId,
                    Authority = authority, Channel = channel, RequestedUtc = requestedUtc };
            }
        }
        public void ClearShutdownRequest() { lock (_hostControlSync) _shutdown = null; }
        public bool ManagerRestartRequested() { lock (_hostControlSync) return _managerRestart; }
        public void RequestManagerRestart() { lock (_hostControlSync) _managerRestart = true; }
        public void ClearManagerRestart() { lock (_hostControlSync) _managerRestart = false; }
    }
}
