using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Central-only coordinator that turns a complete, clean storage-worker bagaudit run
    /// into the first durable physical storage baseline under settings/data.
    ///
    /// The transient per-worker audit result files remain the handoff mechanism from
    /// BagAuditAgent. This coordinator never promotes a partial or failed run.
    /// </summary>
    public class StorageBaselineCoordinator : ClientlessPluginEntry
    {
        private static readonly string[] StorageRoles =
        {
            "artillery",
            "infantry",
            "control",
            "support",
            "extermination",
            "spirit",
            "dyna",
            "phatz"
        };

        private string _settingsDir;
        private List<WorkerConfig> _workers;
        private bool _enabled;
        private DateTime _nextCheckUtc;
        private string _lastPublishedRunId;
        private string _lastProblemKey;

        public override void Init(string pluginDir)
        {
            // Full physical census and BankingService now own normal-mode recovery.
            if (StartupCensusGate.UsesPhysicalRecovery) return;

            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            BaselineConfig config = LoadConfig();
            if (config == null || config.Roles == null)
            {
                Logger.Warning(
                    "CityBankers storage-baseline coordinator could not read banker role mappings; disabled.");
                return;
            }

            WorkerConfig central;
            if (!TryGetRole(config.Roles, "central", out central) ||
                central == null ||
                string.IsNullOrWhiteSpace(central.Character))
            {
                Logger.Warning(
                    "CityBankers storage-baseline coordinator could not resolve Central; disabled.");
                return;
            }

            if (!string.Equals(
                Client.CharacterName,
                central.Character,
                StringComparison.OrdinalIgnoreCase))
            {
                // Every exported ClientlessPluginEntry is loaded in every client domain.
                // Only Central coordinates the aggregate baseline publication.
                return;
            }

            _workers = new List<WorkerConfig>();
            foreach (string role in StorageRoles)
            {
                WorkerConfig worker;
                if (!TryGetRole(config.Roles, role, out worker) ||
                    worker == null ||
                    string.IsNullOrWhiteSpace(worker.Character))
                {
                    Logger.Warning(
                        $"CityBankers storage-baseline coordinator cannot resolve role '{role}'; disabled.");
                    return;
                }

                worker.Role = role;
                _workers.Add(worker);
            }

            _enabled = true;
            _nextCheckUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;

            Logger.Information(
                $"CityBankers storage-baseline coordinator armed on Central {Client.CharacterName}. " +
                $"A clean {StorageRoles.Length}-worker bagaudit will publish authoritative state below " +
                $"'{Path.Combine(_settingsDir, "data")}'. No baseline is assumed before that run succeeds.");
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || !Client.InPlay)
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextCheckUtc)
                return;

            _nextCheckUtc = now.AddMilliseconds(500);

            try
            {
                TryPublishBaseline();
            }
            catch (Exception ex)
            {
                string key = "exception|" + ex.GetType().FullName + "|" + ex.Message;
                if (!string.Equals(key, _lastProblemKey, StringComparison.Ordinal))
                {
                    _lastProblemKey = key;
                    Logger.Error($"STORAGE BASELINE publication error: {ex}");
                }
            }
        }

        private void TryPublishBaseline()
        {
            var results = new List<WorkerAuditResult>();

            foreach (WorkerConfig worker in _workers)
            {
                string path = Path.Combine(
                    RuntimeStateStore.GetDataDirectory(_settingsDir),
                    $"citybankers-bagaudit-result-{SafeFileToken(worker.Character)}.json");

                if (!File.Exists(path))
                    return;

                JObject result;
                try
                {
                    result = JObject.Parse(File.ReadAllText(path));
                }
                catch
                {
                    // The audit agent publishes via a temp file + rename, but be conservative
                    // if a network-backed settings directory exposes a short visibility race.
                    return;
                }

                results.Add(new WorkerAuditResult
                {
                    ExpectedRole = worker.Role,
                    ExpectedCharacter = worker.Character,
                    Result = result
                });
            }

            string runId = results
                .Select(r => StringValue(r.Result, "RunId"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(runId))
                return;

            if (string.Equals(runId, _lastPublishedRunId, StringComparison.Ordinal))
                return;

            string validationError;
            if (!ValidateAggregate(results, runId, out validationError))
            {
                string key = runId + "|" + validationError;
                if (!string.Equals(key, _lastProblemKey, StringComparison.Ordinal))
                {
                    _lastProblemKey = key;
                    Logger.Warning(
                        $"STORAGE BASELINE NOT PUBLISHED run={runId}: {validationError}");
                }
                return;
            }

            JObject baseline = BuildBaseline(runId, results);
            string json = baseline.ToString(Formatting.Indented) + Environment.NewLine;

            string dataDir = Path.Combine(_settingsDir, "data");
            string archiveDir = Path.Combine(dataDir, "storage-baselines");
            Directory.CreateDirectory(archiveDir);

            string archivePath = Path.Combine(
                archiveDir,
                $"storage-baseline-{SafeFileToken(runId)}.json");
            string latestPath = Path.Combine(dataDir, "storage-baseline.json");

            // Preserve the exact cutover run forever, then replace the stable latest pointer.
            // The latest file is only touched after aggregate validation succeeds.
            WriteNewFileAtomically(archivePath, json);
            ReplaceFileAtomically(latestPath, json);

            _lastPublishedRunId = runId;
            _lastProblemKey = null;

            int totalBags = results.Sum(r => IntValue(r.Result, "TotalBagCount"));
            int totalItems = CountObservedItems(results);
            Logger.Information(
                $"STORAGE BASELINE PUBLISHED run={runId} workers={results.Count} " +
                $"bags={totalBags} observedItems={totalItems} latest='{latestPath}' " +
                $"archive='{archivePath}'. This run is now the persistent physical cutover baseline.");
        }

        private BaselineConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<BaselineConfig>();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to read the CityBankers settings section: {ex.Message}");
                return null;
            }
        }

        private static bool TryGetRole(
            Dictionary<string, WorkerConfig> roles,
            string role,
            out WorkerConfig value)
        {
            foreach (KeyValuePair<string, WorkerConfig> pair in roles)
            {
                if (string.Equals(pair.Key, role, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static bool ValidateAggregate(
            List<WorkerAuditResult> results,
            string runId,
            out string error)
        {
            error = null;

            if (results == null || results.Count != StorageRoles.Length)
            {
                error = "all " + StorageRoles.Length + " storage-worker results are required";
                return false;
            }

            foreach (WorkerAuditResult worker in results)
            {
                JObject result = worker.Result;
                string actualRunId = StringValue(result, "RunId");
                string actualRole = StringValue(result, "Role");
                string actualCharacter = StringValue(result, "Character");

                if (!string.Equals(actualRunId, runId, StringComparison.Ordinal))
                {
                    error = "worker result RunId mismatch";
                    return false;
                }

                if (!string.Equals(actualRole, worker.ExpectedRole, StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        $"role mismatch: expected {worker.ExpectedRole}, result says {actualRole}";
                    return false;
                }

                if (!string.Equals(
                    actualCharacter,
                    worker.ExpectedCharacter,
                    StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        $"character mismatch for {worker.ExpectedRole}: expected " +
                        $"{worker.ExpectedCharacter}, result says {actualCharacter}";
                    return false;
                }

                if (!BoolValue(result, "BankOpened"))
                {
                    error = $"{worker.ExpectedRole} bank was not verified open";
                    return false;
                }

                string fatal = StringValue(result, "FatalError");
                if (!string.IsNullOrWhiteSpace(fatal))
                {
                    error = $"{worker.ExpectedRole} reported fatal audit error";
                    return false;
                }

                int total = IntValue(result, "TotalBagCount");
                int opened = IntValue(result, "OpenedCount");
                int failed = IntValue(result, "FailedCount");
                int bank = IntValue(result, "BankBagCount");
                int returned = IntValue(result, "BankReturnedCount");
                int returnFailures = IntValue(result, "BankReturnFailureCount");

                if (total <= 0 || opened != total || failed != 0)
                {
                    error =
                        $"{worker.ExpectedRole} incomplete bag inspection: " +
                        $"opened={opened}/{total}, failed={failed}";
                    return false;
                }

                if (returned != bank || returnFailures != 0)
                {
                    error =
                        $"{worker.ExpectedRole} incomplete bank restoration: " +
                        $"returned={returned}/{bank}, returnFailures={returnFailures}";
                    return false;
                }

                JArray bags = result["Bags"] as JArray;
                if (bags == null || bags.Count != total)
                {
                    error =
                        $"{worker.ExpectedRole} bag-detail count does not match total " +
                        $"({bags?.Count ?? 0}/{total})";
                    return false;
                }

                foreach (JObject bag in bags.OfType<JObject>())
                {
                    if (!BoolValue(bag, "Opened"))
                    {
                        error = $"{worker.ExpectedRole} contains an unopened bag detail";
                        return false;
                    }

                    if (string.Equals(
                            StringValue(bag, "Source"),
                            "bank",
                            StringComparison.OrdinalIgnoreCase) &&
                        !BoolValue(bag, "ReturnedToBank"))
                    {
                        error = $"{worker.ExpectedRole} contains a bank bag not verified returned";
                        return false;
                    }
                }
            }

            return true;
        }

        private static JObject BuildBaseline(
            string runId,
            List<WorkerAuditResult> results)
        {
            var workers = new JObject();
            var summary = new JObject
            {
                ["workerCount"] = results.Count,
                ["totalBags"] = results.Sum(r => IntValue(r.Result, "TotalBagCount")),
                ["bankBags"] = results.Sum(r => IntValue(r.Result, "BankBagCount")),
                ["inventoryBags"] = results.Sum(r => IntValue(r.Result, "InventoryBagCount")),
                ["observedStoredItems"] = CountObservedItems(results)
            };

            foreach (WorkerAuditResult worker in results)
            {
                JObject result = worker.Result;
                JArray normalizedBags = BuildNormalizedBags(result);

                workers[worker.ExpectedRole] = new JObject
                {
                    ["role"] = worker.ExpectedRole,
                    ["character"] = worker.ExpectedCharacter,
                    ["observedUtc"] = result["ObservedUtc"]?.DeepClone(),
                    ["playfieldModelId"] = result["PlayfieldModelId"]?.DeepClone(),
                    ["bankOpened"] = result["BankOpened"]?.DeepClone(),
                    ["bagCount"] = result["TotalBagCount"]?.DeepClone(),
                    ["bankBagCount"] = result["BankBagCount"]?.DeepClone(),
                    ["inventoryBagCount"] = result["InventoryBagCount"]?.DeepClone(),
                    ["bags"] = normalizedBags,
                    // Keep the source audit result intact as provenance/debug evidence.
                    ["sourceAuditResult"] = result.DeepClone()
                };
            }

            return new JObject
            {
                ["format"] = "citybankers-storage-baseline-v1",
                ["revision"] = 1,
                ["authoritative"] = true,
                ["runId"] = runId,
                ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["source"] = "Banker.exe bagaudit",
                ["cutoverRule"] =
                    "Published only after all configured storage workers completed a clean audit and all staged bank bags were verified returned.",
                ["summary"] = summary,
                ["workers"] = workers
            };
        }

        private static JArray BuildNormalizedBags(JObject result)
        {
            var normalized = new JArray();
            JArray bags = result["Bags"] as JArray;
            if (bags == null)
                return normalized;

            foreach (JObject bag in bags.OfType<JObject>())
            {
                bool bank = string.Equals(
                    StringValue(bag, "Source"),
                    "bank",
                    StringComparison.OrdinalIgnoreCase);

                string finalOuterSlot = bank
                    ? StringValue(bag, "ReturnedOuterSlot")
                    : StringValue(bag, "OuterSlot");
                string finalOuterSlotType = bank
                    ? StringValue(bag, "ReturnedOuterSlotType")
                    : StringValue(bag, "OuterSlotType");
                int finalOuterSlotInstance = bank
                    ? IntValue(bag, "ReturnedOuterSlotInstance")
                    : IntValue(bag, "OuterSlotInstance");

                normalized.Add(new JObject
                {
                    ["source"] = bank ? "bank" : "inventory",
                    ["finalOuterSlot"] = finalOuterSlot,
                    ["finalOuterSlotType"] = finalOuterSlotType,
                    ["finalOuterSlotInstance"] = finalOuterSlotInstance,
                    ["originalOuterSlot"] = StringValue(bag, "OuterSlot"),
                    ["returnedToOriginalOuterSlot"] = BoolValue(bag, "ReturnedToOriginalOuterSlot"),
                    ["uniqueIdentity"] = StringValue(bag, "UniqueIdentity"),
                    ["uniqueIdentityType"] = StringValue(bag, "UniqueIdentityType"),
                    ["uniqueIdentityInstance"] = IntValue(bag, "UniqueIdentityInstance"),
                    ["name"] = StringValue(bag, "Name"),
                    ["lowId"] = IntValue(bag, "LowId"),
                    ["highId"] = IntValue(bag, "HighId"),
                    ["ql"] = IntValue(bag, "Ql"),
                    ["itemCount"] = IntValue(bag, "ItemCount"),
                    ["freeSlots"] = IntValue(bag, "FreeSlots"),
                    ["items"] = (bag["Items"] as JArray)?.DeepClone() ?? new JArray()
                });
            }

            return new JArray(
                normalized
                    .OfType<JObject>()
                    .OrderBy(b => StringValue(b, "source") == "bank" ? 0 : 1)
                    .ThenBy(b => IntValue(b, "finalOuterSlotInstance")));
        }

        private static int CountObservedItems(IEnumerable<WorkerAuditResult> results)
        {
            int count = 0;
            foreach (WorkerAuditResult worker in results)
            {
                JArray bags = worker.Result["Bags"] as JArray;
                if (bags == null)
                    continue;

                foreach (JObject bag in bags.OfType<JObject>())
                    count += (bag["Items"] as JArray)?.Count ?? 0;
            }
            return count;
        }

        private static string StringValue(JObject value, string property)
        {
            JToken token = value?[property];
            return token == null || token.Type == JTokenType.Null
                ? null
                : token.ToString();
        }

        private static int IntValue(JObject value, string property)
        {
            JToken token = value?[property];
            int result;
            return token != null && int.TryParse(token.ToString(), out result)
                ? result
                : 0;
        }

        private static bool BoolValue(JObject value, string property)
        {
            JToken token = value?[property];
            bool result;
            return token != null && bool.TryParse(token.ToString(), out result) && result;
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token;
        }

        private static void WriteNewFileAtomically(string path, string content)
        {
            if (File.Exists(path))
            {
                // A run id should be unique. If the exact archive already exists, leave it
                // untouched rather than rewriting historical physical evidence.
                return;
            }

            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, content);
                File.Move(temp, path);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static void ReplaceFileAtomically(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, content);
                if (File.Exists(path))
                    File.Replace(temp, path, null, true);
                else
                    File.Move(temp, path);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class BaselineConfig
        {
            public Dictionary<string, WorkerConfig> Roles;
        }

        private sealed class WorkerConfig
        {
            public string Role;
            public string Character;
        }

        private sealed class WorkerAuditResult
        {
            public string ExpectedRole;
            public string ExpectedCharacter;
            public JObject Result;
        }
    }
}
