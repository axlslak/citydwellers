using System;

namespace CityDwellers.Shared
{
    public class BuddyPositionSnapshot
    {
        public string Character { get; set; }
        public int? Level { get; set; }
        public int? Index { get; set; }
        public DateTime ObservedUtc { get; set; }
        public bool InPlay { get; set; }
        public bool Dead { get; set; }
        public int? PlayfieldId { get; set; }
        public string PlayfieldName { get; set; }
        public bool PositionAvailable { get; set; }
        public float? PositionX { get; set; }
        public float? PositionY { get; set; }
        public float? PositionZ { get; set; }
        public bool HeadingAvailable { get; set; }
        public float? HeadingX { get; set; }
        public float? HeadingY { get; set; }
        public float? HeadingZ { get; set; }
        public float? HeadingW { get; set; }
        public int? Health { get; set; }
        public int? MaxHealth { get; set; }
        public int? RunSpeed { get; set; }
        public string HomeJobId { get; set; }
        public string HomeMovementMode { get; set; }
        public string HomeState { get; set; }
        public string HomeDetail { get; set; }
        public float? HomeDistance { get; set; }
        public DateTime? HomeUpdatedUtc { get; set; }
        public string NavigationTraceFile { get; set; }
        public long? NavigationTraceSequence { get; set; }
        public string LastMovementCommandAction { get; set; }
        public DateTime? LastMovementCommandUtc { get; set; }
        public float? LastMovementCommandX { get; set; }
        public float? LastMovementCommandY { get; set; }
        public float? LastMovementCommandZ { get; set; }
        public string LastMovementObservationAction { get; set; }
        public DateTime? LastMovementObservationUtc { get; set; }
        public int? LastMovementObservationDeltaTime { get; set; }
        public float? LastMovementObservationX { get; set; }
        public float? LastMovementObservationY { get; set; }
        public float? LastMovementObservationZ { get; set; }
        public string Error { get; set; }
    }

    public class BuddyHomeDirective
    {
        public string JobId { get; set; }
        public DateTime RequestedUtc { get; set; }
        public string MovementMode { get; set; }
        public bool Cancel { get; set; }
    }
}
