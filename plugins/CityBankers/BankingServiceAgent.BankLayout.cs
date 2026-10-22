using System;
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
        private const int StorageBankBagTarget = 102;
        private sealed class WorkingBagReturn
        {
            public string Identity;
        }
        private ReserveOperation _bankLayout;
        private WorkingBagReturn _layoutBankReturn;
        private bool _bankLayoutResumed, _emptyWorkerChecked;
        private string _layoutHold;
        private string _layoutReconnectBefore;
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
                        throw new InvalidOperationException("New banker is not empty; use the owner's initial audit before enrollment: " + Client.CharacterName);
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
                var filled = candidates.FirstOrDefault(r => r.State.Items.Count > 0);
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
                var empty = candidates.FirstOrDefault(r => r.State.Items.Count == 0)?.Record ??
                    StorageBagPolicy.AllBagRecords().Where(r => r.Location == "bank").FirstOrDefault(r =>
                        worker.Bags.Any(b => b.LastUniqueIdentity == r.Identity.ToString() && b.Items?.Count == 0));
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
                        if (_storageJob != null && IsReserveBatch(_storageJob.Command.Items) &&
                            _storageJob.Command.Items[0].UniqueIdentity == op.Identity)
                        { _storageJob.StoredCount = _storageJob.Index = 1; CompleteStorageJob(); }
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
                if (_returnOffer == null && ((records.Count == 1 && records[0].Location == "inventory") ||
                    (records.Count == 0 && offer.SenderReceiptVerified)))
                {
                    _returnOffer = offer;
                    _returnLocalVerified = offer.SenderReceiptVerified;
                    _returnRequest = _returnLocalVerified ? null : SendReturnMessage(_centralCharacter, "return-prepare", offer);
                    _returnPhase.Restart();
                }
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
            var returnedOffer = new ReturnOffer { Id = Guid.NewGuid().ToString("N"), TransactionId = "bag-layout-" + Guid.NewGuid().ToString("N"),
                Source = Client.CharacterName, SourceSlot = record.Bag.Slot.Instance,
                Item = SnapshotTradeItems(new[] { record.Bag })[0], StartedUtc = DateTime.UtcNow };
            ManagerAccounting.Transaction("Prepare verified empty surplus return", () => {
                // Address the transient empty shell so Central can validate its
                // owner. No donor or item ledger entry is created for a backpack.
                RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, op.Identity,
                    new StorageBagState { Source = "inventory", OuterSlotType = "Inventory", OuterSlotInstance = record.OuterSlot,
                        LastUniqueIdentity = op.Identity, LastHandle = handle });
                SaveReturn(returnedOffer, "intent");
                op.Phase = returnedOffer.Id; SaveBankLayout();
            });
            _returnOffer = returnedOffer;
            _returnRequest = SendReturnMessage(_centralCharacter, "return-prepare", returnedOffer);
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

        private bool ValidLayoutReturn(ReturnOffer offer)
        {
            var op = ManagerMemory.Current.ReadReserveOperation(ManagerAccounting.TransactionId, offer.Source);
            var bag = RuntimeStateStore.LoadStorageState(_settingsDir)?.Workers?.SingleOrDefault(w =>
                string.Equals(w.Character, offer.Source, StringComparison.OrdinalIgnoreCase))?.Bags?.SingleOrDefault(b =>
                    b.LastUniqueIdentity == offer.Item.UniqueIdentity);
            return op?.Purpose == "layout-return" && op.Phase == offer.Id && op.Identity == offer.Item.UniqueIdentity &&
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
