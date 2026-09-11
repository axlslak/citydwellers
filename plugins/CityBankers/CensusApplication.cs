using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    // Central owns this operation while every operational actor is behind the
    // startup gate. The recorded bundle is reused after a partial write; do not
    // rebuild a plan against its own partially applied output.
    internal static class CensusApplication
    {
        internal sealed class Bundle
        {
            public string Format = "citybankers-census-application-v3";
            public string Generation;
            public DateTime RecordedUtc;
            public List<string> Runs;
            public ActiveLedgerState PreviousLedger;
            public List<ActiveLedgerItem> MatchingAnchors;
            public StorageState PreviousStorage;
            public DispatchQueueState PreviousQueue;
            public List<WithdrawalState> ReservedWithdrawals;
            public PhysicalLedgerReconciliation.Plan Plan;
            public StorageState Storage;
            public DispatchQueueState Queue;
        }

        internal static Bundle Apply(string settings, string directory, string generation,
            IList<BagAuditAgent.BagAuditResult> censuses, IDictionary<string, string> roles)
        {
            if (censuses.Count == 0 || censuses.Select(c => c.Character)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != censuses.Count ||
                !censuses.Any(c => string.Equals(c.Role, "central", StringComparison.OrdinalIgnoreCase)) ||
                censuses.Any(c => !roles.TryGetValue(c.Role, out var character) ||
                    !string.Equals(character, c.Character, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Reconciliation requires Central and distinct configured census participants.");
            var runs = censuses.Select(c => c.Character.ToLowerInvariant() + "/" + c.RunId)
                .OrderBy(value => value, StringComparer.Ordinal).ToList();
            string path = Path.Combine(directory, "application.json");
            var bundle = ReadExisting<Bundle>(path);
            if (bundle == null)
            {
                var observations = censuses.SelectMany(c => PhysicalLedgerReconciliation.ReadCensus(settings, c)).ToList();
                var previous = ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(settings)) ?? new ActiveLedgerState();
                var previousStorage = ReadExisting<StorageState>(RuntimeStateStore.GetStorageStatePath(settings));
                if (previous.Items == null) throw new InvalidOperationException("Existing ledger has no item collection.");
                foreach (var observation in observations.Where(o => o.Bag.HasValue &&
                    !string.IsNullOrWhiteSpace(o.BagIdentity) && o.BagIdentity != "(None:0000)"))
                {
                    var oldBags = (previousStorage?.Workers ?? new List<StorageWorkerState>())
                        .Where(w => string.Equals(w.Character, observation.Character, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(w => w.Bags ?? new List<StorageBagState>())
                        .Where(b => b.LastUniqueIdentity == observation.BagIdentity).ToList();
                    var currentBags = censuses.Single(c => c.Character == observation.Character).Bags
                        .Where(b => b.UniqueIdentity == observation.BagIdentity).ToList();
                    if (oldBags.Count == 1 && currentBags.Count == 1)
                    {
                        observation.PreviousBag = oldBags[0].OuterSlotInstance & 65535;
                        observation.PreviousLocation = oldBags[0].Source;
                    }
                }
                var withdrawals = WithdrawalStore.LoadAll(settings).Where(WithdrawalStore.IsActive).ToList();
                var deliveredIds = new HashSet<string>(withdrawals.Where(w => WithdrawalStore.HasConfirmedDelivery(w))
                    .Select(w => w.ActiveLedgerId), StringComparer.Ordinal);
                var anchors = JsonConvert.DeserializeObject<List<ActiveLedgerItem>>(JsonConvert.SerializeObject(
                    previous.Items.Where(i => !deliveredIds.Contains(i.Id)).ToList()));
                var scope = new HashSet<string>(censuses.Select(c => c.Character), StringComparer.OrdinalIgnoreCase);
                foreach (var request in withdrawals.Where(r => !WithdrawalStore.HasStatus(r, "requested")))
                {
                    var anchor = anchors.SingleOrDefault(i => i.Id == request.ActiveLedgerId);
                    if (anchor == null || !scope.Contains(anchor.Character)) continue;
                    anchor.Location = "withdrawal-uncertain"; anchor.Bag = null; anchor.Slot = null;
                }
                foreach (var request in withdrawals.Where(r => r.Item != null && !string.IsNullOrWhiteSpace(r.CentralItemIdentity) &&
                    r.CentralItemIdentity != "(None:0000)" && !deliveredIds.Contains(r.ActiveLedgerId)))
                {
                    var exact = observations.Where(o => string.Equals(o.Character, roles["central"], StringComparison.OrdinalIgnoreCase) &&
                        o.Location == "inventory" && !o.Bag.HasValue && o.Item.UniqueIdentity == request.CentralItemIdentity &&
                        o.Item.LowId == request.Item.AoId && o.Item.HighId == request.Item.HighId && o.Item.Ql == request.Item.Ql).ToList();
                    var anchor = anchors.SingleOrDefault(i => i.Id == request.ActiveLedgerId);
                    if (anchor == null || exact.Count != 1 || withdrawals.Count(r => r.CentralItemIdentity == request.CentralItemIdentity) != 1) continue;
                    anchor.Character = roles["central"]; anchor.Location = "inventory"; anchor.Bag = null; anchor.Slot = exact[0].Slot;
                }
                var plan = PhysicalLedgerReconciliation.Build(anchors, observations, scope);
                // Unknown origin still needs a stable transaction for ordinary
                // routing/accounting. This identifier makes no donor claim.
                foreach (var item in plan.Items)
                    if (string.IsNullOrWhiteSpace(item.TransactionId)) item.TransactionId = "found-" + item.Id;
                bundle = new Bundle
                {
                    Generation = generation, RecordedUtc = DateTime.UtcNow, Runs = runs,
                    PreviousLedger = previous, MatchingAnchors = anchors, PreviousStorage = previousStorage,
                    PreviousQueue = ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(settings)),
                    ReservedWithdrawals = withdrawals,
                    Plan = plan, Storage = BuildStorage(censuses, plan, generation),
                    Queue = BuildQueue(plan, observations, roles, new List<WithdrawalState>(), generation)
                };
                bundle.Storage.Workers.AddRange((previousStorage?.Workers ?? new List<StorageWorkerState>())
                    .Where(w => !scope.Contains(w.Character)));
                RuntimeStateStore.WriteJsonAtomic(path, bundle);
            }
            if (bundle.Format != "citybankers-census-application-v3" || bundle.ReservedWithdrawals == null ||
                bundle.Generation != generation || bundle.Runs == null || !bundle.Runs.SequenceEqual(runs) ||
                bundle.MatchingAnchors == null || bundle.Plan == null || bundle.Storage == null || bundle.Queue == null)
                throw new InvalidOperationException("Census changed during application; retained bundle requires a new reconciliation.");

            // Immutable investigation history is written before removing claims.
            // The time is when the difference was observed, not an invented loss time.
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(ActiveLedgerStore.GetHistoryDirectory(settings),
                "census-" + generation + ".json"), new
                {
                    Format = "citybankers-census-history-v1", bundle.Generation, bundle.RecordedUtc,
                    EventTimeKnown = false, bundle.Runs, bundle.Plan.Differences,
                    Withdrawals = bundle.ReservedWithdrawals.Select(w => new
                    { Original = w, Disposition = WithdrawalStore.HasConfirmedDelivery(w) ? "completed" : "reconciled",
                        ObservedLedgerIds = bundle.Plan.Items.Where(i => i.Id == w.ActiveLedgerId).Select(i => i.Id).ToList() }).ToList()
                });
            foreach (var withdrawal in bundle.ReservedWithdrawals.Where(w => WithdrawalStore.HasConfirmedDelivery(w)))
                ActiveLedgerStore.RecordCensusConfirmedDelivery(settings, withdrawal,
                    bundle.PreviousLedger.Items.SingleOrDefault(i => i.Id == withdrawal.ActiveLedgerId));
            RuntimeStateStore.SaveStorageBaseline(settings, bundle.Storage, "census-" + generation);
            ActiveLedgerStore.ApplyCensus(settings, bundle.Plan.Items, censuses.SelectMany(c =>
                PhysicalLedgerReconciliation.ReadCensus(settings, c)).Select(o => new TransferItemState
                { AoId = o.Item.LowId, HighId = o.Item.HighId, Ql = o.Item.Ql, Name = o.Item.Name }));
            // Old batch status is not a physical instruction after a full census.
            // Its complete record remains in application.json, without inventing
            // a successful transfer for any missing occurrence.
            RuntimeStateStore.SaveDispatchQueue(settings, bundle.Queue);
            WithdrawalStore.ReconcileRequestsAfterCensus(settings, generation, bundle.ReservedWithdrawals);
            WithdrawalStore.ResetRecoveryAfterCensus(settings, Path.Combine(directory, "previous-recovery-reservations.json"));
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "applied.json"), new
            { Generation = generation, bundle.Runs, Count = bundle.Plan.Items.Count });
            return bundle;
        }

        internal static DispatchQueueState BuildQueue(PhysicalLedgerReconciliation.Plan plan,
            List<PhysicalLedgerReconciliation.Observation> observations,
            IDictionary<string, string> roles, List<WithdrawalState> withdrawals, string generation)
        {
            var queue = new DispatchQueueState { UpdatedUtc = DateTime.UtcNow };
            string central = roles["central"];
            foreach (var entry in plan.Items.Where(e => string.Equals(e.Character, central, StringComparison.OrdinalIgnoreCase) &&
                e.Location == "inventory" && !e.Bag.HasValue && e.Slot.HasValue))
            {
                if (withdrawals.Any(w => w.ActiveLedgerId == entry.Id ||
                    (w.Item != null && w.Item.AoId == entry.AoId &&
                    (!entry.HighId.HasValue || w.Item.HighId == entry.HighId) &&
                    (!entry.Ql.HasValue || w.Item.Ql == entry.Ql)))) continue;
                string destination;
                if (string.Equals(entry.Family, "central", StringComparison.OrdinalIgnoreCase) ||
                    !roles.TryGetValue(entry.Family, out destination)) continue;
                var observed = observations.Single(o => string.Equals(o.Character, central, StringComparison.OrdinalIgnoreCase) &&
                    o.Location == "inventory" && !o.Bag.HasValue && o.Slot == entry.Slot);
                queue.Batches.Add(new DispatchBatchState
                {
                    BatchId = "census-" + generation + "-" + entry.Id,
                    TransactionId = entry.TransactionId, Role = entry.Family, Character = destination,
                    Status = "queued", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                    Items = new List<TransferItemState> { new TransferItemState
                    { AoId = observed.Item.LowId, HighId = observed.Item.HighId, Ql = observed.Item.Ql,
                        Name = observed.Item.Name, UniqueIdentity = observed.Item.UniqueIdentity } }
                });
            }
            return queue;
        }

        internal static StorageState BuildStorage(IEnumerable<BagAuditAgent.BagAuditResult> censuses,
            PhysicalLedgerReconciliation.Plan plan, string generation)
        {
            var state = new StorageState { BaselineRunId = "census-" + generation, UpdatedUtc = DateTime.UtcNow };
            foreach (var census in censuses)
            {
                var worker = new StorageWorkerState { Character = census.Character, Role = census.Role,
                    ObservedUtc = census.ObservedUtc };
                foreach (var observed in census.Bags)
                {
                    var bag = new StorageBagState
                    {
                        Source = observed.Source,
                        OuterSlotType = observed.Source == "bank" ? observed.ReturnedOuterSlotType : observed.OuterSlotType,
                        OuterSlotInstance = (observed.Source == "bank" ? observed.ReturnedOuterSlotInstance : observed.OuterSlotInstance) & 65535,
                        LastUniqueIdentity = observed.UniqueIdentity, LastHandle = observed.Handle,
                        Capacity = observed.ItemCount + observed.FreeSlots
                    };
                    foreach (var item in observed.Items)
                    {
                        var entry = plan.Items.Single(e => string.Equals(e.Character, census.Character, StringComparison.OrdinalIgnoreCase) &&
                            e.Location == bag.Source && e.Bag == bag.OuterSlotInstance && e.Slot == (item.SlotInstance & 65535));
                        bag.Items.Add(new StoredItemState { AoId = item.LowId, HighId = item.HighId, Ql = item.Ql,
                            Name = item.Name, InnerSlot = item.SlotInstance & 65535, UniqueIdentity = item.UniqueIdentity,
                            TransactionId = entry.TransactionId, ObservedUtc = census.ObservedUtc });
                    }
                    worker.Bags.Add(bag);
                }
                state.Workers.Add(worker);
            }
            return state;
        }

        internal static T ReadExisting<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            // A corrupt or temporarily unreadable record is not an empty ledger.
            // Let the caller retry; never silently discard its provenance.
            var value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            if (value == null) throw new InvalidOperationException("Unreadable reconciliation record: " + Path.GetFileName(path));
            return value;
        }
    }
}
