using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    // Verified against the game nano catalogue: uploaded wrangles, recipient
    // effects, drain variants and Umbral emitters are explicit-request only.
    public static class BufferRefreshPolicy
    {
        public static bool IsRequestOnlyNano(int id) =>
            (id >= 121215 && id <= 121258) ||
            (id >= 121317 && id <= 121346) ||
            (id >= 235054 && id <= 235064) ||
            (id >= 235233 && id <= 235265) ||
            (id >= 235271 && id <= 235291 && (id & 1) != 0) ||
            id == 287001 || id == 288960 || (id >= 288964 && id <= 288978);
    }

    [Serializable]
    public sealed class LearnedBuffProfile
    {
        public int CharacterId;
        public string Name;
        // Only first versus returning matters; do not persist every future visit.
        public int Encounters;
        public List<int> NanoIds = new List<int>();
        internal LearnedBuffProfile Copy() => new LearnedBuffProfile {
            CharacterId = CharacterId, Name = Name, Encounters = Encounters,
            NanoIds = new List<int>(NanoIds) };
    }

    [Serializable]
    public sealed class NearbyNanoObservation
    {
        public int Id;
        public double RemainingSeconds;
        public double FullSeconds;
        public bool NeedsRefresh => RemainingSeconds > 0 && FullSeconds > 0 &&
            RemainingSeconds < FullSeconds / 2;
        internal NearbyNanoObservation Copy() => (NearbyNanoObservation)MemberwiseClone();
    }

    [Serializable]
    public sealed class NearbyPlayerObservation
    {
        public int CharacterId;
        public string Name;
        public int Level;
        public int Profession;
        public string Observer;
        public DateTime ObservedUtc;
        public bool RestoreMissing;
        // AOSharp's visible effects, not proof of a complete remote NCU inspection.
        // Missing-effect restoration is limited to learned usage on return encounters.
        public int[] VisibleNanoIds = new int[0];
        public NearbyNanoObservation[] Nanos = new NearbyNanoObservation[0];
        public bool NcuComplete => false;

        internal NearbyPlayerObservation Copy() => new NearbyPlayerObservation
        {
            CharacterId = CharacterId, Name = Name, Level = Level,
            Profession = Profession, Observer = Observer, ObservedUtc = ObservedUtc,
            RestoreMissing = RestoreMissing,
            VisibleNanoIds = (VisibleNanoIds ?? new int[0]).ToArray(),
            Nanos = (Nanos ?? new NearbyNanoObservation[0]).Select(n => n.Copy()).ToArray()
        };
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _nearbyPlayerSync = new object();
        private NearbyPlayerObservation[] _nearbyPlayers = new NearbyPlayerObservation[0];
        private readonly HashSet<int> _encounterPlayers = new HashSet<int>();

        // Live timers remain volatile; only learned usage and first/returning status persist.
        public void PublishNearbyPlayers(NearbyPlayerObservation[] observations, int[] visibleCharacterIds = null)
        {
            var copy = (observations ?? new NearbyPlayerObservation[0])
                .Where(p => p != null).Select(p => p.Copy()).ToArray();
            var customers = copy.Where(p => !IsObservedBuffer(p)).ToArray();
            ManagerAccounting.Transaction("Learn nearby buff usage", () =>
            {
                lock (_accountingSync)
                {
                    var state = Writing(ManagerAccounting.TransactionId);
                    var profiles = state.LearnedBuffs;
                    foreach (var player in customers)
                    {
                        var old = profiles.FirstOrDefault(p => p.CharacterId == player.CharacterId);
                        bool arrived = !_encounterPlayers.Contains(player.CharacterId);
                        int encounters = old == null ? 1 : Math.Min(2, old.Encounters + (arrived ? 1 : 0));
                        player.RestoreMissing = encounters >= 2;
                        var added = player.VisibleNanoIds.Where(id => id > 0 &&
                            !BufferRefreshPolicy.IsRequestOnlyNano(id) &&
                            (old == null || !old.NanoIds.Contains(id))).Distinct().ToArray();
                        if (old != null && old.Encounters == encounters && old.Name == player.Name && added.Length == 0)
                            continue;
                        if (ReferenceEquals(profiles, state.LearnedBuffs)) profiles = new List<LearnedBuffProfile>(profiles);
                        var next = old?.Copy() ?? new LearnedBuffProfile { CharacterId = player.CharacterId };
                        next.Name = player.Name;
                        next.Encounters = encounters;
                        next.NanoIds.AddRange(added);
                        if (old != null) profiles.Remove(old);
                        profiles.Add(next);
                    }
                    state.LearnedBuffs = profiles;
                }
            });
            // A failed NCU read is not a departure when the dynel is still present.
            _encounterPlayers.IntersectWith(visibleCharacterIds ?? copy.Select(p => p.CharacterId).ToArray());
            foreach (var player in customers) _encounterPlayers.Add(player.CharacterId);
            lock (_nearbyPlayerSync) _nearbyPlayers = copy;
        }

        public void InvalidateNearbyPlayers()
        {
            // A scan failure invalidates evidence, not encounter history.
            lock (_nearbyPlayerSync) _nearbyPlayers = new NearbyPlayerObservation[0];
        }

        private bool IsObservedBuffer(NearbyPlayerObservation player)
        {
            if (IsPaidBuffer(player.Name)) return true;
            lock (_bufferBotSync)
                return _bufferBots.Values.Any(b =>
                    (player.CharacterId > 0 && b.IdentityInstance == player.CharacterId) ||
                    string.Equals(b.Character, player.Name, StringComparison.OrdinalIgnoreCase));
        }

        public NearbyNanoObservation[] ObservedBuffCandidates(int characterId)
        {
            var player = ReadNearbyPlayers().FirstOrDefault(p => p.CharacterId == characterId);
            if (player == null || IsObservedBuffer(player)) return new NearbyNanoObservation[0];
            var result = player.Nanos.ToList();
            if (player.RestoreMissing)
                lock (_accountingSync)
                {
                    var profile = Accounting(null).LearnedBuffs.FirstOrDefault(p => p.CharacterId == characterId);
                    if (profile != null)
                        result.AddRange(profile.NanoIds.Where(id => !player.VisibleNanoIds.Contains(id))
                            .Select(id => new NearbyNanoObservation { Id = id }));
                }
            return result.Where(n => !BufferRefreshPolicy.IsRequestOnlyNano(n.Id)).ToArray();
        }

        public List<NearbyPlayerObservation> ReadNearbyPlayers()
        {
            DateTime now = DateTime.UtcNow;
            lock (_nearbyPlayerSync)
                return _nearbyPlayers.Where(p => p.ObservedUtc <= now &&
                    now - p.ObservedUtc < TimeSpan.FromSeconds(5))
                    .Select(p => p.Copy()).ToList();
        }

        // Caster feedback and cast-state transitions are not required for this
        // answer. Manager's fresh target observation is the delivery evidence.
        public bool ObservedCastLanded(int requester, int nanoId, DateTime attemptedUtc,
            DateTime previousExpiryUtc, bool missingBefore)
        {
            var player = ReadNearbyPlayers().FirstOrDefault(p =>
                p.CharacterId == requester && p.ObservedUtc > attemptedUtc);
            var effect = PaidBufferCatalogue.FindEffect(nanoId);
            return player != null && player.Nanos.Any(n =>
                (n.Id == nanoId || effect?.MatchesEffect(n.Id) == true) && n.RemainingSeconds > 0 &&
                (missingBefore || player.ObservedUtc.AddSeconds(n.RemainingSeconds) >
                    previousExpiryUtc.AddSeconds(2)));
        }

        // NCU is a per-recipient prerequisite for automatic restoration only.
        // Keep checking observed effects: caster completion does not prove delivery.
        public bool ObservedNcuNeedsRefresh(int characterId)
        {
            if (!ReadPaidBuffers().Any(p => p.Profession == 4)) return false;
            var player = ReadNearbyPlayers().FirstOrDefault(p => p.CharacterId == characterId);
            if (player == null || IsObservedBuffer(player)) return false;
            var ncu = PaidBufferCatalogue.FindEffect(275043);
            double age = (DateTime.UtcNow - player.ObservedUtc).TotalSeconds;
            var effects = player.Nanos.Where(n => ncu.MatchesEffect(n.Id)).ToArray();
            if (effects.Any(n => n.FullSeconds > 0 &&
                n.RemainingSeconds - age >= n.FullSeconds / 2)) return false;
            if (effects.Length != 0) return true;
            if (!player.RestoreMissing) return false;
            lock (_accountingSync)
                return Accounting(null).LearnedBuffs.Any(p => p.CharacterId == characterId &&
                    p.NanoIds.Any(ncu.MatchesEffect));
        }

        public bool ObservedBuffNeedsRefresh(int characterId, int nanoId)
        {
            if (BufferRefreshPolicy.IsRequestOnlyNano(nanoId)) return false;
            if (!PaidBufferCatalogue.FindEffect(275043).MatchesEffect(nanoId) &&
                ObservedNcuNeedsRefresh(characterId)) return false;
            var player = ReadNearbyPlayers().FirstOrDefault(p => p.CharacterId == characterId);
            if (player == null || IsObservedBuffer(player)) return false;
            // Buffer preparation belongs to RebuffInfo, not the nearby-player refresh loop.
            // Include unready/offline registrations: startup must not create duplicate work.
            // Umbral's recipient variants share one uploaded emitter. A healthy
            // variant must satisfy learned usage of the other perk-strength variants.
            var paid = PaidBufferCatalogue.FindEffect(nanoId);
            if (paid != null && paid.EffectIds != null)
            {
                var effects = player.Nanos.Where(n => paid.MatchesEffect(n.Id)).ToArray();
                if (effects.Length != 0)
                    return effects.All(n => n.NeedsRefresh &&
                        n.RemainingSeconds > (DateTime.UtcNow - player.ObservedUtc).TotalSeconds);
            }
            if (player.RestoreMissing && !player.VisibleNanoIds.Contains(nanoId))
                lock (_accountingSync)
                    return Accounting(null).LearnedBuffs.Any(p => p.CharacterId == characterId && p.NanoIds.Contains(nanoId));
            return player.Nanos.Any(n => n.Id == nanoId && n.NeedsRefresh &&
                n.RemainingSeconds > (DateTime.UtcNow - player.ObservedUtc).TotalSeconds);
        }
    }
}


