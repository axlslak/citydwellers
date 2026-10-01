using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class NearbyPlayerObservation
    {
        public int CharacterId;
        public string Name;
        public int Level;
        public int Profession;
        public string Observer;
        public DateTime ObservedUtc;
        // AOSharp's visible effects, not proof of a complete remote NCU inspection.
        // An empty array must not be used to infer that the player needs buffs.
        public int[] VisibleNanoIds = new int[0];
        public bool NcuComplete => false;

        internal NearbyPlayerObservation Copy() => new NearbyPlayerObservation
        {
            CharacterId = CharacterId, Name = Name, Level = Level,
            Profession = Profession, Observer = Observer, ObservedUtc = ObservedUtc,
            VisibleNanoIds = (VisibleNanoIds ?? new int[0]).ToArray()
        };
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _nearbyPlayerSync = new object();
        private NearbyPlayerObservation[] _nearbyPlayers = new NearbyPlayerObservation[0];

        // Volatile observations only: never accounting, persistence, or commands.
        public void PublishNearbyPlayers(NearbyPlayerObservation[] observations)
        {
            var copy = (observations ?? new NearbyPlayerObservation[0])
                .Where(p => p != null).Select(p => p.Copy()).ToArray();
            lock (_nearbyPlayerSync) _nearbyPlayers = copy;
        }

        public List<NearbyPlayerObservation> ReadNearbyPlayers()
        {
            DateTime now = DateTime.UtcNow;
            lock (_nearbyPlayerSync)
                return _nearbyPlayers.Where(p => p.ObservedUtc <= now &&
                    now - p.ObservedUtc < TimeSpan.FromSeconds(5))
                    .Select(p => p.Copy()).ToList();
        }
    }
}
