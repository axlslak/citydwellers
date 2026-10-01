using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private readonly Stopwatch _observerClock = Stopwatch.StartNew();
        private long _nextNearbyObservation;
        private long _nextObserverError;
        private Dictionary<int, NearbyPlayerObservation> _nearbyObserved =
            new Dictionary<int, NearbyPlayerObservation>();

        private void ClearNearbyObserver()
        {
            _nearbyObserved.Clear();
            ManagerMemory.Current.PublishNearbyPlayers(new NearbyPlayerObservation[0]);
        }

        private void TickNearbyObserver()
        {
            long now = _observerClock.ElapsedMilliseconds;
            if (now < _nextNearbyObservation) return;
            _nextNearbyObservation = now + 1000;
            try
            {
                if (!Client.InPlay || DynelManager.LocalPlayer == null)
                {
                    ClearNearbyObserver();
                    return;
                }
                var current = new Dictionary<int, NearbyPlayerObservation>();
                foreach (var player in DynelManager.Players.ToArray())
                {
                    if (player.Identity == DynelManager.LocalPlayer.Identity) continue;
                    var observation = new NearbyPlayerObservation
                    {
                        CharacterId = player.Identity.Instance, Name = player.Name,
                        Level = player.Level, Profession = (int)player.Profession,
                        Observer = Client.CharacterName, ObservedUtc = DateTime.UtcNow,
                        VisibleNanoIds = player.Buffs.Select(b => b.Id).Distinct().OrderBy(id => id).ToArray()
                    };
                    current[observation.CharacterId] = observation;
                }
                ManagerMemory.Current.PublishNearbyPlayers(current.Values.ToArray());
                var previous = _nearbyObserved;
                _nearbyObserved = current;
                foreach (var observation in current.Values)
                {
                    NearbyPlayerObservation old;
                    bool arrived = !previous.TryGetValue(observation.CharacterId, out old);
                    if (!arrived && old.Name == observation.Name && old.Level == observation.Level &&
                        old.Profession == observation.Profession &&
                        old.VisibleNanoIds.SequenceEqual(observation.VisibleNanoIds)) continue;
                    RecordDiagnostic("OBSERVER " + (arrived ? "noticed " : "updated ") +
                        observation.Name + " (" + observation.CharacterId + ") level=" + observation.Level +
                        " profession=" + observation.Profession + " visibleNanoIds=[" +
                        string.Join(",", observation.VisibleNanoIds) + "]; NCU completeness unknown.");
                }
                foreach (var departed in previous.Values.Where(p => !current.ContainsKey(p.CharacterId)))
                    RecordDiagnostic("OBSERVER no longer visible: " + departed.Name + " (" + departed.CharacterId + ").");
            }
            catch (Exception ex)
            {
                // Do not let an observer fault affect banking, tells, or raids.
                // Stop exposing the previous scan as current evidence.
                ClearNearbyObserver();
                if (now >= _nextObserverError)
                {
                    _nextObserverError = now + 30000;
                    Logger.Warning("Nearby observer unavailable: " + ex.Message);
                }
            }
        }
    }
}
