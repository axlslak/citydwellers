using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>
    /// Read-only physical reconciliation census for Banker.exe bagaudit.
    ///
    /// All six client domains run inside one Banker.exe process, so this version uses a
    /// process-scoped session token rather than trying to discover the audit RunId from
    /// transient command/result files. Each client continuously publishes its loose
    /// normal-inventory items. Central waits until the authoritative storage baseline
    /// changes from the baseline seen at process startup, then combines that new five-worker
    /// bag snapshot with all six same-process loose-inventory snapshots.
    ///
    /// This agent never trades, moves, stores, deletes, queues, or changes stock/ledger.
    /// </summary>
    public class PhysicalInventoryCensusV2Agent : ClientlessPluginEntry
    {
        private const int SettleSeconds = 2;
        private const int PollMilliseconds = 500;

        private string _settingsDir;
        private Dictionary<string, RoleConfig> _roles;
        private string _role;
        private bool _isCentral;
        private bool _enabled;
        private DateTime _nextPollUtc;
        private string _sessionKey;
        private string _sessionDirectory;
        private string _initialBaselineRunId;
        private string _publishedRunId;
        private string _lastSignature;

        public override void Init(string pluginDir)
        {
            if (!ServicePolicy.IsBagAuditMode())
            {
                return;
            }

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            CensusConfig config = LoadConfig();
            if (config == null || config.Roles == null)
            {
                Logger.Warning("PHYSICAL CENSUS V2 disabled: the Bankers section could not be read.");
                return;
            }

            _roles = config.Roles;
            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role))
                return;

            _isCentral = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);

            Process process = Process.GetCurrentProcess();
            DateTime processStartUtc;
            try
            {
                processStartUtc = process.StartTime.ToUniversalTime();
            }
            catch
            {
                processStartUtc = DateTime.UtcNow;
            }

            _sessionKey =
                processStartUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-pid" + process.Id;
            _sessionDirectory = Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "physical-census-v2",
                SafeFileToken(_sessionKey));
            Directory.CreateDirectory(_sessionDirectory);

            if (_isCentral)
                _initialBaselineRunId = ReadCurrentBaselineRunId();

            _enabled = true;
            _nextPollUtc = DateTime.UtcNow.AddSeconds(SettleSeconds);
            Client.OnUpdate += Tick;

            Logger.Information(
                $"[CityBankers] PHYSICAL CENSUS V2 armed character={Client.CharacterName} " +
                $"role={_role} session={_sessionKey}; same-process loose inventory will be " +
                "combined with the next clean worker bag baseline.");
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                PublishOwnLooseInventory();
                if (_isCentral)
                    TryPublishPhysicalState();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"PHYSICAL CENSUS V2 failed character={Client.CharacterName}: {ex}");
            }
        }

        private void PublishOwnLooseInventory()
        {
            List<LooseItem> items = SnapshotLooseInventory();
            string signature = string.Join(
                "|",
                items.OrderBy(item => item.SlotInstance).Select(item =>
                    item.SlotType + ":" + item.SlotInstance + ":" + item.UniqueIdentity + ":" +
                    item.LowId + ":" + item.HighId + ":" + item.Ql + ":" + item.Name));

            string path = Path.Combine(
                _sessionDirectory,
                SafeFileToken(Client.CharacterName) + ".json");

            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal) &&
                File.Exists(path))
            {
                return;
            }

            _lastSignature = signature;

            var snapshot = new CensusSnapshot
            {
                Format = "citybankers-physical-census-v2",
                SessionKey = _sessionKey,
                ProcessId = Process.GetCurrentProcess().Id,
                ObservedUtc = DateTime.UtcNow,
                Role = _role,
                Character = Client.CharacterName,
                NormalInventoryItemCount = (Inventory.Items ?? new List<Item>())
                    .Count(item => item != null && item.Slot.Type == IdentityType.Inventory),
                InventoryBagCount = (Inventory.Items ?? new List<Item>())
                    .Count(item => item != null &&
                        item.Slot.Type == IdentityType.Inventory &&
                        item.UniqueIdentity.Type == IdentityType.Container),
                LooseItems = items
            };

            WriteAtomicJson(path, snapshot);

            int managed = items.Count(item => item.Managed);
            Logger.Information(
                $"[CityBankers] PHYSICAL CENSUS V2 character={Client.CharacterName} role={_role} " +
                $"session={_sessionKey} loose={items.Count} managed={managed}.");

            foreach (LooseItem item in items.Where(item => item.Managed))
            {
                Logger.Information(
                    $"[CityBankers] PHYSICAL LOOSE ITEM V2 character={Client.CharacterName} " +
                    $"role={_role} slot={item.SlotType}:{item.SlotInstance} " +
                    $"name='{item.Name}' aoid={item.LowId} high={item.HighId} ql={item.Ql} " +
                    $"uid={item.UniqueIdentity} routesTo={item.RoutedRole}.");
            }
        }

        private void TryPublishPhysicalState()
        {
            JObject baseline = ReadCurrentBaseline();
            if (baseline == null)
                return;

            string runId = baseline["runId"]?.ToString();
            if (string.IsNullOrWhiteSpace(runId) ||
                string.Equals(runId, _initialBaselineRunId, StringComparison.Ordinal) ||
                string.Equals(runId, _publishedRunId, StringComparison.Ordinal))
            {
                return;
            }

            var censuses = new Dictionary<string, CensusSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, RoleConfig> pair in _roles)
            {
                if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Value.Character))
                    continue;

                string path = Path.Combine(
                    _sessionDirectory,
                    SafeFileToken(pair.Value.Character) + ".json");
                if (!File.Exists(path))
                    return;

                CensusSnapshot census;
                try
                {
                    census = JsonConvert.DeserializeObject<CensusSnapshot>(File.ReadAllText(path));
                }
                catch
                {
                    return;
                }

                if (census == null ||
                    census.ProcessId != Process.GetCurrentProcess().Id ||
                    !string.Equals(census.SessionKey, _sessionKey, StringComparison.Ordinal) ||
                    !string.Equals(census.Character, pair.Value.Character, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                censuses[pair.Key] = census;
            }

            if (censuses.Count < 6)
                return;

            PhysicalStateSnapshot physical = BuildPhysicalState(baseline, censuses);
            string dataDir = RuntimeStateStore.GetDataDirectory(_settingsDir);
            string archiveDir = Path.Combine(dataDir, "physical-states");
            Directory.CreateDirectory(archiveDir);

            WriteAtomicJson(Path.Combine(dataDir, "physical-state.json"), physical);
            WriteAtomicJson(
                Path.Combine(archiveDir, "physical-state-" + SafeFileToken(runId) + ".json"),
                physical);

            _publishedRunId = runId;

            int managedCount = physical.Locations.Count(location => location.Managed);
            Logger.Information(
                $"[CityBankers] PHYSICAL STATE PUBLISHED run={runId} locations={physical.Locations.Count} " +
                $"managed={managedCount} session={_sessionKey} " +
                $"file='{Path.Combine(dataDir, "physical-state.json")}'.");

            foreach (PhysicalLocation location in physical.Locations
                .Where(location => location.Managed)
                .OrderBy(location => location.Name)
                .ThenBy(location => location.Character)
                .ThenBy(location => location.Location))
            {
                Logger.Information(
                    $"[CityBankers] PHYSICAL MANAGED ITEM name='{location.Name}' aoid={location.LowId} " +
                    $"high={location.HighId} ql={location.Ql} uid={location.UniqueIdentity} " +
                    $"actual={location.Character}/{location.Location} routesTo={location.RoutedRole} " +
                    $"routeMatch={location.RouteMatchesPhysicalRole}.");
            }
        }

        private static List<LooseItem> SnapshotLooseInventory()
        {
            var result = new List<LooseItem>();
            foreach (Item item in Inventory.Items ?? new List<Item>())
            {
                if (item == null ||
                    item.Slot.Type != IdentityType.Inventory ||
                    item.UniqueIdentity.Type == IdentityType.Container)
                {
                    continue;
                }

                string route;
                bool managed = SymbiantCatalog.TryGetDestinationRole(item.Id, out route);
                if (!managed && item.HighId != item.Id)
                    managed = SymbiantCatalog.TryGetDestinationRole(item.HighId, out route);

                result.Add(new LooseItem
                {
                    Slot = item.Slot.ToString(),
                    SlotType = item.Slot.Type.ToString(),
                    SlotInstance = item.Slot.Instance,
                    UniqueIdentity = item.UniqueIdentity.ToString(),
                    Name = item.Name ?? string.Empty,
                    LowId = item.Id,
                    HighId = item.HighId,
                    Ql = item.Ql,
                    Managed = managed,
                    RoutedRole = managed ? route : null
                });
            }

            return result.OrderBy(item => item.SlotInstance).ToList();
        }

        private PhysicalStateSnapshot BuildPhysicalState(
            JObject baseline,
            Dictionary<string, CensusSnapshot> censuses)
        {
            var result = new PhysicalStateSnapshot
            {
                Format = "citybankers-physical-state-v1",
                RunId = baseline["runId"]?.ToString(),
                ObservedUtc = DateTime.UtcNow,
                CensusSessionKey = _sessionKey,
                Locations = new List<PhysicalLocation>()
            };

            foreach (KeyValuePair<string, CensusSnapshot> pair in censuses)
            {
                CensusSnapshot census = pair.Value;
                foreach (LooseItem item in census.LooseItems ?? new List<LooseItem>())
                {
                    result.Locations.Add(new PhysicalLocation
                    {
                        Role = census.Role,
                        Character = census.Character,
                        LocationKind = "loose-inventory",
                        Location = "inventory:" + item.SlotInstance,
                        UniqueIdentity = item.UniqueIdentity,
                        Name = item.Name,
                        LowId = item.LowId,
                        HighId = item.HighId,
                        Ql = item.Ql,
                        Managed = item.Managed,
                        RoutedRole = item.RoutedRole,
                        RouteMatchesPhysicalRole = item.Managed && string.Equals(
                            item.RoutedRole,
                            census.Role,
                            StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            JObject workers = baseline["workers"] as JObject;
            if (workers != null)
            {
                foreach (JProperty workerProperty in workers.Properties())
                {
                    JObject worker = workerProperty.Value as JObject;
                    if (worker == null)
                        continue;

                    string role = worker["role"]?.ToString() ?? workerProperty.Name;
                    string character = worker["character"]?.ToString();
                    JArray bags = worker["bags"] as JArray;
                    if (bags == null)
                        continue;

                    foreach (JObject bag in bags.OfType<JObject>())
                    {
                        string source = bag["source"]?.ToString() ?? "unknown";
                        int outer = IntToken(bag["finalOuterSlotInstance"]);
                        JArray items = bag["items"] as JArray;
                        if (items == null)
                            continue;

                        foreach (JObject item in items.OfType<JObject>())
                        {
                            int lowId = IntToken(item["LowId"] ?? item["lowId"]);
                            int highId = IntToken(item["HighId"] ?? item["highId"]);
                            int ql = IntToken(item["Ql"] ?? item["ql"]);
                            string name = StringToken(item["Name"] ?? item["name"]);
                            string uid = StringToken(item["UniqueIdentity"] ?? item["uniqueIdentity"]);
                            int inner = IntToken(item["SlotInstance"] ?? item["slotInstance"]);

                            string routed;
                            bool managed = SymbiantCatalog.TryGetDestinationRole(lowId, out routed);
                            if (!managed && highId != lowId)
                                managed = SymbiantCatalog.TryGetDestinationRole(highId, out routed);

                            result.Locations.Add(new PhysicalLocation
                            {
                                Role = role,
                                Character = character,
                                LocationKind = "bag",
                                Location = source + ":" + outer + "/inner:" + inner,
                                UniqueIdentity = uid,
                                Name = name,
                                LowId = lowId,
                                HighId = highId,
                                Ql = ql,
                                Managed = managed,
                                RoutedRole = managed ? routed : null,
                                RouteMatchesPhysicalRole = managed && string.Equals(
                                    routed,
                                    role,
                                    StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }
                }
            }

            return result;
        }

        private JObject ReadCurrentBaseline()
        {
            string path = Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "storage-baseline.json");
            if (!File.Exists(path))
                return null;

            try
            {
                return JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private string ReadCurrentBaselineRunId()
        {
            return ReadCurrentBaseline()?["runId"]?.ToString();
        }

        private CensusConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<CensusConfig>();
            }
            catch
            {
                return null;
            }
        }

        private string ResolveCurrentRole()
        {
            foreach (KeyValuePair<string, RoleConfig> pair in
                _roles ?? new Dictionary<string, RoleConfig>())
            {
                if (pair.Value != null && string.Equals(
                    pair.Value.Character,
                    Client.CharacterName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }
            return null;
        }

        private static int IntToken(JToken token)
        {
            int value;
            return token != null && int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static string StringToken(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token;
        }

        private static void WriteAtomicJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private sealed class CensusConfig
        {
            public Dictionary<string, RoleConfig> Roles;
        }

        private sealed class RoleConfig
        {
            public string Username;
            public string Character;
        }

        private sealed class CensusSnapshot
        {
            public string Format;
            public string SessionKey;
            public int ProcessId;
            public DateTime ObservedUtc;
            public string Role;
            public string Character;
            public int NormalInventoryItemCount;
            public int InventoryBagCount;
            public List<LooseItem> LooseItems = new List<LooseItem>();
        }

        private sealed class LooseItem
        {
            public string Slot;
            public string SlotType;
            public int SlotInstance;
            public string UniqueIdentity;
            public string Name;
            public int LowId;
            public int HighId;
            public int Ql;
            public bool Managed;
            public string RoutedRole;
        }

        private sealed class PhysicalStateSnapshot
        {
            public string Format;
            public string RunId;
            public DateTime ObservedUtc;
            public string CensusSessionKey;
            public List<PhysicalLocation> Locations = new List<PhysicalLocation>();
        }

        private sealed class PhysicalLocation
        {
            public string Role;
            public string Character;
            public string LocationKind;
            public string Location;
            public string UniqueIdentity;
            public string Name;
            public int LowId;
            public int HighId;
            public int Ql;
            public bool Managed;
            public string RoutedRole;
            public bool RouteMatchesPhysicalRole;
        }
    }
}
