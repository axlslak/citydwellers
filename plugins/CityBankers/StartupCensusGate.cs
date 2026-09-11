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
        private static string[] _characters;
        private static bool _invalidated;
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

        public static bool IsOpen
        {
            get
            {
                if (ServicePolicy.IsBagAuditMode() || _invalidated || _directory == null ||
                    _characters == null || _characters.Length != 9)
                    return false;
                try
                {
                    return _characters.All(name => File.Exists(Path.Combine(_directory, name + ".ready"))) &&
                        !Directory.EnumerateFiles(_directory, "*.blocked").Any();
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
            _invalidated = true;
            if (_directory != null)
            {
                File.WriteAllText(Path.Combine(_directory, Client.CharacterName + ".blocked"), reason);
                string data = Directory.GetParent(Directory.GetParent(_directory).FullName).FullName;
                string ready = Path.Combine(data, TrustedOperators.AllBankersReadyMarkerFileName);
                if (File.Exists(ready)) File.Delete(ready);
            }
            Logger.Error("[CityBankers] CENSUS SAFETY HOLD: " + reason);
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
            Client.Disconnected += OnDisconnected;
            Trade.TradeOpened += RejectBeforeCensus;
        }

        private void RejectBeforeCensus(Identity target)
        {
            if (!IsOpen) Trade.Decline();
        }

        private void OnDisconnected()
        {
            Block("Banker disconnected. Old census is invalid; restart requires new physical evidence.");
            _issued = false;
            _finished = false;
            _auditRun = Guid.NewGuid().ToString("N");
        }

        public override void Teardown()
        {
            if (ServicePolicy.IsBagAuditMode()) return;
            Client.OnUpdate -= Tick;
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
                    File.WriteAllText(_command, JsonConvert.SerializeObject(new BagAuditAgent.BagAuditCommand
                    { RunId = _auditRun, Role = _role }));
                    _issued = true;
                    return;
                }
                if (!File.Exists(_result)) return;
                var result = JsonConvert.DeserializeObject<BagAuditAgent.BagAuditResult>(File.ReadAllText(_result));
                File.Copy(_result, Path.Combine(_directory, _character + "." + _auditRun + ".evidence.json"), false);
                _finished = true;
                var errors = new List<string>();
                if (result == null || result.RunId != _auditRun || result.Character != _character ||
                    result.Role != _role || !result.BankOpened || result.FatalError != null ||
                    result.FailedCount != 0 || result.BankReturnFailureCount != 0 ||
                    result.OpenedCount != result.TotalBagCount || result.BankReturnedCount != result.BankBagCount ||
                    result.LooseBankItems == null || result.LooseInventoryItems == null)
                    errors.Add("Incomplete or mismatched census result.");
                else if (!string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
                {
                    if (_expected == null) errors.Add("No expected worker baseline; enrollment requires review.");
                    else
                    {
                        var bags = new List<BagAuditAgent.BagAuditEntry>(result.Bags);
                        foreach (StorageBagState expectedBag in _expected.Bags)
                        {
                            var matches = bags.Where(b => b.UniqueIdentity == expectedBag.LastUniqueIdentity).ToList();
                            if (matches.Count != 1) { errors.Add("Bag identity absent/ambiguous: " + expectedBag.LastUniqueIdentity); continue; }
                            var bag = matches[0]; bags.Remove(bag);
                            var expectedItems = expectedBag.Items.Select(i => (i.InnerSlot & 65535) + "/" + i.AoId + "/" + i.HighId + "/" + i.Ql).OrderBy(x => x);
                            var liveItems = bag.Items.Select(i => (i.SlotInstance & 65535) + "/" + i.LowId + "/" + i.HighId + "/" + i.Ql).OrderBy(x => x);
                            if (!bag.Opened || !expectedItems.SequenceEqual(liveItems))
                                errors.Add("Bag contents differ: " + expectedBag.LastUniqueIdentity);
                        }
                        if (bags.Count != 0) errors.Add("Unexpected bags: " + bags.Count);
                    }
                }
                if (result?.LooseInventoryItems != null && result.LooseBankItems != null)
                {
                    var outsideStorage = result.LooseInventoryItems.Concat(result.LooseBankItems);
                    if (string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
                        outsideStorage = outsideStorage.Concat((result.Bags ?? new List<BagAuditAgent.BagAuditEntry>())
                            .SelectMany(bag => bag.Items ?? new List<BagAuditAgent.BagInnerItem>()));
                    foreach (var item in outsideStorage)
                    {
                        SymbiantCatalog.AcceptanceRule rule;
                        if (SymbiantCatalog.TryGetRule(_settings, item.LowId, out rule))
                            errors.Add("Managed item outside verified storage: " + item.Name +
                                " AOID=" + item.LowId + " QL=" + item.Ql + " slot=" + item.Slot +
                                ". Retained for custody reconciliation, not imported or deleted.");
                    }
                }
                if (string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
                {
                    string custody = Path.Combine(RuntimeStateStore.GetDataDirectory(_settings), "custody-transactions");
                    if (Directory.Exists(custody) && Directory.EnumerateDirectories(custody)
                        .Any(path => !Directory.EnumerateFiles(path, "*-applied.json").Any()))
                        errors.Add("Unfinished durable custody transaction; evidence must be reconciled before release.");
                    var queue = RuntimeStateStore.LoadDispatchQueue(_settings);
                    if (queue?.Batches?.Any(b => b.Status == "custody-hold" || b.Status == "trading" ||
                        b.Status == "transferred") == true)
                        errors.Add("Unresolved persisted custody. Automatic speculative recovery is disabled.");
                }
                File.WriteAllText(Path.Combine(_directory, _character + "." + _auditRun + ".comparison.json"),
                    JsonConvert.SerializeObject(errors, Formatting.Indented));
                if (errors.Count != 0) { Block(string.Join("; ", errors)); return; }
                if (_invalidated) return; // recensus is evidence, not permission to resume old in-flight work
                File.WriteAllText(_ready, Generation);
                Logger.Information("[CityBankers] FULL CENSUS VERIFIED " + _character +
                    "; waiting for all nine bankers before operational initialization.");
            }
            catch (Exception ex) { _finished = true; Block("Census failed: " + ex); }
        }

        public static string CensusDirectory(string settings)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(settings), "startup-census", Generation);
        }
    }
}
