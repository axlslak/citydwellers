using System;
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
            if (_isCentral)
            {
                BankSetupStage("supplying bank bags; existing reserves first, purchases only for remaining shortage");
                foreach (var pending in RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Where(b =>
                    b.Status == "transferred" && IsReserveBatch(b.Items) && IsDispatchDestinationEnabled(b)).ToList())
                    PollStorageResult(pending);
                if (TickBagReserve()) return true;
                TickDispatch(); // During setup this selects reserve deliveries only.
                if (_activeBatch != null || _reserveOperation != null ||
                    !AllConfiguredBanksSupplied(storage, BankerReadiness.GetReadyCharacters(_settingsDir))) return true;
            }
            if (worker.Bags.Count(b => b.Source == "bank") < StorageBankBagTarget ||
                StorageBagPolicy.AllBagRecords().Any(r => r.Location == "inventory"))
            {
                BankSetupStage("waiting for bank-bag supply; ordinary work remains paused");
                return true;
            }
            _bankSetupComplete = true;
            BankSetupStage("complete: 102 storage bags in bank; no storage bags in inventory");
            return false;
        }
        private sealed class WorkingBagReturn
        {
            public string Identity;
        }
        private ReserveOperation _bankLayout;
        private WorkingBagReturn _layoutBankReturn;
        private bool _bankLayoutResumed, _emptyWorkerChecked;
        private string _layoutHold;
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
            else if (_layoutActionAge.ElapsedMilliseconds >= ServicePolicy.BagMoveTimeoutMs)
                StartupCensusGate.Block("Bag layout action remains unconfirmed after send; retained " +
                    _bankLayout.Identity + "/" + _bankLayout.Phase + ". Refresh this local movement only.");
        }

        private WorkingBagReturn BeginWorkingBagReturn(Item bag)
        {
            if (!StorageBagPolicy.IsNormalInventory(bag) || !StorageBagPolicy.IsStorageBag(bag))
                throw new InvalidOperationException("Only a normal inventory storage bag may be returned to bank.");
            string identity = bag.UniqueIdentity.ToString();
            if (StorageBagPolicy.AllRecordsFor(identity, "bank").Count != 0 ||
                StorageBagPolicy.AllRecordsFor(identity, "inventory").Count != 1 || ReserveBankFreeSlots < 1)
                throw new InvalidOperationException("Working bag has no unambiguous free bank destination: " + identity);
            var proof = new WorkingBagReturn { Identity = identity };
            BagOriginTrace.MoveToBank(bag);
            return proof;
        }

        private static bool ObserveWorkingBagReturn(WorkingBagReturn proof, out Item bag)
        {
            bag = null;
            if (proof == null) return false;
            var bank = StorageBagPolicy.AllRecordsFor(proof.Identity, "bank");
            var inventory = StorageBagPolicy.AllRecordsFor(proof.Identity, "inventory");
            // The destination was absent before the move. A cached pre-existing
            // record cannot satisfy this proof, nor can a second outer record.
            if (bank.Count != 1 || inventory.Count != 0) return false;
            bag = bank[0];
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
            if (!_isCentral) return false;
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            return _config.Roles.Values.Any(account => {
                var worker = storage?.Workers?.FirstOrDefault(w => string.Equals(w.Character, account.Character, StringComparison.OrdinalIgnoreCase));
                return worker == null || worker.Bags.Count < StorageBankBagTarget || worker.Bags.Any(b => b.Source == "inventory");
            });
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
                var candidates = inventory.Select(r => new { Record = r,
                    State = worker.Bags.SingleOrDefault(b => b.LastUniqueIdentity == r.Identity.ToString()) })
                    .Where(r => r.State?.Items != null).OrderByDescending(r => r.State.Items.Count).ToList();
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
                var owner = RuntimeStateStore.LoadStorageState(_settingsDir).Workers.Single(w =>
                    string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
                var retainedBag = owner.Bags.SingleOrDefault(b => b.LastUniqueIdentity == op.Identity);
                if (_returnOffer == null && !offer.SenderReceiptVerified && !offer.ReceiverReceiptVerified &&
                    ((string.Equals(ReturnDestination(offer), _centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(LayoutDestination(Client.CharacterName), _centralCharacter, StringComparison.OrdinalIgnoreCase)) ||
                        TwinBankFull() || retainedBag?.Source != "inventory" ||
                        retainedBag.OuterSlotInstance != (offer.SourceSlot & 65535)) &&
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
                else
                    SendLayoutAction("opening", record.Bag, () => RequestLayoutContents(record));
                return true;
            }
            if (record.Location != "inventory") { CheckLayoutActionTimeout(); return true; }
            if (op.Phase == "extracting")
            {
                SendLayoutAction("opening", record.Bag, () => RequestLayoutContents(record));
                return true;
            }
            int handle, count;
            // No contents are changed by an outer-only layout operation. Reuse
            // the wire snapshot from THIS connection, even if it arrived before
            // Use or staging. A synthetic SDK empty container is never proof.
            if (!SharedBagRecovery.TryObserveContainer(record.Identity, 0, out handle, out count))
            { CheckLayoutActionTimeout(); return true; }
            if (count != 0) throw new InvalidOperationException("Surplus bag is occupied; its contents remain untouched: " + op.Identity);
            var current = Inventory.Containers.FirstOrDefault(c => c.Identity == record.Identity);
            if (current?.Items?.Count > 0) throw new InvalidOperationException("Surplus bag contains items; retaining it.");
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
            if (TwinBankFull() || AllConfiguredBanksSupplied(RuntimeStateStore.LoadStorageState(_settingsDir), BankerReadiness.GetReadyCharacters(_settingsDir)))
            {
                string identity = op.Identity;
                FinishBankLayout();
                _reserveEmptyArrivals.Add(identity); // actual current-login empty proof above
                BeginReserveOperation(identity, "discard");
                return true;
            }
            var returnedOffer = new ReturnOffer { Id = Guid.NewGuid().ToString("N"), TransactionId = "bag-layout-" + Guid.NewGuid().ToString("N"),
                Source = Client.CharacterName, Destination = LayoutDestination(Client.CharacterName), SourceSlot = record.Bag.Slot.Instance,
                Item = SnapshotTradeItems(new[] { record.Bag })[0], StartedUtc = DateTime.UtcNow };
            ManagerAccounting.Transaction("Prepare verified empty surplus return", () => {
                // Address the transient empty shell so Central can validate its
                // owner. No donor or item ledger entry is created for a backpack.
                RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity,
                    new StorageBagState { Source = "inventory", OuterSlotType = "Inventory", OuterSlotInstance = record.OuterSlot,
                        LastUniqueIdentity = op.Identity, LastHandle = handle, Capacity = BankerRoles.SmallBackpackCapacity });
                SaveReturn(returnedOffer, "intent");
                op.Phase = returnedOffer.Id; SaveBankLayout();
            });
            _returnOffer = returnedOffer;
            _returnRequest = SendReturnMessage(ReturnDestination(returnedOffer), "return-prepare", returnedOffer);
            _returnPhase.Restart();
            return true;
        }

        private static void RequestLayoutContents(StorageBagPolicy.BagRecord record)
        {
            int handle, count;
            if (!SharedBagRecovery.TryObserveContainer(record.Identity, 0, out handle, out count)) record.Bag.Use();
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
            string category = BankerRoles.Canonical(member.Key);
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            var twins = _config.Roles.Where(p => BankerRoles.Canonical(p.Key) == category &&
                !string.Equals(p.Value.Character, source, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value.Character).ToList();
            if (twins.Count == 0) return _centralCharacter;
            return twins.OrderBy(c => storage.Workers.FirstOrDefault(w => string.Equals(w.Character, c,
                StringComparison.OrdinalIgnoreCase))?.Bags.Count(b => b.Source == "bank") ?? 0).First();
        }

        private bool TwinBankFull()
        {
            string twin = LayoutDestination(Client.CharacterName);
            return twin != null && !string.Equals(twin, _centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                RuntimeStateStore.LoadStorageState(_settingsDir).Workers.Any(w =>
                    string.Equals(w.Character, twin, StringComparison.OrdinalIgnoreCase) &&
                    w.Bags.Count(b => b.Source == "bank") >= StorageBankBagTarget);
        }

        private bool ValidLayoutReturn(ReturnOffer offer)
        {
            var op = ManagerMemory.Current.ReadReserveOperation(ManagerAccounting.TransactionId, offer.Source);
            var bag = RuntimeStateStore.LoadStorageState(_settingsDir)?.Workers?.SingleOrDefault(w =>
                string.Equals(w.Character, offer.Source, StringComparison.OrdinalIgnoreCase))?.Bags?.SingleOrDefault(b =>
                    b.LastUniqueIdentity == offer.Item.UniqueIdentity);
            bool destination = string.Equals(ReturnDestination(offer), Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                (_isCentral || string.Equals(LayoutDestination(offer.Source), Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            return destination && op?.Purpose == "layout-return" && op.Phase == offer.Id && op.Identity == offer.Item.UniqueIdentity &&
                bag?.Items?.Count == 0 && bag.Source == "inventory" && bag.OuterSlotInstance == (offer.SourceSlot & 65535);
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
