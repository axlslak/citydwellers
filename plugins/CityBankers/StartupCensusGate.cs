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
        private static string _traceData;
        private static string _memberCharacter;
        private static string MemberCharacter => _memberCharacter ?? Client.CharacterName;
        private static string _connection = Guid.NewGuid().ToString("N");
        private static string[] _characters;
        private static bool _invalidated;
        private static long _holdVersion;
        private static string _holdCycle;
        private static string _recoveryRequest;
        private static bool _requestPublished;
        private static string _idleInventory;
        private string _idleReconnectCycle, _idleReconnectConnection, _idleReconnectInventory;
        private string _reconnectObserved;
        private readonly Stopwatch _reconnectSettled = new Stopwatch();
        private static readonly List<Action> Deferred = new List<Action>();
        private string _settings, _character, _role, _auditCycle, _auditRun, _signature, _error;
        private string _readyLoggedCycle, _handoffError;
        private long _auditPause;
        private long _presenceRetryAfter;
        private long _stagingRetryAfter;
        private int _stagingHolds;
        private int _censusRejections;
        private string _stagingBag, _stagingFailureLayout, _stagingBeforeLayout;
        private string _stagingRecordLocation;
        private int _stagingRecordSlot = -1;
        private int _stagingBankBefore, _stagingInventoryBefore;
        private Identity _stagingIdentity;
        private bool _extraReserveDeferred;
        private readonly Stopwatch _stagingAge = new Stopwatch();
        private bool _issued, _finished, _quiesced;
        private readonly Stopwatch _poll = Stopwatch.StartNew();
        private readonly Stopwatch _gather = Stopwatch.StartNew();
        private readonly Stopwatch _settled = Stopwatch.StartNew();
        private readonly Stopwatch _retry = Stopwatch.StartNew();
        private JObject _roles;
        private string _capacityLoggedRun;
        private string _duplicateLoggedRun;

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
                try
                {
                    // Plugin Init runs before the network session exists. InPlay
                    // can throw then; fail closed so Defer still queues startup.
                    if (ServicePolicy.IsBagAuditMode() || !ClientlessSessionGuard.BankCacheTrusted ||
                        _invalidated || _directory == null || !Client.InPlay) return false;
                    var cycle = Current();
                    return cycle?.Phase == "released" && Includes(cycle, MemberCharacter, _connection) &&
                        !Requested() && RuntimeStateStore.ReadTextStrict(MemberPath(MemberCharacter, ".ready")) == cycle.Id + "/" + _connection;
                }
                catch (Exception) { return false; }
            }
        }

        // Written only by the running banking actor, never by census completion.
        internal static void PublishOperational()
        {
            var cycle = Current();
            if (cycle?.Phase != "released" || !Includes(cycle, MemberCharacter, _connection) || !IsOpen) return;
            _idleInventory = BankingServiceAgent.CanResumeIdleConnection() ? ReconnectInventory() : null;
            RuntimeStateStore.WriteJsonAtomic(MemberPath(MemberCharacter, ".operational.json"),
                new { Cycle = cycle.Id, Connection = _connection, Stamp = Stopwatch.GetTimestamp() });
        }

        internal static void ClearOperational()
        {
            if (_directory != null)
                RuntimeStateStore.DeleteIfExists(MemberPath(MemberCharacter, ".operational.json"));
        }

        public static bool Defer(Action initialize)
        {
            if (ServicePolicy.IsBagAuditMode()) return true;
            if (IsOpen) return false;
            // The census gate owns deferred startup. Do not depend on callback
            // registration/removal while another OnUpdate handler is running.
            Deferred.Add(initialize);
            return true;
        }

        internal static void CancelDeferred(Action initialize) => Deferred.Remove(initialize);

        private static void InitializeDeferred()
        {
            foreach (var initialize in Deferred.ToArray())
            {
                if (!IsOpen) return;
                Deferred.Remove(initialize);
                try
                {
                    Logger.Information("[CityBankers] Starting deferred component " +
                        initialize.Method.DeclaringType?.FullName + "." + initialize.Method.Name);
                    initialize();
                }
                catch (Exception ex) { Block("Operational initialization failed: " + ex); return; }
            }
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
            BankingServiceAgent.TraceRecovery(reason, "recovery-request:" + _recoveryRequest);
            if (_directory != null)
                CityDwellers.Shared.IncidentJournal.Record(_traceData,
                    "recovery-request:" + _recoveryRequest, MemberCharacter, "recovery.requested", new { Reason = reason }, true);
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
            CityDwellers.Shared.ServiceEvents.Report("census.recovery", "error", reason);
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
            _traceData = RuntimeStateStore.GetDataDirectory(_settings);
            Directory.CreateDirectory(_directory);
            _connection = Guid.NewGuid().ToString("N");
            Client.OnUpdate += Tick;
            Client.Disconnected += OnDisconnected;
            Trade.TradeOpened += RejectBeforeCensus;
        }
        private void RejectBeforeCensus(Identity target) { if (!IsOpen) Trade.Decline(); }
        private void OnDisconnected()
        {
            _stagingBag = _stagingFailureLayout = _stagingBeforeLayout = null;
            _extraReserveDeferred = false;
            _presenceRetryAfter = 0;
            _censusRejections = 0;
            string retiredConnection = _connection;
            bool invalidatedBeforeDisconnect = _invalidated;
            _connection = Guid.NewGuid().ToString("N");
            const string reason = "Banker disconnected; retire connection-bound work and obtain fresh physical evidence.";
            // Invalidate this actor immediately, including any local audit token.
            // A failed reconnect is not a new loss of custody for the roster.
            _invalidated = true;
            _holdVersion++;
            bool recoveryRequested = false;
            try
            {
                Locked(() =>
                {
                    var cycle = Current();
                    // Preserve the original proof through unsuccessful reconnect
                    // attempts. They cannot create new item movements.
                    bool idle = (_idleReconnectCycle != null && _idleReconnectCycle == cycle?.Id) ||
                        (cycle?.Phase == "released" && !invalidatedBeforeDisconnect &&
                         _idleInventory != null && BankingServiceAgent.CanResumeIdleConnection());
                    if (idle && _idleReconnectCycle == null && Includes(cycle, _character, retiredConnection))
                    {
                        _idleReconnectCycle = cycle.Id;
                        _idleReconnectConnection = retiredConnection;
                        _idleReconnectInventory = _idleInventory;
                    }
                    _reconnectObserved = null;
                    _reconnectSettled.Reset();
                    Hold(reason);
                    ClearOperational();
                    RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".presence.json"));
                    // Compare the retired epoch, not the newly allocated one.
                    // Coordinate uses this same mutex: it either already excluded
                    // us or must reconcile the participant it still depends on.
                    if (!idle && Includes(cycle, _character, retiredConnection))
                    {
                        _recoveryRequest = _recoveryRequest ?? Guid.NewGuid().ToString("N");
                        _requestPublished = false;
                        PublishRequest(reason);
                        recoveryRequested = true;
                    }
                    // Preserve any independent recovery request. Never delete or
                    // acknowledge one just because this connection is absent.
                });
                if (recoveryRequested)
                {
                    Logger.Error("[CityBankers] CENSUS RECOVERY " + MemberCharacter + ": " + reason);
                    CityDwellers.Shared.ServiceEvents.Report("census.recovery", "error", reason);
                }
                else
                    Logger.Information("[CityBankers] " + MemberCharacter +
                        " unavailable; healthy census retained. " +
                        (_idleReconnectCycle != null ? "Idle reconnect will validate inventory without a bag audit." :
                        "Connection was outside the current census roster."));
            }
            catch (Exception ex)
            {
                // Unknown membership/publication state must still fail closed.
                Block("Disconnect recovery could not verify census membership: " + ex.Message);
            }
            finally { BagAuditAgent.CancelForRecovery(); }
        }
        public override void Teardown()
        {
            Client.OnUpdate -= Tick;
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
                if (!Client.InPlay || !ClientlessSessionGuard.BankCacheTrusted ||
                    !Inventory.Bank.IsOpen || Stopwatch.GetTimestamp() < _presenceRetryAfter) return;
                if (_idleReconnectCycle != null && !TryResumeIdleConnection()) return;
                if (_stagingFailureLayout != null)
                {
                    // A hold used to end only when the layout changed, and the hold
                    // itself withdraws presence, so a worker that nobody moved a bag
                    // for never rejoined: it sat out every later cycle until the host
                    // was restarted. Retry on a cool-off as well, so the staging step
                    // gets another attempt with a fresh candidate bag.
                    if (_stagingFailureLayout == InventoryLayout() && _recoveryRequest == null &&
                        Stopwatch.GetTimestamp() < _stagingRetryAfter) return;
                    _stagingFailureLayout = null;
                    _stagingBag = null;
                }
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
                    if (IsOpen)
                    {
                        if (_readyLoggedCycle != cycle.Id)
                        {
                            Logger.Information("[CityBankers] BANKER READY " + _character + "; cycle=" + cycle.Id +
                                "; starting deferred components=" + Deferred.Count);
                            CityDwellers.Shared.ServiceEvents.Report("banker.ready", "info", "Banker ready.", new { Cycle = cycle.Id });
                            _readyLoggedCycle = cycle.Id;
                        }
                        InitializeDeferred();
                        _handoffError = null;
                    }
                    else
                    {
                        string reason = "cycle=" + cycle.Id + "; localCycle=" + _auditCycle + "; finished=" + _finished +
                            "; invalidated=" + _invalidated + "; pause=" + _auditPause + "/" + _holdVersion +
                            "; holdCycle=" + _holdCycle + "; recoveryRequested=" + Requested();
                        // A newer local recovery owns its own pause and reports its reason.
                        // It is not a failed handoff of the completed startup audit.
                        if (_holdVersion == _auditPause && _handoffError != reason) Logger.Warning("[CityBankers] Census released; local handoff waiting: " + reason);
                        _handoffError = reason;
                    }
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
                    string signature = InventoryLayout();
                    if (signature != _signature) { _signature = signature; _settled.Restart(); return; }
                    if (_settled.ElapsedMilliseconds < 2000 || _retry.ElapsedMilliseconds < 3000) return;
                    ReportSmallBackpackCapacity();
                    ReportDuplicateBagRecords();
                    if (!PrepareAuditStagingSlot()) return;
                    RuntimeStateStore.DeleteIfExists(resultPath);
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(_directory, _character + ".command.json"),
                        new BagAuditAgent.BagAuditCommand { RunId = _auditRun, Role = _role, BagMoveTimeoutMs = 15000 });
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
                        // Rejoining on a fixed interval lets a persistently
                        // unscannable worker force a fresh cycle indefinitely.
                        // Back off so a repeatedly failing member cannot keep
                        // re-auditing the whole roster: 60s, 120s, 240s, 480s,
                        // then 960s. A census this worker completes resets it.
                        if (_censusRejections < 5) _censusRejections++;
                        long backoff = 60L << (_censusRejections - 1);
                        Logger.Warning("[CityBankers] Census rejected for " + _character +
                            "; retrying in " + backoff + "s (consecutive rejections=" + _censusRejections + ").");
                        _presenceRetryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * backoff;
                        RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".presence.json"));
                    }
                    throw;
                }
                RuntimeStateStore.WriteJsonAtomic(Path.Combine(CycleDirectory(cycle), _character.ToLowerInvariant() + ".json"), result);
                _censusRejections = 0;
                _finished = true;
            }
            catch (Exception ex)
            {
                if (_error != ex.Message) Logger.Error("[CityBankers] Census waiting: " + ex.Message);
                _error = ex.Message;
            }
        }

        private static string InventoryLayout()
        {
            // Include the owning collection: slot numbers alone do not prove a move.
            return string.Join(";", Inventory.Items.Where(i => i != null)
                .Select(i => "inventory/" + i.Slot + "/" + i.UniqueIdentity)
                .Concat(Inventory.Bank.Items.Where(i => i != null)
                    .Select(i => "bank/" + i.Slot + "/" + i.UniqueIdentity)).OrderBy(s => s));
        }

        private static string ReconnectInventory()
        {
            // Fresh SDK objects must describe the same top-level items and bags.
            // Slot location, template, quality and stack quantity all matter.
            return string.Join(";", Inventory.Items.Where(i => i != null)
                .Select(i => "inventory/" + i.Slot + "/" + i.UniqueIdentity + "/" + i.Id + "/" + i.HighId + "/" + i.Ql + "/" + StackableItems.Quantity(i))
                .Concat(Inventory.Bank.Items.Where(i => i != null)
                    .Select(i => "bank/" + i.Slot + "/" + i.UniqueIdentity + "/" + i.Id + "/" + i.HighId + "/" + i.Ql + "/" + StackableItems.Quantity(i)))
                .OrderBy(s => s));
        }

        private bool TryResumeIdleConnection()
        {
            string observed = ReconnectInventory();
            if (_reconnectObserved != observed)
            {
                _reconnectObserved = observed;
                _reconnectSettled.Restart();
                return false;
            }
            if (_reconnectSettled.ElapsedMilliseconds < 5000) return false;
            bool resumed = false;
            bool localAudit = false;
            Locked(() =>
            {
                var cycle = Current();
                if (cycle?.Id != _idleReconnectCycle || cycle.Phase != "released" || Requested())
                {
                    // An independently requested census owns recovery now.
                    _idleReconnectCycle = null;
                    return;
                }
                if (!Includes(cycle, _character, _idleReconnectConnection))
                    throw new InvalidOperationException("Idle reconnect lost its prior census membership.");
                if (!BankingServiceAgent.CanResumeIdleConnection())
                {
                    _idleReconnectCycle = null;
                    Block("Reconnect validation found unresolved item work; retained idle evidence cannot release this banker.");
                    return;
                }
                // Rebind only this member. Other members retain their current
                // cycle, ready tokens and physical evidence, even while offline.
                cycle.Participants[_character] = _connection;
                RuntimeStateStore.WriteJsonAtomic(CyclePath, cycle);
                _idleReconnectConnection = _connection;
                PublishReadyRoster(cycle);
                RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".blocked"));
                File.WriteAllText(MemberPath(_character, ".ready"), cycle.Id + "/" + _connection);
                _invalidated = false;
                if (observed != _idleReconnectInventory)
                {
                    // Ready is only provisional within this callback. No fresh
                    // operational heartbeat is published before the local hold.
                    bool acquired;
                    try { acquired = BankingServiceAgent.ReconcileIdleReconnect(); }
                    catch
                    {
                        _invalidated = true;
                        RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".ready"));
                        throw;
                    }
                    if (!acquired)
                    {
                        Hold("Waiting for ownership of reconnect inventory reconciliation.");
                        return;
                    }
                    localAudit = true;
                }
                else _auditPause = _holdVersion;
                _idleReconnectCycle = null;
                resumed = true;
            });
            if (resumed && !localAudit)
            {
                Logger.Information("[CityBankers] " + _character + " idle reconnect verified; resumed without bag audit.");
                CityDwellers.Shared.ServiceEvents.Report("banker.reconnected", "info", "Idle reconnect verified; resumed without bag audit.");
            }
            return _idleReconnectCycle == null;
        }

        private void PublishReadyRoster(Cycle cycle)
        {
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(RuntimeStateStore.GetDataDirectory(_settings),
                TrustedOperators.AllBankersReadyMarkerFileName), new
            {
                format = "citybankers-all-bankers-ready-v2", generation = Generation, cycle = cycle.Id,
                readyUtc = DateTime.UtcNow,
                characters = cycle.Participants.Select(p => new { character = p.Key, connection = p.Value,
                    role = _roles.Properties().Single(r => string.Equals((string)r.Value["Character"], p.Key, StringComparison.OrdinalIgnoreCase)).Name }).ToList()
            });
        }

        private bool WaitForStagingChange(string reason)
        {
            _stagingFailureLayout = InventoryLayout();
            _stagingBag = null;
            if (_stagingHolds < 5) _stagingHolds++;
            long cooloff = 60L << (_stagingHolds - 1);
            _stagingRetryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * cooloff;
            RuntimeStateStore.WriteJsonAtomic(MemberPath(_character, ".blocked"), new { Reason = reason });
            // Withdrawing presence lets the running cycle drop this member and
            // finish instead of waiting for a result it will not produce. The
            // cool-off above is what brings the member back: without it the hold
            // was permanent, because presence is republished only after this
            // check clears and the roster of every later cycle is built from
            // presence files alone.
            if (!string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
                RuntimeStateStore.DeleteIfExists(MemberPath(_character, ".presence.json"));
            Logger.Error("[CityBankers] Census staging waiting: " + reason +
                " Retrying in " + cooloff + "s, or sooner if the observed layout changes.");

            // This character is already logged in by this process, so nobody can
            // inspect it with the game client. Record the banker's own view of
            // its inventory, bank and containers instead. Read-only, and written
            // once per observed layout because this method is reached only when
            // the layout has changed.
            AmbiguousBagReport.Write(_settings, _character, _role, reason);
            return false;
        }

        // A bag listed at two outer slots is one physical bag whose removal at the
        // source slot was missed. It no longer stops the census, but it is still
        // recorded: the audit works from the client's view, and a view that is
        // wrong about where a bag sits is worth keeping evidence of.
        private void ReportDuplicateBagRecords()
        {
            string duplicates = StorageBagPolicy.DescribeDuplicates();
            if (duplicates == null) { _duplicateLoggedRun = null; return; }
            if (_duplicateLoggedRun == _auditRun) return;
            _duplicateLoggedRun = _auditRun;
            Logger.Warning("[CityBankers] DUPLICATE BAG RECORD " + _character + ": " + duplicates +
                " Auditing each bag once and continuing.");
            AmbiguousBagReport.Write(_settings, _character, _role, duplicates);
        }

        private void ReportSmallBackpackCapacity()
        {
            if (_capacityLoggedRun == _auditRun || !Inventory.Bank.IsOpen) return;
            if (!string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
            {
                var rules = SymbiantCatalog.GetRetentionRules(_settings).Where(r =>
                    string.Equals(r.Role, _role, StringComparison.OrdinalIgnoreCase)).ToList();
                // Count bags, not records: a bag listed twice is still one bag.
                int available = StorageBagPolicy.DistinctBags().Count;
                int required = (rules.Where(r => r.MaxCopies > 0).Sum(r => r.MaxCopies) + 20) / 21;
                if (available < required)
                    Logger.Warning("[CityBankers] SMALL BACKPACK SHORTAGE " + _character +
                        ": have=" + available + "; need=" + required + "; missing=" + (required - available) +
                        ". Supply Small Backpacks (99228). Equipped and other bag types cannot be used for storage.");
            }
            _capacityLoggedRun = _auditRun;
        }

        private bool PrepareAuditStagingSlot()
        {
            if (_stagingBag != null)
            {
                int bankCopies = Inventory.Bank.Items.Count(i => i != null && i.UniqueIdentity.ToString() == _stagingBag);
                int inventoryCopies = Inventory.Items.Count(i => StorageBagPolicy.IsNormalInventory(i) && i.UniqueIdentity.ToString() == _stagingBag);
                bool inBank = bankCopies != 0;
                bool inInventory = inventoryCopies != 0;
                // Judge the move by what it changed. Requiring one copy in bank and
                // none in inventory is unreachable for a bag the client lists twice,
                // even when the move itself succeeded.
                if (bankCopies == _stagingBankBefore + 1 && inventoryCopies == _stagingInventoryBefore - 1 &&
                    StorageBagPolicy.FreeInventorySlots() > 0)
                {
                    Logger.Information("[CityBankers] Census staging slot verified; bag=" + _stagingBag);
                    _stagingHolds = 0;
                    _stagingBag = null;
                    _signature = null;
                    _settled.Restart();
                    return false; // Collector snapshots the new location after settling.
                }
                if (_stagingAge.ElapsedMilliseconds >= 15000)
                {
                    if (bankCopies == _stagingBankBefore && inventoryCopies == _stagingInventoryBefore &&
                        StorageBagPolicy.FreeInventorySlots() > 0 &&
                        InventoryLayout() == _stagingBeforeLayout)
                    {
                        // Nothing moved and nothing changed. Moves carry the item's
                        // slot, so silence means the server has nothing in the slot
                        // that was addressed: record it and prefer the other record
                        // of that bag from now on.
                        if (_stagingRecordSlot >= 0 && inventoryCopies > 1)
                            StorageBagPolicy.NoteUnresponsiveRecord(
                                _stagingRecordLocation, _stagingRecordSlot, _stagingIdentity);
                        // Extra receiving headroom is optional. The move did not
                        // change the observed layout, so audit the actual location.
                        // Do not resend this failed reserve move in later cycles.
                        Logger.Warning("[CityBankers] Extra receiving reserve move left inventory unchanged; " +
                            "continuing census with " + StorageBagPolicy.FreeInventorySlots() +
                            " free slots; bag=" + _stagingBag);
                        _extraReserveDeferred = true;
                        _stagingBag = null;
                        _signature = null;
                        _settled.Restart();
                        return false;
                    }
                    return WaitForStagingChange("Inventory bag move to bank was not verified; bag=" + _stagingBag +
                        "; inInventory=" + inInventory + "; inBank=" + inBank +
                        "; freeSlots=" + StorageBagPolicy.FreeInventorySlots() +
                        " (client reports " + Inventory.NumFreeSlots + ")");
                }
                return false;
            }
            string layoutError;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out layoutError))
                return WaitForStagingChange(layoutError + " Fresh bank evidence is required before staging.");
            // Keep receiving capacity after the audit too: a full donation plus
            // one slot to stage its destination bag. Moving only one bag allowed
            // census to finish but left every multi-item prepare permanently busy.
            int requiredSlots = _extraReserveDeferred ? 1 : ServicePolicy.MaxTradeItems + 1;
            // Count slots, not records. A bag the client lists twice occupies one
            // slot; NumFreeSlots subtracts two. Kbarty reported ten free against a
            // requirement of eleven while physically holding eleven, so the phantom
            // was the only reason this step ran at all.
            if (StorageBagPolicy.FreeInventorySlots() >= requiredSlots)
            {
                _stagingHolds = 0;
                return true;
            }
            // Never stage a bag the client lists twice while an unambiguous one is
            // available. A move carries the item's slot, so acting on the stale
            // record of a duplicated bag sends the server an action for a slot it
            // considers empty: nothing happens, and the census parks on a bag that
            // was never going to move. Kbarty has one duplicated bag and seventeen
            // clean ones; this picks a clean one.
            var ambiguous = new HashSet<Identity>(StorageBagPolicy.DuplicatedIdentities());
            var staging = StorageBagPolicy.DistinctBags()
                .FirstOrDefault(r => r.Location == "inventory" && !ambiguous.Contains(r.Identity));
            var bag = staging == null ? null : staging.Bag;
            if (Inventory.Bank.NumFreeSlots <= 0 || bag == null)
            {
                if (StorageBagPolicy.FreeInventorySlots() > 0 || !Inventory.Bank.Items.Any(i => i != null &&
                    StorageBagPolicy.IsStorageBag(i)))
                {
                    // Audit still works at reduced capacity. Dispatch reports the
                    // actual space shortage if a later batch does not fit.
                    Logger.Warning("[CityBankers] Census receiving reserve limited: free inventory slots=" +
                        StorageBagPolicy.FreeInventorySlots() + "; desired=" + requiredSlots +
                        "; bank free slots=" + Inventory.Bank.NumFreeSlots);
                    return true;
                }
                return WaitForStagingChange("No normal-inventory staging slot and no bank space for an inventory bag.");
            }
            _stagingBeforeLayout = InventoryLayout();
            _stagingBag = bag.UniqueIdentity.ToString();
            _stagingIdentity = bag.UniqueIdentity;
            _stagingRecordLocation = "inventory";
            _stagingRecordSlot = bag.Slot.Instance & 65535;
            _stagingBankBefore = Inventory.Bank.Items.Count(i => i != null && i.UniqueIdentity == bag.UniqueIdentity);
            _stagingInventoryBefore = Inventory.Items.Count(i =>
                StorageBagPolicy.IsNormalInventory(i) && i.UniqueIdentity == bag.UniqueIdentity);
            _stagingAge.Restart();
            Logger.Information("[CityBankers] Census preparing staging slot; moving inventory bag " + _stagingBag +
                " at inventory/" + _stagingRecordSlot + " to bank; bankCopiesBefore=" + _stagingBankBefore +
                "; inventoryCopiesBefore=" + _stagingInventoryBefore + ".");
            try { bag.MoveToBank(); }
            catch (Exception ex) { return WaitForStagingChange("Inventory bag move failed: " + ex.Message); }
            return false;
        }

        private void Coordinate()
        {
            var cycle = Current();
            if (cycle?.Phase == "collecting" && cycle.Participants != null)
            {
                // An explicit recovery request is a deliberate instruction and
                // still replaces the cycle.
                if (Requested())
                {
                    cycle.Phase = "superseded";
                    RuntimeStateStore.WriteJsonAtomic(CyclePath, cycle);
                }
                else
                {
                    // A member that stops publishing presence withdraws from this
                    // cycle; it does not cancel it. Replacing the cycle changes its
                    // id, and every banker that observes a new id retires its own
                    // audit, so one unscannable member used to destroy healthy and
                    // already completed peer censuses and the roster could never
                    // converge. Reconciliation is scoped to the characters that
                    // actually produced a census, so a smaller participant set is
                    // the same condition as those members having been offline when
                    // the cycle was created.
                    var withdrawn = cycle.Participants.Where(p => !Present(p.Key, p.Value) &&
                        Read<BagAuditAgent.BagAuditResult>(Path.Combine(CycleDirectory(cycle),
                            p.Key.ToLowerInvariant() + ".json")) == null).Select(p => p.Key).ToList();
                    if (withdrawn.Count != 0)
                    {
                        var remaining = cycle.Participants
                            .Where(p => !withdrawn.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                        // Reconciliation requires Central. Without it there is no
                        // scoped census to apply, so the cycle is replaced instead.
                        if (remaining.Count == 0 ||
                            !remaining.ContainsKey(_character))
                        {
                            cycle.Phase = "superseded";
                        }
                        else
                        {
                            cycle.Participants = remaining;
                            Logger.Warning("[CityBankers] Census " + cycle.Id + " withdrew " +
                                string.Join(", ", withdrawn) + "; " + remaining.Count +
                                " participants continue without restarting their audits.");
                        }
                        RuntimeStateStore.WriteJsonAtomic(CyclePath, cycle);
                    }
                }
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
                var application = CensusApplication.Apply(_settings, CycleDirectory(cycle), cycle.Id, censuses,
                    _roles.Properties().ToDictionary(p => p.Name, p => (string)p.Value["Character"], StringComparer.OrdinalIgnoreCase));
                bool release = cycle.Participants.All(p => Present(p.Key, p.Value)) && !Requested();
                cycle.Phase = release ? "released" : "superseded";
                if (release) PublishReadyRoster(cycle);
                RuntimeStateStore.WriteJsonAtomic(CyclePath, cycle);
                Logger.Information("[CityBankers] Census " + cycle.Id + " " + cycle.Phase + "; audited bankers=" + censuses.Count + "; queued routes=" + application.Queue.Batches.Count);
                return;
            }
            var online = _characters.Select(c => new { Character = c, Presence = Read<Presence>(MemberPath(c, ".presence.json")) })
                .Where(p => p.Presence != null && Present(p.Character, p.Presence.Connection))
                .ToDictionary(p => p.Character, p => p.Presence.Connection, StringComparer.OrdinalIgnoreCase);
            if (!online.ContainsKey(_character)) return;
            // A missing heartbeat changes availability, not physical custody.
            // ReadyConnection already excludes that banker from new requests.
            if (cycle?.Phase == "released" && !Requested() &&
                online.All(p => Includes(cycle, p.Key, p.Value))) return;
            var next = new Cycle { Id = Guid.NewGuid().ToString("N"), Phase = "collecting", Participants = online };
            var requests = _characters.Select(c => Read<JObject>(MemberPath(c, ".recovery.json"))).Where(r => r != null).ToList();
            CityDwellers.Shared.IncidentJournal.Record(RuntimeStateStore.GetDataDirectory(_settings),
                "recovery:history/census-" + next.Id + ".json", _character, "recovery.started",
                new { Participants = online.Keys.ToList(), Requests = requests, Reason = requests.Count == 0 ? "Startup or roster admission" : "Explicit recovery requests" },
                requests.Count > 0, requests.Select(r => "recovery-request:" + (string)r["Id"]));
            foreach (var request in requests)
                CityDwellers.Shared.IncidentJournal.Record(_traceData, "recovery-request:" + (string)request["Id"],
                    _character, "recovery.assigned", new { Cycle = next.Id }, false,
                    new[] { "recovery:history/census-" + next.Id + ".json" });
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
