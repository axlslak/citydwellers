using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
    public static partial class CruPolicy
    {
        public static bool IsCru(WithdrawalState row) => row?.Item != null && IsCru(row.Item.AoId);
    }

    public sealed class WithdrawalState
    {
        public string Format = "citybankers-withdrawal-v2";
        public string Id;
        public string OrderId;
        public long Revision;
        public int RecoveryAttempts;
        public int LiveInventoryRecoveryAttempts;
        public int LiveInventoryAnchorAttempts;
        public string Status;
        public DateTime CreatedUtc;
        public DateTime UpdatedUtc;
        public DateTime? PickupExpiresUtc;
        public string PickupHostGeneration;
        public long PickupDeadlineStamp;
        public DateTime? DeliveredUtc;
        public string RequestedBy;
        public string RecipientMain;
        public List<string> AllowedCharacters = new List<string>();
        public string ActiveLedgerId;
        public string DonationTransactionId;
        public string SourceRole;
        public string SourceCharacter;
        public string SourceBag;
        public int SourceBagOuterSlot;
        public int SourceInnerSlot;
        public string SourceItemIdentity;
        public List<int> PreExtractionInventorySlots = new List<int>();
        public string ExtractedItemIdentity;
        public int? LiveInventoryAnchorSlot;
        public string LiveInventoryAnchorIdentity;
        public string CentralItemIdentity;
        public string TransferAttemptId;
        public string ReturnBatchId;
        public string ReconciledByCensus;
        public string RecoveryCensusId;
        public string Error;
        public TransferItemState Item;
    }

    public sealed class WithdrawalQueueState
    {
        public string Format = "citybankers-withdrawal-queue-v2";
        public List<WithdrawalState> Withdrawals = new List<WithdrawalState>();
    }

    public static class WithdrawalStore
    {
        private static readonly string HostGeneration = Process.GetCurrentProcess().Id + "-" +
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

        public static bool IsReadyForRequests(string directory) => BankerReadiness.IsReadyForRequests(directory);
        public static bool IsCharacterReady(string directory, string character) => BankerReadiness.IsCharacterReady(directory, character);
        public static HashSet<string> GetReadyCharacters(string directory) => BankerReadiness.GetReadyCharacters(directory);

        public static bool PickupWindowOpen(WithdrawalState row) => row != null &&
            row.PickupHostGeneration == HostGeneration && row.PickupDeadlineStamp > Stopwatch.GetTimestamp();

        public static void RenewPickupWindow(WithdrawalState row)
        {
            row.PickupExpiresUtc = DateTime.UtcNow.AddSeconds(PickupSeconds); // Display/history only.
            row.PickupHostGeneration = HostGeneration;
            row.PickupDeadlineStamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency * PickupSeconds;
        }

        public sealed class RecoveryReservation
        {
            public string OperationId;
            public string LedgerId;
            public string CensusCharacter;
            public bool CensusCentral;
            public string WithdrawalCensusId;
        }

        private static string RecoveryPath(string directory) => Path.Combine(
            RuntimeStateStore.GetDataDirectory(directory), "recovery-reservations.json");
        private static List<RecoveryReservation> ReadRecovery(string directory)
        {
            string path = RecoveryPath(directory);
            if (!File.Exists(path)) return new List<RecoveryReservation>();
            var rows = JsonConvert.DeserializeObject<List<RecoveryReservation>>(File.ReadAllText(path));
            if (rows == null || rows.Any(r => r == null || string.IsNullOrWhiteSpace(r.OperationId) || (string.IsNullOrWhiteSpace(r.LedgerId) && string.IsNullOrWhiteSpace(r.CensusCharacter))))
                throw new InvalidDataException("Invalid recovery reservation state.");
            return rows;
        }

        public static HashSet<string> GetRecoveryReservedIds(string directory) => Locked(() =>
            new HashSet<string>(ReadRecovery(directory).Where(r => !string.IsNullOrWhiteSpace(r.LedgerId)).Select(r => r.LedgerId), StringComparer.Ordinal));

        public static HashSet<string> GetCensusCharacters(string directory) => Locked(() =>
            new HashSet<string>(ReadRecovery(directory).Where(r => !string.IsNullOrWhiteSpace(r.CensusCharacter))
                .Select(r => r.CensusCharacter), StringComparer.OrdinalIgnoreCase));

        public static bool OwnsCensus(string directory, string operation, string character) => Locked(() =>
            ReadRecovery(directory).Any(r => r.OperationId == operation &&
                string.Equals(r.CensusCharacter, character, StringComparison.OrdinalIgnoreCase)));

        public static string GetWithdrawalCensusId(string directory, string character) => Locked(() =>
            ReadRecovery(directory).Where(r => string.Equals(r.CensusCharacter, character, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.WithdrawalCensusId).SingleOrDefault());

        public static bool TryReserveWithdrawalCensus(string directory, string id, string central,
            IDictionary<string, string> runs, IList<WithdrawalState> originals)
        {
            return Update(directory, rows =>
            {
                var scope = new HashSet<string>(runs.Keys, StringComparer.OrdinalIgnoreCase);
                var ids = new HashSet<string>(originals.Select(r => r.Id), StringComparer.Ordinal);
                if (rows.Any(r => IsActive(r) && !CruPolicy.IsCru(r) && (!HasStatus(r, "requested") || scope.Contains(r.SourceCharacter)) &&
                    !ids.Contains(r.Id))) return false;
                foreach (var original in originals)
                {
                    var current = rows.SingleOrDefault(r => r.Id == original.Id);
                    bool frozen = current?.RecoveryCensusId == id && HasStatus(current, "reconciling") &&
                        current.Revision == original.Revision + 1;
                    if (!frozen && (current == null || current.Revision != original.Revision ||
                        current.Status != original.Status || !IsActive(current) || current.RecoveryCensusId != null)) return false;
                }
                var reservations = ReadRecovery(directory);
                if (reservations.Any(r => !string.IsNullOrWhiteSpace(r.CensusCharacter) && scope.Contains(r.CensusCharacter) &&
                    (r.WithdrawalCensusId != id || r.OperationId != runs[r.CensusCharacter]))) return false;
                foreach (var run in runs)
                    if (!reservations.Any(r => r.OperationId == run.Value))
                        reservations.Add(new RecoveryReservation { OperationId = run.Value, CensusCharacter = run.Key,
                            CensusCentral = string.Equals(run.Key, central, StringComparison.OrdinalIgnoreCase), WithdrawalCensusId = id });
                // Leases precede status changes under the same admission mutex.
                // If either write fails, the retained grant and exact revisions
                // let the same recovery finish freezing the requests on retry.
                RuntimeStateStore.WriteJsonAtomic(RecoveryPath(directory), reservations);
                foreach (var row in rows.Where(r => ids.Contains(r.Id) && r.RecoveryCensusId != id))
                {
                    row.Status = "reconciling";
                    row.RecoveryCensusId = id;
                    Touch(row);
                }
                return true;
            });
        }

        public static void RestoreRequestsAfterWithdrawalCensus(string directory, string id,
            IDictionary<string, string> runs, IList<WithdrawalState> originals, IList<WithdrawalState> dispositions)
        {
            Update(directory, rows =>
            {
                var leases = ReadRecovery(directory);
                if (runs.Any(run => !leases.Any(r => r.OperationId == run.Value && r.WithdrawalCensusId == id &&
                    string.Equals(r.CensusCharacter, run.Key, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException("Withdrawal census lost a participant reservation.");
                if (originals.Count != dispositions.Count || dispositions.Select(r => r.Id).Distinct().Count() != originals.Count)
                    throw new InvalidOperationException("Withdrawal census has incomplete request dispositions.");
                foreach (var original in originals)
                {
                    var current = rows.SingleOrDefault(r => r.Id == original.Id);
                    if (current?.ReconciledByCensus == id && current.RecoveryCensusId == null) continue;
                    if (current?.RecoveryCensusId != id || !HasStatus(current, "reconciling") ||
                        current.Revision != original.Revision + 1)
                        throw new InvalidOperationException("Withdrawal changed while its census owned it.");
                    var restored = JsonConvert.DeserializeObject<WithdrawalState>(JsonConvert.SerializeObject(
                        dispositions.Single(r => r.Id == original.Id)));
                    if (restored.Status != "requested" && restored.Status != "central-ready" &&
                        restored.Status != "completed" && restored.Status != "reconciled")
                        throw new InvalidOperationException("Invalid post-census withdrawal status.");
                    restored.Revision = current.Revision;
                    restored.RecoveryCensusId = null;
                    restored.ReconciledByCensus = id;
                    if (HasStatus(restored, "central-ready")) RenewPickupWindow(restored);
                    Touch(restored);
                    rows[rows.IndexOf(current)] = restored;
                }
                return true;
            });
        }

        public static bool TryReserveCensus(string directory, string operation, string character, bool central = false)
        {
            return Locked(() =>
            {
                if (Read(directory).Any(r => IsActive(r) &&
                    (central || (string.Equals(r.SourceCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                        !HasStatus(r, "requested"))))) return false;
                var rows = ReadRecovery(directory);
                var existing = rows.FirstOrDefault(r => string.Equals(r.CensusCharacter, character, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return existing.OperationId == operation;
                rows.Add(new RecoveryReservation { OperationId = operation, CensusCharacter = character, CensusCentral = central });
                RuntimeStateStore.WriteJsonAtomic(RecoveryPath(directory), rows);
                return true;
            });
        }

        public static bool TryReserveDispatchCensus(string directory, string centralRun, string central,
            string workerRun, string worker)
        {
            return Locked(() =>
            {
                // Requested rows have no AO action in flight. Existing pickup
                // or extraction transactions need their own recovery protocol.
                if (Read(directory).Any(r => IsActive(r) && !HasStatus(r, "requested"))) return false;
                var rows = ReadRecovery(directory);
                if (rows.Any(r => !string.IsNullOrWhiteSpace(r.CensusCharacter) &&
                    ((string.Equals(r.CensusCharacter, central, StringComparison.OrdinalIgnoreCase) && r.OperationId != centralRun) ||
                     (string.Equals(r.CensusCharacter, worker, StringComparison.OrdinalIgnoreCase) && r.OperationId != workerRun)))) return false;
                if (!rows.Any(r => r.OperationId == centralRun))
                    rows.Add(new RecoveryReservation { OperationId = centralRun, CensusCharacter = central, CensusCentral = true });
                if (!rows.Any(r => r.OperationId == workerRun))
                    rows.Add(new RecoveryReservation { OperationId = workerRun, CensusCharacter = worker });
                RuntimeStateStore.WriteJsonAtomic(RecoveryPath(directory), rows);
                return true;
            });
        }

        // Serialize extraction admission with the census lease. A queued GET
        // has not moved anything and must not prevent the source from auditing.
        public static bool TryBeginExtraction(string directory, WithdrawalState request)
        {
            return Update(directory, rows =>
            {
                var current = rows.SingleOrDefault(r => r.Id == request.Id);
                if (current == null || current.Revision != request.Revision ||
                    !HasStatus(current, "requested") || !IsCharacterReady(directory, request.SourceCharacter) ||
                    ReadRecovery(directory).Any(r => r.CensusCentral || r.LedgerId == request.ActiveLedgerId ||
                        string.Equals(r.CensusCharacter, request.SourceCharacter, StringComparison.OrdinalIgnoreCase)))
                    return false;
                request.Status = "extracting";
                Touch(request);
                rows[rows.IndexOf(current)] = request;
                return true;
            });
        }

        public static void ReconcileQueuedRequestsAfterLocalCensus(string directory, string run,
            string character, IList<WithdrawalState> originals)
        {
            if (originals == null) throw new InvalidOperationException("Local census has no request snapshot.");
            Update(directory, rows =>
            {
                if (!ReadRecovery(directory).Any(r => r.OperationId == run &&
                    string.Equals(r.CensusCharacter, character, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Local census no longer owns its source.");
                var ids = new HashSet<string>(originals.Select(r => r.Id), StringComparer.Ordinal);
                if (rows.Any(r => IsActive(r) && string.Equals(r.SourceCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                    (!HasStatus(r, "requested") || !ids.Contains(r.Id))))
                    throw new InvalidOperationException("Source withdrawal changed while its census owned admission.");
                foreach (var original in originals)
                {
                    if (!HasStatus(original, "requested") ||
                        !string.Equals(original.SourceCharacter, character, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Local census cannot retire an active transfer.");
                    var current = rows.SingleOrDefault(r => r.Id == original.Id);
                    if (current?.ReconciledByCensus == run) continue;
                    if (current == null || current.Revision != original.Revision || current.Status != original.Status)
                        throw new InvalidOperationException("Retained request changed during local census.");
                    current.Status = "reconciled";
                    current.ReconciledByCensus = run;
                    current.Error = "Source inventory refreshed before extraction; no delivery inferred. Please request the item again.";
                    Touch(current);
                }
                return true;
            });
        }

        public static bool OwnsRecovery(string directory, string operation, string ledgerId) => Locked(() =>
            ReadRecovery(directory).Any(r => r.OperationId == operation && r.LedgerId == ledgerId));

        public static bool TryReserveRecovery(string directory, string operation, string ledgerId)
        {
            return Locked(() =>
            {
                if (Read(directory).Any(r => IsActive(r) && r.ActiveLedgerId == ledgerId)) return false;
                var rows = ReadRecovery(directory);
                var existing = rows.FirstOrDefault(r => r.LedgerId == ledgerId);
                if (existing != null) return existing.OperationId == operation;
                rows.Add(new RecoveryReservation { OperationId = operation, LedgerId = ledgerId });
                RuntimeStateStore.WriteJsonAtomic(RecoveryPath(directory), rows);
                return true;
            });
        }

        public static void ReleaseRecovery(string directory, string operation)
        {
            Locked(() =>
            {
                var rows = ReadRecovery(directory);
                if (rows.RemoveAll(r => r.OperationId == operation) != 0)
                    RuntimeStateStore.WriteJsonAtomic(RecoveryPath(directory), rows);
                return true;
            });
        }

        public static void ResetRecoveryAfterCensus(string directory, string archivePath)
        {
            Locked(() =>
            {
                // A completed census supersedes old movement leases even if
                // their cache is malformed. Preserve the original text first.
                if (!File.Exists(archivePath)) RuntimeStateStore.WriteJsonAtomic(archivePath, new
                {
                    OriginalContents = File.Exists(RecoveryPath(directory)) ? File.ReadAllText(RecoveryPath(directory)) : null,
                    Reason = "superseded-by-complete-physical-census"
                });
                RuntimeStateStore.WriteJsonAtomic(RecoveryPath(directory), new List<RecoveryReservation>());
                return true;
            });
        }
        public static void ReconcileRequestsAfterCensus(string directory, string generation,
            IList<WithdrawalState> originals)
        {
            if (originals == null) throw new InvalidOperationException("Census has no withdrawal snapshot.");
            Update(directory, rows =>
            {
                var ids = new HashSet<string>(originals.Select(r => r.Id), StringComparer.Ordinal);
                if (rows.Any(r => IsActive(r) && !ids.Contains(r.Id)))
                    throw new InvalidOperationException("A withdrawal was added while startup census owned admission.");
                foreach (var original in originals)
                {
                    var current = rows.SingleOrDefault(r => r.Id == original.Id);
                    if (current == null) throw new InvalidOperationException("A retained withdrawal disappeared during census application.");
                    if (current.ReconciledByCensus == generation) continue;
                    if (current.Revision != original.Revision || current.Status != original.Status)
                        throw new InvalidOperationException("A withdrawal changed during startup census application.");
                    current.Status = HasConfirmedDelivery(original) ? "completed" : "reconciled";
                    current.ReconciledByCensus = generation;
                    current.RecoveryCensusId = null;
                    current.Error = HasConfirmedDelivery(original)
                        ? "Previously confirmed delivery retained during startup census."
                        : "Interrupted request closed after full physical census; no delivery inferred. Please request the item again.";
                    Touch(current);
                }
                return true;
            });
        }

        public const int PickupSeconds = 180;
        public const int MaximumOrders = 4;
        public const int MaximumOrderItems = 3;
        public const string FileName = "withdrawal.json";
        private const string MutexName = "CityBankers.Withdrawal.v1";

        public static string GetPath(string directory)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(directory), FileName);
        }

        // All readers fail closed: unreadable state is never an empty bank queue.
        private static List<WithdrawalState> Read(string directory)
        {
            string path = GetPath(directory);
            if (!File.Exists(path)) return new List<WithdrawalState>();
            JObject root = JObject.Parse(File.ReadAllText(path));
            List<WithdrawalState> rows;
            if (root["Withdrawals"] != null)
            {
                if ((string)root["Format"] != "citybankers-withdrawal-queue-v2")
                    throw new InvalidDataException("Unknown withdrawal queue format.");
                rows = root["Withdrawals"].ToObject<List<WithdrawalState>>();
            }
            else
            {
                string format = (string)root["Format"];
                if (format != "citybankers-withdrawal-v1" && format != "citybankers-withdrawal-v2")
                    throw new InvalidDataException("Unknown withdrawal state format.");
                rows = new List<WithdrawalState> { root.ToObject<WithdrawalState>() };
            }
            if (rows == null || rows.Any(row => row == null || string.IsNullOrWhiteSpace(row.Id)))
                throw new InvalidDataException("Invalid withdrawal queue.");
            if (rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                throw new InvalidDataException("Duplicate withdrawal IDs.");
            foreach (WithdrawalState row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.OrderId)) row.OrderId = row.Id;
                row.CreatedUtc = NormalizeUtc(row.CreatedUtc);
                row.UpdatedUtc = NormalizeUtc(row.UpdatedUtc);
                if (row.PickupExpiresUtc.HasValue) row.PickupExpiresUtc = NormalizeUtc(row.PickupExpiresUtc.Value);
                if (row.DeliveredUtc.HasValue) row.DeliveredUtc = NormalizeUtc(row.DeliveredUtc.Value);
            }
            return rows;
        }

        private static T Locked<T>(Func<T> action)
        {
            using (var mutex = new Mutex(false, MutexName))
            {
                bool entered = false;
                try
                {
                    try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (!entered) throw new TimeoutException("Withdrawal queue is busy.");
                    return action();
                }
                finally { if (entered) mutex.ReleaseMutex(); }
            }
        }

        public static List<WithdrawalState> LoadAll(string directory)
        {
            return Locked(() => Read(directory));
        }

        public static WithdrawalState Load(string directory)
        {
            return LoadAll(directory).FirstOrDefault(IsActive);
        }

        public static bool HasStatus(WithdrawalState row, string status)
        {
            return row != null && string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase);
        }

        // Mutate against current state, not a stale copy held by another client domain.
        public static T Update<T>(string directory, Func<List<WithdrawalState>, T> action)
        {
            return Locked(() =>
            {
                List<WithdrawalState> rows = Read(directory);
                string before = JsonConvert.SerializeObject(rows);
                T result = action(rows);
                if (!string.Equals(before, JsonConvert.SerializeObject(rows), StringComparison.Ordinal))
                    RuntimeStateStore.WriteJsonAtomic(GetPath(directory),
                        new WithdrawalQueueState { Withdrawals = rows });
                return result;
            });
        }

        public static void Save(string directory, WithdrawalState state)
        {
            Update(directory, rows =>
            {
                WithdrawalState current = rows.FirstOrDefault(row => row.Id == state.Id);
                if (current == null || current.Revision != state.Revision)
                    throw new InvalidOperationException("Withdrawal changed concurrently; reload before continuing.");
                state.Revision++;
                state.UpdatedUtc = DateTime.UtcNow;
                rows[rows.IndexOf(current)] = state;
                return true;
            });
        }

        public static void Touch(WithdrawalState row)
        {
            row.Revision++;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        public static bool TryAdd(string directory, WithdrawalState request, out string error, int centralSupplyUnits = -1)
        {
            string reason = null;
            bool added = Update(directory, rows =>
            {
                List<WithdrawalState> active = rows.Where(IsActive).ToList();
                List<WithdrawalState> own = active.Where(row => string.Equals(
                    row.RecipientMain, request.RecipientMain, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!IsCharacterReady(directory, request.SourceCharacter))
                    reason = "The bank is checking its physical inventory. Please try again when it is ready.";
                else if (active.Any(row => row.ActiveLedgerId == request.ActiveLedgerId))
                    reason = "That copy was just reserved. Please select it again.";
                else if (ReadRecovery(directory).Any(r => r.CensusCentral || r.LedgerId == request.ActiveLedgerId ||
                    string.Equals(r.CensusCharacter, request.SourceCharacter, StringComparison.OrdinalIgnoreCase)))
                    reason = "That item is being moved. Please try again shortly.";
                else if (CruPolicy.IsCru(request) && (centralSupplyUnits < 0 ||
                    active.Count(CruPolicy.IsCru) >= centralSupplyUnits))
                    reason = "No CRU is available right now.";
                else if (!CruPolicy.IsCru(request) && !RequestStillStored(directory, request))
                    reason = "That item has moved. Please refresh stock and select it again.";
                else if (own.Any(row => HasStatus(row, "pickup-trading")))
                    reason = "Finish your open pickup trade before adding another item.";
                else if (own.Count >= MaximumOrderItems)
                    reason = "Your order already has three items. Collect the ready items first.";
                else if (own.Count == 0 && active.Select(row => row.OrderId).Distinct().Count() >= MaximumOrders)
                    reason = "All four pickup orders are occupied. Please try again after a pickup.";
                if (reason != null) return false;
                request.OrderId = own.Count > 0 ? own[0].OrderId : "order-" + Guid.NewGuid().ToString("N");
                request.Revision = 1;
                request.UpdatedUtc = DateTime.UtcNow;
                foreach (WithdrawalState row in own.Where(row => HasStatus(row, "central-ready")))
                {
                    RenewPickupWindow(row);
                    Touch(row);
                }
                rows.Add(request);
                return true;
            });
            error = reason;
            return added;
        }

        private static bool RequestStillStored(string directory, WithdrawalState request)
        {
            string path = Path.Combine(RuntimeStateStore.GetDataDirectory(directory), "ledger.json");
            if (!File.Exists(path) || request.Item == null) return false;
            var ledger = JObject.Parse(File.ReadAllText(path));
            var entries = (ledger["Items"] as JArray)?.Where(e => (string)e["Id"] == request.ActiveLedgerId).ToList();
            if (entries == null || entries.Count != 1) return false;
            var entry = entries[0];
            return string.Equals((string)entry["Character"], request.SourceCharacter, StringComparison.OrdinalIgnoreCase) &&
                (string)entry["Location"] == request.SourceBag && (int?)entry["Bag"] == request.SourceBagOuterSlot &&
                (int?)entry["Slot"] == request.SourceInnerSlot && (int?)entry["AoId"] == request.Item.AoId &&
                (string)entry["TransactionId"] == request.DonationTransactionId &&
                RuntimeStateStore.LoadCurrentStock(directory).Items.Any(i => i.Character == request.SourceCharacter &&
                    i.BagSource == request.SourceBag && i.BagOuterSlot == request.SourceBagOuterSlot &&
                    i.InnerSlot == request.SourceInnerSlot && i.AoId == request.Item.AoId &&
                    i.HighId == request.Item.HighId && i.Ql == request.Item.Ql && i.TransactionId == request.DonationTransactionId);
        }

        // DeliveredUtc is written only by the verified physical pickup callback.
        // A later archival failure may change Status to failed, but must not
        // turn that delivered occurrence into a new extraction request.
        public static bool HasConfirmedDelivery(WithdrawalState state) => state != null &&
            (HasStatus(state, "delivery-confirmed") || state.DeliveredUtc.HasValue);

        public static bool IsTerminal(WithdrawalState state)
        {
            return state == null || HasStatus(state, "completed") || HasStatus(state, "expired") || HasStatus(state, "reconciled");
        }

        public static bool IsActive(WithdrawalState state) { return state != null && !IsTerminal(state); }

        public static bool IsAllowedCollector(WithdrawalState state, string character)
        {
            return state != null && !string.IsNullOrWhiteSpace(character) &&
                (state.AllowedCharacters ?? new List<string>()).Any(name =>
                    string.Equals(name, character, StringComparison.OrdinalIgnoreCase));
        }

        public static bool OwnsCentralTrade(WithdrawalState state)
        {
            return HasStatus(state, "extracting") || HasStatus(state, "pickup-trading") ||
                HasStatus(state, "central-received");
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
