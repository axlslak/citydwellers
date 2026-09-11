using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    // All client domains in the unified host share a process generation, not a
    // wall-clock freshness threshold. Never reuse evidence from a previous process.
    public sealed class StartupCensusGate : ClientlessPluginEntry
    {
        private static readonly string Generation = Process.GetCurrentProcess().Id + "-" +
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private static string _directory;
        private static bool _isCentralClient;
        private static string[] _characters;
        private static bool _invalidated;
        private static long _holdVersion;
        private string _settings;
        private string _character;
        private string _role;
        private string _command;
        private string _result;
        private string _ready;
        private StorageWorkerState _expected;
        private bool _issued;
        private bool _finished;
        private string _auditRun = Guid.NewGuid().ToString("N");
        private bool _applicationFinished;
        private readonly Stopwatch _applicationPoll = Stopwatch.StartNew();
        private string _applicationError;

        public static bool UsesPhysicalRecovery => !ServicePolicy.IsBagAuditMode();

        public static bool IsOpen
        {
            get
            {
                if (ServicePolicy.IsBagAuditMode() || _invalidated || _directory == null ||
                    _characters == null || _characters.Length != 9)
                    return false;
                try
                {
                    return File.Exists(Path.Combine(_directory, "released.json")) &&
                        File.Exists(Path.Combine(_directory, Client.CharacterName + ".ready")) &&
                        !File.Exists(Path.Combine(_directory, Client.CharacterName + ".blocked"));
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }

        public static bool Defer(Action initialize)
        {
            if (ServicePolicy.IsBagAuditMode())
                return true;
            if (IsOpen)
                return false;
            EventHandler<double> pending = null;
            pending = (sender, delta) =>
            {
                if (!IsOpen) return;
                Client.OnUpdate -= pending;
                try { initialize(); }
                catch (Exception ex) { Block("Operational initialization failed: " + ex); }
            };
            Client.OnUpdate += pending;
            return true;
        }

        public static void Block(string reason)
        {
            _holdVersion++;
            _invalidated = true;
            if (_directory != null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(_directory, Client.CharacterName + ".blocked"), reason);
                    string data = Directory.GetParent(Directory.GetParent(_directory).FullName).FullName;
                    string ready = Path.Combine(data, TrustedOperators.AllBankersReadyMarkerFileName);
                    if (_isCentralClient && File.Exists(ready)) File.Delete(ready);
                }
                catch (IOException ex) { Logger.Error("[CityBankers] Local hold record unavailable: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Logger.Error("[CityBankers] Local hold record unavailable: " + ex.Message); }
            }
            Logger.Error("[CityBankers] LOCAL CENSUS HOLD " + Client.CharacterName + ": " + reason);
        }

        internal static long PauseLocalCensus(string reason)
        {
            if (!IsOpen) throw new InvalidOperationException("Cannot replace an unrelated census hold.");
            Block(reason);
            return _holdVersion;
        }

        internal static bool OwnsLocalPause(long version) => _invalidated && version == _holdVersion;

        internal static bool ResumeLocalCensus(long version)
        {
            if (!OwnsLocalPause(version) || !Client.InPlay ||
                !File.Exists(Path.Combine(_directory, "released.json"))) return false;
            File.WriteAllText(Path.Combine(_directory, Client.CharacterName + ".ready"), Generation);
            RuntimeStateStore.DeleteIfExists(Path.Combine(_directory, Client.CharacterName + ".blocked"));
            _invalidated = false;
            return true;
        }

        public override void Init(string pluginDir)
        {
            if (ServicePolicy.IsBagAuditMode()) return;
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settings, out error))
                throw new InvalidOperationException(error);
            JObject roles = SettingsPaths.ReadBankersSettings(_settings)["Roles"] as JObject;
            if (roles == null) { Block("Missing banker roster."); return; }
            _characters = roles.Properties().Select(p => (string)p.Value["Character"]).ToArray();
            if (_characters.Length != 9 || _characters.Any(string.IsNullOrWhiteSpace) ||
                _characters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 9)
            { Block("Census requires nine distinct configured bankers."); return; }
            _character = Client.CharacterName;
            _role = roles.Properties().Where(p => string.Equals((string)p.Value["Character"],
                _character, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).Single();
            _isCentralClient = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);
            _directory = Path.Combine(RuntimeStateStore.GetDataDirectory(_settings),
                "startup-census", Generation);
            Directory.CreateDirectory(_directory);
            string oldReady = Path.Combine(RuntimeStateStore.GetDataDirectory(_settings),
                TrustedOperators.AllBankersReadyMarkerFileName);
            if (File.Exists(oldReady)) File.Delete(oldReady);
            _command = Path.Combine(_directory, _character + ".command.json");
            _result = Path.Combine(_directory, _character + ".result.json");
            _ready = Path.Combine(_directory, _character + ".ready");
            if (File.Exists(_ready)) File.Delete(_ready);
            StorageState expected = RuntimeStateStore.LoadStorageState(_settings);
            _expected = expected?.Workers?.SingleOrDefault(w => string.Equals(w.Character,
                _character, StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(Path.Combine(_directory, _character + ".expected.json"),
                JsonConvert.SerializeObject(_expected, Formatting.Indented));
            Client.OnUpdate += Tick;
            if (string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
                Client.OnUpdate += TickApplication;
            Client.Disconnected += OnDisconnected;
            Trade.TradeOpened += RejectBeforeCensus;
        }

        private void RejectBeforeCensus(Identity target)
        {
            if (!IsOpen) Trade.Decline();
        }

        private void OnDisconnected()
        {
            RuntimeStateStore.DeleteIfExists(Path.Combine(_directory, _character + ".observed"));
            Block("Banker disconnected. Old census is invalid; restart requires new physical evidence.");
            _issued = false;
            _finished = false;
            _auditRun = Guid.NewGuid().ToString("N");
        }

        public override void Teardown()
        {
            if (ServicePolicy.IsBagAuditMode()) return;
            Client.OnUpdate -= Tick;
            Client.OnUpdate -= TickApplication;
            if (_directory != null)
                RuntimeStateStore.DeleteIfExists(Path.Combine(_directory, _character + ".observed"));
            Client.Disconnected -= OnDisconnected;
            Trade.TradeOpened -= RejectBeforeCensus;
            if (_directory != null) Block("Banker unloaded; census generation closed.");
        }

        private void Tick(object sender, double delta)
        {
            if (_finished || !Client.InPlay || !Inventory.Bank.IsOpen) return;
            try
            {
                if (!_issued)
                {
                    _expected = RuntimeStateStore.LoadStorageState(_settings)?.Workers?.SingleOrDefault(w =>
                        string.Equals(w.Character, _character, StringComparison.OrdinalIgnoreCase));
                    File.WriteAllText(Path.Combine(_directory, _character + "." + _auditRun + ".expected.json"),
                        JsonConvert.SerializeObject(_expected, Formatting.Indented));
                    if (File.Exists(_result)) File.Delete(_result);
                    var request = new BagAuditAgent.BagAuditCommand { RunId = _auditRun, Role = _role };
                    // The collector consumes/deletes its command. Retain a separate
                    // generation-bound request for Central to verify the result.
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(_directory, _character + ".request.json"), request);
                    RuntimeStateStore.WriteJsonAtomic(_command, request);
                    _issued = true;
                    return;
                }
                if (!File.Exists(_result)) return;
                var result = JsonConvert.DeserializeObject<BagAuditAgent.BagAuditResult>(File.ReadAllText(_result));
                File.Copy(_result, Path.Combine(_directory, _character + "." + _auditRun + ".evidence.json"), false);
                _finished = true;
                if (result == null || result.RunId != _auditRun || result.Character != _character || result.Role != _role)
                    throw new InvalidOperationException("Mismatched census result.");
                var observations = PhysicalLedgerReconciliation.ReadCensus(_settings, result);
                File.WriteAllText(Path.Combine(_directory, _character + ".observed"), _auditRun);
                // Differences are data to reconcile, not a readiness failure.
                // Central records and applies the combined physical plan before
                // released.json permits any operational actor to start.
                if (_invalidated) return;
                File.WriteAllText(_ready, Generation);
                Logger.Information("[CityBankers] FULL CENSUS COLLECTED " + _character +
                    "; observed items=" + observations.Count + "; awaiting physical ledger application.");
            }
            catch (Exception ex) { _finished = true; Block("Census failed: " + ex); }
        }

        public static string CensusDirectory(string settings)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(settings), "startup-census", Generation);
        }

        private void TickApplication(object sender, double delta)
        {
            if (_applicationFinished || !Client.InPlay || _applicationPoll.ElapsedMilliseconds < 1000) return;
            _applicationPoll.Restart();
            try
            {
                var censuses = new List<BagAuditAgent.BagAuditResult>();
                foreach (string character in _characters)
                {
                    string observed = Path.Combine(_directory, character + ".observed");
                    if (!File.Exists(observed)) return;
                    var command = RuntimeStateStore.ReadJson<BagAuditAgent.BagAuditCommand>(
                        Path.Combine(_directory, character + ".request.json"));
                    var result = RuntimeStateStore.ReadJson<BagAuditAgent.BagAuditResult>(
                        Path.Combine(_directory, character + ".result.json"));
                    if (command == null || result == null || result.RunId != command.RunId ||
                        result.RunId != File.ReadAllText(observed) || result.Role != command.Role ||
                        !string.Equals(result.Character, character, StringComparison.OrdinalIgnoreCase)) return;
                    censuses.Add(result);
                }
                var bundle = CensusApplication.Apply(_settings, _directory, Generation, censuses);
                RuntimeStateStore.WriteJsonAtomic(Path.Combine(RuntimeStateStore.GetDataDirectory(_settings),
                    TrustedOperators.AllBankersReadyMarkerFileName), new
                {
                    format = "citybankers-all-bankers-ready-v1", generation = Generation,
                    readyUtc = DateTime.UtcNow,
                    characters = censuses.Select(c => new { role = c.Role, character = c.Character }).ToList()
                });
                // Release last: if readiness publication failed, no worker could
                // have started moving items before the retained bundle is retried.
                RuntimeStateStore.WriteJsonAtomic(Path.Combine(_directory, "released.json"), new
                { Generation, Count = bundle.Plan.Items.Count });
                _applicationFinished = true;
                Logger.Information("[CityBankers] PHYSICAL LEDGER APPLIED: items=" + bundle.Plan.Items.Count +
                    " differences=" + bundle.Plan.Differences.Count + " routing=" + bundle.Plan.Routing.Count +
                    ". Previous claims and complete census evidence retained.");
            }
            catch (Exception ex)
            {
                if (_applicationError != ex.Message)
                    Logger.Error("[CityBankers] Census application waiting for persistence: " + ex.Message);
                _applicationError = ex.Message;
            }
        }
    }
}
