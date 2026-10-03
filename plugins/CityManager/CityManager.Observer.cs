using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
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
        private readonly HashSet<int> _waitingForObservedNcu = new HashSet<int>();
        private Dictionary<int, NearbyPlayerObservation> _nearbyObserved =
            new Dictionary<int, NearbyPlayerObservation>();

        private void ClearNearbyObserver()
        {
            _nearbyObserved.Clear();
            _refreshRetryAfter.Clear();
            _refreshAwaiting.Clear();
            _waitingForObservedNcu.Clear();
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
                var visiblePlayers = DynelManager.Players.Where(p => p != null).ToArray();
                foreach (var player in visiblePlayers)
                {
                    if (player == null) continue;
                    if (player.Identity == DynelManager.LocalPlayer.Identity) continue;
                    try
                    {
                        var buffs = player.Buffs.ToArray();
                        var observation = new NearbyPlayerObservation
                        {
                            CharacterId = player.Identity.Instance, Name = player.Name,
                            Level = -1, Profession = -1,
                            Observer = Client.CharacterName, ObservedUtc = DateTime.UtcNow,
                            VisibleNanoIds = buffs.Select(b => b.Id).Distinct().OrderBy(id => id).ToArray(),
                            Nanos = buffs.Select(b => new NearbyNanoObservation {
                                Id = b.Id, RemainingSeconds = b.Cooldown?.RemainingTime ?? 0,
                                FullSeconds = b.NanoItem?.TotalTime ?? 0 }).ToArray()
                        };
                        // Optional metadata must never invalidate the NCU observation.
                        observation.Level = ReadOptionalObserverStat(player, Stat.Level, now);
                        observation.Profession = ReadOptionalObserverStat(player, Stat.Profession, now);
                        current[observation.CharacterId] = observation;
                    }
                    catch (Exception ex)
                    {
                        LogObserverFailure("NCU scan player=" + player.Identity, ex, now);
                    }
                }
                ManagerMemory.Current.PublishNearbyPlayers(current.Values.ToArray(),
                    visiblePlayers.Select(p => p.Identity.Instance).ToArray());
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
                        observation.Name + " (" + observation.CharacterId + ") level=" +
                        (observation.Level < 0 ? "unknown" : observation.Level.ToString()) +
                        " profession=" + (observation.Profession < 0 ? "unknown" : observation.Profession.ToString()) + " visibleNanoIds=[" +
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
                _nearbyObserved.Clear();
                ManagerMemory.Current.InvalidateNearbyPlayers();
                // Keep submission cooldowns: a scan error must not cause duplicate casts.
                LogObserverFailure("scan", ex, now);
            }
        }

        private int ReadOptionalObserverStat(PlayerChar player, Stat stat, long now)
        {
            try
            {
                int value;
                return player.TryGetStat(stat, out value) ? value : -1;
            }
            catch (Exception ex)
            {
                LogObserverFailure("optional " + stat + " player=" + player.Identity, ex, now);
                return -1;
            }
        }

        private void LogObserverFailure(string stage, Exception ex, long now)
        {
            if (now < _nextObserverError) return;
            _nextObserverError = now + 30000;
            Logger.Warning("Nearby observer failure at " + stage + ": " + ex);
        }

        private void RefreshObservedBuffs(IEnumerable<NearbyPlayerObservation> players, long now)
        {
            var providers = ManagerMemory.Current.BufferBotInfos().Where(b => b.Ready && b.InPlay &&
                b.ObservedUtc >= DateTime.UtcNow.AddSeconds(-5) &&
                !ManagerMemory.Current.IsPaidBuffer(b.Character)).ToArray();
            var memory = ManagerMemory.Current;
            var paidProviders = memory.ReadPaidBuffers().Where(p => !p.Blocked &&
                DateTime.UtcNow >= p.RetryAfterUtc).OrderBy(p => p.Character, StringComparer.OrdinalIgnoreCase).ToArray();
            // Withdraw unclaimed automatic work if the player leaves or another
            // caster has already restored it. An invitation is not a cast obligation.
            foreach (var pending in memory.PendingBufferPublicCommands(128).Where(r => r.ObservedNanoId > 0))
                if (!memory.ObservedBuffNeedsRefresh(unchecked((int)pending.SenderId), pending.ObservedNanoId))
                    memory.CancelUnclaimedObservedBufferCommand(pending.Id);
            var visibleKeys = new HashSet<string>();
            var present = players.ToArray();
            _waitingForObservedNcu.IntersectWith(present.Select(p => p.CharacterId));
            foreach (var player in present)
            {
                bool waiting = memory.ObservedNcuNeedsRefresh(player.CharacterId);
                if (waiting && _waitingForObservedNcu.Add(player.CharacterId))
                    RecordDiagnostic("OBSERVER NCU first: " + player.Name +
                        "; holding other automatic buffs until fixer NCU is observed above half duration.");
                else if (!waiting && _waitingForObservedNcu.Remove(player.CharacterId))
                    RecordDiagnostic("OBSERVER NCU prerequisite cleared: " + player.Name +
                        "; reevaluating automatic buffs.");
            }
            foreach (var player in present)
            foreach (var nano in ManagerMemory.Current.ObservedBuffCandidates(player.CharacterId))
            {
                string key = player.CharacterId + ":" + nano.Id;
                visibleKeys.Add(key);
                if (_waitingForObservedNcu.Contains(player.CharacterId) &&
                    !PaidBufferCatalogue.FindEffect(275043).MatchesEffect(nano.Id))
                {
                    // Discard submission cooldowns for deferred work. Learned usage
                    // remains intact and is reconsidered immediately after NCU lands.
                    _refreshRetryAfter.Remove(key);
                    _refreshAwaiting.Remove(key);
                    continue;
                }
                if (!ManagerMemory.Current.ObservedBuffNeedsRefresh(player.CharacterId, nano.Id))
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
                string providerName;
                if (provider != null)
                {
                    if (!memory.RequestObservedBuffRefresh(provider.Character, player.CharacterId, nano.Id)) continue;
                    providerName = provider.Character;
                }
                else
                {
                    var paid = paidProviders.FirstOrDefault(p =>
                        PaidBufferCatalogue.ForProfession(p.Profession).Any(n => n.MatchesEffect(nano.Id)));
                    if (paid == null) continue;
                    providerName = paid.Character;
                    int castId = PaidBufferCatalogue.FindEffect(nano.Id).Id;
                    // Join an existing manual request for this exact nano rather
                    // than queueing another cast while it waits for team/account.
                    bool alreadyQueued = memory.PendingBufferPublicCommands(128).Any(r =>
                        r.SenderId == unchecked((uint)player.CharacterId) &&
                        string.Equals(r.TargetCharacter, paid.Character, StringComparison.OrdinalIgnoreCase) &&
                        (r.Arguments ?? new string[0]).Any(tag => tag == castId.ToString() ||
                            PaidBufferCatalogue.Find(tag)?.Id == castId));
                    if (!alreadyQueued && !memory.BeginBufferPublicCommand(new BufferPublicCommandRequest {
                        Id = "observed:" + player.CharacterId + ":" + nano.Id,
                        Command = "cast", TargetCharacter = paid.Character,
                        SenderId = unchecked((uint)player.CharacterId), SenderName = player.Name,
                        Arguments = new[] { castId.ToString() }, ObservedNanoId = nano.Id,
                        CreatedUtc = DateTime.UtcNow })) continue;
                }
                _refreshRetryAfter[key] = now + 180000;
                _refreshAwaiting.Add(key);
                RecordDiagnostic("OBSERVER refresh requested: " + player.Name + " nano=" + nano.Id +
                    " reason=" + (player.VisibleNanoIds.Contains(nano.Id) ? "below-half" : "missing-learned-buff") +
                    " remaining=" + (int)nano.RemainingSeconds + "s full=" + (int)nano.FullSeconds +
                    "s buffer=" + providerName + ".");
            }
            foreach (var key in _refreshRetryAfter.Keys.Where(k => !visibleKeys.Contains(k)).ToArray())
            { _refreshRetryAfter.Remove(key); _refreshAwaiting.Remove(key); }
        }
    }
}


