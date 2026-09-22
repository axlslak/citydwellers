using File = CityDwellers.Shared.DiskFiles;
using Directory = System.IO.Directory;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

using CityDwellers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
    public static class ServicePolicy
    {
        public const string TrustedAdminName = "Kavem";
        public const int MaxTradeItems = 10;
        public const int MaxInternalTradeItems = MaxTradeItems;
        public const int MaxStoredCopiesPerTemplate = 10;
        public const int DonationInactivitySeconds = 30;
        public const int EmptyTradeTimeoutSeconds = 30;
        public const int PublicDonationTimeoutSeconds = 120;
        public const int TradeTimeoutSeconds = 20;
        public const int InternalAddItemRetryMilliseconds = 1200;
        public const int InternalAddItemMaxAttempts = 4;
        public const int ItemMoveTimeoutMs = 5000;
        public const int BagMoveTimeoutMs = 5000;
        public const int BagOpenTimeoutMs = 3000;
        public const int DeleteVerifyTimeoutMs = 10000;

        public static bool IsBagAuditMode()
        {
            return Environment.GetCommandLineArgs().Any(arg =>
                string.Equals(arg, "bagaudit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "bankers-bagaudit", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Personal/service equipment must never become stock or a worker-to-Central route.
    public static class BankerPersonalItems
    {
        public const int ColonistBackpackId = 296977;
        public const int PortableBankTerminalId = 288762;
        public static bool IsPersonal(int lowId, int highId) =>
            lowId == ColonistBackpackId || highId == ColonistBackpackId ||
            lowId == PortableBankTerminalId || highId == PortableBankTerminalId;
    }

    public static partial class CruPolicy
    {
        public const int AoId = 257110;
        public const string Name = "Upgraded Controller Recompiler Unit";
        public static bool IsCru(int id) => id == AoId;
    }

    // Raised only by a read that moved nothing: the file was held by a
    // concurrent writer for longer than the reader was willing to wait. It
    // carries no custody implication, so a caller may retry it instead of
    // treating it as evidence that an item's location is now unknown.
    [Serializable]
    public sealed class StateContentionException : IOException
    {
        public StateContentionException(string path, Exception inner)
            : base("Another process still holds '" + path + "'.", inner)
        {
        }

        // Plugins run in their own AppDomains, so this must survive marshalling.
        private StateContentionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    public static class RuntimeStateStore
    {
        private const string StorageFileName = "storage-state.json";
        private const string QueueFileName = "dispatch-queue.json";
        private const string StateMutexName = "CityBankers.RuntimeState.v1";
        private const string LedgerMutexName = "CityBankers.Ledger.v1";
        private const string LogMutexName = "CityBankers.ActivityLog.v1";
        private const string IdentityNoneText = "(None:0000)";

        public static bool TryReadUtc(JToken token, out DateTime value)
        {
            value = DateTime.MinValue;
            if (token == null || token.Type == JTokenType.Null)
                return false;

            try
            {
                // JObject.Parse materializes ISO timestamps as Date-valued tokens.
                // Reading that value directly preserves its Kind. Calling ToString()
                // first can localize it and discard the trailing Z, causing a later
                // ToUniversalTime() to apply the host offset a second time.
                DateTime parsed = token.Type == JTokenType.Date
                    ? token.Value<DateTime>()
                    : token.ToObject<DateTime>();
                value = UtcTimestamp.Normalize(parsed);
                return value != DateTime.MinValue;
            }
            catch
            {
                return false;
            }
        }

        public static string GetDataDirectory(string settingsDir)
        {
            return CityDwellers.Shared.SettingsPaths.GetDataDirectory(settingsDir);
        }

        public static string GetLedgerDirectory(string settingsDir)
        {
            return EnsureDirectory(Path.Combine(GetDataDirectory(settingsDir), "ledger"));
        }

        public static string GetLogDirectory(string settingsDir)
        {
            return EnsureDirectory(Path.Combine(GetDataDirectory(settingsDir), "logs"));
        }

        public static string GetStorageStatePath(string settingsDir)
        {
            return Path.Combine(GetDataDirectory(settingsDir), StorageFileName);
        }

        public static string GetDispatchQueuePath(string settingsDir)
        {
            return Path.Combine(GetDataDirectory(settingsDir), QueueFileName);
        }

        public static string GetDispatchCommandPath(string settingsDir, string character)
        {
            return Path.Combine(
                GetDataDirectory(settingsDir),
                "citybankers-dispatch-command-" + SafeFileToken(character) + ".json");
        }

        public static string GetStorageResultPath(string settingsDir, string character)
        {
            return Path.Combine(
                GetDataDirectory(settingsDir),
                "citybankers-storage-result-" + SafeFileToken(character) + ".json");
        }

        public static StorageState LoadStorageState(string settingsDir)
        {
            return ManagerMemory.Current.ReadStorage(ManagerAccounting.TransactionId);
        }

        public static void SaveCurrentStock(CurrentStockState state)
        {
            CityDwellers.Shared.BankerState.SaveStock(state);
        }

        public static CurrentStockState LoadCurrentStock(string settingsDir)
        {
            CurrentStockState state = CityDwellers.Shared.BankerState.ReadStock<CurrentStockState>();
            return state ?? new CurrentStockState { UpdatedUtc = DateTime.UtcNow };
        }

        public static DispatchQueueState LoadDispatchQueue(string settingsDir)
        {
            DispatchQueueState state = ManagerMemory.Current.ReadDispatch(ManagerAccounting.TransactionId);
            if (state == null)
                state = new DispatchQueueState();
            if (state.Batches == null)
                state.Batches = new List<DispatchBatchState>();
            return state;
        }

        public static void SaveStorageBaseline(
            string settingsDir,
            StorageState storageState,
            string transactionId)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction("CityBankers.RuntimeState.v1", () =>
            {
                if (storageState == null)
                    throw new ArgumentNullException("storageState");

                WithMutex(StateMutexName, delegate
                {
                    storageState.UpdatedUtc = DateTime.UtcNow;
                    ManagerMemory.Current.ChangeStorage(ManagerAccounting.TransactionId, storageState);
                    CurrentStockState stock = BuildCurrentStock(settingsDir, storageState);
                    SaveCurrentStock(stock);
                });

                AppendLedger(
                    settingsDir,
                    new LedgerRecord
                    {
                        Utc = DateTime.UtcNow,
                        Event = "storage_baseline_saved",
                        TransactionId = transactionId,
                        Actor = "bagaudit",
                        Message =
                            "Authoritative physical bag/content baseline saved from completed audit run " +
                            storageState.BaselineRunId + "."
                    });
            });
        }

        public static void MergeAuditedStorageWorker(
            string settingsDir,
            StorageWorkerState replacement,
            string baselineRunId,
            string transactionId)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction("CityBankers.RuntimeState.v1", () =>
            {
                if (replacement == null)
                    throw new ArgumentNullException("replacement");
                if (string.IsNullOrWhiteSpace(replacement.Role) ||
                    string.IsNullOrWhiteSpace(replacement.Character))
                {
                    throw new InvalidOperationException(
                        "Audited storage worker is missing role or character identity.");
                }

                WithMutex(StateMutexName, delegate
                {
                    StorageState state = ManagerMemory.Current.ReadStorage(ManagerAccounting.TransactionId) ??
                        new StorageState();
                    if (state.Workers == null)
                        state.Workers = new List<StorageWorkerState>();

                    StorageWorkerState previous = state.Workers.FirstOrDefault(worker =>
                        worker != null &&
                        (string.Equals(
                             worker.Role,
                             replacement.Role,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             worker.Character,
                             replacement.Character,
                             StringComparison.OrdinalIgnoreCase)));
                    PreserveAuditedItemMetadata(previous, replacement);

                    state.Workers.RemoveAll(worker => worker != null &&
                        (string.Equals(
                             worker.Role,
                             replacement.Role,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             worker.Character,
                             replacement.Character,
                             StringComparison.OrdinalIgnoreCase)));
                    state.Workers.Add(replacement);
                    state.BaselineRunId = baselineRunId;
                    state.UpdatedUtc = DateTime.UtcNow;

                    ManagerMemory.Current.ChangeStorage(ManagerAccounting.TransactionId, state);
                    SaveCurrentStock(BuildCurrentStock(settingsDir, state));
                });

                AppendLedger(
                    settingsDir,
                    new LedgerRecord
                    {
                        Utc = DateTime.UtcNow,
                        Event = "storage_worker_audit_merged",
                        TransactionId = transactionId,
                        Actor = "startup-enrollment",
                        Role = replacement.Role,
                        Character = replacement.Character,
                        Message =
                            "Audited worker storage map merged from completed startup self-check " +
                            baselineRunId + "."
                    });
            });
        }

        private static void PreserveAuditedItemMetadata(
            StorageWorkerState previous,
            StorageWorkerState replacement)
        {
            if (previous == null)
                return;

            var known = (previous.Bags ?? new List<StorageBagState>())
                .Where(bag => bag != null)
                .SelectMany(bag => bag.Items ?? new List<StoredItemState>())
                .Where(item => item != null && IsUsableItemIdentity(item.UniqueIdentity))
                .GroupBy(item => item.UniqueIdentity, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (StoredItemState item in (replacement.Bags ?? new List<StorageBagState>())
                .Where(bag => bag != null)
                .SelectMany(bag => bag.Items ?? new List<StoredItemState>()))
            {
                StoredItemState existing;
                if (item != null && IsUsableItemIdentity(item.UniqueIdentity) &&
                    known.TryGetValue(item.UniqueIdentity, out existing))
                {
                    item.TransactionId = existing.TransactionId;
                    item.ObservedUtc = existing.ObservedUtc;
                }
            }
        }

        public static void SaveDispatchQueue(
            string settingsDir,
            DispatchQueueState state)
        {
            if (state == null)
                throw new ArgumentNullException("state");

            WithMutex(StateMutexName, delegate
            {
                state.UpdatedUtc = DateTime.UtcNow;
                ManagerMemory.Current.ChangeDispatch(ManagerAccounting.TransactionId, state);
            });
        }

        public static bool RecordPlacement(
            string settingsDir,
            string transactionId,
            string role,
            string character,
            string bagSource,
            int bagOuterSlot,
            string bagUniqueIdentity,
            int bagHandle,
            TransferItemState item,
            string observedItemIdentity,
            int innerSlot,
            out string error)
        {
            error = null;
            bool success = false;

            try
            {
                WithMutex(StateMutexName, delegate
                {
                    StorageState state = ManagerMemory.Current.ReadStorage(ManagerAccounting.TransactionId);
                    if (state == null)
                        throw new InvalidOperationException(
                            "Persistent storage baseline is missing. Run CityDwellers.exe bankers-bagaudit first.");

                    string routedRole = null;
                    bool managed = item != null &&
                        SymbiantCatalog.TryGetDestinationRole(settingsDir, item.AoId, out routedRole);
                    if (!managed && item != null && item.HighId != item.AoId)
                        managed = SymbiantCatalog.TryGetDestinationRole(settingsDir, item.HighId, out routedRole);
                    if (!managed)
                        throw new InvalidOperationException(
                            "Refusing to persist an unmanaged or unresolved storage item.");
                    if (!string.Equals(routedRole, role, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Refusing to persist item routed to '" + routedRole +
                            "' on worker role '" + role + "'.");

                    StorageWorkerState worker = (state.Workers ?? new List<StorageWorkerState>())
                        .FirstOrDefault(w =>
                            string.Equals(w.Role, role, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(w.Character, character, StringComparison.OrdinalIgnoreCase));
                    if (worker == null)
                        throw new InvalidOperationException(
                            "No storage baseline exists for role '" + role + "' character '" +
                            character + "'.");

                    StorageBagState bag = (worker.Bags ?? new List<StorageBagState>())
                        .FirstOrDefault(b =>
                            string.Equals(b.Source, bagSource, StringComparison.OrdinalIgnoreCase) &&
                            b.OuterSlotInstance == bagOuterSlot);
                    if (bag == null)
                        throw new InvalidOperationException(
                            "Storage bag " + bagSource + ":" + bagOuterSlot +
                            " is not present in the persisted baseline.");

                    if (bag.Items == null)
                        bag.Items = new List<StoredItemState>();

                    if (bag.Items.Count >= bag.Capacity)
                        throw new InvalidOperationException(
                            "Persisted bag is already full: " + bagSource + ":" + bagOuterSlot + ".");

                    if (IsUsableItemIdentity(observedItemIdentity) &&
                        bag.Items.Any(i => string.Equals(
                            i.UniqueIdentity,
                            observedItemIdentity,
                            StringComparison.Ordinal)))
                    {
                        success = true;
                        return;
                    }

                    bag.LastUniqueIdentity = bagUniqueIdentity;
                    bag.LastHandle = bagHandle;
                    bag.Items.Add(new StoredItemState
                    {
                        UniqueIdentity = IsUsableItemIdentity(observedItemIdentity)
                            ? observedItemIdentity
                            : null,
                        AoId = item != null ? item.AoId : 0,
                        HighId = item != null ? item.HighId : 0,
                        Ql = item != null ? item.Ql : 0,
                        Name = item != null ? item.Name : string.Empty,
                        InnerSlot = innerSlot,
                        ObservedUtc = DateTime.UtcNow,
                        TransactionId = transactionId
                    });

                    worker.ObservedUtc = DateTime.UtcNow;
                    state.UpdatedUtc = DateTime.UtcNow;
                    ManagerMemory.Current.ChangeStorage(ManagerAccounting.TransactionId, state);
                    SaveCurrentStock(BuildCurrentStock(settingsDir, state));
                    success = true;
                });
            }
            catch (Exception ex)
            {
                error = ex.Message;
                success = false;
            }

            return success;
        }

        public static bool RemoveStoredItem(
            string settingsDir,
            string character,
            string bagSource,
            int bagOuterSlot,
            int innerSlot,
            string uniqueIdentity,
            int aoId,
            out string error)
        {
            error = null;
            try
            {
                WithMutex(StateMutexName, delegate
                {
                    StorageState state = ManagerMemory.Current.ReadStorage(ManagerAccounting.TransactionId);
                    StorageWorkerState worker = (state?.Workers ?? new List<StorageWorkerState>())
                        .FirstOrDefault(value => string.Equals(
                            value.Character, character, StringComparison.OrdinalIgnoreCase));
                    StorageBagState bag = (worker?.Bags ?? new List<StorageBagState>())
                        .FirstOrDefault(value =>
                            string.Equals(value.Source, bagSource, StringComparison.OrdinalIgnoreCase) &&
                            value.OuterSlotInstance == bagOuterSlot);
                    StoredItemState item = (bag?.Items ?? new List<StoredItemState>())
                        .FirstOrDefault(value => value != null &&
                            value.InnerSlot == innerSlot && value.AoId == aoId &&
                            (!IsUsableItemIdentity(uniqueIdentity) ||
                             !IsUsableItemIdentity(value.UniqueIdentity) ||
                             string.Equals(value.UniqueIdentity, uniqueIdentity, StringComparison.Ordinal)));
                    if (item == null)
                        throw new InvalidOperationException(
                            "The exact persisted storage item could not be found.");
                    bag.Items.Remove(item);
                    worker.ObservedUtc = DateTime.UtcNow;
                    state.UpdatedUtc = DateTime.UtcNow;
                    ManagerMemory.Current.ChangeStorage(ManagerAccounting.TransactionId, state);
                    SaveCurrentStock(BuildCurrentStock(settingsDir, state));
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static StorageBagState FindNextFreeBag(
            StorageState state,
            string role,
            string character)
        {
            if (state == null || state.Workers == null)
                return null;

            StorageWorkerState worker = state.Workers.FirstOrDefault(w =>
                string.Equals(w.Role, role, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(w.Character, character, StringComparison.OrdinalIgnoreCase));
            if (worker == null || worker.Bags == null)
                return null;

            return worker.Bags
                .Where(b => b != null &&
                    (b.Items == null ? 0 : b.Items.Count) < b.Capacity)
                .OrderBy(b =>
                    string.Equals(b.Source, "bank", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenBy(b => b.OuterSlotInstance)
                .FirstOrDefault();
        }

        public static CurrentStockState BuildCurrentStock(
            string settingsDir,
            StorageState storageState)
        {
            var result = new CurrentStockState
            {
                BaselineRunId = storageState != null
                    ? storageState.BaselineRunId
                    : null,
                UpdatedUtc = DateTime.UtcNow,
                Items = new List<StockItemState>()
            };

            if (storageState == null || storageState.Workers == null)
                return result;

            // SaveStorageBaseline/MergeAuditedStorageWorker hold the writer for
            // the complete atomic update. Resolve policy once, not once per item:
            // each single-item lookup otherwise repeats SQL metadata queries for
            // every worker, including unchanged workers retained after a census.
            int[] templateIds = storageState.Workers
                .Where(worker => worker != null && worker.Bags != null &&
                    !string.Equals(worker.Role, "central", StringComparison.OrdinalIgnoreCase))
                .SelectMany(worker => worker.Bags)
                .Where(bag => bag != null && bag.Items != null)
                .SelectMany(bag => bag.Items)
                .Where(item => item != null && !CruPolicy.IsCru(item.AoId))
                .SelectMany(item => new[] { item.AoId, item.HighId }).Distinct().ToArray();
            var rules = templateIds.Length == 0
                ? new Dictionary<int, SymbiantCatalog.AcceptanceRule>()
                : SymbiantCatalog.GetRulesFor(settingsDir, templateIds);

            foreach (StorageWorkerState worker in storageState.Workers)
            {
                if (worker == null || worker.Bags == null)
                    continue;
                // Central keeps a physical bag map for extraction/review, but
                // these contents are not ordinary worker stock until routed.
                if (string.Equals(worker.Role, "central", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (StorageBagState bag in worker.Bags)
                {
                    if (bag == null || bag.Items == null)
                        continue;

                    foreach (StoredItemState item in bag.Items)
                    {
                        if (item == null || CruPolicy.IsCru(item.AoId))
                            continue;

                        SymbiantCatalog.AcceptanceRule route;
                        bool managed = rules.TryGetValue(item.AoId, out route);
                        if (!managed && item.HighId != item.AoId)
                            managed = rules.TryGetValue(item.HighId, out route);
                        string routedRole = managed ? route.Role : null;

                        result.Items.Add(new StockItemState
                        {
                            TransactionId = item.TransactionId,
                            Role = managed ? routedRole : worker.Role,
                            PhysicalRole = worker.Role,
                            RouteMatchesPhysicalRole = managed && string.Equals(
                                routedRole,
                                worker.Role,
                                StringComparison.OrdinalIgnoreCase),
                            Character = worker.Character,
                            BagSource = bag.Source,
                            BagOuterSlot = bag.OuterSlotInstance,
                            InnerSlot = item.InnerSlot,
                            UniqueIdentity = IsUsableItemIdentity(item.UniqueIdentity)
                                ? item.UniqueIdentity
                                : null,
                            AoId = item.AoId,
                            HighId = item.HighId,
                            Ql = item.Ql,
                            Name = item.Name,
                            ObservedUtc = item.ObservedUtc
                        });
                    }
                }
            }

            return result;
        }

        public static void WriteDispatchCommand(
            string settingsDir,
            DispatchCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.DestinationCharacter))
                throw new InvalidOperationException("Dispatch command destination is missing.");

            ManagerAccounting.Transaction("Dispatch command", () => ManagerMemory.Current.ChangeDispatchCommand(
                ManagerAccounting.TransactionId, command.DestinationCharacter, command));
        }

        public static DispatchCommand ReadDispatchCommand(
            string settingsDir,
            string character)
        {
            return ManagerMemory.Current.ReadDispatchCommand(ManagerAccounting.TransactionId, character);
        }

        public static void WriteStorageResult(
            string settingsDir,
            StorageBatchResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.Character))
                throw new InvalidOperationException("Storage result character is missing.");

            ManagerAccounting.Transaction("Storage result", () => ManagerMemory.Current.ChangeStorageResult(
                ManagerAccounting.TransactionId, result.Character, result));
        }

        public static StorageBatchResult ReadStorageResult(
            string settingsDir,
            string character)
        {
            return ManagerMemory.Current.ReadStorageResult(ManagerAccounting.TransactionId, character);
        }

        public static void DeleteDispatchCommand(string settingsDir, string character) =>
            ManagerAccounting.Transaction("Clear dispatch command", () => ManagerMemory.Current.ChangeDispatchCommand(ManagerAccounting.TransactionId, character, null));
        public static void DeleteStorageResult(string settingsDir, string character) =>
            ManagerAccounting.Transaction("Clear storage result", () => ManagerMemory.Current.ChangeStorageResult(ManagerAccounting.TransactionId, character, null));

        public static void DeleteIfExists(string path)
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

        public static void AppendLedger(string settingsDir, LedgerRecord record)
        {
            if (record == null)
                return;

            if (record.Utc == DateTime.MinValue)
                record.Utc = DateTime.UtcNow;

            CityDwellers.Shared.IncidentJournal.Record(GetDataDirectory(settingsDir),
                record.TransactionId ?? record.BatchId, record.Actor ?? record.Character,
                record.Event, record, CityDwellers.Shared.IncidentJournal.IsProblem(record.Event),
                new[] { record.BatchId });

            ManagerAccounting.Transaction("Transaction history", () =>
                ManagerMemory.Current.RecordTransaction(ManagerAccounting.TransactionId, record));
        }

        public static void AppendActivity(
            string settingsDir,
            string character,
            string role,
            string message)
        {
            WithMutex(LogMutexName, delegate
            {
                DateTime now = DateTime.Now;
                string directory = GetLogDirectory(settingsDir);
                string path = Path.Combine(
                    directory,
                    "citybankers-" +
                    now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    ".log");
                string line =
                    "[" + now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture) +
                    "] [" + (character ?? "unknown") + "] [" + (role ?? "unknown") + "] " +
                    (message ?? string.Empty) + Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
            });
        }

        public static T ReadJson<T>(string path) where T : class
        {
            return ReadJsonStrict<T>(path);
        }

        // Native files are reserved for configuration and explicit diagnostics.
        // Business state is read through the typed Manager methods above.
        public static string ReadTextShared(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", "path");
            return File.ReadAllTextOrNull(path);
        }

        public static string ReadTextStrict(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", "path");
            return File.ReadAllTextOrNull(path);
        }

        public static T ReadJsonStrict<T>(string path) where T : class
        {
            string text = ReadTextStrict(path);
            if (text == null) return null;
            var value = JsonConvert.DeserializeObject<T>(text);
            if (value == null) throw new InvalidDataException("Unreadable record: " + Path.GetFileName(path));
            return value;
        }

        public static void WriteJsonAtomic(string path, object value)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", "path");

            WithMutex(GetFileMutexName(path), delegate
            {
                WriteJsonAtomicNoLock(path, value);
                CityDwellers.Shared.IncidentJournal.ObserveWrite(path, value);
            });
        }

        private static T ReadJsonNoLock<T>(string path) where T : class
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
        }

        private static void WriteJsonAtomicNoLock(string path, object value)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented),
                new UTF8Encoding(false));
        }

        private static bool IsUsableItemIdentity(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, IdentityNoneText, StringComparison.Ordinal);
        }

        private static string GetFileMutexName(string path)
        {
            return StateMutexName + ".File." +
                SafeFileToken(Path.GetFullPath(path ?? string.Empty));
        }

        private static string EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WithMutex(string name, Action action)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction(name, action);
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        }
    }
}
