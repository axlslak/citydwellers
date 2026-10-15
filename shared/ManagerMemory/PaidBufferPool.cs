using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    public sealed partial class ManagerMemory
    {
        // Pending and accepted paid commands are one pool. Caster queues are only
        // execution windows; acceptance is not delivery and does not retire intent.
        private readonly Dictionary<int, NearbyPlayerObservation> _paidPoolPlayers =
            new Dictionary<int, NearbyPlayerObservation>();
        private readonly HashSet<int> _paidPoolNeedsNcu = new HashSet<int>();
        private readonly HashSet<string> _paidPoolDeferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _paidPoolUpdates;
        private DateTime _paidPoolObservedUtc;
        private long _paidPoolVersion, _paidNcuVersion = -1;
        private bool _paidNcuPending;
        private void PaidPoolChangedLocked() { _paidPoolVersion++; }

        private static string PaidPoolKey(string character, int requester, int nano) =>
            character + ":" + requester + ":" + nano;

        public void BeginPaidPoolUpdate() { lock (_bufferPublicCommandSync) _paidPoolUpdates++; }
        public bool PaidPoolUpdating { get { lock (_bufferPublicCommandSync) return _paidPoolUpdates != 0; } }
        public void EndPaidPoolUpdate()
        {
            lock (_bufferPublicCommandSync)
            {
                _paidPoolUpdates--;
                System.Threading.Monitor.PulseAll(_bufferPublicCommandSync);
            }
        }

        // Manager computes NCU need outside the pool lock. No game, SQL, nearby-
        // player or buffer-info lookup is performed while holding this lock.
        public void PublishPaidPoolPlayers(NearbyPlayerObservation[] players, int[] needsNcu)
        {
            lock (_bufferPublicCommandSync)
            {
                var next = players.ToDictionary(p => p.CharacterId, p => p.Copy());
                foreach (int departed in _paidPoolPlayers.Keys.Except(next.Keys))
                    foreach (var provider in _paidBuffers.Values)
                        foreach (var nano in PaidBufferCatalogue.ForProfession(provider.Profession))
                            _paidPoolDeferred.Remove(PaidPoolKey(provider.Character, departed, nano.Id));
                foreach (var player in next.Values)
                {
                    NearbyPlayerObservation old;
                    bool retry = !_paidPoolPlayers.TryGetValue(player.CharacterId, out old) ||
                        (player.FreeNcu >= 0 && old.FreeNcu >= 0 && player.FreeNcu > old.FreeNcu) ||
                        (_paidPoolNeedsNcu.Contains(player.CharacterId) && !needsNcu.Contains(player.CharacterId)) ||
                        old.Nanos.Any(n => n.FullSeconds >= 120 && !player.VisibleNanoIds.Contains(n.Id));
                    if (retry)
                        foreach (var provider in _paidBuffers.Keys)
                            foreach (var nano in PaidBufferCatalogue.ForProfession(_paidBuffers[provider].Profession))
                                _paidPoolDeferred.Remove(PaidPoolKey(provider, player.CharacterId, nano.Id));
                }
                _paidPoolPlayers.Clear();
                foreach (var p in next) _paidPoolPlayers.Add(p.Key, p.Value);
                _paidPoolNeedsNcu.Clear();
                _paidPoolNeedsNcu.UnionWith(needsNcu);
                _paidPoolObservedUtc = DateTime.UtcNow;
                PaidPoolChangedLocked();
            }
        }

        private static PaidBufferNano[] PaidRequestNanos(BufferPublicCommandRequest request)
        {
            if (request.PendingNanoIds.Length != 0)
                return request.PendingNanoIds.Select(PaidBufferCatalogue.FindEffect).Where(n => n != null).ToArray();
            if (request.ObservedNanoId > 0)
            {
                var nano = PaidBufferCatalogue.FindEffect(request.ObservedNanoId);
                return nano == null ? new PaidBufferNano[0] : new[] { nano };
            }
            return (request.Arguments ?? new string[0]).Select(PaidBufferCatalogue.Find)
                .Where(n => n != null).Distinct().ToArray();
        }

        private bool PaidNanoFitsLocked(string character, int requester, PaidBufferNano nano)
        {
            NearbyPlayerObservation player;
            if (nano == null || _paidPoolObservedUtc < DateTime.UtcNow.AddSeconds(-3) ||
                !_paidPoolPlayers.TryGetValue(requester, out player) ||
                _paidPoolDeferred.Contains(PaidPoolKey(character, requester, nano.Id))) return false;
            int required = player.VisibleNanoIds.Any(nano.MatchesEffect) ? 0 : nano.Ncu;
            return player.FreeNcu < 0 || player.FreeNcu >= required;
        }

        private bool PaidNcuPendingLocked()
        {
            if (_paidNcuVersion != _paidPoolVersion)
            {
                _paidNcuPending = _bufferPublicCommands.Values.Any(r =>
                    !string.IsNullOrWhiteSpace(r.TargetCharacter) && PaidRequestNanos(r).Any(n =>
                        n.Id == 275043 && PaidNanoFitsLocked(r.TargetCharacter, unchecked((int)r.SenderId), n)));
                _paidNcuVersion = _paidPoolVersion;
            }
            return _paidNcuPending;
        }

        private bool PaidRequestReadyLocked(BufferPublicCommandRequest request)
        {
            if (_paidPoolUpdates != 0 || request.NcuWatchId != null) return false;
            int id = unchecked((int)request.SenderId);
            return PaidRequestNanos(request).Any(n => PaidNanoFitsLocked(request.TargetCharacter, id, n) &&
                (n.Id == 275043 || (!_paidPoolNeedsNcu.Contains(id) && !PaidNcuPendingLocked())));
        }

        public bool PaidTeamRequestReady(BufferPublicCommandRequest request)
        {
            lock (_bufferPublicCommandSync) return PaidRequestReadyLocked(request);
        }

        public bool CanCastPaidBuff(string character, int requester, int nanoId)
        {
            lock (_bufferPublicCommandSync)
            {
                var nano = PaidBufferCatalogue.FindEffect(nanoId);
                return _paidPoolUpdates == 0 && PaidNanoFitsLocked(character, requester, nano) &&
                    (nano.Id == 275043 || (!_paidPoolNeedsNcu.Contains(requester) && !PaidNcuPendingLocked()));
            }
        }

        public void DeferPaidBuff(string character, int requester, int nanoId)
        {
            lock (_bufferPublicCommandSync)
                if (_paidPoolPlayers.ContainsKey(requester))
                {
                    _paidPoolDeferred.Add(PaidPoolKey(character, requester, nanoId));
                    PaidPoolChangedLocked();
                }
        }

        public bool PaidBuffRecipientCanReceive(string character, int requester, int nanoId)
        {
            lock (_bufferPublicCommandSync)
                return PaidNanoFitsLocked(character, requester, PaidBufferCatalogue.FindEffect(nanoId));
        }

        public void ReturnBufferPublicCommand(string id, string character)
        {
            lock (_bufferPublicCommandSync)
            {
                BufferPublicCommandRequest request;
                if (_bufferPublicCommands.TryGetValue(id, out request) && !request.Accepted &&
                    string.Equals(request.ClaimedBy, character, StringComparison.OrdinalIgnoreCase))
                { request.ClaimedBy = null; request.ClaimedUtc = null; }
            }
        }

        public void ReportPaidBuffFinished(string character, int requester, int nanoId, bool delivered, bool defer = true)
        {
            lock (_bufferPublicCommandSync)
            {
                if (!delivered && defer && _paidPoolPlayers.ContainsKey(requester))
                    _paidPoolDeferred.Add(PaidPoolKey(character, requester, nanoId));
                foreach (var request in _bufferPublicCommands.Values.Where(r => r.Accepted &&
                    r.SenderId == unchecked((uint)requester) &&
                    string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                    r.PendingNanoIds.Contains(nanoId)).ToArray())
                {
                    if (delivered)
                    {
                        request.PendingNanoIds = request.PendingNanoIds.Where(id => id != nanoId).ToArray();
                        if (request.PendingNanoIds.Length == 0) _bufferPublicCommands.Remove(request.Id);
                    }
                    else
                    {
                        request.Accepted = false;
                        request.Arguments = request.PendingNanoIds.Select(id => id.ToString()).ToArray();
                        request.ClaimedBy = null;
                        request.ClaimedUtc = null;
                    }
                }
                PaidPoolChangedLocked();
                System.Threading.Monitor.PulseAll(_bufferPublicCommandSync);
            }
        }

        public bool HasPaidTeamWork(string character, int[] customers)
        {
            lock (_bufferPublicCommandSync)
                return _bufferPublicCommands.Values.Any(r =>
                    string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                    customers.Contains(unchecked((int)r.SenderId)) &&
                    PaidBufferCatalogue.RequiresManagerTeam(r) && (r.Accepted || PaidRequestReadyLocked(r)));
        }
    }
}
