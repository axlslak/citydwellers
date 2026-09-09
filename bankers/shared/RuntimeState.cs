using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Newtonsoft.Json;

namespace CityBankers.Shared
{
    public static class ServicePolicy
    {
        public const string TrustedAdminName = "Kavem";
        public const int MaxTradeItems = 10;
        public const int MaxStoredCopiesPerTemplate = 10;
        public const int DonationInactivitySeconds = 30;
        public const int EmptyTradeTimeoutSeconds = 30;
        public const int TradeTimeoutSeconds = 20;
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

    public class StorageState
    {
        public string Format = "citybankers-storage-state-v1";
        public string BaselineRunId;
        public DateTime UpdatedUtc;
        public List<StorageWorkerState> Workers = new List<StorageWorkerState>();
    }

    public class StorageWorkerState
    {
        public string Role;
        public string Character;
        public DateTime ObservedUtc;
        public List<StorageBagState> Bags = new List<StorageBagState>();
    }

    public class StorageBagState
    {
        public string Source;
        public string OuterSlotType;
        public int OuterSlotInstance;
        public string LastUniqueIdentity;
        public int LastHandle;
        public int Capacity = 21;
        public List<StoredItemState> Items = new List<StoredItemState>();
    }

    public class StoredItemState
    {
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        public int InnerSlot;
        public DateTime ObservedUtc;
        public string TransactionId;
    }

    public class CurrentStockState
    {
        public string Format = "citybankers-current-stock-v1";
        public string BaselineRunId;
        public DateTime UpdatedUtc;
        public List<StockItemState> Items = new List<StockItemState>();
    }

    public class StockItemState
    {
        public string TransactionId;
        public string Role;
        public string PhysicalRole;
        public bool RouteMatchesPhysicalRole;
        public string Character;
        public string BagSource;
        public int BagOuterSlot;
        public int InnerSlot;
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        public DateTime ObservedUtc;
    }

    public class DispatchQueueState
    {
        public string Format = "citybankers-dispatch-queue-v1";
        public DateTime UpdatedUtc;
        public List<DispatchBatchState> Batches = new List<DispatchBatchState>();
    }

    public class DispatchBatchState
    {
        public string BatchId;
        public string TransactionId;
        public string Role;
        public string Character;
        public string Status;
        public DateTime CreatedUtc;
        public DateTime UpdatedUtc;
        public int AttemptCount;
        public string LastError;
        public List<TransferItemState> Items = new List<TransferItemState>();
    }

    public class TransferItemState
    {
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
    }

    public class DispatchCommand
    {
        public string Format = "citybankers-dispatch-command-v1";
        public string BatchId;
        public string TransactionId;
        public string Role;
        public string SourceCharacter;
        public string DestinationCharacter;
        public DateTime CreatedUtc;
        public List<TransferItemState> Items = new List<TransferItemState>();
    }

    public class StorageBatchResult
    {
        public string Format = "citybankers-storage-result-v1";
        public string BatchId;
        public string TransactionId;
        public string Role;
        public string Character;
        public DateTime CompletedUtc;
        public bool Success;
        public int ExpectedCount;
        public int StoredCount;
        public string Error;
    }

    public class LedgerRecord
    {
        public string Format = "citybankers-ledger-v1";
        public DateTime Utc;
        public string Event;
        public string TransactionId;
        public string BatchId;
        public string Actor;
        public string Role;
        public string Character;
        public string Source;
        public string Destination;
        public string Message;
        public List<LedgerItem> Items;
    }

    public class LedgerItem
    {
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        public string Role;
        public string BagSource;
        public int? BagOuterSlot;
        public int? InnerSlot;
    }

    public static class RuntimeStateStore
    {
        private const string StorageFileName = "storage-state.json";
        private const string StockFileName = "current-stock.json";
        private const string QueueFileName = "dispatch-queue.json";
        private const string StateMutexName = "CityBankers.RuntimeState.v1";
        private const string LedgerMutexName = "CityBankers.Ledger.v1";
        private const string LogMutexName = "CityBankers.ActivityLog.v1";
        private const string IdentityNoneText = "(None:0000)";
        private const int AtomicFileRetryCount = 50;
        private const int AtomicFileRetryDelayMilliseconds = 100;

