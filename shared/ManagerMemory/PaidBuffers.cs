using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    public sealed class PaidBufferNano
    {
        public readonly int Id, Profession, Level, Ncu;
        public readonly string Name, Description;
        public readonly string[] Tags;
        public int[] EffectIds { get; internal set; }
        public bool IsTeam => Id == 275043 || Id == 235291;
        public bool MatchesEffect(int id) => Id == id || (EffectIds != null && EffectIds.Contains(id));
        internal PaidBufferNano(int id, int profession, int level, int ncu,
            string name, string description, params string[] tags)
        {
            Id = id; Profession = profession; Level = level; Ncu = ncu;
            Name = name; Description = description; Tags = tags;
        }
    }

    // Cold-start routing advertises the configured role, never invented uploaded nanos.
    // Every request is checked against the actual SpellList after login.
    public static class PaidBufferCatalogue
    {
        private static readonly PaidBufferNano[] Nanos = {
            new PaidBufferNano(273629, 9, 215, 56, "Improved Essence of Behemoth", "+1998 HP, +54 Strength/Stamina", "ibehe", "ibehemo", "es1"),
            new PaidBufferNano(222856, 10, 0, 56, "Improved Instinctive Control", "+350 Nano Init, +75 Nano Resist, +300 Max Nano", "iic"),
            new PaidBufferNano(235291, 7, 205, 53, "Umbral Wrangler (Premium)", "Team +147-153 weapon/nano skills (perks); recipient effect 250 seconds", "umbral")
                { EffectIds = new[] { 235064, 235263, 235264, 235265 } },
            new PaidBufferNano(252050, 4, 205, 25, "Lasting Ultimatum", "+466–502 HoT", "lh1", "lu"),
            new PaidBufferNano(275043, 4, 215, 1, "Firewalled Sync Compressor", "Team +500 NCU", "ncu", "fsc")
                { EffectIds = new[] { 275044, 275135 } },
            new PaidBufferNano(227680, 3, 210, 55, "Gift of Assurance", "+5000 AC, 4 hours, Shadowlands required", "goa", "ga"),
            new PaidBufferNano(220345, 12, 0, 7, "Neuronal Stimulator", "+23 Intelligence/Psychic, 4 hours", "ns"),
            new PaidBufferNano(220331, 12, 15, 6, "Composite Teachings", "+25 nano skills, 4 hours, Shadowlands required", "ct", "compt"),
            new PaidBufferNano(220333, 12, 40, 13, "Composite Mastery", "+50 nano skills, 4 hours, Shadowlands required", "cma", "cmastery", "compmast"),
            new PaidBufferNano(220335, 12, 90, 25, "Composite Infuse With Knowledge", "+90 nano skills, 4 hours, Shadowlands required", "ci", "cominf"),
            new PaidBufferNano(220337, 12, 175, 48, "Composite Mochams (1 hour)", "+140 nano skills, Shadowlands required", "cm1h", "cm1", "cm", "compmoch1"),
            new PaidBufferNano(220339, 12, 201, 51, "Composite Mochams (2 hours)", "+140 nano skills, Shadowlands required", "cm2h", "cm2", "compmoch2"),
            new PaidBufferNano(220341, 12, 205, 54, "Composite Mochams (4 hours)", "+140 nano skills, Shadowlands required", "cm4h", "cm4", "compmoch4"),
            new PaidBufferNano(220343, 12, 209, 55, "Composite Mochams (8 hours)", "+140 nano skills, Shadowlands required", "cm8h", "cm8", "compmoch8")
        };
        public static bool UsesManagerTeam(int profession) => profession == 4 || profession == 7;

        // Team ownership belongs to the requested nano, not the caster's profession.
        // In particular, Lasting Ultimatum is a direct target HoT.
        public static bool RequiresManagerTeam(BufferPublicCommandRequest request) =>
            request != null && !string.IsNullOrWhiteSpace(request.TargetCharacter) &&
            string.Equals(request.Command, "cast", StringComparison.OrdinalIgnoreCase) &&
            (request.ObservedNanoId > 0
                ? FindEffect(request.ObservedNanoId)?.IsTeam == true
                : (request.Arguments ?? new string[0]).Any(tag => Find(tag)?.IsTeam == true));

        public static int ProfessionId(string name)
        {
            if (string.Equals(name, "Enforcer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Enf", StringComparison.OrdinalIgnoreCase)) return 9;
            if (string.Equals(name, "Doctor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Doc", StringComparison.OrdinalIgnoreCase)) return 10;
            if (string.Equals(name, "Trader", StringComparison.OrdinalIgnoreCase)) return 7;
            if (string.Equals(name, "Engineer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Engi", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(name, "Fixer", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(name, "MP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Metaphysicist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Meta-Physicist", StringComparison.OrdinalIgnoreCase)) return 12;
            return 0;
        }
        public static int ConfiguredProfession(string profession, string character)
        {
            if (!string.IsNullOrWhiteSpace(profession)) return ProfessionId(profession);
            // Existing owner-specified providers: omission is not a universal Fixer default.
            if (string.Equals(character, "Krumpie", StringComparison.OrdinalIgnoreCase)) return 9;
            if (string.Equals(character, "Doczy", StringComparison.OrdinalIgnoreCase)) return 10;
            if (string.Equals(character, "Patriciana", StringComparison.OrdinalIgnoreCase)) return 7;
            if (string.Equals(character, "Kavsta", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(character, "Littleangie", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(character, "Zdrahonia", StringComparison.OrdinalIgnoreCase)) return 12;
            return 0;
        }
        public static string ProfessionName(int profession) => profession == 12 ? "MP" :
            profession == 4 ? "Fixer" : profession == 3 ? "Engineer" :
            profession == 9 ? "Enforcer" : profession == 10 ? "Doctor" : profession == 7 ? "Trader" : "Unconfigured";
        public static PaidBufferNano Find(string tag)
        {
            int id;
            bool numeric = int.TryParse(tag, out id);
            return Nanos.FirstOrDefault(n => (numeric && n.Id == id) ||
                n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }
        public static PaidBufferNano FindEffect(int id) => Nanos.FirstOrDefault(n => n.MatchesEffect(id));
        public static PaidBufferNano[] ForProfession(int profession) =>
            Nanos.Where(n => n.Profession == profession).ToArray();
    }

    [Serializable]
    public sealed class PaidBufferSession
    {
        public string Character;
        public int Profession;
        public bool Running, Ready, Draining, Parked, Blocked;
        public DateTime StartedUtc, ObservedUtc, BusyUtc, RetryAfterUtc;
        public int TeamCastNano;
        public DateTime TeamCastAttemptedUtc, TeamReleaseAfterUtc, TeamCastDeadlineUtc;
        public int[] TeamCastMembers = new int[0];
        internal PaidBufferSession Copy()
        {
            var copy = (PaidBufferSession)MemberwiseClone();
            copy.TeamCastMembers = TeamCastMembers.ToArray();
            return copy;
        }
    }

    public sealed partial class ManagerMemory
    {
        // Account names never leave this in-memory arbiter or enter diagnostics.
        private readonly object _aoAccountSync = new object();
        private readonly Dictionary<string, string> _aoAccountOwners =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aoAccountWaiters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PaidBufferSession> _paidBuffers =
            new Dictionary<string, PaidBufferSession>(StringComparer.OrdinalIgnoreCase);

        public bool TryAcquireAoAccount(string account, string owner, bool priority)
        {
            lock (_aoAccountSync)
            {
                string current, waiter;
                if (priority) _aoAccountWaiters[account] = owner;
                if (_aoAccountOwners.TryGetValue(account, out current)) return current == owner;
                if (_aoAccountWaiters.TryGetValue(account, out waiter) && waiter != owner) return false;
                _aoAccountOwners.Add(account, owner);
                if (waiter == owner) _aoAccountWaiters.Remove(account);
                return true;
            }
        }

        public bool AoAccountHasWaiter(string account)
        {
            lock (_aoAccountSync) return _aoAccountWaiters.ContainsKey(account);
        }

        public void CancelAoAccountWait(string account, string owner)
        {
            lock (_aoAccountSync)
            {
                string current;
                if (_aoAccountWaiters.TryGetValue(account, out current) && current == owner)
                    _aoAccountWaiters.Remove(account);
            }
        }

        public void ReleaseAoAccount(string account, string owner)
        {
            lock (_aoAccountSync)
            {
                string current;
                if (_aoAccountOwners.TryGetValue(account, out current) && current == owner)
                    _aoAccountOwners.Remove(account);
            }
        }

        public void RegisterPaidBuffers(PaidBufferSession[] providers)
        {
            lock (_bufferPublicCommandSync)
            {
                // A failed unload must not reset a live session or its account lock.
                if (_paidBuffers.Values.Any(p => p.Running))
                    throw new InvalidOperationException("Previous paid buffer session has not unloaded.");
                _paidBuffers.Clear();
                foreach (var provider in providers)
                    _paidBuffers.Add(provider.Character, new PaidBufferSession {
                        Character = provider.Character, Profession = provider.Profession });
            }
        }

        public PaidBufferSession[] ReadPaidBuffers()
        {
            lock (_bufferPublicCommandSync) return _paidBuffers.Values.Select(p => p.Copy()).ToArray();
        }

        public PaidBufferSession ReadPaidBuffer(string character)
        {
            lock (_bufferPublicCommandSync) return PaidBufferLocked(character)?.Copy();
        }

        private PaidBufferSession PaidBufferLocked(string character)
        {
            PaidBufferSession state;
            return character != null && _paidBuffers.TryGetValue(character, out state) ? state : null;
        }

        public bool IsPaidBuffer(string character)
        {
            lock (_bufferPublicCommandSync) return IsPaidBufferLocked(character);
        }

        private bool IsPaidBufferLocked(string character) => PaidBufferLocked(character) != null;

        private bool HasPaidRequestsLocked(string character) =>
            _bufferPublicCommands.Values.Any(r =>
                string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                (r.Accepted || PaidRequestReadyLocked(r)));

        private bool PaidRequestAdmittedLocked(BufferPublicCommandRequest request) =>
            PaidRequestReadyLocked(request) && (!PaidBufferCatalogue.RequiresManagerTeam(request) ||
            (string.Equals(request.TargetCharacter, _paidFixerTeamCharacter, StringComparison.OrdinalIgnoreCase) &&
             PaidFixerMemberLocked(request.SenderId)));

        public DateTime OldestPaidBufferRequest(string character)
        {
            lock (_bufferPublicCommandSync)
            {
                PruneBufferPublicCommands(DateTime.UtcNow);
                return _bufferPublicCommands.Values.Where(r =>
                    string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                    !r.Accepted && PaidRequestReadyLocked(r))
                    .Select(r => r.CreatedUtc).DefaultIfEmpty(DateTime.MaxValue).Min();
            }
        }

        private string _paidFixerTeamCharacter;
        private int _paidFixerManagerId;
        private int[] _paidFixerTeamMembers = new int[0];
        private DateTime _paidFixerTeamObservedUtc;
        private DateTime _nextPaidFixerLoginUtc;
        private bool _paidFixerBatchReady;

        // Only the Manager's AO thread publishes actual team membership. No invitation
        // or chat acknowledgement is sufficient evidence to start a fixer session.
        public void PublishPaidFixerTeam(string character, int managerId, int[] members, bool batchReady = false)
        {
            lock (_bufferPublicCommandSync)
            {
                _paidFixerTeamCharacter = character;
                _paidFixerManagerId = managerId;
                _paidFixerTeamMembers = (members ?? new int[0]).Distinct().ToArray();
                _paidFixerBatchReady = character != null && batchReady;
                _paidFixerTeamObservedUtc = DateTime.UtcNow;
            }
        }

        private bool PaidFixerMemberLocked(uint requester) =>
            _paidFixerBatchReady && _paidFixerManagerId != 0 &&
            _paidFixerTeamObservedUtc >= DateTime.UtcNow.AddSeconds(-3) &&
            _paidFixerTeamMembers.Contains(unchecked((int)requester));

        public int PaidFixerManager(string character)
        {
            lock (_bufferPublicCommandSync)
            {
                var state = PaidBufferLocked(character);
                return state != null && _paidFixerBatchReady && PaidBufferCatalogue.UsesManagerTeam(state.Profession) && state.Running &&
                    string.Equals(character, _paidFixerTeamCharacter, StringComparison.OrdinalIgnoreCase) &&
                    _paidFixerTeamObservedUtc >= DateTime.UtcNow.AddSeconds(-3)
                    ? _paidFixerManagerId : 0;
            }
        }

        public bool PaidFixerMember(string character, int requester)
        {
            lock (_bufferPublicCommandSync)
                return PaidBufferCatalogue.UsesManagerTeam(PaidBufferLocked(character)?.Profession ?? 0) &&
                    string.Equals(character, _paidFixerTeamCharacter, StringComparison.OrdinalIgnoreCase) &&
                    PaidFixerMemberLocked(unchecked((uint)requester));
        }

        public bool StartPaidBufferSession(string character)
        {
            lock (_bufferPublicCommandSync)
            {
                var _paidBuffer = _paidBuffers[character];
                if (_paidBuffers.Values.Any(p => p.Running) || _paidBuffer.Blocked || DateTime.UtcNow < _paidBuffer.RetryAfterUtc)
                    return false;
                if (_paidPoolUpdates != 0 || !_bufferPublicCommands.Values.Any(r =>
                    string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                    PaidRequestAdmittedLocked(r))) return false;
                if (PaidBufferCatalogue.UsesManagerTeam(_paidBuffer.Profession))
                {
                    if (DateTime.UtcNow < _nextPaidFixerLoginUtc) return false;
                    // Prefer gathering the pending team batch before spending a login.
                    // Direct-only work needs neither a team nor four customers.
                    var teamRequests = _bufferPublicCommands.Values.Where(r =>
                        string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                        PaidBufferCatalogue.RequiresManagerTeam(r) && PaidRequestReadyLocked(r)).ToArray();
                    if (teamRequests.Length != 0 && !teamRequests.Any(PaidRequestAdmittedLocked)) return false;
                    _nextPaidFixerLoginUtc = DateTime.UtcNow.AddSeconds(30);
                }
                _paidBuffer.Running = true;
                _paidBuffer.Ready = _paidBuffer.Draining = _paidBuffer.Parked = false;
                _paidBuffer.StartedUtc = _paidBuffer.ObservedUtc = _paidBuffer.BusyUtc = DateTime.UtcNow;
                _paidBuffer.TeamCastNano = 0;
                _paidBuffer.TeamCastAttemptedUtc = _paidBuffer.TeamReleaseAfterUtc =
                    _paidBuffer.TeamCastDeadlineUtc = DateTime.MinValue;
                _paidBuffer.TeamCastMembers = new int[0];
                return true;
            }
        }

        public void DrainPaidBuffer(string character)
        {
            lock (_bufferPublicCommandSync)
            {
                var state = PaidBufferLocked(character);
                if (state != null) state.Draining = true;
            }
        }

        public void ReportPaidTeamDelivery(string character, int nanoId, int[] members,
            DateTime attemptedUtc, DateTime releaseAfterUtc, DateTime deadlineUtc)
        {
            lock (_bufferPublicCommandSync)
            {
                var state = PaidBufferLocked(character);
                if (state == null || !state.Running || PaidBufferCatalogue.FindEffect(nanoId)?.IsTeam != true) return;
                state.TeamCastNano = nanoId;
                state.TeamCastMembers = members.Distinct().ToArray();
                state.TeamCastAttemptedUtc = attemptedUtc;
                state.TeamReleaseAfterUtc = releaseAfterUtc;
                state.TeamCastDeadlineUtc = deadlineUtc;
            }
        }

        // Called on the AO update thread after command admission. Parking and claiming
        // share the same lock: an idle snapshot cannot race a newly accepted cast.
        public bool UpdatePaidBufferActivity(string character, bool ready, bool busy)
        {
            lock (_bufferPublicCommandSync)
            {
                var _paidBuffer = PaidBufferLocked(character);
                if (!IsPaidBufferLocked(character) || !_paidBuffer.Running) return false;
                var now = DateTime.UtcNow;
                _paidBuffer.ObservedUtc = now;
                _paidBuffer.Ready = ready;
                PruneBufferPublicCommands(now);
                if (busy || (!_paidBuffer.Draining && HasPaidRequestsLocked(character))) _paidBuffer.BusyUtc = now;
                // Queue completion alone must not park the team caster before
                // Manager confirms delivery to every customer and releases the batch.
                if (_paidPoolUpdates == 0 && ready && !busy && (_paidBuffer.Draining ||
                    (!HasPaidRequestsLocked(character) &&
                     !(string.Equals(character, _paidFixerTeamCharacter, StringComparison.OrdinalIgnoreCase) &&
                       _paidFixerBatchReady && _paidFixerTeamObservedUtc >= now.AddSeconds(-3)))))
                    _paidBuffer.Parked = _paidBuffer.Draining = true;
                System.Threading.Monitor.PulseAll(_bufferPublicCommandSync);
                return _paidBuffer.Parked;
            }
        }

        public void StopPaidBufferSession(string character, bool failed, string message)
        {
            lock (_bufferPublicCommandSync)
            {
                var _paidBuffer = PaidBufferLocked(character);
                if (_paidBuffer == null) return;
                _paidBuffer.Running = _paidBuffer.Ready = false;
                // A priority account handoff can unload between caster acceptance
                // and Manager's delivery acknowledgement. Preserve that work.
                foreach (var request in _bufferPublicCommands.Values.Where(r => r.Accepted &&
                    string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase)))
                {
                    request.Accepted = false;
                    request.Arguments = request.PendingNanoIds.Select(id => id.ToString()).ToArray();
                    request.ClaimedBy = null;
                    request.ClaimedUtc = null;
                }
                PaidPoolChangedLocked();
                _paidBuffer.RetryAfterUtc = DateTime.UtcNow.AddSeconds(failed ? 60 : 0);
                if (failed) FailPaidRequestsLocked(character, message);
                System.Threading.Monitor.PulseAll(_bufferPublicCommandSync);
            }
        }

        // An accepted public command means queued, not cast. A macro advances
        // only after the provider has finished its queue and post-cast team hold.
        public bool WaitForPaidBufferIdle(string character, int timeoutMilliseconds)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            lock (_bufferPublicCommandSync)
            {
                while (true)
                {
                    var state = PaidBufferLocked(character);
                    if (state == null || !state.Running || state.Parked || state.Blocked) return true;
                    int remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                    if (remaining <= 0) return false;
                    System.Threading.Monitor.Wait(_bufferPublicCommandSync, remaining);
                }
            }
        }

        public void FailPaidBufferRequests(string message)
        {
            lock (_bufferPublicCommandSync)
                foreach (var character in _paidBuffers.Keys) FailPaidRequestsLocked(character, message);
        }

        public void BlockPaidBuffer(string character, string message)
        {
            lock (_bufferPublicCommandSync)
            {
                var _paidBuffer = PaidBufferLocked(character);
                if (_paidBuffer == null) return;
                _paidBuffer.Blocked = _paidBuffer.Draining = true;
                FailPaidRequestsLocked(character, message);
            }
        }

        private void FailPaidRequestsLocked(string character, string message)
        {
            foreach (var r in _bufferPublicCommands.Values.Where(r =>
                string.Equals(r.TargetCharacter, character, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _bufferPublicCommands.Remove(r.Id);
                _bufferPublicOutcomes[r.Id] = new BufferPublicCommandOutcome {
                    Id = r.Id, Success = false, Message = message, CompletedUtc = DateTime.UtcNow
                };
            }
            System.Threading.Monitor.PulseAll(_bufferPublicCommandSync);
            PaidPoolChangedLocked();
        }
    }
}



