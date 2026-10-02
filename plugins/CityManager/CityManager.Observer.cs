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
        private readonly Dictionary<string, long> _refreshRetryAfter = new Dictionary<string, long>();
        private readonly HashSet<string> _refreshAwaiting = new HashSet<string>();
        private Dictionary<int, NearbyPlayerObservation> _nearbyObserved =
            new Dictionary<int, NearbyPlayerObservation>();

        private void ClearNearbyObserver()
        {
            _nearbyObserved.Clear();
            _refreshRetryAfter.Clear();
            _refreshAwaiting.Clear();
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
                        VisibleNanoIds = player.Buffs.Select(b => b.Id).Distinct().OrderBy(id => id).ToArray(),
                        Nanos = player.Buffs.Select(b => new NearbyNanoObservation {
                            Id = b.Id, RemainingSeconds = b.Cooldown?.RemainingTime ?? 0,
                            FullSeconds = b.NanoItem?.TotalTime ?? 0 }).ToArray()
                    };
                    current[observation.CharacterId] = observation;
                }
                ManagerMemory.Current.PublishNearbyPlayers(current.Values.ToArray());
                RefreshObservedBuffs(current.Values, now);
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
                        string.Join(",", observation.VisibleNanoIds) + "]; durations=[" +
                        string.Join(",", observation.Nanos.Select(n => n.Id + ":" +
                            (int)n.RemainingSeconds + "/" + (int)n.FullSeconds + "s")) +
                        "]; NCU completeness unknown.");
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

        private void RefreshObservedBuffs(IEnumerable<NearbyPlayerObservation> players, long now)
        {
            var providers = ManagerMemory.Current.BufferBotInfos().Where(b => b.Ready && b.InPlay &&
                b.ObservedUtc >= DateTime.UtcNow.AddSeconds(-5) &&
                !ManagerMemory.Current.IsPaidBuffer(b.Character)).ToArray();
            var visibleKeys = new HashSet<string>();
            foreach (var player in players)
            foreach (var nano in player.Nanos)
            {
                string key = player.CharacterId + ":" + nano.Id;
                visibleKeys.Add(key);
                if (!nano.NeedsRefresh)
                {
                    if (nano.FullSeconds > 0 && nano.RemainingSeconds >= nano.FullSeconds / 2 &&
                        _refreshAwaiting.Remove(key))
                        RecordDiagnostic("OBSERVER refresh observed: " + player.Name + " nano=" + nano.Id +
                            " remaining=" + (int)nano.RemainingSeconds + "s full=" + (int)nano.FullSeconds + "s.");
                    continue;
                }
                long retryAfter;
                if (_refreshRetryAfter.TryGetValue(key, out retryAfter) && now < retryAfter) continue;
                var provider = providers.OrderBy(b => b.Character, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(b => (b.AdvertisedBuffs ?? new BufferAdvertisedBuff[0]).Any(buff =>
                        buff.Type == "Single" && (buff.NanoIds ?? new int[0]).Contains(nano.Id)));
                if (provider == null || !ManagerMemory.Current.RequestObservedBuffRefresh(
                    provider.Character, player.CharacterId, nano.Id)) continue;
                _refreshRetryAfter[key] = now + 180000;
                _refreshAwaiting.Add(key);
                RecordDiagnostic("OBSERVER refresh requested: " + player.Name + " nano=" + nano.Id +
                    " remaining=" + (int)nano.RemainingSeconds + "s full=" + (int)nano.FullSeconds +
                    "s buffer=" + provider.Character + ".");
            }
            foreach (var key in _refreshRetryAfter.Keys.Where(k => !visibleKeys.Contains(k) &&
                now >= _refreshRetryAfter[k]).ToArray())
            { _refreshRetryAfter.Remove(key); _refreshAwaiting.Remove(key); }
        }
    }
}
