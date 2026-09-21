using System;

namespace CityDwellers.Shared
{
    // Manager's service lifetime is the host lifetime, independent of its AO login.
    // Child AppDomains receive a reference before loading a plugin. Calls cross the
    // AppDomain boundary in-process: no socket, database, file or polling worker.
    public sealed partial class ManagerMemory : MarshalByRefObject
    {
        private const string DomainKey = "CityDwellers.ManagerMemory";
        private static ManagerMemory _current;

        public static ManagerMemory Current
        {
            get
            {
                return _current ?? (_current = AppDomain.CurrentDomain.GetData(DomainKey) as ManagerMemory)
                    ?? throw new InvalidOperationException("Manager memory was not attached before client startup.");
            }
        }

        public static void Start()
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only ManagerHost may start the shared Manager service.");
            if (_current == null) _current = new ManagerMemory();
        }

        public static void Attach(AppDomain client) => client.SetData(DomainKey, Current);
        public override object InitializeLifetimeService() => null;
        public void DisconnectClient(string character)
        {
            SetBuddyReady(character, false);
            PublishBuddyPosition(character, null);
            ClearBankerPresence(character);
            ClearBankerOperational(character);
            ClearBankerHealth(character);
            RemoveTellSender(character);
            RelinquishTell(character);
        }
        private ManagerMemory() { }
    }
}