        public static string GetDataDirectory(string settingsDir)
        {
            return EnsureDirectory(Path.Combine(settingsDir, "data"));
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

        public static string GetCurrentStockPath(string settingsDir)
        {
            return Path.Combine(GetDataDirectory(settingsDir), StockFileName);
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
            return ReadJson<StorageState>(GetStorageStatePath(settingsDir));
        }

        public static CurrentStockState LoadCurrentStock(string settingsDir)
        {
            CurrentStockState state = ReadJson<CurrentStockState>(
                GetCurrentStockPath(settingsDir));
            return state ?? new CurrentStockState { UpdatedUtc = DateTime.UtcNow };
        }

        public static DispatchQueueState LoadDispatchQueue(string settingsDir)
        {
            DispatchQueueState state = ReadJson<DispatchQueueState>(
                GetDispatchQueuePath(settingsDir));
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
            if (storageState == null)
                throw new ArgumentNullException("storageState");

            WithMutex(StateMutexName, delegate
            {
                storageState.UpdatedUtc = DateTime.UtcNow;
                WriteJsonAtomic(GetStorageStatePath(settingsDir), storageState);
                CurrentStockState stock = BuildCurrentStock(settingsDir, storageState);
                WriteJsonAtomic(GetCurrentStockPath(settingsDir), stock);
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
                WriteJsonAtomic(GetDispatchQueuePath(settingsDir), state);
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
                    StorageState state = ReadJson<StorageState>(
                        GetStorageStatePath(settingsDir));
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
                    WriteJsonAtomic(GetStorageStatePath(settingsDir), state);
                    WriteJsonAtomic(
                        GetCurrentStockPath(settingsDir),
                        BuildCurrentStock(settingsDir, state));
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
                    StorageState state = ReadJsonNoLock<StorageState>(
                        GetStorageStatePath(settingsDir));
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
                    WriteJsonAtomicNoLock(GetStorageStatePath(settingsDir), state);
                    WriteJsonAtomicNoLock(
                        GetCurrentStockPath(settingsDir),
                        BuildCurrentStock(settingsDir, state));
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

            foreach (StorageWorkerState worker in storageState.Workers)
            {
                if (worker == null || worker.Bags == null)
                    continue;

                foreach (StorageBagState bag in worker.Bags)
                {
                    if (bag == null || bag.Items == null)
                        continue;

                    foreach (StoredItemState item in bag.Items)
                    {
                        if (item == null)
                            continue;

                        string routedRole = null;
                        bool managed = SymbiantCatalog.TryGetDestinationRole(settingsDir, item.AoId, out routedRole);
                        if (!managed && item.HighId != item.AoId)
                            managed = SymbiantCatalog.TryGetDestinationRole(settingsDir, item.HighId, out routedRole);

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

            WriteJsonAtomic(
                GetDispatchCommandPath(settingsDir, command.DestinationCharacter),
                command);
        }

        public static DispatchCommand ReadDispatchCommand(
            string settingsDir,
            string character)
        {
            return ReadJson<DispatchCommand>(
                GetDispatchCommandPath(settingsDir, character));
        }

        public static void WriteStorageResult(
            string settingsDir,
            StorageBatchResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.Character))
                throw new InvalidOperationException("Storage result character is missing.");

            WriteJsonAtomic(
                GetStorageResultPath(settingsDir, result.Character),
                result);
        }

        public static StorageBatchResult ReadStorageResult(
            string settingsDir,
            string character)
        {
            return ReadJson<StorageBatchResult>(
                GetStorageResultPath(settingsDir, character));
        }

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

            WithMutex(LedgerMutexName, delegate
            {
                string directory = GetLedgerDirectory(settingsDir);
                string path = Path.Combine(
                    directory,
                    "citybankers-" +
                    record.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    ".jsonl");
                string line = JsonConvert.SerializeObject(record, Formatting.None) +
                    Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
            });
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
            try
            {
                T result = null;
                WithMutex(GetFileMutexName(path), delegate
                {
                    result = ReadJsonNoLock<T>(path);
                });
                return result;
            }
            catch
            {
                return null;
            }
        }

        public static void WriteJsonAtomic(string path, object value)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", "path");

            WithMutex(GetFileMutexName(path), delegate
            {
                WriteJsonAtomicNoLock(path, value);
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
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(
                temp,
                JsonConvert.SerializeObject(value, Formatting.Indented),
                new UTF8Encoding(false));

            Exception lastError = null;
            try
            {
                for (int attempt = 1; attempt <= AtomicFileRetryCount; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Replace(temp, path, null);
                        else
                            File.Move(temp, path);
                        return;
                    }
                    catch (IOException ex)
                    {
                        lastError = ex;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        lastError = ex;
                    }

                    if (attempt < AtomicFileRetryCount)
                        Thread.Sleep(AtomicFileRetryDelayMilliseconds);
                }

                throw new IOException(
                    "Unable to replace '" + path + "' after " +
                    AtomicFileRetryCount + " attempts; the last good file was preserved.",
                    lastError);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
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
            using (var mutex = new Mutex(false, name))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                        throw new IOException(
                            "Timed out waiting for CityBankers runtime-state lock '" + name + "'.");

                    action();
                }
                finally
                {
                    if (acquired)
                    {
                        try
                        {
                            mutex.ReleaseMutex();
                        }
                        catch
                        {
                        }
                    }
                }
            }
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
