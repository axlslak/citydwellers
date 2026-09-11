using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Restores bank-origin source bags that were left staged in normal inventory after the
    /// repair item itself was already physically verified returned to Central.
    ///
    /// The serial Extermination return proved the item reached Central, but the following
    /// diagnostic showed Kbinfa at 101 bank bags + 9 inventory bags instead of its normal
    /// 102 + 8. This cleanup uses the repair plan's archived source baseline to identify the
    /// exact container UID. It acts only for plan items already marked returned-central,
    /// waits for the live bank to be open, and may move only that exact container back to
    /// bank. It never moves or opens a symbiant and never participates in trade.
    /// </summary>
    public class RouteRepairReturnedBagCleanupAgent : ClientlessPluginEntry
    {
        private const string PlanFile = "route-repair-plan.json";
        private const string SentinelFile = "route-repair-active.json";
        private const string BaselineArchiveDirectory = "storage-baselines";
        private const int PollMilliseconds = 250;
        private const int ReturnTimeoutSeconds = 15;

        private string _settingsDir;
        private string _dataDir;
        private bool _enabled;
        private DateTime _nextPollUtc;
        private string _returningBagIdentity;
        private string _returningItemId;
        private DateTime _deadlineUtc;
        private readonly HashSet<string> _completeItemIds =
            new HashSet<string>(StringComparer.Ordinal);

        public override void Init(string pluginDir)
        {
            // Full physical census and BankingService now own normal-mode recovery.
            if (StartupCensusGate.UsesPhysicalRecovery) return;

            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            if (ServicePolicy.IsBagAuditMode())
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _dataDir = RuntimeStateStore.GetDataDirectory(_settingsDir);
            _enabled = true;
            _nextPollUtc = DateTime.MinValue;
            Client.OnUpdate += Tick;
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

            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                CleanupReturnedBagIfNeeded();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"[CityBankers] ROUTE REPAIR RETURNED BAG CLEANUP failed " +
                    $"character={Client.CharacterName}: {ex}");
            }
        }

        private void CleanupReturnedBagIfNeeded()
        {
            if (!File.Exists(Path.Combine(_dataDir, SentinelFile)) ||
                Inventory.Bank == null || !Inventory.Bank.IsOpen)
            {
                return;
            }

            JObject plan = ReadJson(Path.Combine(_dataDir, PlanFile));
            if (plan == null)
                return;

            JArray items = Value(plan, "Items") as JArray;
            JObject item = items?.OfType<JObject>().FirstOrDefault(candidate =>
                string.Equals(Text(candidate, "State"), "returned-central", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Text(candidate, "SourceCharacter"), Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                !_completeItemIds.Contains(Text(candidate, "Id") ?? string.Empty));
            if (item == null)
                return;

            string itemId = Text(item, "Id") ?? string.Empty;
            string runId = Text(plan, "SourceRunId");
            string role = Text(item, "SourceRole");
            int aoid = Int(item, "AoId");
            int highId = Int(item, "HighId");
            int ql = Int(item, "Ql");

            BaselineMatch evidence = FindBaselineMatch(runId, role, aoid, highId, ql);
            if (evidence == null)
                return;

            string source = Text(evidence.Bag, "source");
            string bagIdentity = Text(evidence.Bag, "uniqueIdentity");
            if (!string.Equals(source, "bank", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(bagIdentity))
            {
                _completeItemIds.Add(itemId);
                return;
            }

            List<Item> bankMatches = FindBag(Inventory.Bank.Items, bagIdentity, false);
            List<Item> inventoryMatches = FindBag(Inventory.Items, bagIdentity, true);
            if (bankMatches.Count + inventoryMatches.Count != 1)
            {
                Logger.Error(
                    $"[CityBankers] ROUTE REPAIR RETURNED BAG CLEANUP blocked " +
                    $"character={Client.CharacterName} aoid={aoid} bag={bagIdentity}; " +
                    $"bankMatches={bankMatches.Count} inventoryMatches={inventoryMatches.Count}.");
                _completeItemIds.Add(itemId);
                return;
            }

            if (bankMatches.Count == 1)
            {
                if (string.Equals(_returningItemId, itemId, StringComparison.Ordinal))
                {
                    Logger.Information(
                        $"[CityBankers] ROUTE REPAIR RETURNED BAG CLEANUP verified " +
                        $"character={Client.CharacterName} aoid={aoid} bag={bagIdentity} " +
                        $"liveOuter={bankMatches[0].Slot.Instance} restored to bank.");
                }
                _returningBagIdentity = null;
                _returningItemId = null;
                _deadlineUtc = DateTime.MinValue;
                _completeItemIds.Add(itemId);
                return;
            }

            if (!string.Equals(_returningItemId, itemId, StringComparison.Ordinal))
            {
                Item staged = inventoryMatches[0];
                _returningItemId = itemId;
                _returningBagIdentity = bagIdentity;
                _deadlineUtc = DateTime.UtcNow.AddSeconds(ReturnTimeoutSeconds);
                staged.MoveToBank();
                Logger.Information(
                    $"[CityBankers] ROUTE REPAIR RETURNED BAG CLEANUP returning " +
                    $"character={Client.CharacterName} aoid={aoid} bag={bagIdentity} " +
                    $"inventorySlot={staged.Slot.Instance} to open bank. No symbiant was moved.");
                return;
            }

            if (DateTime.UtcNow >= _deadlineUtc)
            {
                Logger.Error(
                    $"[CityBankers] ROUTE REPAIR RETURNED BAG CLEANUP timed out " +
                    $"character={Client.CharacterName} aoid={aoid} bag={_returningBagIdentity}. " +
                    "No symbiant was moved by cleanup.");
                _completeItemIds.Add(itemId);
            }
        }

        private BaselineMatch FindBaselineMatch(
            string runId,
            string role,
            int aoid,
            int highId,
            int ql)
        {
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(role))
                return null;

            string path = Path.Combine(
                _dataDir,
                BaselineArchiveDirectory,
                "storage-baseline-" + SafeFileToken(runId) + ".json");
            JObject baseline = ReadJson(path);
            if (baseline == null || !string.Equals(Text(baseline, "runId"), runId, StringComparison.Ordinal))
                return null;

            JObject workers = Value(baseline, "workers") as JObject;
            JObject worker = workers?.Properties()
                .FirstOrDefault(property => string.Equals(property.Name, role, StringComparison.OrdinalIgnoreCase))
                ?.Value as JObject;
            if (worker == null)
                return null;

            var matches = new List<BaselineMatch>();
            JArray bags = Value(worker, "bags") as JArray;
            foreach (JObject bag in bags?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                JArray bagItems = Value(bag, "items") as JArray;
                foreach (JObject stored in bagItems?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (IntEither(stored, "LowId", "lowId") == aoid &&
                        IntEither(stored, "HighId", "highId") == highId &&
                        IntEither(stored, "Ql", "ql") == ql)
                    {
                        matches.Add(new BaselineMatch { Bag = bag });
                    }
                }
            }

            if (matches.Count != 1)
            {
                Logger.Error(
                    $"[CityBankers] ROUTE REPAIR RETURNED BAG CLEANUP cannot resolve audited bag " +
                    $"character={Client.CharacterName} aoid={aoid} matches={matches.Count} run={runId}.");
                return null;
            }
            return matches[0];
        }

        private static List<Item> FindBag(IEnumerable<Item> source, string identity, bool requireInventorySlot)
        {
            return (source ?? Enumerable.Empty<Item>())
                .Where(value => value != null &&
                    value.UniqueIdentity.Type == IdentityType.Container &&
                    (!requireInventorySlot || value.Slot.Type == IdentityType.Inventory) &&
                    string.Equals(value.UniqueIdentity.ToString(), identity, StringComparison.Ordinal))
                .ToList();
        }

        private JObject ReadJson(string path)
        {
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        private static JToken Value(JObject value, string name)
        {
            return value?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        }

        private static string Text(JObject value, string name)
        {
            JToken token = Value(value, name);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static int Int(JObject value, string name)
        {
            int result;
            JToken token = Value(value, name);
            return token != null && int.TryParse(token.ToString(), out result) ? result : 0;
        }

        private static int IntEither(JObject value, string first, string second)
        {
            int result = Int(value, first);
            return result != 0 ? result : Int(value, second);
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        }

        private sealed class BaselineMatch
        {
            public JObject Bag;
        }
    }
}
