using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using CityDwellers.Shared;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private const int StorageBankBagTarget = BankerRoles.BankBagTarget;
        private bool _bankSetupComplete;
        private string _bankSetupStage;

        private void BankSetupStage(string stage)
        {
            if (_bankSetupStage == stage) return;
            _bankSetupStage = stage;
            Logger.Information("[CityBankers] BANK SETUP " + Client.CharacterName + ": " + stage);
        }

        private bool TickInitialInventoryStorage()
        {
            if (_bankSetupComplete || Trade.IsTrading || !Inventory.Bank.IsOpen || _receipt != null ||
                _afterReceipt != null || _reserveOperation != null || _bagShop != null ||
                _workerCommand != null || _reservedDispatch != null || _activeBatch != null ||
                _withdrawal != null || _donationActive || _donationCleanup != null ||
                (_returnOffer != null && ReturnHasPhysicalWork)) return false;
            Guid transfer;
            if (_bankLayout != null && (_bankLayout.Purpose != "layout-return" ||
                !Guid.TryParseExact(_bankLayout.Phase, "N", out transfer))) return false;
            if (_bankLayout != null)
            {
                var retained = ManagerMemory.Current.ReadReturn(ManagerAccounting.TransactionId, _bankLayout.Phase);
                if (retained == null || HasUnresolvedReturnReceipt(retained)) return false;
            }
            if (_storageJob != null)
            {
                if (!_storageJob.LocalRecovery) return false;
                TickStorageJob();
                return true;
            }
            TickLocalStorageRecovery(startup: true);
            if (_storageJob != null) { TickStorageJob(); return true; }
            StartRecoveryExtraction(localStorage: true);
            if (_extraction == null) return false;
            BankSetupStage("emptying inventory bags into bank bags before surplus trades");
            return true;
        }

        private bool TickInitialBankSetup()
        {
            if (_bankSetupComplete) return false;
            if (_bankLayout != null) return TickBankLayout();
            if (_storageJob != null) { TickStorageJob(); return true; }
            if (_bankSetupStage == null) BankSetupStage("placing stored items and working bags in bank before ordinary work");
            TickLocalStorageRecovery(startup: true);
            if (_storageJob != null) { TickStorageJob(); return true; }
            if (TickBankLayout()) return true;
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            var worker = storage.Workers.Single(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            var stagedSupply = _isCentral ? new HashSet<string>(ReadReserve().Bags
                .Where(bag => !bag.Delivered && !bag.Quarantined).Select(bag => bag.Identity)) : new HashSet<string>();
            bool inventoryBags = StorageBagPolicy.AllBagRecords().Any(r => r.Location == "inventory" &&
                !stagedSupply.Contains(r.Identity.ToString()));
            if (_isCentral && inventoryBags)
            {
                // Only Central's own staged shells need its trade window. Other
                // bankers' provisioning is background demand, not a service gate.
                if (TickBagReserve()) return true;
                TickDispatch();
                return true;
            }
            if (inventoryBags)
            {
                BankSetupStage("finishing this banker's surplus transfers");
                return true;
            }
            _bankSetupComplete = true;
            BankSetupStage(_isCentral ? "public buffer ready; no automatic storage-bag target" :
                "local storage ready; " + worker.Bags.Count(b => b.Source == "bank") +
                "/" + StorageBankBagTarget + " bank bags; remaining supply runs in background");
            return false;
        }
        private sealed class WorkingBagReturn
        {
            public string Identity;
            public Identity Source;
            public bool Acknowledged, RefreshRequested, FallbackAttempted;
            public string RefreshRoute;
            public readonly List<string> Packets = new List<string>();
            public long RefreshAfter;
            public readonly Stopwatch Age = new Stopwatch();
        }
        private readonly List<WorkingBagReturn> _workingBagReturns = new List<WorkingBagReturn>();

        private void CaptureWorkingBagReturns(SmokeLounge.AOtomation.Messaging.Messages.Message message)
        {
            var added = message?.Body as SmokeLounge.AOtomation.Messaging.Messages.N3Messages.ContainerAddItem;
            if (added == null) return;
            foreach (var proof in _workingBagReturns.Where(p => p.Packets.Count < 4 &&
                (p.Source == added.Source || added.Target.Type == IdentityType.Bank)))
                proof.Packets.Add("owner=" + added.Identity + ",source=" + added.Source +
                    ",target=" + added.Target + ",slot=" + added.Slot);
            if (DynelManager.LocalPlayer == null || added.Identity != DynelManager.LocalPlayer.Identity ||
                added.Target.Type != IdentityType.Bank) return;
            foreach (var proof in _workingBagReturns.Where(p => p.Source == added.Source)) proof.Acknowledged = true;
        }

        private static bool NativeBankReturnObserved(WorkingBagReturn proof)
        {
            int slot;
            if (proof == null || !proof.RefreshRequested ||
                !ClientlessSessionGuard.TryNativeBankSlot(proof.Identity, proof.RefreshAfter, out slot)) return false;
            var bank = StorageBagPolicy.AllRecordsFor(proof.Identity, "bank");
            return bank.Count == 1 && bank[0].Slot.Instance == slot &&
                StorageBagPolicy.AllRecordsFor(proof.Identity, "inventory").Count == 0;
        }
        private static bool WorkingBagReturnSettled(WorkingBagReturn proof) => proof != null &&
            (proof.Acknowledged || NativeBankReturnObserved(proof)) &&
            StorageBagPolicy.AllRecordsFor(proof.Identity, "inventory").Count == 0;
        private static bool WorkingBagReturnTimedOut(WorkingBagReturn proof) => proof == null ||
            proof.Age.ElapsedMilliseconds >= ServicePolicy.BagMoveTimeoutMs;

        private static string WorkingBagReturnDetail(WorkingBagReturn proof)
        {
            if (proof == null) return "bank-return proof missing";
            int slot;
            bool native = ClientlessSessionGuard.TryNativeBankSlot(proof.Identity, proof.RefreshAfter, out slot);
            return "bag=" + proof.Identity + "; source=" + proof.Source + "; ack=" + proof.Acknowledged +
                "; inventory=" + StorageBagPolicy.AllRecordsFor(proof.Identity, "inventory").Count +
                "; bank=" + StorageBagPolicy.AllRecordsFor(proof.Identity, "bank").Count +
                "; refresh=" + proof.RefreshRequested + "; route=" + proof.RefreshRoute +
                "; fallback=" + proof.FallbackAttempted + "; revision=" + ClientlessSessionGuard.BankSnapshotRevision +
                "; after=" + proof.RefreshAfter + "; nativeSlot=" + (native ? slot.ToString() : "unavailable") +
                "; packets=[" + string.Join(" | ", proof.Packets) + "]";
        }
        private ReserveOperation _bankLayout;
        private WorkingBagReturn _layoutBankReturn;
        private bool _bankLayoutResumed, _emptyWorkerChecked;
        private string _layoutHold;
        private string _layoutBatchReading;
        private readonly Stopwatch _layoutBatchReadAge = new Stopwatch();
        private string _layoutReconnectBefore;
        private Task<string> _layoutCancelRequest;
        private readonly Stopwatch _layoutActionAge = new Stopwatch();

        private void SendLayoutAction(string phase, Item source, Action send)
        {
            if (_layoutReconnectBefore == null)
                _layoutReconnectBefore = StartupCensusGate.ReconnectInventory(_bankLayout.Identity);
            _bankLayout.Phase = phase;
            _bankLayout.SourceSlot = source.Slot.Instance;
            _bankLayout.StartedUtc = DateTime.UtcNow;
            _layoutActionAge.Reset();
            SaveBankLayout(); // Intent must commit before the packet is sent.
            send();
            // SQL admission/commit time is not time spent awaiting AO.
            _layoutActionAge.Restart();
            Logger.Information("[CityBankers] BAG LAYOUT " + phase + " requested " + _bankLayout.Identity +
                "; source=" + source.Slot + "; awaiting native evidence.");
        }

        private void BeginLayoutBankReturn(Item bag) => SendLayoutAction("banking", bag,
            () => _layoutBankReturn = BeginWorkingBagReturn(bag));

        private void CheckLayoutActionTimeout()
        {
            if (!_layoutActionAge.IsRunning)
                StartupCensusGate.Block("Retained bag layout has no unique fresh source or destination for " +
                    _bankLayout.Identity + "/" + _bankLayout.Phase + "; intent retained.");
            else if ((_layoutBankReturn?.Age.ElapsedMilliseconds ?? _layoutActionAge.ElapsedMilliseconds) >= ServicePolicy.BagMoveTimeoutMs)
                StartupCensusGate.Block("Bag layout action remains unconfirmed after send; retained " +
                    _bankLayout.Identity + "/" + _bankLayout.Phase + ". Refresh this local movement only. " +
                    WorkingBagReturnDetail(_layoutBankReturn));
        }

        private WorkingBagReturn BeginWorkingBagReturn(Item bag)
        {
            if (!StorageBagPolicy.IsNormalInventory(bag) || !StorageBagPolicy.IsStorageBag(bag))
                throw new InvalidOperationException("Only a normal inventory storage bag may be returned to bank.");
            string identity = bag.UniqueIdentity.ToString();
            if (StorageBagPolicy.AllRecordsFor(identity, "bank").Count != 0 ||
                StorageBagPolicy.AllRecordsFor(identity, "inventory").Count != 1 || ReserveBankFreeSlots < 1)
                throw new InvalidOperationException("Working bag has no unambiguous free bank destination: " + identity);
            var proof = new WorkingBagReturn { Identity = identity, Source = bag.Slot };
            _workingBagReturns.Add(proof);
            BagOriginTrace.MoveToBank(bag);
            proof.Age.Restart();
            return proof;
        }

        private bool ObserveWorkingBagReturn(WorkingBagReturn proof, out Item bag)
        {
            bag = null;
            if (proof == null) return false;
            // The requested full bank snapshot is independent proof. Do not
            // require an intermediate callback before even requesting it.
            if (!proof.RefreshRequested)
            {
                proof.RefreshAfter = ClientlessSessionGuard.BankSnapshotRevision;
                string route;
                if (!CityBankers.RequestBankSnapshot(false, out route)) return false;
                proof.RefreshRoute = route;
                proof.FallbackAttempted = route == "terminal";
                proof.RefreshRequested = true;
                proof.Age.Restart(); // Snapshot is a separate bounded network request.
                Logger.Information("[CityBankers] BANK RETURN snapshot requested; " + WorkingBagReturnDetail(proof));
                return false;
            }
            int nativeSlot;
            if (!ClientlessSessionGuard.TryNativeBankSlot(proof.Identity, proof.RefreshAfter, out nativeSlot))
            {
                if (!proof.FallbackAttempted && WorkingBagReturnTimedOut(proof) && !Trade.IsTrading)
                {
                    proof.FallbackAttempted = true;
                    long after = ClientlessSessionGuard.BankSnapshotRevision;
                    string route;
                    if (CityBankers.RequestBankSnapshot(true, out route))
                    {
                        proof.RefreshAfter = after;
                        proof.RefreshRoute = route;
                        proof.Age.Restart();
                        Logger.Warning("[CityBankers] BANK RETURN portable snapshot did not arrive; terminal fallback sent; " +
                            WorkingBagReturnDetail(proof));
                    }
                }
                return false;
            }
            var bank = StorageBagPolicy.AllRecordsFor(proof.Identity, "bank");
            var inventory = StorageBagPolicy.AllRecordsFor(proof.Identity, "inventory");
            // The destination was absent before the move. A cached pre-existing
            // record cannot satisfy this proof, nor can a second outer record.
            if (bank.Count != 1 || inventory.Count != 0 || bank[0].Slot.Instance != nativeSlot) return false;
            bag = bank[0];
            _workingBagReturns.Remove(proof);
            return true;
        }

        private void SaveBankLayout() => ManagerAccounting.Transaction("Bank-only bag layout", () =>
            ManagerMemory.Current.ChangeReserveOperation(ManagerAccounting.TransactionId, _bankLayout));

        private void FinishBankLayout()
        {
            _bankLayout.Phase = "completed";
            SaveBankLayout();
            _bankLayout = null; _layoutBankReturn = null; _layoutHold = null;
            _layoutReconnectBefore = null; _layoutActionAge.Reset();
            _layoutCancelRequest = null;
            _layoutBatchReading = null; _layoutBatchReadAge.Reset();
        }

        private void ResumeBankLayout()
        {
            if (!Inventory.Bank.IsOpen || !ClientlessSessionGuard.BankCacheTrusted) return;
            if (!_emptyWorkerChecked)
            {
                var state = RuntimeStateStore.LoadStorageState(_settingsDir);
                if (!state.Workers.Any(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase)))
                {
                    // Owner-authorized first enrollment of a genuinely empty
                    // new character. Existing characters never enter this path.
                    if (Inventory.Bank.Items.Count != 0 || StorageBagPolicy.AllBagRecords().Count != 0 ||
                        Inventory.Items.Any(i => StorageBagPolicy.IsNormalInventory(i) && !BankerPersonalItems.IsPersonal(i.Id, i.HighId)) ||
                        ManagerMemory.Current.ReadLedger(ManagerAccounting.TransactionId).Items.Any(e =>
                            string.Equals(e.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase)))
                        { StartupCensusGate.Block("Enroll this banker's existing native inventory automatically."); return; }
                    RuntimeStorageStateTransactions.ReplaceCensusedWorker(_settingsDir, new StorageWorkerState {
                        Role = _role, Character = Client.CharacterName, ObservedUtc = DateTime.UtcNow });
                    Logger.Information("[CityBankers] Enrolled empty new banker " + Client.CharacterName + "; no existing banker audited.");
                }
                _emptyWorkerChecked = true;
            }
            if (_bankLayoutResumed) return;
            _bankLayoutResumed = true;
            var pending = ManagerMemory.Current.ReadReserveOperation(ManagerAccounting.TransactionId, Client.CharacterName);
            if (pending == null || (pending.Purpose != "layout-bank" && pending.Purpose != "layout-return")) return;
            _bankLayout = pending;
            pending.StartedUtc = DateTime.UtcNow;
            if (pending.Purpose == "layout-return" && !Guid.TryParseExact(pending.Phase, "N", out var ignored))
                pending.Phase = "locate"; // native observation sequence belongs to this connection
            Logger.Information("[CityBankers] Resuming bank-only layout for " + pending.Identity + "; " + pending.Purpose + "/" + pending.Phase);
        }

        private bool BankLayoutPending()
        {
            if (_bankLayout != null || (Inventory.Bank.IsOpen && StorageBagPolicy.AllBagRecords().Any(r => r.Location == "inventory"))) return true;
            return false;
        }

        private bool TickBankLayout()
        {
            if (!Inventory.Bank.IsOpen || Trade.IsTrading) return _bankLayout != null;
            if (_bankLayout == null)
            {
                if (!CanStartLocalCensus() || _extraction != null) return false;
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                var worker = storage.Workers.Single(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
                var inventory = StorageBagPolicy.AllBagRecords().Where(r => r.Location == "inventory").ToList();
                if (inventory.Count == 0) return false;
                // Reserve donations/purchases have their own exact receipt and
                // empty verification actor; do not adopt an unknown container.
                var reserveOwned = _isCentral ? new HashSet<string>(ReadReserve().Bags
                    .Where(bag => !bag.Delivered && !bag.Quarantined).Select(bag => bag.Identity)) : new HashSet<string>();
                var candidates = inventory.Select(r => new { Record = r,
                    State = worker.Bags.SingleOrDefault(b => b.LastUniqueIdentity == r.Identity.ToString()) })
                    .Where(r => r.State?.Items != null && !reserveOwned.Contains(r.Record.Identity.ToString()))
                    .OrderByDescending(r => r.State.Items.Count).ToList();
                var filled = candidates.FirstOrDefault(r => r.State.Items.Count > 0 || r.State.Capacity == 0);
                if (ReserveBankFreeSlots > 0 && (filled != null || StorageBagPolicy.AllBagRecords().Count <= StorageBankBagTarget))
                {
                    var selected = filled ?? candidates.FirstOrDefault();
                    if (selected == null) return false;
                    _bankLayout = new ReserveOperation { Character = Client.CharacterName, Identity = selected.Record.Identity.ToString(),
                        Purpose = "layout-bank", Phase = "banking", SourceSlot = selected.Record.Bag.Slot.Instance, StartedUtc = DateTime.UtcNow };
                    BeginLayoutBankReturn(selected.Record.Bag);
                    return true;
                }
                // Central distributes its own empty surplus through the normal
                // reserve dispatch actor, including empty bank bags that can make
                // room for an occupied inventory bag.
                if (_isCentral) return false;
                var bankEmpty = StorageBagPolicy.AllBagRecords().Where(r => r.Location == "bank").FirstOrDefault(r =>
                    worker.Bags.Any(b => b.LastUniqueIdentity == r.Identity.ToString() && b.Capacity > 0 && b.Items?.Count == 0));
                // If occupied inventory bags need bank slots, release an EMPTY
                // bank shell first. Never leave occupied bags for ordinary work.
                var empty = filled != null ? bankEmpty : candidates.FirstOrDefault(r => r.State.Capacity > 0 && r.State.Items.Count == 0)?.Record ?? bankEmpty;
                if (empty == null)
                {
                    const string reason = "Bank is full and has no verified empty bag to exchange; occupied bags retained.";
                    if (_layoutHold != reason) Logger.Warning("[CityBankers] " + Client.CharacterName + ": " + reason);
                    _layoutHold = reason;
                    return true;
                }
                _bankLayout = new ReserveOperation { Character = Client.CharacterName, Identity = empty.Identity.ToString(),
                    Purpose = "layout-return", Phase = "locate", SourceSlot = empty.Bag.Slot.Instance, StartedUtc = DateTime.UtcNow };
                SaveBankLayout();
            }
            var op = _bankLayout;
            var records = StorageBagPolicy.AllBagRecords().Where(r => r.Identity.ToString() == op.Identity).ToList();
            if (op.Purpose == "layout-bank")
            {
                Item returned;
                bool resumed = _layoutBankReturn == null && records.Count == 1 && records[0].Location == "bank";
                if (resumed || ObserveWorkingBagReturn(_layoutBankReturn, out returned))
                {
                    int slot = records.Single(r => r.Location == "bank").OuterSlot;
                    ManagerAccounting.Transaction("Commit bank-only layout", () => {
                        RuntimeStorageStateTransactions.RecordBankReturn(_settingsDir, Client.CharacterName, op.Identity, slot);
                        FinishBankLayout();
                        AdvanceReserveStorage(op.Identity);
                    });
                    return true;
                }
                if (_layoutBankReturn == null && records.Count == 1 && records[0].Location == "inventory")
                    BeginLayoutBankReturn(records[0].Bag);
                else CheckLayoutActionTimeout(); // Completion evidence always wins over a late tick.
                return true;
            }
            // A layout return persists its normal, receipt-verified return ID in
            // Phase. Restart can recover it without inventing another transfer.
            Guid returnId;
            if (Guid.TryParseExact(op.Phase, "N", out returnId))
            {
                var offer = ManagerMemory.Current.ReadReturn(ManagerAccounting.TransactionId, op.Phase);
                if (offer == null) throw new InvalidOperationException("Surplus bag return intent is missing.");
                if (offer.CompletedUtc.HasValue) { FinishBankLayout(); return true; }
                if (_returnOffer == null && !offer.SenderReceiptVerified && !offer.ReceiverReceiptVerified &&
                    ((string.Equals(ReturnDestination(offer), _centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(LayoutDestination(Client.CharacterName), _centralCharacter, StringComparison.OrdinalIgnoreCase)) ||
                        ReturnPeerLimit(ReturnDestination(offer)) < ReturnItems(offer).Count || LayoutReturnRoom(ReturnDestination(offer)) < ReturnItems(offer).Count ||
                        !LayoutReturnSourceMatches(offer)) &&
                    records.Count == 1 && records[0].Location == "inventory" &&
                    !HasUnresolvedReturnReceipt(offer))
                {
                    // Retire only an unsent reservation, either for an old
                    // Central route or a twin that has already reached capacity.
                    // Physical or receipt-bound transfers keep their destination.
                    if (_layoutCancelRequest == null)
                        _layoutCancelRequest = SendReturnMessage(ReturnDestination(offer), "return-cancel", offer);
                    else if (_layoutCancelRequest.IsCompleted)
                    {
                        string response = _layoutCancelRequest.Result; _layoutCancelRequest = null;
                        if (response == "cancelled:" + offer.Id) FinishBankLayout();
                    }
                    return true;
                }
                if (_returnOffer == null && !offer.SenderReceiptVerified && !offer.ReceiverReceiptVerified &&
                    records.Count == 1 && records[0].Location == "bank")
                {
                    // The unique retained shell is still ours. Stage its fresh
                    // address instead of waiting forever for an old inventory slot.
                    if (!_layoutActionAge.IsRunning)
                    {
                        _layoutReconnectBefore = StartupCensusGate.ReconnectInventory(op.Identity);
                        BagOriginTrace.MoveToInventory(records[0].Bag);
                        _layoutActionAge.Restart();
                    }
                    else CheckLayoutActionTimeout();
                    return true;
                }
                if (_returnOffer == null && ((records.Count == 1 && records[0].Location == "inventory") ||
                    (records.Count == 0 && offer.SenderReceiptVerified)))
                {
                    _returnOffer = offer;
                    _returnLocalVerified = offer.SenderReceiptVerified;
                    _returnRequest = _returnLocalVerified ? null : SendReturnMessage(ReturnDestination(offer), "return-prepare", offer);
                    _returnPhase.Restart();
                }
                else if (_returnOffer == null) CheckLayoutActionTimeout();
                return true;
            }
            if (records.Count != 1) { CheckLayoutActionTimeout(); return true; }
            var record = records[0];
            if (op.Phase == "locate")
            {
                if (record.Location == "bank")
                    SendLayoutAction("extracting", record.Bag, () => BagOriginTrace.MoveToInventory(record.Bag));
                else op.Phase = "staged";
                if (record.Location == "bank") return true;
            }
            if (record.Location != "inventory") { CheckLayoutActionTimeout(); return true; }
            int handle = 0;
            var source = RuntimeStateStore.LoadStorageState(_settingsDir).Workers.Single(w =>
                string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase)).Bags.Single(b => b.LastUniqueIdentity == op.Identity);
            if (source.Items?.Count != 0) throw new InvalidOperationException("Surplus return cannot remove occupied storage.");
            if (source.Source == "bank" && ReserveBankFreeSlots > 0 &&
                RuntimeStateStore.LoadStorageState(_settingsDir).Workers.Single(w => string.Equals(w.Character,
                    Client.CharacterName, StringComparison.OrdinalIgnoreCase)).Bags.Any(b => b.Source == "inventory" &&
                    b.Items?.Count > 0 && StorageBagPolicy.AllRecordsFor(b.LastUniqueIdentity, "inventory").Count == 1))
            {
                // The empty shell has freed a bank slot. Record its temporary
                // inventory address, then let job 1 bank the occupied bag NOW;
                // no peer trade is needed before the contents are back in bank.
                ManagerAccounting.Transaction("Free bank slot for occupied working bag", () => {
                    RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity,
                        new StorageBagState { Source = "inventory", OuterSlotType = "Inventory",
                            OuterSlotInstance = record.OuterSlot, LastUniqueIdentity = op.Identity,
                            LastHandle = handle, Capacity = BankerRoles.SmallBackpackCapacity });
                    FinishBankLayout();
                });
                return true;
            }
            if (AllConfiguredBanksSupplied(RuntimeStateStore.LoadStorageState(_settingsDir), BankerReadiness.GetReadyCharacters(_settingsDir)))
            {
                string identity = op.Identity;
                FinishBankLayout();
                BeginReserveOperation(identity, "discard");
                return true;
            }
            string destination = LayoutDestination(Client.CharacterName);
            if (destination == null) return true;
            var batch = BuildLayoutReturnBatch(record, destination);
            if (batch == null) return true;
            var returnedOffer = new ReturnOffer { Id = Guid.NewGuid().ToString("N"), TransactionId = "bag-layout-" + Guid.NewGuid().ToString("N"),
                Source = Client.CharacterName, Destination = destination, SourceSlot = batch[0].Slot,
                Item = batch[0].Item, Bags = batch, StartedUtc = DateTime.UtcNow };
            ManagerAccounting.Transaction("Queue surplus bags", () => {
                foreach (var entry in batch)
                {
                    RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, entry.Item.UniqueIdentity,
                        new StorageBagState { Source = "inventory", OuterSlotType = "Inventory", OuterSlotInstance = entry.Slot & 65535,
                            LastUniqueIdentity = entry.Item.UniqueIdentity, LastHandle = 0, Capacity = BankerRoles.SmallBackpackCapacity });
                }
                SaveReturn(returnedOffer, "queued");
                op.Phase = returnedOffer.Id; SaveBankLayout();
            });
            Logger.Information("[CityBankers] SURPLUS BATCH queued " + batch.Count + " bag(s) from " +
                Client.CharacterName + " to " + ReturnDestination(returnedOffer) + "; id=" + returnedOffer.Id);
            _returnOffer = returnedOffer;
            _returnRequest = SendReturnMessage(ReturnDestination(returnedOffer), "return-prepare", returnedOffer);
            _returnPhase.Restart();
            return true;
        }

        internal static bool TryRetainLayoutReconnect(out string bag, out string otherItems)
        {
            bag = otherItems = null;
            var actor = _ipcOwner;
            var op = actor?._bankLayout;
            Guid transfer;
            if (op == null || actor._layoutReconnectBefore == null || !NoRetainedItemWork(actor) ||
                (op.Purpose != "layout-bank" && op.Purpose != "layout-return") ||
                Guid.TryParseExact(op.Phase, "N", out transfer)) return false;
            bag = op.Identity; otherItems = actor._layoutReconnectBefore;
            return true;
        }

        internal static bool VerifyLayoutReconnect(string bag, string otherItems)
        {
            var actor = _ipcOwner;
            if (actor == null || string.IsNullOrEmpty(bag) || !NoRetainedItemWork(actor) || otherItems == null ||
                StartupCensusGate.ReconnectInventory(bag) != otherItems) return false;
            var op = ManagerMemory.Current.ReadReserveOperation(ManagerAccounting.TransactionId, Client.CharacterName);
            Guid transfer;
            if (op?.Identity != bag || (op.Purpose != "layout-bank" && op.Purpose != "layout-return") ||
                Guid.TryParseExact(op.Phase, "N", out transfer)) return false;
            var own = RuntimeStateStore.LoadStorageState(actor._settingsDir)?.Workers?.SingleOrDefault(w =>
                string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            return own?.Bags?.Count(b => b.LastUniqueIdentity == bag) == 1 &&
                StorageBagPolicy.AllBagRecords().Count(r => r.Identity.ToString() == bag) == 1;
        }

        private static bool IsLayoutReturn(ReturnOffer offer) => offer != null && offer.LedgerId == null &&
            offer.TransactionId?.StartsWith("bag-layout-", StringComparison.Ordinal) == true && IsReserveBag(offer.Item);

        private string LayoutDestination(string source)
        {
            var member = _config.Roles.FirstOrDefault(p => string.Equals(p.Value.Character, source, StringComparison.OrdinalIgnoreCase));
            if (member.Value == null) return null;
            var ready = BankerReadiness.GetReadyCharacters(_settingsDir);
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            string category = BankerRoles.Canonical(member.Key);
            return _config.Roles.Where(p => !string.Equals(p.Value.Character, source, StringComparison.OrdinalIgnoreCase) &&
                    IsRoleEnabled(BankerRoles.Canonical(p.Key)) && ready.Contains(p.Value.Character))
                .Select(p => new { Character = p.Value.Character, Category = BankerRoles.Canonical(p.Key),
                    Worker = storage.Workers.FirstOrDefault(w => string.Equals(w.Character, p.Value.Character, StringComparison.OrdinalIgnoreCase)) })
                .Where(p => p.Category != "central" && p.Worker != null && p.Worker.Bags.Count < StorageBankBagTarget)
                .OrderBy(p => p.Category == category ? 0 : p.Category == "central" ? 2 : 1)
                .ThenBy(p => p.Worker.Bags.Count).ThenBy(p => p.Character, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Character).FirstOrDefault();
        }

        private int LayoutReturnRoom(string character)
        {
            if (SameCharacter(character, _centralCharacter)) return 0;
            var worker = RuntimeStateStore.LoadStorageState(_settingsDir)?.Workers?.SingleOrDefault(w =>
                string.Equals(w.Character, character, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0, StorageBankBagTarget - (worker?.Bags?.Select(b => b.LastUniqueIdentity).Distinct().Count() ?? 0));
        }

        private List<ExtractionSlot> BuildLayoutReturnBatch(StorageBagPolicy.BagRecord anchor, string destination)
        {
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error)) throw new InvalidOperationException(error);
            var own = RuntimeStateStore.LoadStorageState(_settingsDir).Workers.Single(w =>
                string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            int room = destination == null ? 0 : LayoutReturnRoom(destination);
            int count = Math.Min(ReturnPeerLimit(destination), Math.Min(room, Math.Max(1, own.Bags.Count - StorageBankBagTarget)));
            if (count == 0) { FinishBankLayout(); return null; }
            var candidates = StorageBagPolicy.AllBagRecords().Where(r => r.Location == "inventory" &&
                own.Bags.Any(b => b.LastUniqueIdentity == r.Identity.ToString() && b.Items?.Count == 0 && b.Capacity > 0))
                .OrderBy(r => r.Identity == anchor.Identity ? 0 : 1).ThenBy(r => r.OuterSlot).Take(count).ToList();
            if (candidates.Count == 0 || candidates[0].Identity != anchor.Identity)
                throw new InvalidOperationException("Surplus batch lost its source anchor.");
            var result = new List<ExtractionSlot>();
            foreach (var candidate in candidates)
            {
                result.Add(new ExtractionSlot { Slot = candidate.Bag.Slot.Instance, Item = SnapshotTradeItems(new[] { candidate.Bag })[0] });
            }
            _layoutBatchReading = null; _layoutBatchReadAge.Reset();
            return result;
        }

        private bool LayoutReturnSourceMatches(ReturnOffer offer)
        {
            var slots = ReturnSlots(offer);
            if (!IsReserveBatch(ReturnItems(offer)) || slots.Any(s => s?.Item == null) ||
                slots.Select(s => s.Slot).Distinct().Count() != slots.Count ||
                slots[0].Slot != offer.SourceSlot || CustodyKey(slots[0].Item) != CustodyKey(offer.Item)) return false;
            var op = ManagerMemory.Current.ReadReserveOperation(ManagerAccounting.TransactionId, offer.Source);
            var own = RuntimeStateStore.LoadStorageState(_settingsDir)?.Workers?.SingleOrDefault(w =>
                string.Equals(w.Character, offer.Source, StringComparison.OrdinalIgnoreCase));
            return op?.Purpose == "layout-return" && op.Phase == offer.Id && op.Identity == offer.Item.UniqueIdentity &&
                slots.All(slot => own?.Bags?.Count(b => b.LastUniqueIdentity == slot.Item.UniqueIdentity &&
                    b.Items?.Count == 0 && b.Capacity > 0 && b.Source == "inventory" &&
                    b.OuterSlotInstance == (slot.Slot & 65535)) == 1);
        }

        private bool ValidLayoutReturn(ReturnOffer offer)
        {
            bool destination = string.Equals(ReturnDestination(offer), Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                _config.Roles.Any(p => string.Equals(p.Value?.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            return destination && LayoutReturnSourceMatches(offer);
        }

        private string SurplusDisposalReceiptId => "reserve-discard:" + Client.CharacterName.ToLowerInvariant();

        private void CompleteSurplusBagDisposal()
        {
            var op = _reserveOperation;
            if (op == null || op.Purpose != "discard") throw new InvalidOperationException("Empty surplus disposal lost its intent.");
            ManagerAccounting.Transaction("Commit verified empty surplus disposal", () => {
                RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity, null);
                var reserve = ReadReserve();
                reserve.Bags.RemoveAll(b => b.Identity == op.Identity);
                SaveReserve(reserve);
                op.Phase = "completed"; SaveReserveOperation();
                _receipt.Phase = "applied";
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, _receipt);
            });
            Logger.Information("[CityBankers] Removed verified empty surplus backpack " + op.Identity + "; all configured banks already supplied.");
            _reserveOperation = null;
        }

        private void BeginSurplusBagDisposal(Item bag)
        {
            var expected = SnapshotTradeItems(new[] { bag });
            PrepareReceipt("reserve-discard", "bag-layout-discard-" + _reserveOperation.Identity,
                _reserveOperation.Identity, expected, -1, SurplusDisposalReceiptId);
            PersistReceipt("prepared");
            _reserveOperation.Phase = "discarding";
            _reserveOperation.SenderVerifiedEmpty = true;
            SaveReserveOperation();
            AwaitPhysicalReceipt(expected, CompleteSurplusBagDisposal);
            bag.Delete();
        }

        private void ResumeSurplusBagDisposal()
        {
            if (_reserveOperation != null || _bankLayout != null || _receipt != null || !Inventory.Bank.IsOpen) return;
            var op = ManagerMemory.Current.ReadReserveOperation(ManagerAccounting.TransactionId, Client.CharacterName);
            if (op?.Purpose != "discard" || op.Phase != "discarding") return;
            var receipt = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, SurplusDisposalReceiptId);
            if (receipt?.Kind != "reserve-discard" || !op.SenderVerifiedEmpty || receipt.Expected?.Count != 1 ||
                receipt.Expected[0].UniqueIdentity != op.Identity || receipt.Direction != -1)
                throw new InvalidOperationException("Interrupted surplus disposal has incomplete evidence.");
            _reserveOperation = op;
            _reserveResumeChecked = true;
            var physical = PhysicalInventory().Select(CustodyKey).OrderBy(k => k).ToArray();
            if (receipt.Before.Select(CustodyKey).OrderBy(k => k).SequenceEqual(physical))
            {
                // The original operation had no physical effect. Read the exact
                // bag empty again before allowing another delete request.
                receipt.Phase = "applied"; receipt.Direction = 0;
                ManagerAccounting.Transaction("Retire unchanged surplus disposal", () =>
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, receipt));
                op.Phase = "start"; op.SenderVerifiedEmpty = false;
                op.ObservationAfter = SharedBagRecovery.ContainerObservationSequence;
                op.StartedUtc = DateTime.UtcNow; SaveReserveOperation();
                return;
            }
            _receipt = receipt;
            _receiptLiveBeforeItems = null;
            _custodyVerificationWait = Stopwatch.StartNew();
            _settledInventory = null; _inventoryStableFor.Restart();
            AwaitPhysicalReceipt(receipt.Expected, CompleteSurplusBagDisposal);
        }
    }
}
