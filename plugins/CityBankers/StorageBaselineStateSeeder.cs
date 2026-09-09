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
    /// Central-only bridge from the validated rich bagaudit snapshot to the canonical
    /// operational RuntimeStateStore model used by future trade/storage code.
    ///
    /// StorageBaselineCoordinator is the validation/provenance boundary. This seeder
    /// only consumes a snapshot already marked authoritative by that coordinator.
    /// </summary>
    public class StorageBaselineStateSeeder : ClientlessPluginEntry
    {
        private string _settingsDir;
        private bool _enabled;
        private DateTime _nextCheckUtc;
        private string _lastSeenRunId;
        private string _lastErrorKey;

        public override void Init(string pluginDir)
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            string centralCharacter = ResolveConfiguredCentral();
            if (string.IsNullOrWhiteSpace(centralCharacter) ||
                !string.Equals(
                    Client.CharacterName,
                    centralCharacter,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _enabled = true;
            _nextCheckUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;

            Logger.Information(
                $"CityBankers canonical storage-state seeder armed on Central {Client.CharacterName}. " +
                "It will seed storage-state/current-stock/ledger only from a validated " +
                "settings/data/storage-baseline.json cutover snapshot.");
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay)
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextCheckUtc)
                return;

            _nextCheckUtc = now.AddMilliseconds(500);

            try
            {
                TrySeed();
            }
            catch (Exception ex)
            {
                string key = ex.GetType().FullName + "|" + ex.Message;
                if (!string.Equals(key, _lastErrorKey, StringComparison.Ordinal))
                {
                    _lastErrorKey = key;
                    Logger.Error($"STORAGE STATE SEED failed: {ex}");
                }
            }
        }

        private void TrySeed()
        {
            string snapshotPath = Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "storage-baseline.json");
            if (!File.Exists(snapshotPath))
                return;

            JObject snapshot;
            try
            {
                snapshot = JObject.Parse(File.ReadAllText(snapshotPath));
            }
            catch
            {
                // Same-directory atomic publication should make this rare, but a linked
                // network filesystem may briefly expose visibility oddities. Retry later.
                return;
            }

            if (!string.Equals(
                    StringValue(snapshot, "format"),
                    "citybankers-storage-baseline-v1",
                    StringComparison.Ordinal) ||
                !BoolValue(snapshot, "authoritative"))
            {
                return;
            }

            string runId = StringValue(snapshot, "runId");
            if (string.IsNullOrWhiteSpace(runId) ||
                string.Equals(runId, _lastSeenRunId, StringComparison.Ordinal))
            {
                return;
            }

            StorageState current = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (current != null &&
                string.Equals(current.BaselineRunId, runId, StringComparison.Ordinal))
            {
                _lastSeenRunId = runId;
                return;
            }

            StorageState state = BuildStorageState(snapshot, runId);
            string transactionId = "baseline:" + runId;

            RuntimeStateStore.SaveStorageBaseline(
                _settingsDir,
                state,
                transactionId);

            _lastSeenRunId = runId;
            _lastErrorKey = null;

            int bagCount = state.Workers.Sum(w => w.Bags != null ? w.Bags.Count : 0);
            int itemCount = state.Workers.Sum(w =>
                w.Bags != null
                    ? w.Bags.Sum(b => b.Items != null ? b.Items.Count : 0)
                    : 0);

            Logger.Information(
                $"STORAGE STATE SEEDED run={runId} workers={state.Workers.Count} " +
                $"bags={bagCount} observedItems={itemCount} " +
                $"storage='{RuntimeStateStore.GetStorageStatePath(_settingsDir)}' " +
                $"stock='{RuntimeStateStore.GetCurrentStockPath(_settingsDir)}'. " +
                "This fresh audit is now canonical operational state for future trades.");
        }

        private StorageState BuildStorageState(JObject snapshot, string runId)
        {
            var state = new StorageState
            {
                BaselineRunId = runId,
                UpdatedUtc = DateTime.UtcNow,
                Workers = new List<StorageWorkerState>()
            };

            JObject workers = snapshot["workers"] as JObject;
            if (workers == null || !workers.Properties().Any())
                throw new InvalidOperationException("Validated baseline contains no worker map.");

            foreach (JProperty property in workers.Properties())
            {
                JObject workerSnapshot = property.Value as JObject;
                if (workerSnapshot == null)
                    continue;

                string role = StringValue(workerSnapshot, "role") ?? property.Name;
                string character = StringValue(workerSnapshot, "character");
                DateTime observedUtc = DateValue(workerSnapshot, "observedUtc");
                if (observedUtc == DateTime.MinValue)
                    observedUtc = DateTime.UtcNow;

                JObject sourceResult = workerSnapshot["sourceAuditResult"] as JObject;
                JArray sourceBags = sourceResult?["Bags"] as JArray;
                if (sourceBags == null)
                    throw new InvalidOperationException(
                        "Validated baseline worker '" + role + "' has no source bag details.");

                var worker = new StorageWorkerState
                {
                    Role = role,
                    Character = character,
                    ObservedUtc = observedUtc,
                    Bags = new List<StorageBagState>()
                };

                foreach (JObject bagSnapshot in sourceBags.OfType<JObject>())
                {
                    bool bank = string.Equals(
                        StringValue(bagSnapshot, "Source"),
                        "bank",
                        StringComparison.OrdinalIgnoreCase);

                    string finalSlotType = bank
                        ? StringValue(bagSnapshot, "ReturnedOuterSlotType")
                        : StringValue(bagSnapshot, "OuterSlotType");
                    int finalSlotInstance = bank
                        ? IntValue(bagSnapshot, "ReturnedOuterSlotInstance")
                        : IntValue(bagSnapshot, "OuterSlotInstance");

                    int itemCount = IntValue(bagSnapshot, "ItemCount");
                    int freeSlots = IntValue(bagSnapshot, "FreeSlots");
                    int observedCapacity =
                        itemCount >= 0 && freeSlots >= 0
                            ? itemCount + freeSlots
                            : 21;
                    if (observedCapacity <= 0)
                        observedCapacity = 21;

                    var bag = new StorageBagState
                    {
                        Source = bank ? "bank" : "inventory",
                        OuterSlotType = finalSlotType,
                        OuterSlotInstance = finalSlotInstance,
                        LastUniqueIdentity = StringValue(bagSnapshot, "UniqueIdentity"),
                        LastHandle = IntValue(bagSnapshot, "Handle"),
                        Capacity = observedCapacity,
                        Items = new List<StoredItemState>()
                    };

                    JArray items = bagSnapshot["Items"] as JArray;
                    if (items != null)
                    {
                        foreach (JObject item in items.OfType<JObject>())
                        {
                            bag.Items.Add(new StoredItemState
                            {
                                UniqueIdentity = StringValue(item, "UniqueIdentity"),
                                AoId = IntValue(item, "LowId"),
                                HighId = IntValue(item, "HighId"),
                                Ql = IntValue(item, "Ql"),
                                Name = StringValue(item, "Name") ?? string.Empty,
                                InnerSlot = IntValue(item, "SlotInstance"),
                                ObservedUtc = observedUtc,
                                TransactionId = "baseline:" + runId
                            });
                        }
                    }

                    worker.Bags.Add(bag);
                }

                worker.Bags = worker.Bags
                    .OrderBy(b =>
                        string.Equals(b.Source, "bank", StringComparison.OrdinalIgnoreCase)
                            ? 0
                            : 1)
                    .ThenBy(b => b.OuterSlotInstance)
                    .ToList();

                state.Workers.Add(worker);
            }

            state.Workers = state.Workers
                .OrderBy(w => RoleOrder(w.Role))
                .ThenBy(w => w.Character, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (state.Workers.Count != 8)
                throw new InvalidOperationException(
                    "Validated baseline did not yield exactly eight storage workers.");

            return state;
        }

        private string ResolveConfiguredCentral()
        {
            try
            {
                JObject config = SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = config["Roles"] as JObject;
                if (roles == null)
                    return null;

                foreach (JProperty property in roles.Properties())
                {
                    if (!string.Equals(
                            property.Name,
                            "central",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JObject central = property.Value as JObject;
                    return central != null
                        ? StringValue(central, "Character")
                        : null;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    "Unable to resolve Central for storage-state seeder from the Bankers section: " +
                    ex.Message);
            }

            return null;
        }

        private static int RoleOrder(string role)
        {
            switch ((role ?? string.Empty).ToLowerInvariant())
            {
                case "artillery": return 0;
                case "infantry": return 1;
                case "control": return 2;
                case "support": return 3;
                case "extermination": return 4;
                case "spirit": return 5;
                case "dyna": return 6;
                case "phatz": return 7;
                default: return 99;
            }
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

        private static DateTime DateValue(JObject value, string property)
        {
            JToken token = value?[property];
            DateTime result;
            return token != null && DateTime.TryParse(token.ToString(), out result)
                ? result.ToUniversalTime()
                : DateTime.MinValue;
        }
    }
}
