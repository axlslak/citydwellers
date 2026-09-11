using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    // A connection is a separate custody epoch even inside the same host process.
    // The coordinator freezes operational actors before collecting any new plan.
    public sealed class StartupCensusGate : ClientlessPluginEntry
    {
        private static readonly string Generation = Process.GetCurrentProcess().Id + "-" +
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private static string _directory;
        private static string _memberCharacter;
        private static string MemberCharacter => _memberCharacter ?? Client.CharacterName;
        private static string _connection = Guid.NewGuid().ToString("N");
        private static string[] _characters;
        private static bool _invalidated;
        private static long _holdVersion;
        private static string _holdCycle;
        private static string _recoveryRequest;
        private static bool _requestPublished;
        private static readonly List<EventHandler<double>> Deferred = new List<EventHandler<double>>();
        private string _settings, _character, _role, _auditCycle, _auditRun, _signature, _error;
        private long _auditPause;
        private long _presenceRetryAfter;
        private bool _issued, _finished, _quiesced;
        private readonly Stopwatch _poll = Stopwatch.StartNew();
        private readonly Stopwatch _gather = Stopwatch.StartNew();
        private readonly Stopwatch _settled = Stopwatch.StartNew();
        private readonly Stopwatch _retry = Stopwatch.StartNew();
        private JObject _roles;

        internal sealed class Presence
        {
            public string Connection;
            public long Stamp;
        }
        internal sealed class Cycle
        {
            public string Id;
            public string Phase;
            public Dictionary<string, string> Participants;
        }

        public static bool UsesPhysicalRecovery => !ServicePolicy.IsBagAuditMode();
        public static string CensusDirectory(string settings) => Path.Combine(
            RuntimeStateStore.GetDataDirectory(settings), "startup-census", Generation);
        private static string MemberPath(string character, string suffix) => Path.Combine(_directory, character.ToLowerInvariant() + suffix);
        private static string CyclePath => Path.Combine(_directory, "cycle.json");
        private static string CycleDirectory(Cycle cycle) => Path.Combine(_directory, "cycle-" + cycle.Id);
        private static T Read<T>(string path) where T : class => CensusApplication.ReadExisting<T>(path);
        private static Cycle Current() => Read<Cycle>(CyclePath);
        private static bool Requested() => _characters.Any(c => File.Exists(MemberPath(c, ".recovery.json")));
        private static bool Includes(Cycle cycle, string character, string connection) =>
            cycle?.Participants != null && cycle.Participants.Any(p =>
                string.Equals(p.Key, character, StringComparison.OrdinalIgnoreCase) && p.Value == connection);
        private static bool Present(string character, string connection)
        {
            var presence = Read<Presence>(MemberPath(character, ".presence.json"));
            long age = presence == null ? -1 : Stopwatch.GetTimestamp() - presence.Stamp;
            return presence?.Connection == connection && age >= 0 && age < Stopwatch.Frequency * 10;
        }
        private static void Locked(Action action)
        {
            using (var mutex = new Mutex(false, "CityBankers.Census." + Generation))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(1000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Census coordinator is busy.");
                    action();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        public static bool IsOpen
        {
            get
            {
                if (ServicePolicy.IsBagAuditMode() || _invalidated || _directory == null || !Client.InPlay) return false;
                try
                {
                    var cycle = Current();
                    return cycle?.Phase == "released" && Includes(cycle, MemberCharacter, _connection) &&
                        !Requested() && File.ReadAllText(MemberPath(MemberCharacter, ".ready")) == cycle.Id + "/" + _connection;
                }
                catch (Exception) { return false; }
            }
        }

        public static bool Defer(Action initialize)
        {
            if (ServicePolicy.IsBagAuditMode()) return true;
            if (IsOpen) return false;
            EventHandler<double> pending = null;
            pending = (sender, delta) =>
            {
                if (!IsOpen) return;
                Client.OnUpdate -= pending;
                Deferred.Remove(pending);
                try { initialize(); }
                catch (Exception ex) { Block("Operational initialization failed: " + ex); }
            };
            Deferred.Add(pending);
            Client.OnUpdate += pending;
            return true;
        }

        private static void Hold(string reason)
        {
            _holdVersion++;
            _invalidated = true;
            if (_directory == null) return;
            _holdCycle = Current()?.Id;
            RuntimeStateStore.WriteJsonAtomic(MemberPath(MemberCharacter, ".blocked"), new { Reason = reason });
            RuntimeStateStore.DeleteIfExists(MemberPath(MemberCharacter, ".ready"));
        }

        public static void Block(string reason)
        {
            // Ordinary local census pauses do not request a roster-wide recovery.
            // An unrelated error explicitly supersedes that owner through a new
            // cycle; no old local completion is allowed to release its token.
            _invalidated = true;
            _holdVersion++;
            _recoveryRequest = _recoveryRequest ?? Guid.NewGuid().ToString("N");
            _requestPublished = false;
            try
            {
                if (_directory != null) Locked(() =>
                {
                    Hold(reason);
                    PublishRequest(reason);
                });
            }
            catch (Exception ex) { Logger.Error("[CityBankers] Recovery publication retry: " + ex.Message); }
            Logger.Error("[CityBankers] CENSUS RECOVERY " + MemberCharacter + ": " + reason);
        }

        private static void PublishRequest(string reason)
        {
            RuntimeStateStore.WriteJsonAtomic(MemberPath(MemberCharacter, ".recovery.json"), new
            { Id = _recoveryRequest, Connection = _connection, Reason = reason });
            _requestPublished = true;
        }

        internal static bool RosterRecoveryActive
        {
            get
            {
                try { return _directory == null || Current()?.Phase != "released" || Requested(); }
                catch (Exception) { return true; }
            }
        }

        internal static long PauseLocalCensus(string reason)
        {
            if (!IsOpen) throw new InvalidOperationException("Cannot replace an unrelated census hold.");
            Hold(reason);
            return _holdVersion;
        }
        internal static bool OwnsLocalPause(long version)
        {
            if (!_invalidated || version != _holdVersion) return false;
            var cycle = Current();
            return cycle?.Id == _holdCycle && Includes(cycle, MemberCharacter, _connection);
        }
        internal static bool IsCurrentParticipant
        {
            get
            {
                try { return Includes(Current(), MemberCharacter, _connection); }
                catch (Exception) { return false; }
            }
        }
        internal static bool ResumeLocalCensus(long version)
        {
            if (!OwnsLocalPause(version) || !Client.InPlay || Requested()) return false;
            var cycle = Current();
            if (cycle?.Phase != "released" || !Includes(cycle, MemberCharacter, _connection)) return false;
            RuntimeStateStore.DeleteIfExists(MemberPath(MemberCharacter, ".blocked"));
            File.WriteAllText(MemberPath(MemberCharacter, ".ready"), cycle.Id + "/" + _connection);
            _invalidated = false;
            return true;
        }

        public override void Init(string pluginDir)
        {
            if (ServicePolicy.IsBagAuditMode()) return;
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settings, out error)) throw new InvalidOperationException(error);
            _roles = SettingsPaths.ReadBankersSettings(_settings)["Roles"] as JObject;
            _characters = _roles?.Properties().Select(p => (string)p.Value["Character"]).ToArray();
            if (_characters == null || _characters.Length != 9 || _characters.Any(string.IsNullOrWhiteSpace) ||
                _characters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 9)
            { Block("Census requires nine distinct configured bankers."); return; }
            _character = Client.CharacterName;
            _memberCharacter = _character;
            _role = _roles.Properties().Single(p => string.Equals((string)p.Value["Character"],
                _character, StringComparison.OrdinalIgnoreCase)).Name;
            _directory = CensusDirectory(_settings);
            Directory.CreateDirectory(_directory);
            _connection = Guid.NewGuid().ToString("N");
            Client.OnUpdate += Tick;
            Client.Disconnected += OnDisconnected;
            Trade.TradeOpened += RejectBeforeCensus;
        }
        private void RejectBeforeCensus(Identity target) { if (!IsOpen) Trade.Decline(); }
        private void OnDisconnected()
        {
            _connection = Guid.NewGuid().ToString("N");
            Block("Banker disconnected; retire connection-bound work and obtain fresh physical evidence.");
            try { RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".presence.json")); }
            finally { BagAuditAgent.CancelForRecovery(); }
        }
        public override void Teardown()
        {
            Client.OnUpdate -= Tick;
            foreach (var pending in Deferred) Client.OnUpdate -= pending;
            Deferred.Clear();
            Client.Disconnected -= OnDisconnected;
            Trade.TradeOpened -= RejectBeforeCensus;
            if (_directory == null) return;
            Block("Banker unloaded; connection-bound work is retired.");
            RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".presence.json"));
        }

        private void Tick(object sender, double delta)
        {
            if (_poll.ElapsedMilliseconds < 500) return;
            _poll.Restart();
            try
            {
                if (!Client.InPlay || !Inventory.Bank.IsOpen || Stopwatch.GetTimestamp() < _presenceRetryAfter) return;
                RuntimeStateStore.WriteJsonAtomic(MemberPath(_character, ".presence.json"),
                    new Presence { Connection = _connection, Stamp = Stopwatch.GetTimestamp() });
                // Retry a failed request write. Joining a new cycle consumes it.
                if (_recoveryRequest != null && !_requestPublished) Locked(() => PublishRequest("Retained local recovery request."));
                if (string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase) && _gather.ElapsedMilliseconds >= 5000) Locked(Coordinate);
                var cycle = Current();
                if (!Includes(cycle, _character, _connection)) return;
                if (cycle.Phase == "released")
                {
                    if (_auditCycle == cycle.Id && _finished) ResumeLocalCensus(_auditPause);
                    return;
                }
                if (cycle.Phase != "collecting" && cycle.Phase != "applying") return;
                if (_auditCycle != cycle.Id)
                {
                    Hold("Coordinated census " + cycle.Id);
                    _auditPause = _holdVersion;
                    _auditCycle = cycle.Id;
                    _auditRun = Guid.NewGuid().ToString("N");
                    _issued = _finished = _quiesced = false;
                    _signature = null;
                    _recoveryRequest = null;
                    BagAuditAgent.CancelForRecovery();
                }
                if (_finished || !OwnsLocalPause(_auditPause)) return;
                if (!_quiesced)
                {
                    if (!BankingServiceAgent.QuiesceForCensus(CycleDirectory(cycle))) return;
                    _quiesced = true;
                }
                if (Trade.IsTrading) { Trade.Decline(); _settled.Restart(); return; }
                string resultPath = Path.Combine(_directory, _character + ".result.json");
                if (!_issued)
                {
                    // Allow outstanding AO moves/closure to settle before the collector
                    // takes responsibility for every bank and inventory bag.
                    string signature = string.Join(";", Inventory.Items.Concat(Inventory.Bank.Items).Where(i => i != null)
                        .Select(i => i.Slot + "/" + i.UniqueIdentity).OrderBy(s => s));
                    if (signature != _signature) { _signature = signature; _settled.Restart(); return; }
                    if (_settled.ElapsedMilliseconds < 2000 || _retry.ElapsedMilliseconds < 3000) return;
                    RuntimeStateStore.DeleteIfExists(resultPath);
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(_directory, _character + ".command.json"),
                        new BagAuditAgent.BagAuditCommand { RunId = _auditRun, Role = _role });
                    _issued = true;
                    return;
                }
                var result = Read<BagAuditAgent.BagAuditResult>(resultPath);
                if (result == null || result.RunId != _auditRun) return;
                if (result.Character != _character || result.Role != _role) throw new InvalidOperationException("Mismatched census result.");
                try { PhysicalLedgerReconciliation.ReadCensus(_settings, result); }
                catch
                {
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(CycleDirectory(cycle), _character + ".failed-" + _auditRun + ".json"), result);
                    _issued = false; _auditRun = Guid.NewGuid().ToString("N"); _retry.Restart();
                    // An unscannable worker is unavailable, not a prerequisite
                    // that prevents healthy members from starting indefinitely.
                    if (!string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
                    {
                        _presenceRetryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
                        RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".presence.json"));
                    }
                    throw;
                }
                RuntimeStateStore.WriteJsonAtomic(Path.Combine(CycleDirectory(cycle), _character.ToLowerInvariant() + ".json"), result);
                _finished = true;
            }
            catch (Exception ex)
            {
                if (_error != ex.Message) Logger.Error("[CityBankers] Census waiting: " + ex.Message);
                _error = ex.Message;
            }
        }

        private void Coordinate()
        {
            var cycle = Current();
            bool membersPresent = cycle?.Participants != null && cycle.Participants.All(p => Present(p.Key, p.Value));
            if (cycle?.Phase == "collecting" && (!membersPresent || Requested()))
            {
                cycle.Phase = "superseded";
                RuntimeStateStore.WriteJsonAtomic(CyclePath, cycle);
            }
            if (cycle?.Phase == "collecting" || cycle?.Phase == "applying")
            {
                var censuses = cycle.Participants.Keys.Select(c => Read<BagAuditAgent.BagAuditResult>(
                    Path.Combine(CycleDirectory(cycle), c.ToLowerInvariant() + ".json"))).ToList();
                if (censuses.Any(c => c == null)) return;
                // Finish an interrupted fixed application even if a peer disappeared.
                // That snapshot is never released; a fresh cycle follows it.
                cycle.Phase = "applying";
                RuntimeStateStore.WriteJsonAtomic(CyclePath, cycle);
                CensusApplication.Apply(_settings, CycleDirectory(cycle), cycle.Id, censuses,
                    _roles.Properties().ToDictionary(p => p.Name, p => (string)p.Value["Character"], StringComparer.OrdinalIgnoreCase));
                bool release = cycle.Participants.All(p => Present(p.Key, p.Value)) && !Requested();
                cycle.Phase = release ? "released" : "superseded";
                if (release)
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(RuntimeStateStore.GetDataDirectory(_settings),
                        TrustedOperators.AllBankersReadyMarkerFileName), new
                    {
                        format = "citybankers-all-bankers-ready-v2", generation = Generation, cycle = cycle.Id,
                        readyUtc = DateTime.UtcNow,
                        characters = cycle.Participants.Select(p => new { character = p.Key, connection = p.Value, role = _roles.Properties().Single(r => string.Equals((string)r.Value["Character"], p.Key, StringComparison.OrdinalIgnoreCase)).Name }).ToList()
                    });
                RuntimeStateStore.WriteJsonAtomic(CyclePath, cycle);
                Logger.Information("[CityBankers] Census " + cycle.Id + " " + cycle.Phase + "; audited bankers=" + censuses.Count);
                return;
            }
            var online = _characters.Select(c => new { Character = c, Presence = Read<Presence>(MemberPath(c, ".presence.json")) })
                .Where(p => p.Presence != null && Present(p.Character, p.Presence.Connection))
                .ToDictionary(p => p.Character, p => p.Presence.Connection, StringComparer.OrdinalIgnoreCase);
            if (!online.ContainsKey(_character)) return;
            if (cycle?.Phase == "released" && !Requested() && membersPresent &&
                online.All(p => Includes(cycle, p.Key, p.Value))) return;
            var next = new Cycle { Id = Guid.NewGuid().ToString("N"), Phase = "collecting", Participants = online };
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(CycleDirectory(next), "participants.json"), next);
            // Close admission before consuming requests. A failed publication
            // must never expose the previous released cycle in between writes.
            RuntimeStateStore.WriteJsonAtomic(CyclePath, next);
            foreach (string character in _characters)
            {
                string request = MemberPath(character, ".recovery.json");
                if (File.Exists(request))
                {
                    File.Copy(request, Path.Combine(CycleDirectory(next), character + ".recovery.json"), true);
                    File.Delete(request);
                }
            }
        }
    }
}
