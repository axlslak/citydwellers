using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CityBankers.Shared;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class CensusCycle
    {
        public string Id;
        public string Phase;
        public Dictionary<string, string> Participants;
        internal CensusCycle Copy() => new CensusCycle { Id = Id, Phase = Phase,
            Participants = Participants == null ? null : new Dictionary<string, string>(Participants, StringComparer.OrdinalIgnoreCase) };
    }

    [Serializable]
    public sealed class CensusPresence
    {
        public string Connection;
        public long Stamp;
        internal CensusPresence Copy() => (CensusPresence)MemberwiseClone();
    }

    [Serializable]
    public sealed class CensusAdmission
    {
        public string Cycle, Connection, Character, Run;
        public BagAuditResult Result;
        internal CensusAdmission Copy()
        {
            var result = (CensusAdmission)MemberwiseClone();
            result.Result = Result?.Copy();
            return result;
        }
    }

    [Serializable]
    public sealed class BankerAvailability
    {
        public string Character, Role, Connection, ReadyCycle, ReadyConnection, Hold;
        public string OperationalCycle, OperationalConnection;
        public long PresenceStamp, OperationalStamp;
        internal BankerAvailability Copy() => (BankerAvailability)MemberwiseClone();
    }

    [Serializable]
    public sealed class BankerAvailabilitySnapshot
    {
        public CensusCycle Cycle;
        public List<BankerAvailability> Bankers;
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _bankerSync = new object();
        private CensusCycle _census;
        private readonly Dictionary<string, BankerHealthHeartbeat> _bankerHealth = new Dictionary<string, BankerHealthHeartbeat>(StringComparer.OrdinalIgnoreCase);
        public void ReportBankerHealth(BankerHealthHeartbeat heartbeat)
        { lock (_bankerSync) _bankerHealth[heartbeat.Character] = heartbeat.Copy(); }
        public void ClearBankerHealth(string character)
        { lock (_bankerSync) _bankerHealth.Remove(character); }
        public BankerHealthHeartbeat BankerHealth(string character)
        { lock (_bankerSync) { BankerHealthHeartbeat health; return _bankerHealth.TryGetValue(character, out health) ? health.Copy() : null; } }

        private readonly Dictionary<string, BankerAvailability> _bankers = new Dictionary<string, BankerAvailability>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _initialAudits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CensusAdmission> _admissions = new Dictionary<string, CensusAdmission>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CensusAdmission> _admissionGrants = new Dictionary<string, CensusAdmission>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BagAuditCommand> _auditCommands = new Dictionary<string, BagAuditCommand>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BagAuditResult> _auditResults = new Dictionary<string, BagAuditResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BagAuditResult> _completedCensuses = new Dictionary<string, BagAuditResult>(StringComparer.OrdinalIgnoreCase);

        private BankerAvailability Banker(string character)
        {
            BankerAvailability banker;
            if (!_bankers.TryGetValue(character, out banker))
                _bankers.Add(character, banker = new BankerAvailability { Character = character });
            return banker;
        }
        public void ConfigureBankers(Dictionary<string, string> characterRoles)
        {
            lock (_bankerSync) foreach (var role in characterRoles) Banker(role.Key).Role = role.Value;
        }
        public CensusCycle CurrentCensus() { lock (_bankerSync) return _census?.Copy(); }
        public void SetCensus(CensusCycle cycle) { lock (_bankerSync) _census = cycle?.Copy(); }
        public bool InitialAuditUsed(string character) { lock (_bankerSync) return _initialAudits.Contains(character); }
        public bool ConsumeInitialAudit(string character) { lock (_bankerSync) return _initialAudits.Add(character); }
        public void ReportBankerPresence(string character, string connection, long stamp)
        {
            lock (_bankerSync) { var banker = Banker(character); banker.Connection = connection; banker.PresenceStamp = stamp; }
        }
        public CensusPresence BankerPresence(string character)
        {
            lock (_bankerSync)
            {
                var banker = Banker(character);
                return banker.Connection == null ? null : new CensusPresence { Connection = banker.Connection, Stamp = banker.PresenceStamp };
            }
        }
        public void ClearBankerPresence(string character)
        {
            lock (_bankerSync) { var banker = Banker(character); banker.Connection = null; banker.PresenceStamp = 0; }
        }
        public void ReportBankerOperational(string character, string connection, string cycle, long stamp)
        {
            lock (_bankerSync)
            {
                var banker = Banker(character);
                banker.OperationalConnection = connection; banker.OperationalCycle = cycle; banker.OperationalStamp = stamp;
            }
        }
        public void ClearBankerOperational(string character)
        { lock (_bankerSync) Banker(character).OperationalStamp = 0; }
        public void HoldBanker(string character, string reason)
        {
            lock (_bankerSync)
            {
                var banker = Banker(character);
                banker.Hold = reason; banker.ReadyCycle = banker.ReadyConnection = null; banker.OperationalStamp = 0;
            }
        }
        public void ReadyBanker(string character, string cycle, string connection)
        {
            lock (_bankerSync)
            {
                var banker = Banker(character);
                banker.Hold = null; banker.ReadyCycle = cycle; banker.ReadyConnection = connection;
            }
        }
        public bool BankerHasReadyToken(string character, string cycle, string connection)
        {
            lock (_bankerSync) { var banker = Banker(character); return banker.Hold == null && banker.ReadyCycle == cycle && banker.ReadyConnection == connection; }
        }
        public BankerAvailabilitySnapshot BankerAvailability()
        {
            lock (_bankerSync) return new BankerAvailabilitySnapshot { Cycle = _census?.Copy(), Bankers = _bankers.Values.Select(b => b.Copy()).ToList() };
        }
        public CensusAdmission Admission(string character, bool grant = false)
        {
            lock (_bankerSync) { CensusAdmission result; return (grant ? _admissionGrants : _admissions).TryGetValue(character, out result) ? result.Copy() : null; }
        }
        public void SetAdmission(string character, CensusAdmission admission, bool grant = false)
        {
            lock (_bankerSync)
            {
                var entries = grant ? _admissionGrants : _admissions;
                if (admission == null) entries.Remove(character); else entries[character] = admission.Copy();
            }
        }
        public void IssueInitialAudit(string character, BagAuditCommand command)
        {
            lock (_bankerSync) { _auditResults.Remove(character); _auditCommands[character] = command.Copy(); }
        }
        public BagAuditCommand InitialAuditCommand(string character)
        { lock (_bankerSync) { BagAuditCommand command; return _auditCommands.TryGetValue(character, out command) ? command.Copy() : null; } }
        public void ClearInitialAuditCommand(string character)
        { lock (_bankerSync) _auditCommands.Remove(character); }
        public void ReportInitialAudit(BagAuditResult result)
        { lock (_bankerSync) _auditResults[result.Character] = result.Copy(); }
        public BagAuditResult InitialAuditResult(string character)
        { lock (_bankerSync) { BagAuditResult result; return _auditResults.TryGetValue(character, out result) ? result.Copy() : null; } }
        public void AcceptInitialCensus(string character, BagAuditResult result)
        { lock (_bankerSync) _completedCensuses[character] = result.Copy(); }
        public BagAuditResult CompletedInitialCensus(string character)
        { lock (_bankerSync) { BagAuditResult result; return _completedCensuses.TryGetValue(character, out result) ? result.Copy() : null; } }
    }
}
