using System;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class CloakState
    {
        public int Status;
        public int ShieldTimerInSeconds;
        public DateTime? LastObservedUtc, LastChangedUtc, CanRaiseAtUtc;
        public bool RaiseDueLogged, RaiseTimeIsProvisional;
        public string ObservationSource;
        internal CloakState Copy() => (CloakState)MemberwiseClone();
    }
    [Serializable]
    public sealed class CloakEvent
    {
        public string Id;
        public DateTime OccurredUtc;
        public int PreviousStatus, NewStatus;
        public int? ShieldTimerInSeconds;
        public DateTime? CanRaiseAtUtc;
        public string EventType, Source, Actor, ChannelName, RawMessage;
        internal CloakEvent Copy() => (CloakEvent)MemberwiseClone();
    }
}
