using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class PlayerSeenRecord
    {
        // Timestamps describe observations, not invented exact login/logout times.
        public string Name;
        public uint CharacterId;
        public DateTime TrackedUtc;
        public DateTime? ReportedUtc, OnlineUtc, OfflineUtc;
        public bool? ReportedOnline;
        public string Reporter;
        internal PlayerSeenRecord Copy() => (PlayerSeenRecord)MemberwiseClone();
    }

    [Serializable]
    public sealed class PlayerPresenceReport
    {
        public string Name, Reporter;
        public uint CharacterId;
        public bool Online;
        public DateTime ObservedUtc;
    }

    [Serializable]
    public sealed class PlayerPresenceSnapshot
    {
        public string Name;
        public PlayerSeenRecord Seen;
        // Null means no current-session buddy report, never an inferred logoff.
        public bool? Online;
        public string Error;
    }

    public interface IPlayerSeenPersistence
    {
        void SaveSeen(PlayerSeenRecord[] records, PlayerPresenceReport[] reports);
    }

    public sealed partial class ManagerMemory
    {
        // Separate indexed state avoids flattening the entire banking ledger or
        // 100,000 tracked players for each buddy event. SQL still has ONE owner.
        private readonly Dictionary<string, PlayerSeenRecord> _seen =
            new Dictionary<string, PlayerSeenRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _livePresence =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _presenceErrors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _presenceRequests = new Queue<string>();
        private readonly HashSet<string> _presenceQueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private IPlayerSeenPersistence _seenPersistence;
        private string _presenceOwner, _presenceToken;
        private long _presenceRevision;
        private bool _presenceReady;
        private string _presenceStatus = "Proxy presence is not configured (Manager.ChatProxy).";

        public static void InitializePlayerSeen(PlayerSeenRecord[] records, IPlayerSeenPersistence persistence)
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only ManagerHost owns seen persistence.");
            lock (Current._accountingSync)
            {
                Current._seenPersistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
                foreach (var record in records) Current._seen.Add(record.Name, record.Copy());
            }
        }

        private static string PresenceName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 30 || name.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("Invalid character name.");
            return char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
        }

        // Any component may request tracking and read the result through memory.
        // Only the owning Manager adapter consumes the requests or supplies reports.
        public void TrackPlayerPresence(string[] names)
        {
            var normalized = names.Select(PresenceName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            lock (_accountingSync)
            {
                var added = normalized.Where(name => !_seen.ContainsKey(name))
                    .Select(name => new PlayerSeenRecord { Name = name, TrackedUtc = DateTime.UtcNow }).ToArray();
                if (added.Length != 0)
                {
                    _seenPersistence.SaveSeen(added, new PlayerPresenceReport[0]);
                    foreach (var row in added) _seen.Add(row.Name, row);
                    _presenceRevision++;
                }
                foreach (string name in normalized)
                    if (!_livePresence.ContainsKey(name) && !_presenceErrors.ContainsKey(name) && _presenceQueued.Add(name))
                        _presenceRequests.Enqueue(name);
            }
        }

        public PlayerPresenceSnapshot ReadPlayerPresence(string name)
        {
            name = PresenceName(name);
            lock (_accountingSync)
            {
                PlayerSeenRecord seen;
                bool online;
                string error;
                return new PlayerPresenceSnapshot {
                    Name = name,
                    Seen = _seen.TryGetValue(name, out seen) ? seen.Copy() : null,
                    Online = _presenceReady && _livePresence.TryGetValue(name, out online) ? (bool?)online : null,
                    Error = _presenceErrors.TryGetValue(name, out error) ? error : null
                };
            }
        }

        public string ReadPresenceServiceStatus() { lock (_accountingSync) return _presenceStatus; }
        public bool IsPresenceServiceReady() { lock (_accountingSync) return _presenceReady; }
        public long ReadPresenceRevision() { lock (_accountingSync) return _presenceRevision; }

        public bool AcquirePresenceService(string character, string token)
        {
            lock (_accountingSync)
            {
                if (_presenceToken != null) return false;
                _presenceOwner = character;
                _presenceToken = token;
                return true;
            }
        }

        public void SetPresenceService(string token, bool ready, string status)
        {
            lock (_accountingSync)
            {
                if (token != _presenceToken) return;
                _presenceStatus = status;
                _presenceReady = ready;
                _presenceRevision++;
                _livePresence.Clear();
                _presenceErrors.Clear();
                if (ready)
                {
                    // A reconnect must get fresh reports for every saved name.
                    _presenceRequests.Clear();
                    _presenceQueued.Clear();
                    foreach (string name in _seen.Keys)
                    {
                        _presenceQueued.Add(name);
                        _presenceRequests.Enqueue(name);
                    }
                }
            }
        }

        public string[] TakePresenceRequests(string token, int maximum)
        {
            lock (_accountingSync)
            {
                if (token != _presenceToken || !_presenceReady) return new string[0];
                var result = new List<string>();
                while (result.Count < maximum && _presenceRequests.Count != 0)
                {
                    string name = _presenceRequests.Dequeue();
                    _presenceQueued.Remove(name);
                    result.Add(name);
                }
                return result.ToArray();
            }
        }

        public void ApplyPresenceReports(string token, PlayerPresenceReport[] reports)
        {
            lock (_accountingSync)
            {
                if (token != _presenceToken || !_presenceReady) return;
                reports = reports.Where(r => r != null && r.CharacterId != 0 && r.CharacterId != uint.MaxValue &&
                    _seen.ContainsKey(r.Name)).ToArray();
                if (reports.Length == 0) return;
                var updated = new Dictionary<string, PlayerSeenRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var report in reports)
                {
                    PlayerSeenRecord row;
                    if (!updated.TryGetValue(report.Name, out row)) row = _seen[report.Name].Copy();
                    row.CharacterId = report.CharacterId;
                    row.ReportedUtc = report.ObservedUtc;
                    row.ReportedOnline = report.Online;
                    row.Reporter = report.Reporter;
                    if (report.Online) row.OnlineUtc = report.ObservedUtc;
                    else row.OfflineUtc = report.ObservedUtc;
                    updated[row.Name] = row;
                }
                _seenPersistence.SaveSeen(updated.Values.ToArray(), reports);
                _presenceRevision++;
                foreach (var row in updated.Values)
                {
                    _seen[row.Name] = row;
                    _livePresence[row.Name] = row.ReportedOnline.Value;
                    _presenceErrors.Remove(row.Name);
                }
            }
        }

        public void PresenceLookupFailed(string token, string name)
        {
            lock (_accountingSync)
                if (token == _presenceToken && _seen.ContainsKey(name))
                {
                    _livePresence.Remove(name);
                    _presenceErrors[name] = "Character not found by AO chat.";
                    _presenceRevision++;
                }
        }

        public void InvalidatePlayerPresence(string token, string name)
        {
            lock (_accountingSync)
                if (token == _presenceToken && _seen.ContainsKey(name))
                {
                    _livePresence.Remove(name);
                    _presenceErrors.Remove(name);
                    _presenceRevision++;
                    if (_presenceQueued.Add(name)) _presenceRequests.Enqueue(name);
                }
        }

        public PlayerPresenceSnapshot[] ReadKnownAltPresence(string name)
        {
            lock (_accountingSync) return KnownPresenceNames(name).Select(ReadPlayerPresence).ToArray();
        }

        public void TrackKnownAltPresence(string name)
        {
            lock (_accountingSync) TrackPlayerPresence(KnownPresenceNames(name));
        }

        private string[] KnownPresenceNames(string name)
        {
            name = PresenceName(name);
            foreach (var group in Accounting(null).Alts.Groups)
            {
                var names = new HashSet<string>((group.ObservedCharacters ?? new List<string>())
                    .Concat(group.AddedCharacters ?? new List<string>()), StringComparer.OrdinalIgnoreCase);
                names.ExceptWith(group.RemovedCharacters ?? new List<string>());
                names.Add(group.Main);
                if (names.Contains(name)) return names.Where(n => !string.IsNullOrWhiteSpace(n))
                    .Concat(new[] { name }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            return new[] { name };
        }

        public void ReleasePresenceService(string token)
        {
            lock (_accountingSync)
                if (token == _presenceToken)
                {
                    _presenceToken = _presenceOwner = null;
                    _presenceReady = false;
                    _presenceStatus = "Proxy presence is disconnected.";
                    _presenceRevision++;
                    _livePresence.Clear();
                    _presenceErrors.Clear();
                }
        }

        private void DisconnectPresenceOwner(string character)
        {
            lock (_accountingSync)
                if (string.Equals(character, _presenceOwner, StringComparison.OrdinalIgnoreCase))
                    ReleasePresenceService(_presenceToken);
        }
    }
}
