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
        private sealed class BagReserve
        {
            public List<ReserveBag> Bags = new List<ReserveBag>();
            public Dictionary<string, int> Targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        private sealed class ReserveBag
        {
            public string Identity, Transaction, Destination, Role, Batch;
            public bool Quarantined, Delivered;
        }
        private sealed class ReserveOperation
        {
            public string Identity, Purpose, Phase;
            public int SourceSlot;
            public DateTime StartedUtc;
            [JsonIgnore] public Container Before;
        }
        private ReserveOperation _reserveOperation;
        private readonly Stopwatch _reservePoll = Stopwatch.StartNew();
        private string ReservePath => Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir), "bag-recovery", "central-reserve.json");
        private string ReserveMovePath => Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir),
            "bag-recovery", Client.CharacterName.ToLowerInvariant(), "reserve-move.json");
        private BagReserve ReadReserve() => CensusApplication.ReadExisting<BagReserve>(ReservePath) ?? new BagReserve();
        private void SaveReserve(BagReserve state) => RuntimeStateStore.WriteJsonAtomic(ReservePath, state);
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
            return state.Targets.TryGetValue(character, out target) ? target : 0;
        }

        private void CaptureReserveTargets(BagReserve state, StorageState storage, HashSet<string> ready)
        {
            bool changed = false;
            foreach (var worker in storage.Workers.Where(w => !string.Equals(w.Role, "central", StringComparison.OrdinalIgnoreCase) &&
                ready.Contains(w.Character)))
            {
                int actual = worker.Bags.Select(b => b.LastUniqueIdentity).Distinct().Count();
                int target;
                if (!state.Targets.TryGetValue(worker.Character, out target))
                {
                    // Bootstrap the just-completed repair without filling the gap
                    // between historical stock and theoretical retention capacity.
                    string history = Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir), "bag-recovery",
                        worker.Character.ToLowerInvariant(), "history");
                    int deleted = Directory.Exists(history) ? Directory.EnumerateFiles(history, "*.json")
                        .Select(f => CensusApplication.ReadExisting<SharedBagRecovery.State>(f))
                        .Where(r => r != null && r.EmptyShellsDisappeared && r.Phase == "complete")
                        .Select(r => r.Bag).Distinct().Count() : 0;
                    target = Math.Max(actual, Math.Min(RetentionBagCapacity(worker.Role), actual + deleted));
                    state.Targets[worker.Character] = target; changed = true;
                }
                else if (actual > target)
                { state.Targets[worker.Character] = actual; changed = true; }
            }
            if (changed) SaveReserve(state);
        }

        private bool TickWorkerReserveRecovery()
        {
            if (_isCentral || !CanStartLocalCensus() || CensusReservedHere() || _reservePoll.ElapsedMilliseconds < 1500) return false;
            _reservePoll.Restart();
            var reserve = ReadReserve();
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            var known = storage?.Workers?.SingleOrDefault(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            var candidate = StorageBagPolicy.AllBagRecords().FirstOrDefault(r => r.Location == "inventory" &&
                known?.Bags?.Any(b => b.LastUniqueIdentity == r.Identity.ToString() && b.Items?.Count == 0) == true &&
                reserve.Bags.Any(b => !b.Quarantined && b.Identity == r.Identity.ToString() &&
                    string.Equals(b.Destination, Client.CharacterName, StringComparison.OrdinalIgnoreCase)));
            if (candidate == null || Inventory.Bank.NumFreeSlots < 1) return false;
            // Restart census already established custody; complete the interrupted
            // outer placement, without requesting or receiving another bag.
            BeginReserveOperation(candidate.Identity.ToString(), "bank");
            return true;
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
            if (!IsReserveBatch(command.Items) || Inventory.Bank.NumFreeSlots < 1 ||
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
                    if (Inventory.Bank.NumFreeSlots < 1) continue;
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

        private void BeginReserveOperation(string identity, string purpose)
        {
            _reserveOperation = new ReserveOperation { Identity = identity, Purpose = purpose, Phase = "start", StartedUtc = DateTime.UtcNow };
            SaveReserveOperation();
        }
        private void SaveReserveOperation() => RuntimeStateStore.WriteJsonAtomic(ReserveMovePath, _reserveOperation);
        private void ReservePhase(string phase, Item source)
        {
            _reserveOperation.Phase = phase; _reserveOperation.SourceSlot = source.Slot.Instance;
            _reserveOperation.StartedUtc = DateTime.UtcNow; SaveReserveOperation();
        }
        private bool TickReserveOperation()
        {
            var op = _reserveOperation;
            if (op == null) return false;
            if (Trade.IsTrading || !Inventory.Bank.IsOpen) return true;
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error)) throw new InvalidOperationException(error);
            var records = StorageBagPolicy.AllBagRecords().Where(r => r.Identity.ToString() == op.Identity).ToList();
            if ((DateTime.UtcNow - op.StartedUtc).TotalSeconds > 20)
            {
                _reserveOperation = null;
                StartupCensusGate.Block("Reserve bag operation timed out; reconcile its exact identity before retry: " + op.Identity + " / " + op.Phase);
                return true;
            }
            if (records.Count == 0 && (op.Phase == "extracting" || op.Phase == "banking")) return true;
            if (records.Count != 1) throw new InvalidOperationException("Reserve bag has ambiguous physical location: " + op.Identity);
            var record = records[0];
            if (op.Phase == "start" && record.Location == "bank")
            {
                if (Inventory.NumFreeSlots < 1) return true;
                ReservePhase("extracting", record.Bag); record.Bag.MoveToInventory(); return true;
            }
            if (op.Phase == "extracting" && record.Location != "inventory") return true;
            if (op.Phase == "start" || op.Phase == "extracting")
            {
                op.Before = Inventory.Containers.FirstOrDefault(c => c.Identity == record.Identity);
                // A restart census has already opened inventory bags. Reusing its
                // exact empty result avoids toggling that same window closed.
                var known = RuntimeStateStore.LoadStorageState(_settingsDir)?.Workers?.Where(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(w => w.Bags).SingleOrDefault(b => b.LastUniqueIdentity == op.Identity &&
                        b.Source == "inventory" && b.OuterSlotInstance == (record.Bag.Slot.Instance & 65535));
                if (known?.Items?.Count == 0 && op.Before != null && op.Before.IsOpen &&
                    op.Before.Handle == known.LastHandle && op.Before.Items?.Count == 0)
                { ReservePhase("verified-empty", record.Bag); return true; }
                ReservePhase("opening", record.Bag); record.Bag.Use(); return true;
            }
            if (op.Phase == "opening" || op.Phase == "verified-empty")
            {
                if (record.Location != "inventory" || record.Bag.Slot.Instance != op.SourceSlot)
                    throw new InvalidOperationException("Reserve bag moved while awaiting its contents.");
                var container = Inventory.Containers.FirstOrDefault(c => c.Identity == record.Identity);
                if (container == null || !container.IsOpen ||
                    (op.Phase == "opening" && ReferenceEquals(container, op.Before))) return true;
                if (container.Items == null) return true;
                if (container.Items.Count != 0)
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
                if (Inventory.Bank.NumFreeSlots < 1) return true;
                ReservePhase("banking", record.Bag); record.Bag.MoveToBank(); return true;
            }
            if (op.Phase == "banking" && record.Location == "bank")
            {
                RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity,
                    new StorageBagState { Source = "bank", OuterSlotType = record.Bag.Slot.Type.ToString(),
                        OuterSlotInstance = record.Bag.Slot.Instance & 65535, LastUniqueIdentity = op.Identity,
                        Capacity = 21, Items = new List<StoredItemState>() });
                op.Phase = "completed"; SaveReserveOperation(); _reserveOperation = null;
                if (!_isCentral && _storageJob != null && IsReserveBatch(_storageJob.Command.Items))
                { _storageJob.StoredCount = 1; _storageJob.Index = 1; CompleteStorageJob(); }
                Logger.Information("[CityBankers] EMPTY BAG RESERVE banked " + op.Identity + " on " + Client.CharacterName);
                return true;
            }
            return true;
        }
    }
}
