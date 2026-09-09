using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Central-owned accounting authority.
    ///
    /// The active ledger contains one row per physical symbiant CityBankers owns now.
    /// Immutable/repeated AO metadata lives in a separate index. When an item leaves
    /// custody its active row is removed and appended to monthly JSONL history.
    ///
    /// Workers never write ledger/index/history. They continue to report physical work
    /// through the existing dispatch/result/event surfaces; Central consumes that evidence
    /// and mutates the authoritative accounting files.
    /// </summary>
    public sealed class ActiveLedgerCoordinator : ClientlessPluginEntry
    {
        private string _settingsDir;
        private bool _isCentral;
        private DateTime _nextTickUtc;
        private readonly Dictionary<string, long> _eventOffsets =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public override void Init(string pluginDir)
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _isCentral = IsCurrentCharacterCentral(_settingsDir, Client.CharacterName);
            if (!_isCentral)
                return;

            ActiveLedgerStore.EnsureInitialized(
                _settingsDir,
                Client.CharacterName,
                RuntimeStateStore.LoadCurrentStock(_settingsDir));

            InitializeEventOffsets();
            _nextTickUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;

            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                "central",
                "ACTIVE LEDGER coordinator initialized; Central is sole ledger/index/history writer.");
        }

        public override void Teardown()
        {
            if (_isCentral)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_isCentral || !Client.InPlay || DateTime.UtcNow < _nextTickUtc)
                return;

            _nextTickUtc = DateTime.UtcNow.AddMilliseconds(250);

            try
            {
                ProcessNewLegacyEvents();
                ActiveLedgerStore.SyncStoredLocations(
                    _settingsDir,
                    RuntimeStateStore.LoadCurrentStock(_settingsDir));
            }
            catch (Exception ex)
            {
                Logger.Error($"ACTIVE LEDGER coordinator tick failed: {ex}");
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    "central",
                    "ACTIVE LEDGER ERROR: " + ex);
            }
        }

        private void InitializeEventOffsets()
        {
            string directory = ActiveLedgerStore.GetLegacyEventDirectory(_settingsDir);
            if (!Directory.Exists(directory))
                return;

            foreach (string path in Directory.GetFiles(directory, "citybankers-*.jsonl"))
                _eventOffsets[path] = new FileInfo(path).Length;
        }

        private void ProcessNewLegacyEvents()
        {
            string directory = ActiveLedgerStore.GetLegacyEventDirectory(_settingsDir);
            if (!Directory.Exists(directory))
                return;

            foreach (string path in Directory.GetFiles(directory, "citybankers-*.jsonl")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                long offset;
                if (!_eventOffsets.TryGetValue(path, out offset))
                    offset = 0;

                FileInfo info = new FileInfo(path);
                if (offset > info.Length)
                    offset = 0;
                if (offset == info.Length)
                {
                    _eventOffsets[path] = offset;
                    continue;
                }

                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                                ProcessLegacyEvent(line);
                        }
                    }
                    _eventOffsets[path] = stream.Position;
                }
            }
        }

        private void ProcessLegacyEvent(string line)
        {
            JObject record;
            try
            {
                record = JObject.Parse(line);
            }
            catch
            {
                return;
            }

            string eventName = record["Event"]?.ToString();
            string transactionId = record["TransactionId"]?.ToString();
            DateTime utc = ParseUtc(record["Utc"]);
            List<TransferItemState> items = ParseItems(record["Items"] as JArray);

            if (string.Equals(eventName, "player_trade_completed", StringComparison.OrdinalIgnoreCase))
            {
                string donor = record["Source"]?.ToString();
                if (string.IsNullOrWhiteSpace(donor))
                    donor = TrustedOperators.BootstrapAdmin;

                ActiveLedgerStore.RecordDonation(
                    _settingsDir,
                    transactionId,
                    donor,
                    Client.CharacterName,
                    utc,
                    items);
                return;
            }

            if (string.Equals(eventName, "dispatch_trade_completed", StringComparison.OrdinalIgnoreCase))
            {
                string batchId = record["BatchId"]?.ToString();
                DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                DispatchBatchState batch = queue?.Batches?.FirstOrDefault(candidate =>
                    string.Equals(candidate.BatchId, batchId, StringComparison.Ordinal));
                if (batch != null && !string.IsNullOrWhiteSpace(batch.Character))
                {
                    ActiveLedgerStore.MarkDispatched(
                        _settingsDir,
                        transactionId,
                        batch.Character,
                        items);
                }
                return;
            }

            if (string.Equals(eventName, "dispatch_stored", StringComparison.OrdinalIgnoreCase))
            {
                ActiveLedgerStore.SyncStoredLocations(
                    _settingsDir,
                    RuntimeStateStore.LoadCurrentStock(_settingsDir));
                return;
            }

            if (string.Equals(eventName, "donation_overcap_deleted", StringComparison.OrdinalIgnoreCase))
            {
                foreach (TransferItemState item in items)
                {
                    ActiveLedgerStore.ArchiveActiveItem(
                        _settingsDir,
                        transactionId,
                        item.AoId,
                        utc,
                        "deleted_overcap",
                        null);
                }
            }
        }

        private static bool IsCurrentCharacterCentral(string settingsDir, string currentCharacter)
        {
            try
            {
                JObject root = SettingsPaths.ReadBankersSettings(settingsDir);
                JObject roles = root["Roles"] as JObject ?? root["roles"] as JObject;
                JObject central = roles?["central"] as JObject;
                string character = central?["Character"]?.ToString() ?? central?["character"]?.ToString();
                return !string.IsNullOrWhiteSpace(character) &&
                    string.Equals(character, currentCharacter, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static List<TransferItemState> ParseItems(JArray items)
        {
            var result = new List<TransferItemState>();
            if (items == null)
                return result;

            foreach (JObject item in items.OfType<JObject>())
            {
                result.Add(new TransferItemState
                {
                    UniqueIdentity = item["UniqueIdentity"]?.ToString(),
                    AoId = IntToken(item["AoId"]),
                    HighId = IntToken(item["HighId"]),
                    Ql = IntToken(item["Ql"]),
                    Name = item["Name"]?.ToString() ?? string.Empty
                });
            }
            return result;
        }

        private static DateTime ParseUtc(JToken token)
        {
            DateTime value;
            return token != null && DateTime.TryParse(token.ToString(), out value)
                ? value.ToUniversalTime()
                : DateTime.UtcNow;
        }

        private static int IntToken(JToken token)
        {
            int value;
            return token != null && int.TryParse(token.ToString(), out value)
                ? value
                : 0;
        }
    }

    internal sealed class ActiveLedgerState
    {
        public string Format = "citybankers-active-ledger-v1";
        public DateTime UpdatedUtc;
        public List<ActiveLedgerItem> Items = new List<ActiveLedgerItem>();
    }

    internal sealed class ActiveLedgerItem
    {
        public string Id;
        public int AoId;
        public string TransactionId;
        public string From;
        public DateTime ReceivedUtc;
        public string Family;
        public string Character;
        public string Location;
        public int? Bag;
        public int? Slot;
    }

    internal sealed class SymbiantIndexState
    {
        public string Format = "citybankers-symbiant-index-v1";
        public DateTime UpdatedUtc;
        public List<SymbiantIndexItem> Items = new List<SymbiantIndexItem>();
    }

    internal sealed class SymbiantIndexItem
    {
        public int AoId;
        public string Family;
        public int? HighId;
        public int? Ql;
        public string Name;
        public string Slot;
    }

    internal sealed class ActiveHistoryRecord
    {
        public string Format = "citybankers-history-v1";
        public DateTime LeftUtc;
        public string Reason;
        public string Recipient;
        public ActiveLedgerItem Item;
    }

    internal sealed class LegacyProvenance
    {
        public string Donor;
        public DateTime Utc;
    }

    internal static class ActiveLedgerStore
    {
        private const string LedgerFileName = "ledger.json";
        private const string IndexFileName = "symbiant-index.json";
        private static readonly object HistoryLock = new object();

        public static string GetActiveLedgerPath(string settingsDir)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(settingsDir), LedgerFileName);
        }

        public static string GetIndexPath(string settingsDir)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(settingsDir), IndexFileName);
        }

        public static string GetHistoryDirectory(string settingsDir)
        {
            string path = Path.Combine(
                RuntimeStateStore.GetDataDirectory(settingsDir),
                "history");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetLegacyEventDirectory(string settingsDir)
        {
            return RuntimeStateStore.GetLedgerDirectory(settingsDir);
        }

        public static void EnsureInitialized(
            string settingsDir,
            string centralCharacter,
            CurrentStockState legacyStock)
        {
            EnsureIndexSeeded(settingsDir);

            ActiveLedgerState ledger = LoadLedger(settingsDir);
            if (ledger == null)
            {
                ledger = MigrateLegacyStock(settingsDir, legacyStock);
                SaveLedger(settingsDir, ledger);
                RuntimeStateStore.AppendActivity(
                    settingsDir,
                    centralCharacter,
                    "central",
                    "ACTIVE LEDGER migrated from current physical stock; entries=" +
                    ledger.Items.Count + ".");
            }

            UpsertIndexFromStock(settingsDir, legacyStock);
            MigrateLegacyHistory(settingsDir, centralCharacter);
        }

        public static ActiveLedgerState LoadLedger(string settingsDir)
        {
            ActiveLedgerState state = RuntimeStateStore.ReadJson<ActiveLedgerState>(
                GetActiveLedgerPath(settingsDir));
            if (state != null && state.Items == null)
                state.Items = new List<ActiveLedgerItem>();
            return state;
        }

        public static SymbiantIndexState LoadIndex(string settingsDir)
        {
            SymbiantIndexState state = RuntimeStateStore.ReadJson<SymbiantIndexState>(
                GetIndexPath(settingsDir));
            if (state != null && state.Items == null)
                state.Items = new List<SymbiantIndexItem>();
            return state;
        }

        public static void RecordDonation(
            string settingsDir,
            string transactionId,
            string donor,
            string centralCharacter,
            DateTime receivedUtc,
            IEnumerable<TransferItemState> items)
        {
            List<TransferItemState> incoming = (items ?? Enumerable.Empty<TransferItemState>())
                .Where(item => item != null && item.AoId != 0)
                .ToList();
            if (incoming.Count == 0)
                return;

            UpsertIndex(settingsDir, incoming);
            ActiveLedgerState ledger = LoadLedger(settingsDir) ?? NewLedger();
            bool changed = false;

            foreach (IGrouping<int, TransferItemState> group in incoming.GroupBy(item => item.AoId))
            {
                int existingCount = ledger.Items.Count(entry =>
                    entry.AoId == group.Key &&
                    string.Equals(entry.TransactionId, transactionId, StringComparison.Ordinal));

                foreach (TransferItemState item in group.Skip(existingCount))
                {
                    string family;
                    SymbiantCatalog.TryGetDestinationRole(settingsDir, item.AoId, out family);
                    ledger.Items.Add(new ActiveLedgerItem
                    {
                        Id = "cb-" + Guid.NewGuid().ToString("N"),
                        AoId = item.AoId,
                        TransactionId = transactionId,
                        From = donor,
                        ReceivedUtc = receivedUtc == DateTime.MinValue ? DateTime.UtcNow : receivedUtc,
                        Family = family,
                        Character = centralCharacter,
                        Location = "inventory",
                        Bag = null,
                        Slot = null
                    });
                    changed = true;
                }
            }

            if (changed)
                SaveLedger(settingsDir, ledger);
        }

        public static void MarkDispatched(
            string settingsDir,
            string transactionId,
            string workerCharacter,
            IEnumerable<TransferItemState> items)
        {
            ActiveLedgerState ledger = LoadLedger(settingsDir);
            if (ledger == null)
                return;

            bool changed = false;
            foreach (IGrouping<int, TransferItemState> group in
                (items ?? Enumerable.Empty<TransferItemState>()).GroupBy(item => item.AoId))
            {
                List<ActiveLedgerItem> entries = ledger.Items
                    .Where(entry =>
                        entry.AoId == group.Key &&
                        string.Equals(entry.TransactionId, transactionId, StringComparison.Ordinal))
                    .OrderBy(entry => entry.Id, StringComparer.Ordinal)
                    .Take(group.Count())
                    .ToList();

                foreach (ActiveLedgerItem entry in entries)
                {
                    entry.Character = workerCharacter;
                    entry.Location = "inventory";
                    entry.Bag = null;
                    entry.Slot = null;
                    changed = true;
                }
            }

            if (changed)
                SaveLedger(settingsDir, ledger);
        }

        public static void SyncStoredLocations(
            string settingsDir,
            CurrentStockState stock)
        {
            List<StockItemState> physical = (stock?.Items ?? new List<StockItemState>())
                .Where(item => item != null)
                .ToList();
            if (physical.Count == 0)
                return;

            UpsertIndexFromStock(settingsDir, stock);
            ActiveLedgerState ledger = LoadLedger(settingsDir);
            if (ledger == null)
                return;

            Dictionary<string, LegacyProvenance> provenance = null;
            bool changed = false;

            foreach (IGrouping<string, StockItemState> transactionGroup in physical.GroupBy(item =>
                (item.TransactionId ?? string.Empty) + "\n" + item.AoId))
            {
                List<StockItemState> physicalItems = transactionGroup
                    .OrderBy(item => item.Character, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.BagSource, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.BagOuterSlot)
                    .ThenBy(item => item.InnerSlot)
                    .ToList();
                StockItemState first = physicalItems[0];
                List<ActiveLedgerItem> entries = ledger.Items
                    .Where(entry =>
                        entry.AoId == first.AoId &&
                        string.Equals(entry.TransactionId, first.TransactionId, StringComparison.Ordinal))
                    .OrderBy(entry => entry.Id, StringComparer.Ordinal)
                    .ToList();

                while (entries.Count < physicalItems.Count)
                {
                    if (provenance == null)
                        provenance = LoadLegacyProvenance(settingsDir);
                    StockItemState missingPhysical = physicalItems[entries.Count];
                    LegacyProvenance source;
                    provenance.TryGetValue(missingPhysical.TransactionId ?? string.Empty, out source);
                    ActiveLedgerItem added = FromLegacyStock(missingPhysical, source);
                    ledger.Items.Add(added);
                    entries.Add(added);
                    changed = true;
                }

                for (int index = 0; index < physicalItems.Count; index++)
                {
                    StockItemState item = physicalItems[index];
                    ActiveLedgerItem entry = entries[index];
                    string location = string.IsNullOrWhiteSpace(item.BagSource)
                        ? "inventory"
                        : item.BagSource;
                    int? bag = string.IsNullOrWhiteSpace(item.BagSource)
                        ? (int?)null
                        : item.BagOuterSlot;
                    int? slot = string.IsNullOrWhiteSpace(item.BagSource)
                        ? (int?)null
                        : item.InnerSlot;

                    if (!string.Equals(entry.Character, item.Character, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(entry.Location, location, StringComparison.OrdinalIgnoreCase) ||
                        entry.Bag != bag || entry.Slot != slot)
                    {
                        entry.Character = item.Character;
                        entry.Location = location;
                        entry.Bag = bag;
                        entry.Slot = slot;
                        entry.Family = item.Role;
                        changed = true;
                    }
                }
            }

            if (changed)
                SaveLedger(settingsDir, ledger);
        }

        public static void ArchiveActiveItem(
            string settingsDir,
            string transactionId,
            int aoId,
            DateTime leftUtc,
            string reason,
            string recipient)
        {
            ActiveLedgerState ledger = LoadLedger(settingsDir);
            if (ledger == null)
                return;

            ActiveLedgerItem entry = FindActive(ledger, transactionId, aoId);
            if (entry == null)
                return;

            DateTime when = leftUtc == DateTime.MinValue ? DateTime.UtcNow : leftUtc;
            if (!HistoryContains(settingsDir, when, entry.Id, reason))
            {
                AppendHistory(
                    settingsDir,
                    new ActiveHistoryRecord
                    {
                        LeftUtc = when,
                        Reason = reason,
                        Recipient = recipient,
                        Item = Clone(entry)
                    });
            }

            ledger.Items.Remove(entry);
            SaveLedger(settingsDir, ledger);
        }

        public static bool ArchiveActiveItemById(
            string settingsDir,
            string itemId,
            DateTime leftUtc,
            string reason,
            string recipient)
        {
            ActiveLedgerState ledger = LoadLedger(settingsDir);
            ActiveLedgerItem entry = ledger?.Items?.FirstOrDefault(item =>
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (entry == null)
                return HistoryContains(settingsDir, leftUtc, itemId, reason);
            DateTime when = leftUtc == DateTime.MinValue ? DateTime.UtcNow : leftUtc;
            if (!HistoryContains(settingsDir, when, entry.Id, reason))
            {
                AppendHistory(settingsDir, new ActiveHistoryRecord
                {
                    LeftUtc = when,
                    Reason = reason,
                    Recipient = recipient,
                    Item = Clone(entry)
                });
            }
            ledger.Items.Remove(entry);
            SaveLedger(settingsDir, ledger);
            return true;
        }

        public static CurrentStockState BuildStockView(string settingsDir)
        {
            ActiveLedgerState ledger = LoadLedger(settingsDir) ?? NewLedger();
            SymbiantIndexState index = LoadIndex(settingsDir) ?? new SymbiantIndexState();
            Dictionary<int, SymbiantIndexItem> metadata = (index.Items ?? new List<SymbiantIndexItem>())
                .GroupBy(item => item.AoId)
                .ToDictionary(group => group.Key, group => group.First());

            var result = new CurrentStockState
            {
                UpdatedUtc = ledger.UpdatedUtc,
                Items = new List<StockItemState>()
            };

            foreach (ActiveLedgerItem entry in ledger.Items ?? new List<ActiveLedgerItem>())
            {
                SymbiantIndexItem meta;
                metadata.TryGetValue(entry.AoId, out meta);
                result.Items.Add(new StockItemState
                {
                    TransactionId = entry.TransactionId,
                    Role = entry.Family ?? meta?.Family,
                    PhysicalRole = entry.Family ?? meta?.Family,
                    RouteMatchesPhysicalRole = true,
                    Character = entry.Character,
                    BagSource = entry.Location,
                    BagOuterSlot = entry.Bag ?? 0,
                    InnerSlot = entry.Slot ?? 0,
                    AoId = entry.AoId,
                    HighId = meta?.HighId ?? entry.AoId,
                    Ql = meta?.Ql ?? 0,
                    Name = meta?.Name ?? string.Empty,
                    ObservedUtc = entry.ReceivedUtc
                });
            }
            return result;
        }

        private static ActiveLedgerState MigrateLegacyStock(
            string settingsDir,
            CurrentStockState stock)
        {
            var ledger = NewLedger();
            Dictionary<string, LegacyProvenance> provenance = LoadLegacyProvenance(settingsDir);
            foreach (StockItemState item in stock?.Items ?? new List<StockItemState>())
            {
                LegacyProvenance source;
                provenance.TryGetValue(item.TransactionId ?? string.Empty, out source);
                ledger.Items.Add(FromLegacyStock(item, source));
            }
            return ledger;
        }

        private static void MigrateLegacyHistory(string settingsDir, string centralCharacter)
        {
            string directory = GetLegacyEventDirectory(settingsDir);
            if (!Directory.Exists(directory))
                return;

            Dictionary<string, LegacyProvenance> provenance = LoadLegacyProvenance(settingsDir);
            int migrated = 0;

            foreach (string path in Directory.GetFiles(directory, "citybankers-*.jsonl")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        JObject record;
                        try
                        {
                            record = JObject.Parse(line);
                        }
                        catch
                        {
                            continue;
                        }

                        if (!string.Equals(
                            record["Event"]?.ToString(),
                            "donation_overcap_deleted",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string transactionId = record["TransactionId"]?.ToString();
                        DateTime leftUtc = ParseUtcToken(record["Utc"]);
                        JArray sourceItems = record["Items"] as JArray;
                        if (sourceItems == null)
                            continue;

                        var occurrenceByAoid = new Dictionary<int, int>();
                        foreach (JObject sourceItem in sourceItems.OfType<JObject>())
                        {
                            int aoId = IntTokenValue(sourceItem["AoId"]);
                            if (aoId == 0)
                                continue;

                            int occurrence;
                            occurrenceByAoid.TryGetValue(aoId, out occurrence);
                            occurrence++;
                            occurrenceByAoid[aoId] = occurrence;
                            string historyId =
                                "legacy-" + (transactionId ?? "unknown") + "-" +
                                aoId + "-" + leftUtc.Ticks +
                                (occurrence > 1 ? "-" + occurrence : string.Empty);
                            if (HistoryDepartureCount(
                                    settingsDir,
                                    leftUtc,
                                    transactionId,
                                    aoId,
                                    "deleted_overcap") >= occurrence)
                            {
                                continue;
                            }

                            var observed = new TransferItemState
                            {
                                AoId = aoId,
                                HighId = IntTokenValue(sourceItem["HighId"]),
                                Ql = IntTokenValue(sourceItem["Ql"]),
                                Name = sourceItem["Name"]?.ToString() ?? string.Empty
                            };
                            UpsertIndex(settingsDir, new[] { observed });

                            LegacyProvenance source;
                            provenance.TryGetValue(transactionId ?? string.Empty, out source);
                            string family;
                            SymbiantCatalog.TryGetDestinationRole(settingsDir, aoId, out family);

                            AppendHistory(
                                settingsDir,
                                new ActiveHistoryRecord
                                {
                                    LeftUtc = leftUtc,
                                    Reason = "deleted_overcap",
                                    Recipient = null,
                                    Item = new ActiveLedgerItem
                                    {
                                        Id = historyId,
                                        AoId = aoId,
                                        TransactionId = transactionId,
                                        From = source?.Donor ?? TrustedOperators.BootstrapAdmin,
                                        ReceivedUtc = source != null && source.Utc != DateTime.MinValue
                                            ? source.Utc
                                            : leftUtc,
                                        Family = family,
                                        Character = centralCharacter,
                                        Location = "inventory",
                                        Bag = null,
                                        Slot = null
                                    }
                                });
                            migrated++;
                        }
                    }
                }
                catch
                {
                }
            }

            if (migrated > 0)
            {
                RuntimeStateStore.AppendActivity(
                    settingsDir,
                    centralCharacter,
                    "central",
                    "ACTIVE HISTORY migrated legacy departures=" + migrated + ".");
            }
        }

        private static ActiveLedgerItem FromLegacyStock(
            StockItemState item,
            LegacyProvenance source)
        {
            return new ActiveLedgerItem
            {
                Id = "cb-" + Guid.NewGuid().ToString("N"),
                AoId = item.AoId,
                TransactionId = item.TransactionId,
                From = source?.Donor ??
                    ((item.TransactionId ?? string.Empty).StartsWith("don-", StringComparison.OrdinalIgnoreCase)
                        ? TrustedOperators.BootstrapAdmin
                        : null),
                ReceivedUtc = source != null && source.Utc != DateTime.MinValue
                    ? source.Utc
                    : item.ObservedUtc,
                Family = item.Role,
                Character = item.Character,
                Location = string.IsNullOrWhiteSpace(item.BagSource) ? "inventory" : item.BagSource,
                Bag = string.IsNullOrWhiteSpace(item.BagSource) ? (int?)null : item.BagOuterSlot,
                Slot = string.IsNullOrWhiteSpace(item.BagSource) ? (int?)null : item.InnerSlot
            };
        }

        private static ActiveLedgerItem FindActive(
            ActiveLedgerState ledger,
            string transactionId,
            int aoId)
        {
            return ledger?.Items?.FirstOrDefault(entry =>
                entry.AoId == aoId &&
                string.Equals(entry.TransactionId, transactionId, StringComparison.Ordinal));
        }

        private static ActiveLedgerState NewLedger()
        {
            return new ActiveLedgerState
            {
                UpdatedUtc = DateTime.UtcNow,
                Items = new List<ActiveLedgerItem>()
            };
        }

        private static void SaveLedger(string settingsDir, ActiveLedgerState ledger)
        {
            ledger.UpdatedUtc = DateTime.UtcNow;
            ledger.Items = (ledger.Items ?? new List<ActiveLedgerItem>())
                .OrderBy(item => item.AoId)
                .ThenBy(item => item.ReceivedUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            RuntimeStateStore.WriteJsonAtomic(GetActiveLedgerPath(settingsDir), ledger);
        }

        private static void EnsureIndexSeeded(string settingsDir)
        {
            SymbiantIndexState index = LoadIndex(settingsDir) ?? new SymbiantIndexState();
            Dictionary<int, SymbiantIndexItem> byAoid = (index.Items ?? new List<SymbiantIndexItem>())
                .GroupBy(item => item.AoId)
                .ToDictionary(group => group.Key, group => group.First());
            bool changed = false;

            foreach (SymbiantCatalog.AcceptanceRule route in SymbiantCatalog.GetRules(settingsDir))
            {
                SymbiantIndexItem entry;
                if (!byAoid.TryGetValue(route.AoId, out entry))
                {
                    entry = new SymbiantIndexItem
                    {
                        AoId = route.AoId,
                        Family = route.Role
                    };
                    index.Items.Add(entry);
                    byAoid[route.AoId] = entry;
                    changed = true;
                }
                else if (!string.Equals(entry.Family, route.Role, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Family = route.Role;
                    changed = true;
                }
            }

            if (changed || !File.Exists(GetIndexPath(settingsDir)))
                SaveIndex(settingsDir, index);
        }

        private static void UpsertIndexFromStock(string settingsDir, CurrentStockState stock)
        {
            UpsertIndex(
                settingsDir,
                (stock?.Items ?? new List<StockItemState>()).Select(item => new TransferItemState
                {
                    AoId = item.AoId,
                    HighId = item.HighId,
                    Ql = item.Ql,
                    Name = item.Name
                }));
        }

        private static void UpsertIndex(
            string settingsDir,
            IEnumerable<TransferItemState> observed)
        {
            List<TransferItemState> items = (observed ?? Enumerable.Empty<TransferItemState>())
                .Where(item => item != null && item.AoId != 0)
                .ToList();
            if (items.Count == 0)
                return;

            SymbiantIndexState index = LoadIndex(settingsDir) ?? new SymbiantIndexState();
            Dictionary<int, SymbiantIndexItem> byAoid = (index.Items ?? new List<SymbiantIndexItem>())
                .GroupBy(item => item.AoId)
                .ToDictionary(group => group.Key, group => group.First());
            bool changed = false;

            foreach (TransferItemState item in items)
            {
                SymbiantIndexItem entry;
                if (!byAoid.TryGetValue(item.AoId, out entry))
                {
                    entry = new SymbiantIndexItem { AoId = item.AoId };
                    index.Items.Add(entry);
                    byAoid[item.AoId] = entry;
                    changed = true;
                }

                string family;
                SymbiantCatalog.TryGetDestinationRole(settingsDir, item.AoId, out family);
                string slot = DetectSlot(item.Name);
                int? highId = item.HighId != 0 ? (int?)item.HighId : null;
                int? ql = item.Ql != 0 ? (int?)item.Ql : null;

                if (!string.Equals(entry.Family, family, StringComparison.OrdinalIgnoreCase) ||
                    entry.HighId != highId ||
                    entry.Ql != ql ||
                    !string.Equals(entry.Name, item.Name, StringComparison.Ordinal) ||
                    !string.Equals(entry.Slot, slot, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Family = family;
                    entry.HighId = highId;
                    entry.Ql = ql;
                    entry.Name = item.Name;
                    entry.Slot = slot;
                    changed = true;
                }
            }

            if (changed)
                SaveIndex(settingsDir, index);
        }

        private static void SaveIndex(string settingsDir, SymbiantIndexState index)
        {
            index.UpdatedUtc = DateTime.UtcNow;
            index.Items = (index.Items ?? new List<SymbiantIndexItem>())
                .OrderBy(item => item.AoId)
                .ToList();
            RuntimeStateStore.WriteJsonAtomic(GetIndexPath(settingsDir), index);
        }

        private static Dictionary<string, LegacyProvenance> LoadLegacyProvenance(string settingsDir)
        {
            var result = new Dictionary<string, LegacyProvenance>(StringComparer.Ordinal);
            string directory = GetLegacyEventDirectory(settingsDir);
            if (!Directory.Exists(directory))
                return result;

            foreach (string path in Directory.GetFiles(directory, "citybankers-*.jsonl")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        JObject record;
                        try
                        {
                            record = JObject.Parse(line);
                        }
                        catch
                        {
                            continue;
                        }

                        if (!string.Equals(
                            record["Event"]?.ToString(),
                            "player_trade_completed",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string transactionId = record["TransactionId"]?.ToString();
                        if (string.IsNullOrWhiteSpace(transactionId))
                            continue;

                        DateTime utc;
                        if (!DateTime.TryParse(record["Utc"]?.ToString(), out utc))
                            utc = DateTime.MinValue;
                        else
                            utc = utc.ToUniversalTime();

                        string donor = record["Source"]?.ToString();
                        if (string.IsNullOrWhiteSpace(donor))
                            donor = TrustedOperators.BootstrapAdmin;

                        result[transactionId] = new LegacyProvenance
                        {
                            Donor = donor,
                            Utc = utc
                        };
                    }
                }
                catch
                {
                }
            }
            return result;
        }

        private static bool HistoryContains(
            string settingsDir,
            DateTime leftUtc,
            string itemId,
            string reason)
        {
            DateTime utc = leftUtc == DateTime.MinValue ? DateTime.UtcNow : leftUtc.ToUniversalTime();
            string path = Path.Combine(
                GetHistoryDirectory(settingsDir),
                "history-" + utc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
            if (!File.Exists(path))
                return false;

            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    JObject record;
                    try
                    {
                        record = JObject.Parse(line);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.Equals(record["Reason"]?.ToString(), reason, StringComparison.OrdinalIgnoreCase))
                        continue;
                    JObject item = record["Item"] as JObject;
                    if (item != null && string.Equals(
                        item["Id"]?.ToString(),
                        itemId,
                        StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static int HistoryDepartureCount(
            string settingsDir,
            DateTime leftUtc,
            string transactionId,
            int aoId,
            string reason)
        {
            DateTime utc = leftUtc == DateTime.MinValue ? DateTime.UtcNow : leftUtc.ToUniversalTime();
            string path = Path.Combine(
                GetHistoryDirectory(settingsDir),
                "history-" + utc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
            if (!File.Exists(path))
                return 0;

            int count = 0;
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    JObject record;
                    try
                    {
                        record = JObject.Parse(line);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.Equals(record["Reason"]?.ToString(), reason, StringComparison.OrdinalIgnoreCase))
                        continue;
                    DateTime recordedLeftUtc = ParseUtcToken(record["LeftUtc"]);
                    if (recordedLeftUtc.Ticks != utc.Ticks)
                        continue;
                    JObject item = record["Item"] as JObject;
                    if (item != null &&
                        IntTokenValue(item["AoId"]) == aoId &&
                        string.Equals(item["TransactionId"]?.ToString(), transactionId, StringComparison.Ordinal))
                    {
                        count++;
                    }
                }
            }
            catch
            {
            }
            return count;
        }

        private static void AppendHistory(string settingsDir, ActiveHistoryRecord record)
        {
            lock (HistoryLock)
            {
                DateTime utc = record.LeftUtc == DateTime.MinValue
                    ? DateTime.UtcNow
                    : record.LeftUtc.ToUniversalTime();
                string path = Path.Combine(
                    GetHistoryDirectory(settingsDir),
                    "history-" + utc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
                string line = JsonConvert.SerializeObject(record, Formatting.None) + Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }

        private static ActiveLedgerItem Clone(ActiveLedgerItem item)
        {
            return new ActiveLedgerItem
            {
                Id = item.Id,
                AoId = item.AoId,
                TransactionId = item.TransactionId,
                From = item.From,
                ReceivedUtc = item.ReceivedUtc,
                Family = item.Family,
                Character = item.Character,
                Location = item.Location,
                Bag = item.Bag,
                Slot = item.Slot
            };
        }

        private static DateTime ParseUtcToken(JToken token)
        {
            DateTime value;
            return token != null && DateTime.TryParse(token.ToString(), out value)
                ? value.ToUniversalTime()
                : DateTime.UtcNow;
        }

        private static int IntTokenValue(JToken token)
        {
            int value;
            return token != null && int.TryParse(token.ToString(), out value)
                ? value
                : 0;
        }

        private static string DetectSlot(string name)
        {
            string value = (name ?? string.Empty).ToLowerInvariant();
            if (value.Contains("brain symbiant")) return "brain";
            if (value.Contains("eye symbiant")) return "eye";
            if (value.Contains("ear symbiant")) return "ear";
            if (value.Contains("chest symbiant")) return "chest";
            if (value.Contains("waist symbiant")) return "waist";
            if (value.Contains("left arm symbiant")) return "leftarm";
            if (value.Contains("right arm symbiant")) return "rightarm";
            if (value.Contains("left wrist symbiant")) return "leftwrist";
            if (value.Contains("right wrist symbiant")) return "rightwrist";
            if (value.Contains("left hand symbiant")) return "lefthand";
            if (value.Contains("right hand symbiant")) return "righthand";
            if (value.Contains("thigh symbiant")) return "thigh";
            if (value.Contains("feet symbiant")) return "feet";
            return null;
        }
    }
}
