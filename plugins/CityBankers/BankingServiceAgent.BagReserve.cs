using Directory = System.IO.Directory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        // Central is the sole writer. Entries are enrolled before accepting an
        // offer, so a crash between physical receipt and disposition loses no bag.
        private long _reserveReceiptObservationAfter;
        private readonly Dictionary<string, long> _reserveArrivalAfter = new Dictionary<string, long>();
        private readonly HashSet<string> _reserveEmptyArrivals = new HashSet<string>();
        private ReserveOperation _reserveOperation;
        private Container _reserveBefore;
        private readonly Stopwatch _reservePoll = Stopwatch.StartNew();
        private string ReservePath => Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir), "bag-recovery", "central-reserve.json");
        private string ReserveMovePath => Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir),
            "bag-recovery", Client.CharacterName.ToLowerInvariant(), "reserve-move.json");
        // The supplied SDK assumes 104 slots; the deployed AO bank fills at
        // 102. Never let those two fictitious slots authorize reserve movement.
        private static int ReserveBankFreeSlots => !Inventory.Bank.IsOpen || Inventory.Bank.Items == null ? 0 :
            Math.Max(0, Math.Min(Inventory.Bank.NumFreeSlots, 102 - Inventory.Bank.Items.Count));
        private BagReserve ReadReserve() => CityDwellers.Shared.ManagerMemory.Current.ReadReserve(CityDwellers.Shared.ManagerAccounting.TransactionId);
        private void SaveReserve(BagReserve state) => CityDwellers.Shared.ManagerAccounting.Transaction("Reserve bags", () =>
            CityDwellers.Shared.ManagerMemory.Current.ChangeReserve(CityDwellers.Shared.ManagerAccounting.TransactionId, state));
        private static bool IsReserveBag(TransferItemState item) => item != null &&
            (item.AoId == StorageBagPolicy.SmallBackpackId || item.HighId == StorageBagPolicy.SmallBackpackId);
        private static bool IsReserveBatch(IEnumerable<TransferItemState> items) =>
            items != null && items.Count() == 1 && items.All(IsReserveBag);
        private int RetentionBagCapacity(string role) => (SymbiantCatalog.GetRetentionRules(_settingsDir)
            .Where(r => string.Equals(r.Role, role, StringComparison.OrdinalIgnoreCase) && r.MaxCopies > 0)
            .Sum(r => r.MaxCopies) + 20) / 21;

        private static int ReserveTarget(BagReserve state, string character)
        {
            int target;
            return state.TryGetTarget(character, out target) ? target : 0;
        }

        private void CaptureReserveTargets(BagReserve state, StorageState storage, HashSet<string> ready)
        {
            bool changed = false;
            foreach (var worker in storage.Workers.Where(w => !string.Equals(w.Role, "central", StringComparison.OrdinalIgnoreCase) &&
                ready.Contains(w.Character)))
            {
                int actual = worker.Bags.Select(b => b.LastUniqueIdentity).Distinct().Count();
                int target;
                if (!state.TryGetTarget(worker.Character, out target))
                {
                    // Bootstrap the just-completed repair without filling the gap
                    // between historical stock and theoretical retention capacity.
                    int deleted = CityDwellers.Shared.ManagerMemory.Current.ReadBagHistory(
                        CityDwellers.Shared.ManagerAccounting.TransactionId, worker.Character)
                        .Where(row => row.EmptyShellsDisappeared && row.Phase == "complete").Select(row => row.Bag).Distinct().Count();
                    target = Math.Max(actual, Math.Min(RetentionBagCapacity(worker.Role), actual + deleted));
                    state.SetTarget(worker.Character, target); changed = true;
                }
                else if (actual > target)
                { state.SetTarget(worker.Character, actual); changed = true; }
            }
            if (changed) SaveReserve(state);
        }

        private void EnrollReserveOffer()
        {
            var bags = _donationSnapshot.Where(IsReserveBag).ToList();
            if (bags.Count == 0) return;
            if (!TrustedOperators.IsTrustedAdmin(_donationPartnerName))
                throw new InvalidOperationException("Only Kavem can supply reserve bags.");
            var state = ReadReserve();
            foreach (var bag in bags)
            {
                if (!IsUsableIdentity(bag.UniqueIdentity) || _receipt.Before.Any(i => i.UniqueIdentity == bag.UniqueIdentity))
                    throw new InvalidOperationException("Reserve donation lacks a new container identity.");
                if (!state.Bags.Any(b => b.Identity == bag.UniqueIdentity))
                    state.Bags.Add(new ReserveBag { Identity = bag.UniqueIdentity, Transaction = _donationTransactionId });
            }
            SaveReserve(state);
        }

        private bool ReserveWorkerReady(DispatchCommand command)
        {
            if (string.Equals(_role, "spirit", StringComparison.OrdinalIgnoreCase) || !IsReserveBatch(command.Items) ||
                StorageBagPolicy.DuplicatedIdentities().Count != 0 ||
                StorageBagPolicy.DistinctBags().Count >= ReserveTarget(ReadReserve(), Client.CharacterName)) return false;
            var item = command.Items[0];
            return ReadReserve().Bags.Any(b => !b.Delivered && !b.Quarantined && b.Identity == item.UniqueIdentity &&
                b.Transaction == command.TransactionId && string.Equals(b.Destination, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
        }

        // Called only while this actor is otherwise idle. Never steal an existing
        // Central storage bag: membership is limited to Kavem's accepted offers.
        private bool TickBagReserve()
        {
            if (!_isCentral || !CanStartLocalCensus() || CensusReservedHere() ||
                _reservePoll.ElapsedMilliseconds < 1500) return false;
            _reservePoll.Restart();
            var state = ReadReserve();
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (storage?.Workers == null) return false;
            var ready = BankerReadiness.GetReadyCharacters(_settingsDir);
            CaptureReserveTargets(state, storage, ready);
            if (state.Bags.Count == 0) return false;
            var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            var records = StorageBagPolicy.AllBagRecords();
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error))
            { StartupCensusGate.Block(error); return true; }
            var censusing = WithdrawalStore.GetCensusCharacters(_settingsDir);
            foreach (var bag in state.Bags.Where(b => !b.Delivered && !b.Quarantined))
            {
                // A queued/retrying transfer owns its bag until receipt/census
                // settles it. Never bank its offer or create a second assignment.
                if (queue.Batches.Any(b => (b.Items ?? new List<TransferItemState>()).Any(i => i.UniqueIdentity == bag.Identity))) continue;
                var holders = storage.Workers.Where(w => w.Bags.Any(b => b.LastUniqueIdentity == bag.Identity)).ToList();
                if (bag.Destination != null && holders.Count == 1 &&
                    string.Equals(holders[0].Character, bag.Destination, StringComparison.OrdinalIgnoreCase))
                {
                    bag.Delivered = true; SaveReserve(state);
                    Logger.Information("[CityBankers] EMPTY BAG RESERVE replacement registered on " + bag.Destination + "; " + bag.Identity);
                    continue;
                }
                var own = records.SingleOrDefault(r => r.Identity.ToString() == bag.Identity);
                if (own == null) continue; // cancelled donation or unresolved custody: no invented stock
                if (bag.Destination != null)
                {
                    // Only a complete census can retire a lost dispatch queue.
                    // Re-evaluate need from that census before recreating an offer.
                    bag.Destination = null; bag.Role = null; bag.Batch = null; SaveReserve(state);
                }
                if (own.Location == "inventory")
                {
                    var known = storage.Workers.Where(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(w => w.Bags).SingleOrDefault(b => b.LastUniqueIdentity == bag.Identity);
                    if (known?.Items?.Count > 0)
                    {
                        bag.Quarantined = true; SaveReserve(state);
                        TellKavem("Reserve bag " + bag.Identity + " contains items and is excluded from replacement stock.");
                        continue;
                    }
                    if (ReserveBankFreeSlots < 1) continue;
                    BeginReserveOperation(bag.Identity, "bank"); return true;
                }
            }
            if (queue.Batches.Any(b => IsReserveBatch(b.Items))) return false; // one replacement at a time
            var candidate = state.Bags.FirstOrDefault(b => !b.Delivered && !b.Quarantined && b.Destination == null &&
                records.Count(r => r.Identity.ToString() == b.Identity && r.Location == "bank") == 1 &&
                storage.Workers.Where(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(w => w.Bags).Any(s => s.LastUniqueIdentity == b.Identity && s.Source == "bank" && s.Items?.Count == 0));
            if (candidate == null || Inventory.NumFreeSlots < 2) return false;
            var target = storage.Workers.Where(w => !string.Equals(w.Role, "central", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(w.Role, "spirit", StringComparison.OrdinalIgnoreCase) &&
                    _config.Roles.Any(p => p.Key == w.Role && string.Equals(p.Value.Character, w.Character, StringComparison.OrdinalIgnoreCase)) &&
                    ready.Contains(w.Character) &&
                    !censusing.Contains(w.Character) && !queue.Batches.Any(b => string.Equals(b.Character, w.Character, StringComparison.OrdinalIgnoreCase)) &&
                    DynelManager.Players.Any(p => p != null && string.Equals(p.Name, w.Character, StringComparison.OrdinalIgnoreCase)))
                .Select(w => new { Worker = w, Missing = ReserveTarget(state, w.Character) - w.Bags.Select(b => b.LastUniqueIdentity).Distinct().Count() })
                .Where(w => w.Missing > 0).OrderBy(w => w.Missing).ThenBy(w => w.Worker.Character).FirstOrDefault();
            if (target == null) return false;
            candidate.Destination = target.Worker.Character; candidate.Role = target.Worker.Role;
            candidate.Batch = "bag-reserve-" + Guid.NewGuid().ToString("N");
            SaveReserve(state); // reservation precedes extraction
            BeginReserveOperation(candidate.Identity, "dispatch");
            return true;
        }

        private void RememberReserveArrival(IEnumerable<TransferItemState> items, DispatchCommand command = null)
        {
            foreach (var item in items.Where(IsReserveBag))
            {
                _reserveArrivalAfter[item.UniqueIdentity] = _reserveReceiptObservationAfter;
                // This path runs ONLY after exact physical receipt. Central owns
                // the closed bag exclusively from empty verification to dispatch;
                // transferring that same container does not add contents to it.
                if (command != null && ReadReserve().Bags.Any(b => b.Identity == item.UniqueIdentity &&
                    b.EmptyProofBatch == command.BatchId && b.Transaction == command.TransactionId &&
                    !b.Quarantined && !b.Delivered && string.Equals(b.Destination, Client.CharacterName, StringComparison.OrdinalIgnoreCase)))
                    _reserveEmptyArrivals.Add(item.UniqueIdentity);
            }
        }

        private void BindReserveProofToAttempt(List<TransferItemState> items, string batch, string transaction)
        {
            if (!IsReserveBatch(items)) return;
            var reserve = ReadReserve();
            var bag = reserve.Bags.Single(b => b.Identity == items[0].UniqueIdentity);
            if (string.IsNullOrEmpty(bag.EmptyProofBatch) || bag.Quarantined || bag.Delivered || bag.Transaction != transaction ||
                !string.Equals(bag.Destination, _activeBatch.Character, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reserve dispatch has no matching empty source proof.");
            // A cancelled trade may obtain a new batch id. The existing physical
            // source check and paired cancellation keep the same bag in custody.
            bag.EmptyProofBatch = batch; SaveReserve(reserve);
        }

        private void BeginReserveOperation(string identity, string purpose)
        {
            long after;
            if (!_reserveArrivalAfter.TryGetValue(identity, out after)) after = SharedBagRecovery.ContainerObservationSequence;
            _reserveArrivalAfter.Remove(identity);
            _reserveBefore = null;
            _reserveOperation = new ReserveOperation { Identity = identity, Purpose = purpose, Phase = "start", StartedUtc = DateTime.UtcNow,
                ObservationAfter = after, SenderVerifiedEmpty = _reserveEmptyArrivals.Remove(identity) };
            SaveReserveOperation();
        }
        private void SaveReserveOperation()
        {
            _reserveOperation.Character = Client.CharacterName;
            CityDwellers.Shared.ManagerAccounting.Transaction("Reserve movement", () =>
                CityDwellers.Shared.ManagerMemory.Current.ChangeReserveOperation(CityDwellers.Shared.ManagerAccounting.TransactionId, _reserveOperation));
        }
        private void ReservePhase(string phase, Item source)
        {
            _reserveOperation.Phase = phase; _reserveOperation.SourceSlot = source.Slot.Instance;
            _reserveOperation.StartedUtc = DateTime.UtcNow; SaveReserveOperation();
        }
        private void RegisterReservePlacement(ReserveOperation op, StorageBagPolicy.BagRecord record, string location, int handle)
        {
            RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity,
                new StorageBagState { Source = location, OuterSlotType = record.Bag.Slot.Type.ToString(),
                    OuterSlotInstance = record.Bag.Slot.Instance & 65535, LastUniqueIdentity = op.Identity,
                    LastHandle = handle, Capacity = 21, Items = new List<StoredItemState>() });
            op.Phase = "completed"; SaveReserveOperation(); _reserveOperation = null;
            if (!_isCentral && _storageJob != null && IsReserveBatch(_storageJob.Command.Items))
            { _storageJob.StoredCount = 1; _storageJob.Index = 1; CompleteStorageJob(); }
            Logger.Information("[CityBankers] EMPTY BAG RESERVE registered " + op.Identity + " on " +
                Client.CharacterName + "; location=" + location + ".");
        }

        private bool TickReserveOperation()
        {
            var op = _reserveOperation;
            if (op == null) return false;
            if (Trade.IsTrading || !Inventory.Bank.IsOpen) return true;
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error)) throw new InvalidOperationException(error);
            var records = StorageBagPolicy.AllBagRecords().Where(r => r.Identity.ToString() == op.Identity).ToList();
            if (op.Phase != "read-backoff" && (DateTime.UtcNow - op.StartedUtc).TotalSeconds > 20)
            {
                if (op.Phase == "opening" && records.Count == 1 && records[0].Location == "inventory" &&
                    records[0].Bag.Slot.Instance == op.SourceSlot)
                {
                    op.ReadRetries = Math.Min(7, op.ReadRetries + 1);
                    var current = Inventory.Containers.FirstOrDefault(c => c.Identity == records[0].Identity);
                    Logger.Warning("[CityBankers] EMPTY BAG RESERVE read retry " + op.Identity +
                        "; attempt=" + op.ReadRetries + "; beforeHandle=" + (_reserveBefore?.Handle ?? 0) +
                        "; currentHandle=" + (current?.Handle ?? 0) + "; observationAfter=" + op.ObservationAfter +
                        ". Only this bag is held; no roster census requested.");
                    ReservePhase("read-backoff", records[0].Bag);
                    return true;
                }
                _reserveOperation = null;
                StartupCensusGate.Block("Reserve bag movement remains unverified; reconcile its exact identity: " + op.Identity + " / " + op.Phase);
                return true;
            }
            if (records.Count == 0 && (op.Phase == "extracting" || op.Phase == "banking" || op.Phase == "refresh-banking")) return true;
            if (records.Count != 1) throw new InvalidOperationException("Reserve bag has ambiguous physical location: " + op.Identity);
            var record = records[0];
            if (op.Phase == "read-backoff")
            {
                if (record.Location != "inventory" || record.Bag.Slot.Instance != op.SourceSlot)
                    throw new InvalidOperationException("Held reserve bag changed location outside its operation.");
                int lateHandle, lateCount;
                if (SharedBagRecovery.TryObserveContainer(record.Identity, op.ObservationAfter, out lateHandle, out lateCount))
                { ReservePhase("opening", record.Bag); return true; }
                if ((DateTime.UtcNow - op.StartedUtc).TotalSeconds < Math.Min(960, 5 * (1 << op.ReadRetries))) return true;
                if (ReserveBankFreeSlots < 1) return true;
                // Re-stage only this container to obtain another server read.
                // Contents remain untouched, even when emptiness is still unknown.
                op.ObservationAfter = SharedBagRecovery.ContainerObservationSequence;
                ReservePhase("refresh-banking", record.Bag); record.Bag.MoveToBank(); return true;
            }
            if (op.Phase == "refresh-banking")
            {
                if (record.Location != "bank") return true;
                // Keep the boundary from before the round trip: banking itself
                // may already have supplied the contents of this closed bag.
                ReservePhase("extracting", record.Bag); record.Bag.MoveToInventory(); return true;
            }
            if (op.Phase == "start" && record.Location == "bank")
            {
                if (Inventory.NumFreeSlots < 1) return true;
                op.ObservationAfter = SharedBagRecovery.ContainerObservationSequence;
                ReservePhase("extracting", record.Bag); record.Bag.MoveToInventory(); return true;
            }
            if (op.Phase == "extracting" && record.Location != "inventory") return true;
            if (op.Phase == "start" || op.Phase == "extracting")
            {
                int arrivedHandle, arrivedCount;
                if (op.SenderVerifiedEmpty || SharedBagRecovery.TryObserveContainer(record.Identity, op.ObservationAfter, out arrivedHandle, out arrivedCount))
                {
                    Logger.Information("[CityBankers] EMPTY BAG RESERVE contents evidence available for " + op.Identity +
                        (op.SenderVerifiedEmpty ? "; source empty proof and exact receipt." : "; incoming snapshot since receipt/move boundary."));
                    ReservePhase("opening", record.Bag); return true; // validate without toggling Use
                }
                _reserveBefore = Inventory.Containers.FirstOrDefault(c => c.Identity == record.Identity);
                // A restart census has already opened inventory bags. Reusing its
                // exact empty result avoids toggling that same window closed.
                var known = RuntimeStateStore.LoadStorageState(_settingsDir)?.Workers?.Where(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(w => w.Bags).SingleOrDefault(b => b.LastUniqueIdentity == op.Identity &&
                        b.Source == "inventory" && b.OuterSlotInstance == (record.Bag.Slot.Instance & 65535));
                if (known?.Items?.Count == 0 && _reserveBefore != null && _reserveBefore.IsOpen &&
                    _reserveBefore.Handle == known.LastHandle && _reserveBefore.Items?.Count == 0)
                { ReservePhase("verified-empty", record.Bag); return true; }
                Logger.Information("[CityBankers] EMPTY BAG RESERVE requesting contents " + op.Identity +
                    "; slot=" + record.Bag.Slot + "; beforeHandle=" + (_reserveBefore?.Handle ?? 0) +
                    "; observationAfter=" + op.ObservationAfter + ".");
                ReservePhase("opening", record.Bag); record.Bag.Use(); return true;
            }
            if (op.Phase == "opening" || op.Phase == "verified-empty")
            {
                if (record.Location != "inventory" || record.Bag.Slot.Instance != op.SourceSlot)
                    throw new InvalidOperationException("Reserve bag moved while awaiting its contents.");
                var container = Inventory.Containers.FirstOrDefault(c => c.Identity == record.Identity);
                int observedHandle, observedCount;
                bool incoming = SharedBagRecovery.TryObserveContainer(record.Identity, op.ObservationAfter, out observedHandle, out observedCount);
                bool census = op.Phase == "verified-empty" && container != null && container.IsOpen &&
                    ReferenceEquals(container, _reserveBefore) && container.Items?.Count == 0;
                if (!incoming && !census && !op.SenderVerifiedEmpty) return true;
                if ((incoming && observedCount != 0) || (container?.Items?.Count > 0))
                {
                    if (_isCentral)
                    {
                        var state = ReadReserve(); var bag = state.Bags.Single(b => b.Identity == op.Identity);
                        bag.Quarantined = true; SaveReserve(state);
                        TellKavem("Reserve bag " + op.Identity + " is not empty. It is retained and excluded from replacements.");
                    }
                    _reserveOperation = null;
                    StartupCensusGate.Block("Reserve bag contains items; retain contents and reconcile before further operations.");
                    return true;
                }
                if (op.Purpose == "dispatch")
                {
                    var state = ReadReserve(); var bag = state.Bags.Single(b => b.Identity == op.Identity);
                    bag.EmptyProofBatch = bag.Batch; SaveReserve(state); // empty source proof precedes queue/trade
                    var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                    if (!queue.Batches.Any(b => b.BatchId == bag.Batch))
                    {
                        queue.Batches.Add(new DispatchBatchState { BatchId = bag.Batch, TransactionId = bag.Transaction,
                            Character = bag.Destination, Role = bag.Role, Status = "queued", CreatedUtc = DateTime.UtcNow,
                            UpdatedUtc = DateTime.UtcNow, Items = SnapshotTradeItems(new[] { record.Bag }) });
                        RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                    }
                    RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity, null);
                    op.Phase = "completed"; SaveReserveOperation(); _reserveOperation = null;
                    Logger.Information("[CityBankers] EMPTY BAG RESERVE queued " + op.Identity + " -> " + bag.Destination);
                    return true;
                }
                // Replacement delivery is complete in normal inventory. Workers
                // never need an extra bank move; only Central banks reserve stock.
                if (!_isCentral)
                {
                    RegisterReservePlacement(op, record, "inventory", incoming ? observedHandle : (container?.Handle ?? 0));
                    return true;
                }
                if (ReserveBankFreeSlots < 1) return true;
                ReservePhase("banking", record.Bag); record.Bag.MoveToBank(); return true;
            }
            if (op.Phase == "banking" && record.Location == "bank")
            {
                RegisterReservePlacement(op, record, "bank", 0);
                return true;
            }
            return true;
        }
    }
}
