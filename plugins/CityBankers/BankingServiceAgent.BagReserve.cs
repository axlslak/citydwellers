using Directory = System.IO.Directory;
using System;
using System.Collections.Generic;
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
        private bool _reserveResumeChecked;
        private Container _reserveBefore;
        private readonly HashSet<string> _supplyMoves = new HashSet<string>();
        private readonly Dictionary<string, DateTime> _supplyMoveStartedUtc = new Dictionary<string, DateTime>();
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
            items != null && items.Any() && items.Count() <= ServicePolicy.MaxInternalTradeItems &&
            items.All(i => IsReserveBag(i) && i.Quantity == 1) &&
            items.Select(i => i.UniqueIdentity).Distinct().Count() == items.Count();
        private static int ReserveTarget(BagReserve state, string character)
        {
            int target;
            return state.TryGetTarget(character, out target) ? target : 0;
        }

        private void CaptureReserveTargets(BagReserve state, StorageState storage, HashSet<string> ready)
        {
            bool changed = false;
            foreach (var worker in storage.Workers.Where(w => ready.Contains(w.Character)))
            {
                if (ReserveTarget(state, worker.Character) == StorageBankBagTarget) continue;
                state.SetTarget(worker.Character, StorageBankBagTarget); changed = true;
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
            if (!IsReserveBatch(command.Items) || ReserveBankFreeSlots < command.Items.Count ||
                StorageBagPolicy.DuplicatedIdentities().Count != 0 ||
                StorageBagPolicy.DistinctBags().Count + command.Items.Count > ReserveTarget(ReadReserve(), Client.CharacterName)) return false;
            return command.Items.All(item => ReadReserve().Bags.Any(b => !b.Delivered && !b.Quarantined &&
                b.Identity == item.UniqueIdentity && b.Batch == command.BatchId &&
                b.Transaction == command.TransactionId && string.Equals(b.Destination, Client.CharacterName, StringComparison.OrdinalIgnoreCase)));
        }

        // Bags are storage equipment. Supply existing surplus first, then
        // Central's empty bank reserve, and only then buy another shell.
        private bool TickBagReserve()
        {
            if (!_isCentral || !CanStartLocalCensus() || CensusReservedHere()) return false;
            var state = ReadReserve();
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (storage?.Workers == null) return false;
            var ready = BankerReadiness.GetReadyCharacters(_settingsDir);
            CaptureReserveTargets(state, storage, ready);
            var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            var records = StorageBagPolicy.AllBagRecords();
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error))
            { StartupCensusGate.Block(error); return true; }
            var preparing = state.Bags.FirstOrDefault(b => !b.Delivered && !b.Quarantined && b.Destination != null &&
                !queue.Batches.Any(q => q.BatchId == b.Batch) &&
                records.Any(r => r.Identity.ToString() == b.Identity));
            if (preparing != null)
            {
                int banked = records.Count(record => record.Location == "bank" && state.Bags.Any(bag =>
                    bag.Batch == preparing.Batch && bag.Identity == record.Identity.ToString()));
                if (Inventory.NumFreeSlots < banked) return false;
                BeginReserveOperation(preparing.Identity, "dispatch"); return true;
            }
            var censusing = WithdrawalStore.GetCensusCharacters(_settingsDir);
            var target = storage.Workers.Where(w => w.Role != "central" &&
                    _config.Roles.Any(p => CityDwellers.Shared.BankerRoles.Canonical(p.Key) == w.Role &&
                        string.Equals(p.Value.Character, w.Character, StringComparison.OrdinalIgnoreCase)) &&
                    ready.Contains(w.Character) && !censusing.Contains(w.Character) && !w.Bags.Any(b => b.Source == "inventory") &&
                    !storage.Workers.Any(source => source.Character != w.Character && ready.Contains(source.Character) &&
                        source.Bags.Count > StorageBankBagTarget && source.Bags.Any(b => b.Source == "inventory") &&
                        string.Equals(LayoutDestination(source.Character), w.Character, StringComparison.OrdinalIgnoreCase)) &&
                    !queue.Batches.Any(b => IsReserveBatch(b.Items) && string.Equals(b.Character, w.Character, StringComparison.OrdinalIgnoreCase)) &&
                    DynelManager.Players.Any(p => p != null && string.Equals(p.Name, w.Character, StringComparison.OrdinalIgnoreCase)))
                .Select(w => new { Worker = w, Missing = StorageBankBagTarget - w.Bags.Select(b => b.LastUniqueIdentity).Distinct().Count() })
                .Where(w => w.Missing > 0).OrderByDescending(w => w.Missing).ThenBy(w => w.Worker.Character).FirstOrDefault();
            var central = storage.Workers.Single(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            // Existing empty bank bags can be supplied without opening them again.
            foreach (var bag in central.Bags.Where(b => b.Items?.Count == 0).Take(Math.Max(0, central.Bags.Count - StorageBankBagTarget)))
            {
                if (state.Bags.Any(b => b.Identity == bag.LastUniqueIdentity && !b.Delivered) ||
                    queue.Batches.Any(b => b.Items.Any(i => i.UniqueIdentity == bag.LastUniqueIdentity))) continue;
                state.Bags.RemoveAll(b => b.Identity == bag.LastUniqueIdentity);
                state.Bags.Add(new ReserveBag { Identity = bag.LastUniqueIdentity, Transaction = "bag-layout-" + Guid.NewGuid().ToString("N") });
            }
            SaveReserve(state);
            foreach (var supplied in state.Bags.Where(b => !b.Delivered && !b.Quarantined && b.Destination != null))
            {
                if (queue.Batches.Any(b => b.Items.Any(i => i.UniqueIdentity == supplied.Identity))) continue;
                if (storage.Workers.Any(w => string.Equals(w.Character, supplied.Destination, StringComparison.OrdinalIgnoreCase) &&
                    w.Bags.Any(b => b.LastUniqueIdentity == supplied.Identity && b.Source == "bank")))
                    supplied.Delivered = true;
            }
            SaveReserve(state);
            bool reserveQueued = queue.Batches.Any(b => IsReserveBatch(b.Items));
            var availableBags = state.Bags.Where(b => !b.Delivered && !b.Quarantined && b.Destination == null &&
                    !queue.Batches.Any(batch => batch.Items.Any(i => i.UniqueIdentity == b.Identity)))
                .Select(b => new { Reserve = b, Physical = records.SingleOrDefault(r => r.Identity.ToString() == b.Identity) })
                .Where(b => b.Physical != null).OrderBy(b => b.Physical.Location == "inventory" ? 0 : 1).ToList();
            var candidate = availableBags.FirstOrDefault();
            // Already staged bags occupy slots, but still belong to this batch.
            // Count them as capacity and leave one spare slot for further moves.
            int stagingCapacity = availableBags.Count(b => b.Physical.Location == "inventory") +
                Math.Max(0, Inventory.NumFreeSlots - 1);
            bool surplusPending = storage.Workers.Any(w => w.Role != "central" && ready.Contains(w.Character) &&
                w.Bags.Count > StorageBankBagTarget && w.Bags.Any(b => b.Source == "inventory") &&
                string.Equals(LayoutDestination(w.Character), _centralCharacter, StringComparison.OrdinalIgnoreCase));
            // Only surplus actually destined for Central competes for this
            // actor. Direct twin transfers run independently of Central supply.
            if (surplusPending && (candidate == null || candidate.Physical.Location == "bank")) return false;
            if (candidate != null && target != null && !reserveQueued && stagingCapacity > 0)
            {
                int desired = Math.Min(target.Missing, Math.Min(ServicePolicy.MaxInternalTradeItems, stagingCapacity));
                string batch = "bag-reserve-" + Guid.NewGuid().ToString("N");
                string transaction = "bag-supply-" + Guid.NewGuid().ToString("N");
                var group = availableBags.Take(desired).Select(b => b.Reserve).ToList();
                foreach (var bag in group)
                { bag.Destination = target.Worker.Character; bag.Role = target.Worker.Role;
                    bag.Batch = batch; bag.Transaction = transaction; bag.EmptyProofBatch = null; }
                SaveReserve(state);
                BeginReserveOperation(group[0].Identity, "dispatch");
                return true;
            }
            var loose = state.Bags.FirstOrDefault(b => !b.Delivered && !b.Quarantined && b.Destination == null &&
                records.Any(r => r.Identity.ToString() == b.Identity && r.Location == "inventory"));
            if (loose != null && ReserveBankFreeSlots > 0)
            { BeginReserveOperation(loose.Identity, "bank"); return true; }
            if (candidate != null && candidate.Physical.Location == "bank" && target == null && !reserveQueued &&
                ReserveBankFreeSlots == 0 && records.Any(r => r.Location == "inventory" && central.Bags.Any(b =>
                    b.LastUniqueIdentity == r.Identity.ToString() && b.Items?.Count > 0)) && AllConfiguredBanksSupplied(storage, ready))
            { BeginReserveOperation(candidate.Reserve.Identity, "discard"); return true; }
            if (loose != null && target == null && !reserveQueued && AllConfiguredBanksSupplied(storage, ready))
            { BeginReserveOperation(loose.Identity, "discard"); return true; }
            if (reserveQueued || records.Any(r => r.Location == "inventory")) return false;
            return false;
        }

        private bool AllConfiguredBanksSupplied(StorageState storage, HashSet<string> ready) => _config.Roles.Values.All(account =>
            ready.Contains(account.Character) && storage.Workers.Any(w => string.Equals(w.Character, account.Character, StringComparison.OrdinalIgnoreCase) &&
                w.Bags.Count(b => b.Source == "bank") >= StorageBankBagTarget));

        private void QueueBagSupply(BagReserve state, string batch)
        {
            var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            if (queue.Batches.Any(b => b.BatchId == batch)) return;
            var group = state.Bags.Where(b => b.Batch == batch && !b.Delivered && !b.Quarantined).ToList();
            var records = StorageBagPolicy.AllBagRecords();
            var staged = group.Select(b => records.SingleOrDefault(r => r.Identity.ToString() == b.Identity)).ToList();
            if (staged.Any(r => r == null)) return; // A bank move is still arriving.
            var banked = staged.Where(r => r.Location == "bank").ToList();
            if (banked.Count != 0)
            {
                if (Inventory.NumFreeSlots < banked.Count) return;
                foreach (var record in banked)
                    if (_supplyMoves.Add(record.Identity.ToString()))
                    {
                        record.Bag.MoveToInventory();
                        _supplyMoveStartedUtc[record.Identity.ToString()] = DateTime.UtcNow;
                        Logger.Information("[CityBankers] BAG SUPPLY inventory move sent " + record.Identity + "; source=" + record.Bag.Slot + "; batch=" + batch + ".");
                    }
                return;
            }
            var first = group[0];
            CityDwellers.Shared.ManagerAccounting.Transaction("Queue bag supply", () =>
            {
                foreach (var bag in group)
                    RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, bag.Identity, null);
                queue.Batches.Add(new DispatchBatchState { BatchId = batch, TransactionId = first.Transaction,
                    Character = first.Destination, Role = first.Role, Status = "queued", CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow, Items = SnapshotTradeItems(staged.Select(r => r.Bag)) });
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            });
            foreach (var bag in group)
            { _supplyMoves.Remove(bag.Identity); _supplyMoveStartedUtc.Remove(bag.Identity); }
            Logger.Information("[CityBankers] BAG SUPPLY queued " + group.Count + " bags -> " + first.Destination + ".");
        }

        private void RememberReserveArrival(IEnumerable<TransferItemState> items, DispatchCommand command = null)
        {
            foreach (var item in items.Where(IsReserveBag))
            {
                _reserveArrivalAfter[item.UniqueIdentity] = _reserveReceiptObservationAfter;
                // Register the supplied bags after the trade, without opening them.
                if (command != null && ReadReserve().Bags.Any(b => b.Identity == item.UniqueIdentity &&
                    b.Batch == command.BatchId && b.Transaction == command.TransactionId &&
                    !b.Quarantined && !b.Delivered && string.Equals(b.Destination, Client.CharacterName, StringComparison.OrdinalIgnoreCase)))
                {
                    var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                    if (!storage.Workers.Any(w => w.Bags.Any(b => b.LastUniqueIdentity == item.UniqueIdentity)))
                    {
                        var physical = StorageBagPolicy.AllRecordsFor(item.UniqueIdentity, "inventory").Single();
                        RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, item.UniqueIdentity,
                            new StorageBagState { Source = "inventory", OuterSlotType = "Inventory",
                                OuterSlotInstance = physical.Slot.Instance & 65535, LastUniqueIdentity = item.UniqueIdentity,
                                Capacity = CityDwellers.Shared.BankerRoles.SmallBackpackCapacity });
                    }
                    _reserveEmptyArrivals.Add(item.UniqueIdentity);
                }
            }
        }

        private void BindBagSupplyToAttempt(List<TransferItemState> items, string batch, string transaction)
        {
            if (!IsReserveBatch(items)) return;
            var reserve = ReadReserve();
            foreach (var item in items)
            {
                var bag = reserve.Bags.Single(b => b.Identity == item.UniqueIdentity);
                if (bag.Quarantined || bag.Delivered || bag.Transaction != transaction ||
                    !string.Equals(bag.Destination, _activeBatch.Character, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bag supply does not match its destination and transaction.");
                bag.Batch = batch;
            }
            SaveReserve(reserve);
        }

        private void InvalidateReserveEvidence()
        {
            // Intent is durable; sequence numbers and native handles belong to
            // the old connection and cannot be used as restart evidence.
            _reserveResumeChecked = false;
            _bankLayoutResumed = _emptyWorkerChecked = false;
            _bankSetupComplete = false;
            _bankSetupStage = null;
            _bankLayout = null; _layoutBankReturn = null;
            _layoutCancelRequest = null;
            _layoutBatchReading = null; _layoutBatchReadAge.Reset();
            _layoutReconnectBefore = null; _layoutActionAge.Reset();
            _reserveOperation = null;
            _reserveBefore = null;
            _reserveArrivalAfter.Clear(); _reserveEmptyArrivals.Clear(); _supplyMoves.Clear(); _supplyMoveStartedUtc.Clear();
        }

        private void ResumeReserveWork()
        {
            if (_bankLayout != null || !StartupCensusGate.IsOpen || !Inventory.Bank.IsOpen || Trade.IsTrading || CensusReservedHere() ||
                _receipt != null || _afterReceipt != null || _workerCommand != null || _reservedDispatch != null ||
                _activeBatch != null || _stackOperation != null || _returnOffer != null || _extraction != null ||
                _withdrawal != null || _donationActive || _donationCleanup != null || _storageRecovery != null ||
                (_storageJob != null && !IsReserveBatch(_storageJob.Command?.Items))) return;

            if (!_reserveResumeChecked)
            {
                var pending = CityDwellers.Shared.ManagerMemory.Current.ReadReserveOperation(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, Client.CharacterName);
                if (pending != null)
                {
                    string error;
                    if (!StorageBagPolicy.TryValidatePhysicalLayout(out error)) throw new InvalidOperationException(error);
                    var records = StorageBagPolicy.AllBagRecords().Where(r => r.Identity.ToString() == pending.Identity).ToList();
                    if (records.Count != 1 || (pending.Purpose != "bank" && pending.Purpose != "dispatch" && pending.Purpose != "discard") ||
                        !new[] { "start", "opening", "verified-empty", "read-backoff", "extracting", "banking", "refresh-banking" }
                            .Contains(pending.Phase))
                        throw new InvalidOperationException("Interrupted reserve operation lacks one current bag address: " + pending.Identity);
                    if (CompleteRecordedReservePlacement(pending, records[0]))
                    {
                        _reserveOperation = null;
                        Logger.Information("[CityBankers] EMPTY BAG RESERVE completed retained banking intent " + pending.Identity +
                            "; recorded placement matches the fresh bank address; stored contents preserved.");
                    }
                    else
                    {
                        // A fresh bank location can finish an already verified return.
                        // Otherwise restart observation from the current outer address,
                        // rather than replaying the old movement or old read boundary.
                        if (pending.Phase != "banking" || records[0].Location != "bank") pending.Phase = "start";
                        pending.SourceSlot = records[0].Bag.Slot.Instance;
                        pending.ObservationAfter = SharedBagRecovery.ContainerObservationSequence;
                        pending.StartedUtc = DateTime.UtcNow; pending.ReadRetries = 0;
                        _reserveOperation = pending; SaveReserveOperation();
                        Logger.Information("[CityBankers] EMPTY BAG RESERVE resumed " + pending.Identity +
                            "; purpose=" + pending.Purpose + "; phase=" + pending.Phase + ".");
                    }
                    BankerActivityGovernor.Wake();
                }
                _reserveResumeChecked = true;
            }
            if (_isCentral || _storageJob != null) return;

            var batches = RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Where(b =>
                b.Status == "transferred" && IsReserveBatch(b.Items) &&
                string.Equals(b.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.Role, _role, StringComparison.OrdinalIgnoreCase)).ToList();
            if (batches.Count == 0) return;
            if (batches.Count != 1) throw new InvalidOperationException("More than one replacement bag owns this worker's receipt.");
            var batch = batches[0];
            if (ReadWorkerStorageReply(batch) != null) return;
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            var own = storage.Workers.Single(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            var reserve = ReadReserve();
            int index = 0;
            foreach (var item in batch.Items)
            {
                if (!reserve.Bags.Any(b => b.Identity == item.UniqueIdentity && b.Batch == batch.BatchId &&
                    b.Transaction == batch.TransactionId && !b.Quarantined &&
                    string.Equals(b.Destination, Client.CharacterName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Bag supply does not match its receipt: " + item.UniqueIdentity);
                var physical = StorageBagPolicy.AllBagRecords().Where(r => r.Identity.ToString() == item.UniqueIdentity).ToList();
                if (physical.Count != 1 || physical[0].Bag.Id != item.AoId ||
                    physical[0].Bag.HighId != item.HighId || physical[0].Bag.Ql != item.Ql)
                    throw new InvalidOperationException("Replacement is not uniquely present: " + item.UniqueIdentity);
                var known = own.Bags.SingleOrDefault(b => b.LastUniqueIdentity == item.UniqueIdentity);
                if (known?.Source != "bank" || physical[0].Location != "bank" ||
                    known.OuterSlotInstance != physical[0].OuterSlot) break;
                index++;
            }
            if (_reserveOperation != null && index < batch.Items.Count &&
                _reserveOperation.Identity != batch.Items[index].UniqueIdentity) return;
            _storageJob = new StorageJob {
                Command = new DispatchCommand { BatchId = batch.BatchId, AttemptId = batch.AttemptId,
                    TransactionId = batch.TransactionId, Role = batch.Role, SourceCharacter = _centralCharacter,
                    DestinationCharacter = Client.CharacterName, CreatedUtc = batch.CreatedUtc, Items = batch.Items },
                Index = index, StoredCount = index, Phase = StoragePhase.FindBag,
                PhaseStartedUtc = DateTime.UtcNow, DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.ItemMoveTimeoutMs)
            };
            if (index == batch.Items.Count) { CompleteStorageJob(); return; }
            string identity = batch.Items[index].UniqueIdentity;
            var record = StorageBagPolicy.AllBagRecords().Single(b => b.Identity.ToString() == identity);
            var registered = own.Bags.SingleOrDefault(b => b.LastUniqueIdentity == identity);
            if (registered != null && record.Location == "inventory")
            {
                _reserveOperation = null;
                _bankLayout = new ReserveOperation { Character = Client.CharacterName, Identity = identity,
                    Purpose = "layout-bank", Phase = "banking", SourceSlot = record.Bag.Slot.Instance, StartedUtc = DateTime.UtcNow };
                BeginLayoutBankReturn(record.Bag);
            }
            else if (_reserveOperation == null)
            { _reserveEmptyArrivals.Add(identity); BeginReserveOperation(identity, "bank"); }
            else { _reserveOperation.SenderVerifiedEmpty = true; SaveReserveOperation(); }
            BankerActivityGovernor.Wake();
            Logger.Information("[CityBankers] EMPTY BAG RESERVE resumed " + batch.BatchId +
                "; banked=" + index + "/" + batch.Items.Count + "; next=" + identity);
        }

        private void AdvanceReserveStorage(string identity)
        {
            var job = _storageJob;
            if (job == null || !IsReserveBatch(job.Command?.Items) || job.Index >= job.Command.Items.Count ||
                job.Command.Items[job.Index].UniqueIdentity != identity) return;
            job.Index++; job.StoredCount++;
            if (job.Index == job.Command.Items.Count) CompleteStorageJob();
        }

        private bool CompleteRecordedReservePlacement(ReserveOperation pending, StorageBagPolicy.BagRecord physical)
        {
            if (pending.Purpose != "bank" || pending.Phase != "banking" || physical.Location != "bank") return false;
            return CityDwellers.Shared.ManagerAccounting.Transaction("Complete recorded reserve placement", () =>
            {
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                if (storage?.Workers == null) return false;
                var owners = storage.Workers.Where(w => w.Bags.Any(b => b.LastUniqueIdentity == pending.Identity)).ToList();
                if (owners.Count == 0) return false;
                if (owners.Count != 1 || !string.Equals(owners[0].Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Retained reserve placement has conflicting recorded ownership: " + pending.Identity);
                var bag = owners[0].Bags.Single(b => b.LastUniqueIdentity == pending.Identity);
                if (bag.Source != physical.Location || bag.OuterSlotInstance != (physical.Bag.Slot.Instance & 65535)) return false;
                // Registration already exists at the exact observed address.
                // Finish only the obsolete movement intent, never rewrite this
                // bag as empty: it may have received stock since registration.
                pending.Phase = "completed";
                CityDwellers.Shared.ManagerMemory.Current.ChangeReserveOperation(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, pending);
                AdvanceReserveStorage(pending.Identity);
                return true;
            });
        }

        private void BeginReserveOperation(string identity, string purpose)
        {
            if (purpose == "dispatch")
            {
                _supplyMoves.Clear(); _supplyMoveStartedUtc.Clear();
                _reserveOperation = new ReserveOperation { Identity = identity, Purpose = purpose,
                    Phase = "start", StartedUtc = DateTime.UtcNow };
                SaveReserveOperation();
                return;
            }
            long after;
            if (!_reserveArrivalAfter.TryGetValue(identity, out after)) after = SharedBagRecovery.ContainerObservationSequence;
            if (RuntimeStateStore.LoadStorageState(_settingsDir).Workers.Any(w =>
                string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                w.Bags.Any(b => b.LastUniqueIdentity == identity && b.Items?.Count == 0)))
                after = 0; // Outer moves do not invalidate an actual current-login contents view.
            _reserveArrivalAfter.Remove(identity);
            _reserveBefore = null;
            _reserveOperation = new ReserveOperation { Identity = identity, Purpose = purpose, Phase = "start", StartedUtc = DateTime.UtcNow,
                ObservationAfter = after, SenderVerifiedEmpty = _reserveEmptyArrivals.Remove(identity) ||
                    (purpose == "bank" && HasReceivedBagSupply(identity)) };
            SaveReserveOperation();
        }
        private bool HasReceivedBagSupply(string identity)
        {
            var command = _storageJob?.Command;
            if (command == null || !IsReserveBatch(command.Items) || !command.Items.Any(i => i.UniqueIdentity == identity)) return false;
            return ReadReserve().Bags.Any(b => b.Identity == identity && b.Batch == command.BatchId &&
                b.Transaction == command.TransactionId && !b.Quarantined &&
                string.Equals(b.Destination, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.Role, _role, StringComparison.OrdinalIgnoreCase));
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
            CityDwellers.Shared.ManagerAccounting.Transaction("Register replacement bag and receipt", () =>
            {
                RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity,
                    new StorageBagState { Source = location, OuterSlotType = record.Bag.Slot.Type.ToString(),
                        OuterSlotInstance = record.Bag.Slot.Instance & 65535, LastUniqueIdentity = op.Identity,
                        LastHandle = handle, Capacity = 21, Items = new List<StoredItemState>() });
                op.Phase = "completed"; SaveReserveOperation();
                AdvanceReserveStorage(op.Identity);
            });
            _reserveOperation = null;
            Logger.Information("[CityBankers] EMPTY BAG RESERVE registered " + op.Identity + " on " +
                Client.CharacterName + "; location=" + location + ".");
        }

        private bool TickReserveOperation()
        {
            var op = _reserveOperation;
            if (op == null) return false;
            if (Trade.IsTrading || !Inventory.Bank.IsOpen) return true;
            if (op.Purpose == "dispatch")
            {
                var supply = ReadReserve();
                var batch = supply.Bags.Single(b => b.Identity == op.Identity).Batch;
                int banked = StorageBagPolicy.AllBagRecords().Count(bagRecord => bagRecord.Location == "bank" && supply.Bags.Any(bag =>
                    bag.Batch == batch && bag.Identity == bagRecord.Identity.ToString()));
                if (_supplyMoveStartedUtc.Count == 0 && Inventory.NumFreeSlots < banked)
                {
                    // A player may have filled slots before the first send. Keep
                    // the intent, but release this actor so ordinary work can free room.
                    op.Phase = "completed"; SaveReserveOperation(); _reserveOperation = null;
                    return false;
                }
                QueueBagSupply(supply, batch);
                if (RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Any(b => b.BatchId == batch))
                { op.Phase = "completed"; SaveReserveOperation(); _reserveOperation = null; }
                else
                {
                    var current = StorageBagPolicy.AllBagRecords();
                    var remaining = current.Where(bagRecord => bagRecord.Location != "inventory" &&
                        supply.Bags.Any(bag => bag.Batch == batch && bag.Identity == bagRecord.Identity.ToString())).ToList();
                    var arrived = new HashSet<string>(current.Where(bagRecord => bagRecord.Location == "inventory").Select(bagRecord => bagRecord.Identity.ToString()));
                    foreach (var completed in _supplyMoveStartedUtc.Keys.Where(arrived.Contains).ToList())
                    { _supplyMoveStartedUtc.Remove(completed); _supplyMoves.Remove(completed); }
                    if (_supplyMoveStartedUtc.Any(move => (DateTime.UtcNow - move.Value).TotalMilliseconds > ServicePolicy.BagMoveTimeoutMs))
                        throw new InvalidOperationException("Bag supply bank move did not finish: " + batch +
                            "; remaining=" + string.Join(",", remaining.Select(bagRecord => bagRecord.Identity + "/" + bagRecord.Bag.Slot)) +
                            "; sent=" + string.Join(",", _supplyMoveStartedUtc.Select(move => move.Key + "@" + move.Value.ToString("O"))) +
                            "; inventory free=" + Inventory.NumFreeSlots + ".");
                }
                return true;
            }
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error)) throw new InvalidOperationException(error);
            var records = StorageBagPolicy.AllBagRecords().Where(r => r.Identity.ToString() == op.Identity).ToList();
            if (op.Purpose == "bank" && records.Count == 1 && records[0].Location == "bank" &&
                (op.Phase == "banking" || op.SenderVerifiedEmpty))
            { RegisterReservePlacement(op, records[0], "bank", 0); return true; }
            if (op.Purpose == "bank" && records.Count == 1 && records[0].Location == "inventory" && op.Phase != "banking")
            {
                if (ReserveBankFreeSlots < 1) return true;
                ReservePhase("banking", records[0].Bag); records[0].Bag.MoveToBank(); return true;
            }
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
                StartupCensusGate.Block("Reserve contents did not arrive; refresh this container automatically: " + op.Identity);
                return true;
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
                        ReportBankerProblem("Reserve bag " + op.Identity + " is not empty. It is retained and excluded from replacements.");
                    }
                    _reserveOperation = null;
                    StartupCensusGate.Block("Reserve bag contains items; retain contents and reconcile before further operations.");
                    return true;
                }
                if (op.Purpose == "discard")
                {
                    BeginSurplusBagDisposal(record.Bag);
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
