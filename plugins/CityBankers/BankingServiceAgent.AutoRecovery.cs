using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using CityDwellers.Shared;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class AutomaticRepair
        {
            public string Id = Guid.NewGuid().ToString("N");
            public HashSet<string> Targets = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, List<BagInnerItem>> Contents = new Dictionary<string, List<BagInnerItem>>(StringComparer.Ordinal);
            public string Current, Phase;
            public bool WasBanked;
            public int Attempts;
            public readonly Stopwatch Age = Stopwatch.StartNew();
        }
        private AutomaticRepair _automaticRepair;
        private static BankingServiceAgent _automaticRepairOwner;

        internal static void InvalidateAutomaticRepair()
        {
            if (_automaticRepairOwner != null) _automaticRepairOwner._automaticRepair = null;
        }

        internal static bool RecoverFreshConnection(string settings, string role, string reason)
        {
            var actor = _ipcOwner ?? _automaticRepairOwner;
            if (actor == null)
            {
                actor = new BankingServiceAgent { _settingsDir = settings, _role = role,
                    _isCentral = string.Equals(role, "central", StringComparison.OrdinalIgnoreCase) };
                actor._config = actor.LoadConfig();
                actor._centralCharacter = actor._config.Roles["central"].Character;
            }
            _automaticRepairOwner = actor;
            if (!Client.InPlay || Trade.IsTrading || !Inventory.Bank.IsOpen || !ClientlessSessionGuard.BankCacheTrusted) return false;
            return actor.TickAutomaticRepair(reason);
        }

        private readonly HashSet<string> _orphanedDispatchBatches = new HashSet<string>(StringComparer.Ordinal);
        private bool TickUnresolvedBatches()
        {
            if (!_isCentral || Trade.IsTrading || _receipt != null || _activeBatch != null || _donationCleanup != null) return false;
            var ready = BankerReadiness.GetReadyCharacters(_settingsDir);
            var failed = RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Where(b =>
                b.Status == "failed" || b.Status == "custody-hold" || b.Status == "retrying" || b.Status == "trading" ||
                (b.Status == "transferred" && ready.Contains(b.Character) &&
                 ManagerMemory.Current.BankerStorageWork(b.Character) != b.BatchId &&
                 ManagerMemory.Current.ReadStorageResult(ManagerAccounting.TransactionId, b.Character)?.BatchId != b.BatchId)).ToList();
            if (failed.Count == 0) return false;
            foreach (var batch in failed) _orphanedDispatchBatches.Add(batch.BatchId);
            foreach (var batch in failed)
                ManagerMemory.Current.RequestBankerRecovery(batch.Character,
                    "Resolve interrupted batch " + batch.BatchId + " from native custody.");
            StartupCensusGate.Block("Resolve interrupted dispatch queue from native custody.");
            return true;
        }

        private bool TickAutomaticRepair(string reason)
        {
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            var own = storage.Workers.SingleOrDefault(w => SameCharacter(w.Character, Client.CharacterName));
            if (_automaticRepair == null)
            {
                _automaticRepair = new AutomaticRepair();
                foreach (string id in new[] { _bankLayout?.Identity, _reserveOperation?.Identity,
                    _storageJob?.BagLiveIdentity, _storageJob?.Bag?.LastUniqueIdentity, _extraction?.BagIdentity })
                    if (!string.IsNullOrEmpty(id)) _automaticRepair.Targets.Add(id);
                var unresolved = RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Where(b =>
                    SameCharacter(b.Character, Client.CharacterName) && b.Status != "queued" && b.Status != "completed" && b.Status != "stored").ToList();
                foreach (var bag in own?.Bags.Where(b => b.Items.Any(i => unresolved.Any(batch =>
                    batch.TransactionId == i.TransactionId))) ?? Enumerable.Empty<StorageBagState>())
                    _automaticRepair.Targets.Add(bag.LastUniqueIdentity);
                foreach (var proof in ManagerMemory.Current.ReadCharacterExtractions(ManagerAccounting.TransactionId, Client.CharacterName))
                    if (!string.IsNullOrEmpty(proof.BagIdentity)) _automaticRepair.Targets.Add(proof.BagIdentity);
                var pending = ManagerMemory.Current.ReadReserveOperation(ManagerAccounting.TransactionId, Client.CharacterName);
                if (pending != null) _automaticRepair.Targets.Add(pending.Identity);
                foreach (var extraction in ManagerMemory.Current.ReadCharacterExtractions(ManagerAccounting.TransactionId, Client.CharacterName))
                {
                    var bag = own?.Bags.SingleOrDefault(b => b.Source == extraction.Source &&
                        (b.OuterSlotInstance & 65535) == extraction.Bag);
                    if (bag != null) _automaticRepair.Targets.Add(bag.LastUniqueIdentity);
                }
                if (_withdrawal?.SourceBag != null)
                {
                    var bag = own?.Bags.SingleOrDefault(b => b.Source == _withdrawal.SourceBag &&
                        (b.OuterSlotInstance & 65535) == (_withdrawal.SourceBagOuterSlot & 65535));
                    if (bag != null) _automaticRepair.Targets.Add(bag.LastUniqueIdentity);
                }
                foreach (string id in SharedBagRecovery.AffectedBagIdentities()) _automaticRepair.Targets.Add(id);
                foreach (var receipt in ManagerMemory.Current.ReadCharacterReceipts(ManagerAccounting.TransactionId, Client.CharacterName))
                    foreach (var item in (receipt.Before ?? new List<TransferItemState>()).Concat(receipt.Expected ?? new List<TransferItemState>()))
                        if (IsReserveBag(item)) _automaticRepair.Targets.Add(item.UniqueIdentity);
                foreach (var bag in own?.Bags.Where(b => b.Capacity == 0) ?? Enumerable.Empty<StorageBagState>())
                    _automaticRepair.Targets.Add(bag.LastUniqueIdentity);
                foreach (var bag in StorageBagPolicy.AllBagRecords().Where(b => own == null ||
                    !own.Bags.Any(known => known.LastUniqueIdentity == b.Identity.ToString())))
                    _automaticRepair.Targets.Add(bag.Identity.ToString());
                Logger.Warning("[CityBankers] AUTOMATIC RECOVERY " + Client.CharacterName +
                    "; affected containers=" + _automaticRepair.Targets.Count + "; reason=" + reason);
            }
            var repair = _automaticRepair;
            if (repair.Current != null)
            {
                var records = StorageBagPolicy.AllBagRecords().Where(b => b.Identity.ToString() == repair.Current).ToList();
                if (records.Count == 0)
                {
                    repair.Contents[repair.Current] = null;
                    repair.Targets.Remove(repair.Current); repair.Current = null;
                    return false;
                }
                // Aliases share one contents list. Read it once from a normal
                // inventory address; the duplicate repair removes empty aliases later.
                var record = records.OrderBy(r => r.Location == "inventory" ? 0 : 1).ThenBy(r => r.OuterSlot).First();
                if (repair.Phase == "banking")
                {
                    if (record.Location == "bank")
                    { repair.Targets.Remove(repair.Current); repair.Current = null; return false; }
                    if (repair.Age.ElapsedMilliseconds >= ServicePolicy.BagMoveTimeoutMs)
                    {
                        if (++repair.Attempts > 2)
                        { repair.Targets.Remove(repair.Current); repair.Current = null; return false; }
                        BagOriginTrace.MoveToBank(record.Bag); repair.Age.Restart();
                    }
                    return false;
                }
                if (record.Location == "bank")
                {
                    if (Inventory.NumFreeSlots < 1)
                    {
                        // Free a staging slot by returning a different healthy shell.
                        var parked = StorageBagPolicy.AllBagRecords().FirstOrDefault(r => r.Location == "inventory" &&
                            r.Identity != record.Identity && StorageBagPolicy.AllRecordsFor(r.Identity.ToString(), "inventory").Count == 1);
                        if (parked != null && Inventory.Bank.NumFreeSlots > 0) BagOriginTrace.MoveToBank(parked.Bag);
                        return false;
                    }
                    if (repair.Phase != "extracting" || repair.Age.ElapsedMilliseconds >= ServicePolicy.BagMoveTimeoutMs)
                    {
                        if (++repair.Attempts > 2)
                        { repair.Contents[repair.Current] = null; repair.Targets.Remove(repair.Current); repair.Current = null; return false; }
                        BagOriginTrace.MoveToInventory(record.Bag); repair.Phase = "extracting"; repair.Age.Restart();
                    }
                    return false;
                }
                int handle;
                List<BagInnerItem> nativeContents;
                if (SharedBagRecovery.TryReadContainer(record.Identity, out handle, out nativeContents))
                {
                    repair.Contents[repair.Current] = nativeContents;
                    if (repair.WasBanked)
                    {
                        BagOriginTrace.MoveToBank(record.Bag);
                        repair.Phase = "banking"; repair.Attempts = 0; repair.Age.Restart(); return false;
                    }
                    repair.Targets.Remove(repair.Current); repair.Current = null; return false;
                }
                if (repair.Phase != "opening")
                { record.Bag.Use(); repair.Phase = "opening"; repair.Age.Restart(); return false; }
                if (repair.Age.ElapsedMilliseconds < ServicePolicy.BagOpenTimeoutMs) return false;
                // Unreadable contents are unavailable claims, never an empty bag.
                // Keep the shell, exclude its capacity, and continue other work.
                repair.Contents[repair.Current] = null;
                if (repair.WasBanked)
                {
                    BagOriginTrace.MoveToBank(record.Bag);
                    repair.Phase = "banking"; repair.Attempts = 0; repair.Age.Restart(); return false;
                }
                repair.Targets.Remove(repair.Current); repair.Current = null; return false;
            }
            string next = repair.Targets.FirstOrDefault();
            if (next != null)
            {
                repair.Current = next; repair.Phase = null; repair.Attempts = 0; repair.Age.Restart();
                repair.WasBanked = StorageBagPolicy.AllRecordsFor(next, "inventory").Count == 0 &&
                    StorageBagPolicy.AllRecordsFor(next, "bank").Count > 0;
                return false;
            }
            ManagerAccounting.Transaction("Resolve affected banker from native inventory", () => ApplyAutomaticRepair(repair, reason));
            Logger.Information("[CityBankers] AUTOMATIC RECOVERY completed for " + Client.CharacterName +
                "; recorded discrepancies in lost/found; resuming work.");
            _automaticRepairOwner = null;
            if (_isCentral) _cruPreparationUnverified = false;
            return !_enabled || QuiesceForCensus("automatic-" + repair.Id);
        }

        private static bool SameCharacter(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static BagInnerItem RecoveryItem(Item item) => new BagInnerItem {
            LowId = item.Id, HighId = item.HighId, Ql = item.Ql, Name = item.Name,
            UniqueIdentity = item.UniqueIdentity.ToString(), SlotInstance = item.Slot.Instance & 65535 };

        private void ApplyAutomaticRepair(AutomaticRepair repair, string reason)
        {
            string character = Client.CharacterName;
            string transaction = ManagerAccounting.TransactionId;
            var manager = ManagerMemory.Current;
            var storage = manager.ReadStorage(transaction);
            var own = storage.Workers.SingleOrDefault(w => SameCharacter(w.Character, character)) ??
                new StorageWorkerState { Character = character, Role = _role };
            var ledger = manager.ReadLedger(transaction);
            var physical = StorageBagPolicy.AllBagRecords().GroupBy(b => b.Identity).Select(g =>
                g.OrderBy(b => b.Location == "inventory" ? 0 : 1).ThenBy(b => b.OuterSlot).First()).ToList();
            var previousBags = own.Bags.ToList();
            var touched = new HashSet<string>(repair.Contents.Keys, StringComparer.Ordinal);
            foreach (var bag in previousBags.Where(b => !physical.Any(p => p.Identity.ToString() == b.LastUniqueIdentity)))
                touched.Add(bag.LastUniqueIdentity);
            var previous = ledger.Items.Where(entry => SameCharacter(entry.Character, character) &&
                (!entry.Bag.HasValue || previousBags.Any(b => touched.Contains(b.LastUniqueIdentity) &&
                    b.Source == entry.Location && (b.OuterSlotInstance & 65535) == (entry.Bag.Value & 65535)))).ToList();
            // Freeze the scope BEFORE rebinding any old bag addresses.
            var coveredIds = new HashSet<string>(previous.Select(e => e.Id), StringComparer.Ordinal);
            Func<ActiveLedgerItem, bool> covered = entry => coveredIds.Contains(entry.Id);
            var observations = new List<PhysicalLedgerReconciliation.Observation>();
            Action<BagInnerItem, string, int?, string, StorageBagState> observe = (item, location, bag, id, old) => {
                if (CruPolicy.IsCru(item.LowId) || BankerPersonalItems.IsPersonal(item.LowId, item.HighId)) return;
                observations.Add(new PhysicalLedgerReconciliation.Observation {
                    Character = character, PhysicalRole = _role, Location = location, Bag = bag, BagIdentity = id,
                    PreviousBag = old?.OuterSlotInstance & 65535, PreviousLocation = old?.Source,
                    Slot = item.SlotInstance & 65535, Item = item, ObservedUtc = DateTime.UtcNow });
            };
            var loose = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                !StorageBagPolicy.IsStorageBag(i)).Concat(Inventory.Bank.Items.Where(i => i != null &&
                !StorageBagPolicy.IsStorageBag(i))).ToList();
            var rules = SymbiantCatalog.GetRulesFor(_settingsDir, loose.SelectMany(i => new[] { i.Id, i.HighId }));
            foreach (var item in loose)
                observe(RecoveryItem(item), item.Slot.Type == IdentityType.Inventory ? "inventory" : "bank", null, null, null);
            var rebuilt = new StorageWorkerState { Character = character, Role = _role, ObservedUtc = DateTime.UtcNow };
            foreach (var record in physical)
            {
                string id = record.Identity.ToString();
                var old = previousBags.FirstOrDefault(b => b.LastUniqueIdentity == id);
                List<BagInnerItem> items;
                bool read = repair.Contents.TryGetValue(id, out items);
                if (!read && old != null)
                {
                    foreach (var entry in ledger.Items.Where(e => !covered(e) && SameCharacter(e.Character, character) &&
                        e.Location == old.Source && e.Bag == old.OuterSlotInstance))
                    { entry.Location = record.Location; entry.Bag = record.OuterSlot; }
                    old.Source = record.Location; old.OuterSlotInstance = record.OuterSlot;
                    old.OuterSlotType = record.Bag.Slot.Type.ToString(); rebuilt.Bags.Add(old); continue;
                }
                rebuilt.Bags.Add(new StorageBagState { Source = record.Location, OuterSlotInstance = record.OuterSlot,
                    OuterSlotType = record.Bag.Slot.Type.ToString(), LastUniqueIdentity = id,
                    Capacity = items == null ? 0 : BankerRoles.SmallBackpackCapacity });
                if (items != null) foreach (var item in items) observe(item, record.Location, record.OuterSlot, id, old);
            }
            rules = SymbiantCatalog.GetRulesFor(_settingsDir, observations.SelectMany(o => new[] { o.Item.LowId, o.Item.HighId }));
            foreach (var observation in observations)
            {
                SymbiantCatalog.AcceptanceRule rule;
                observation.DestinationRole = rules.TryGetValue(observation.Item.LowId, out rule) ||
                    rules.TryGetValue(observation.Item.HighId, out rule) ? rule.Role : "central";
            }
            SharedBagRecovery.ApplyProvenance(_settingsDir, observations, previous);
            var damaged = manager.ReadBagRecovery(transaction, character);
            if (damaged != null)
                foreach (var transfer in damaged.Transfers.Where(t => t.VerifiedUtc != default(DateTime) && t.TargetSlot >= 0))
                {
                    var target = observations.Where(o => o.BagIdentity == new Identity(IdentityType.Container, transfer.TargetBag).ToString() &&
                        o.Slot == transfer.TargetSlot && o.Item.LowId == transfer.Item.Low &&
                        o.Item.HighId == transfer.Item.High && o.Item.Ql == transfer.Item.Ql).ToList();
                    if (target.Count == 1) target[0].RecoveryLedgerId = transfer.AnchorId;
                }
            var unreadableClaims = new HashSet<string>(previous.Where(e => e.Bag.HasValue &&
                previousBags.Any(b => b.Source == e.Location && b.OuterSlotInstance == e.Bag &&
                    repair.Contents.ContainsKey(b.LastUniqueIdentity) && repair.Contents[b.LastUniqueIdentity] == null))
                .Select(e => e.Id), StringComparer.Ordinal);
            // Unreadable contents do not participate in provenance inference.
            // Their claims become unavailable/lost; unrelated physical finds stay
            // unknown-origin instead of borrowing one of those donors.
            var plan = PhysicalLedgerReconciliation.Build(previous.Where(e => !unreadableClaims.Contains(e.Id)), observations, new[] { character });
            foreach (var entry in plan.Items)
                if (string.IsNullOrEmpty(entry.TransactionId)) entry.TransactionId = "found-" + entry.Id;
            foreach (var bag in rebuilt.Bags.Where(b => touched.Contains(b.LastUniqueIdentity) ||
                !previousBags.Any(old => old.LastUniqueIdentity == b.LastUniqueIdentity)))
                foreach (var entry in plan.Items.Where(e => e.Location == bag.Source && e.Bag == bag.OuterSlotInstance))
                {
                    var observed = observations.Single(o => o.Location == entry.Location && o.Bag == entry.Bag && o.Slot == entry.Slot).Item;
                    bag.Items.Add(new StoredItemState { AoId = entry.AoId, HighId = entry.HighId ?? entry.AoId,
                        Ql = entry.Ql ?? 0, InnerSlot = entry.Slot ?? 0, TransactionId = entry.TransactionId,
                        Name = observed.Name, UniqueIdentity = observed.UniqueIdentity, ObservedUtc = DateTime.UtcNow });
                }
            var merged = ledger.Items.Where(e => !covered(e)).Concat(plan.Items).ToList();
            // A freshly received shell can still be recorded against its sender
            // after a lost acknowledgement. Transfer the one physical bag owner,
            // rather than exposing its stock twice in the shared pool.
            foreach (var other in storage.Workers.Where(w => !SameCharacter(w.Character, character)))
            {
                var moved = other.Bags.Where(b => rebuilt.Bags.Any(now => now.LastUniqueIdentity == b.LastUniqueIdentity)).ToList();
                foreach (var bag in moved)
                {
                    var unavailable = merged.Where(e => SameCharacter(e.Character, other.Character) &&
                        e.Location == bag.Source && e.Bag == bag.OuterSlotInstance).ToList();
                    foreach (var entry in unavailable) merged.Remove(entry);
                    other.Bags.Remove(bag);
                }
            }
            manager.ChangeStorage(transaction, storage);
            RuntimeStorageStateTransactions.ReplaceCensusedWorker(_settingsDir, rebuilt);
            ActiveLedgerStore.ApplyCensus(_settingsDir, merged, observations.Select(o => new TransferItemState {
                AoId = o.Item.LowId, HighId = o.Item.HighId, Ql = o.Item.Ql, Name = o.Item.Name,
                UniqueIdentity = o.Item.UniqueIdentity }), "automatic-recovery:" + repair.Id,
                missingReason: "Claim unavailable after automatic native inventory recovery; cause unconfirmed. " + reason);
            foreach (var difference in plan.Differences.Where(d => d.Kind != "location-confirmed"))
                manager.RecordItemHistory(transaction, new ActiveHistoryRecord { LeftUtc = DateTime.UtcNow,
                    Reason = "automatic-" + difference.Kind, Item = difference.Previous, CurrentItem = difference.Current,
                    Source = "automatic-recovery:" + repair.Id, EventTimeKnown = false });
            var queue = manager.ReadDispatch(transaction);
            var batchIds = new HashSet<string>(new[] { _activeBatch?.BatchId, _workerCommand?.BatchId,
                _reservedDispatch?.BatchId, _storageJob?.Command?.BatchId }.Where(id => id != null));
            batchIds.UnionWith(_orphanedDispatchBatches);
            queue.Batches.RemoveAll(b => batchIds.Contains(b.BatchId) ||
                (_isCentral && (b.Status == "failed" || b.Status == "custody-hold" || b.Status == "retrying")) || (!_isCentral && SameCharacter(b.Character, character) &&
                b.Status != "queued" && b.Status != "completed" && b.Status != "stored"));
            if (_isCentral)
            {
                queue.Batches.RemoveAll(b => b.Status == "queued" && !IsReserveBatch(b.Items));
                var routingPlan = new PhysicalLedgerReconciliation.Plan { Items = merged.Where(e =>
                    observations.Any(o => !o.Bag.HasValue && o.Location == "inventory" && o.Slot == e.Slot &&
                        SameCharacter(o.Character, e.Character))).ToList() };
                var routing = CensusApplication.BuildQueue(routingPlan, observations,
                    _config.Roles.ToDictionary(p => p.Key, p => p.Value.Character, StringComparer.OrdinalIgnoreCase),
                    WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList(), "repair-" + repair.Id);
                queue.Batches.AddRange(routing.Batches);
            }
            var reserve = manager.ReadReserve(transaction);
            foreach (var bag in reserve.Bags.Where(b => b.Batch != null && !queue.Batches.Any(q => q.BatchId == b.Batch)))
            { bag.Batch = bag.Destination = bag.Role = null; bag.EmptyProofBatch = null; bag.Delivered = false; }
            foreach (var bag in reserve.Bags)
            {
                var native = rebuilt.Bags.FirstOrDefault(b => b.LastUniqueIdentity == bag.Identity);
                if (native == null) continue;
                List<BagInnerItem> verified;
                if (!repair.Contents.TryGetValue(bag.Identity, out verified) || verified == null) continue;
                bag.Quarantined = verified.Count != 0;
                if (!_isCentral && native.Source == "bank" && verified.Count == 0) bag.Delivered = true;
            }
            manager.ChangeReserve(transaction, reserve);
            manager.ChangeDispatch(transaction, queue);
            foreach (var pending in manager.ReadCharacterReturns(transaction, character, _isCentral))
            {
                bool physicalWork = pending.SenderReceiptVerified || pending.ReceiverReceiptVerified || HasUnresolvedReturnReceipt(pending);
                pending.Phase = "resolved-from-inventory";
                pending.CompletedUtc = DateTime.UtcNow;
                manager.ChangeReturn(transaction, pending);
                string peer = SameCharacter(pending.Source, character) ? ReturnDestination(pending) : pending.Source;
                if (physicalWork && !SameCharacter(peer, character)) manager.RequestBankerRecovery(peer,
                    "Reconcile interrupted return " + pending.Id + " from native custody.");
            }
            foreach (var pending in manager.ReadCharacterExtractions(transaction, character))
            { pending.Phase = "completed"; manager.ChangeExtraction(transaction, pending); }
            var operation = manager.ReadReserveOperation(transaction, character);
            if (operation != null) { operation.Phase = "completed"; manager.ChangeReserveOperation(transaction, operation); }
            var requests = manager.ReadWithdrawals(transaction);
            foreach (var request in requests.Withdrawals.Where(w => WithdrawalStore.IsActive(w) &&
                !WithdrawalStore.HasConfirmedDelivery(w) && (w.Id == _withdrawal?.Id ||
                (SameCharacter(w.SourceCharacter, character) && w.Status != "requested"))))
            { request.Status = "cancelled"; request.Error = "Resolved from native inventory after interrupted operation."; WithdrawalStore.Touch(request); }
            manager.ChangeWithdrawals(transaction, requests);
            var reservations = manager.ReadReservations(transaction);
            reservations.RemoveAll(r => SameCharacter(r.CensusCharacter, character) || previous.Any(e => e.Id == r.LedgerId));
            manager.ChangeReservations(transaction, reservations);
            manager.ChangeDispatchCommand(transaction, character, null);
            manager.ChangeStorageResult(transaction, character, null);
            manager.RecordTransaction(transaction, new LedgerRecord { Id = "repair-" + repair.Id, Utc = DateTime.UtcNow,
                Event = "automatic_recovery", Actor = character, Character = character, Role = _role,
                Message = reason + "; pending operations resolved from native state; unmatched claims preserved in lost/found." });
            foreach (var receipt in manager.ReadCharacterReceipts(transaction, character))
                CityDwellers.Shared.IncidentJournal.Record(RuntimeStateStore.GetDataDirectory(_settingsDir),
                    receipt.TransactionId ?? receipt.Id, character, "receipt.resolved-native", receipt, true);
            manager.RetireCharacterReceipts(transaction, character);
        }
    }
}
