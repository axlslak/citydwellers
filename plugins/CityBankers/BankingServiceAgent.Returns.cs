using File = CityDwellers.Shared.DiskFiles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {



        private ReturnOffer _returnOffer;
        private Identity _returnPartner = Identity.None;
        private bool _returnOpened;
        private bool _returnOpenRequested;
        private bool _returnAccepted;
        private bool _returnLocalVerified;
        private bool _returnSenderVerified;
        private int _returnAddAttempts;
        private HashSet<int> _returnBeforeSlots;
        private Task<string> _returnRequest;
        private readonly Stopwatch _returnPhase = Stopwatch.StartNew();
        private readonly Stopwatch _returnAddWait = Stopwatch.StartNew();
        private readonly Stopwatch _returnPoll = Stopwatch.StartNew();
        private readonly Stopwatch _returnCandidateWait = Stopwatch.StartNew();
        private string _returnCandidate;
        private string _returnCommitError;
        private bool _returnWaitReported;
        private readonly Stopwatch _returnCommitRetry = Stopwatch.StartNew();

        private static async Task<string> SendReturnMessage(string central, string kind, ReturnOffer offer)
        {
            try
            {
                return await SendBankerMemory(central,
                    new DispatchProposal { Kind = kind, Return = offer }).ConfigureAwait(false);
            }
            catch (Exception) { return "busy"; }
        }

        private static bool SameReturn(ReturnOffer a, ReturnOffer b)
        {
            return a != null && b != null && a.Id == b.Id && a.LedgerId == b.LedgerId &&
                a.TransactionId == b.TransactionId && a.SourceSlot == b.SourceSlot &&
                string.Equals(a.Destination, b.Destination, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase) &&
                a.Item != null && b.Item != null && CustodyKey(a.Item) == CustodyKey(b.Item);
        }

        private string ReturnDestination(ReturnOffer offer) => offer?.Destination ?? _centralCharacter;
        private bool IsReturnReceiver => _returnOffer != null &&
            string.Equals(ReturnDestination(_returnOffer), Client.CharacterName, StringComparison.OrdinalIgnoreCase);

        private static void SaveReturn(ReturnOffer offer, string phase)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction("Return", () => {
                var previous = CityDwellers.Shared.ManagerMemory.Current.ReadReturn(CityDwellers.Shared.ManagerAccounting.TransactionId, offer.Id);
                if (previous != null && !SameReturn(previous, offer))
                    throw new InvalidOperationException("Return ID changed its custody evidence.");
                offer.SenderReceiptVerified |= previous?.SenderReceiptVerified == true || phase == "sent";
                offer.ReceiverReceiptVerified |= previous?.ReceiverReceiptVerified == true || phase == "received";
                offer.Phase = phase;
                CityDwellers.Shared.ManagerMemory.Current.ChangeReturn(CityDwellers.Shared.ManagerAccounting.TransactionId, offer);
            });
        }

        private bool HandleReturnProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "return-prepare" && proposal.Kind != "return-sent" && proposal.Kind != "return-cancel") return false;
            var offer = proposal.Return;
            Guid id;
            if (offer?.Item == null || !Guid.TryParseExact(offer.Id, "N", out id) ||
                !string.Equals(ReturnDestination(offer), Client.CharacterName, StringComparison.OrdinalIgnoreCase) ||
                (!_isCentral && !IsLayoutReturn(offer)))
            { proposal.Reply.TrySetResult("busy"); return true; }
            var recorded = CityDwellers.Shared.ManagerMemory.Current.ReadReturn(
                CityDwellers.Shared.ManagerAccounting.TransactionId, offer.Id);
            if (proposal.Kind == "return-cancel")
            {
                bool cancellable = IsLayoutReturn(offer) && SameReturn(recorded, offer) &&
                    !recorded.SenderReceiptVerified && !recorded.ReceiverReceiptVerified &&
                    !Trade.IsTrading && _receipt == null && !_returnOpened && !_returnLocalVerified &&
                    (_returnOffer == null || SameReturn(_returnOffer, offer));
                if (cancellable)
                {
                    recorded.CompletedUtc = DateTime.UtcNow; SaveReturn(recorded, "cancelled-unsent");
                    if (SameReturn(_returnOffer, offer)) ResetReturn();
                }
                proposal.Reply.TrySetResult(cancellable ? "cancelled:" + offer.Id : "busy");
                return true;
            }
            if (recorded?.CompletedUtc != null)
            {
                proposal.Reply.TrySetResult(SameReturn(recorded, offer) ? "complete:" + offer.Id : "busy");
                return true;
            }
            if (proposal.Kind == "return-sent")
            {
                if (_returnOffer == null && IsLayoutReturn(offer) && recorded?.SenderReceiptVerified == true &&
                    recorded.ReceiverReceiptVerified && ValidLayoutReturn(recorded) && CanStartLocalCensus())
                {
                    var arrived = StorageBagPolicy.AllRecordsFor(offer.Item.UniqueIdentity, "inventory");
                    if (arrived.Count == 1 && StorageBagPolicy.AllRecordsFor(offer.Item.UniqueIdentity, "bank").Count == 0)
                    {
                        _returnOffer = recorded;
                        _returnLocalVerified = _returnSenderVerified = true;
                        _returnBeforeSlots = new HashSet<int>(Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                            i.UniqueIdentity.ToString() != offer.Item.UniqueIdentity).Select(i => i.Slot.Instance));
                    }
                }
                if (SameReturn(_returnOffer, offer))
                {
                    _returnSenderVerified = true;
                    SaveReturn(offer, "sender-acknowledged");
                }
                proposal.Reply.TrySetResult("pending");
                return true;
            }
            bool idle = StartupCensusGate.IsOpen && Client.InPlay && Inventory.Bank.IsOpen &&
                (_isCentral || ReserveBankFreeSlots > 0) &&
                !WithdrawalStore.GetCensusCharacters(_settingsDir).Contains(offer.Source) &&
                !Trade.IsTrading && _receipt == null && _activeBatch == null && !_donationActive &&
                _workerCommand == null && _reservedDispatch == null && _storageJob == null &&
                _donationCleanup == null && _withdrawal == null && _extraction == null && _bankLayout == null &&
                _reserveOperation == null && _bagShop == null && Inventory.NumFreeSlots >= 2 &&
                !WithdrawalStore.LoadAll(_settingsDir).Any(WithdrawalStore.OwnsCentralTrade) &&
                (_returnOffer == null || SameReturn(_returnOffer, offer)) &&
                _config.Roles.Any(pair => !string.Equals(pair.Key, "central", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(pair.Value?.Character, offer.Source, StringComparison.OrdinalIgnoreCase));
            var ledger = idle ? ActiveLedgerStore.LoadLedger(_settingsDir) : null;
            var entry = ledger?.Items?.SingleOrDefault(e => e.Id == offer.LedgerId);
            bool valid = entry != null && entry.TransactionId == offer.TransactionId && entry.AoId == offer.Item.AoId &&
                (!entry.HighId.HasValue || entry.HighId == offer.Item.HighId) &&
                (!entry.Ql.HasValue || entry.Ql == offer.Item.Ql) &&
                string.Equals(entry.Character, offer.Source, StringComparison.OrdinalIgnoreCase) &&
                entry.Location == "inventory" && !entry.Bag.HasValue &&
                (!entry.Slot.HasValue || (entry.Slot.Value & 65535) == (offer.SourceSlot & 65535));
            valid = idle && (valid || (IsLayoutReturn(offer) && ValidLayoutReturn(offer)));
            if (valid)
            {
                _returnOffer = offer;
                _returnPhase.Restart();
                SaveReturn(offer, "prepared");
            }
            proposal.Reply.TrySetResult(valid ? "ready:" + offer.Id : "busy");
            return true;
        }

        private bool TickReturnTransfer()
        {
            if (_returnOffer == null) return false;
            if (_returnLocalVerified)
            {
                if (IsReturnReceiver)
                {
                    if (_returnSenderVerified && _returnCommitRetry.ElapsedMilliseconds >= 1000)
                    {
                        _returnCommitRetry.Restart();
                        try { CommitReturnOnCentral(); }
                        catch (Exception ex)
                        {
                            if (_returnCommitError != ex.Message)
                                Logger.Warning("[CityBankers] Verified return accounting retry: " + ex.Message);
                            _returnCommitError = ex.Message;
                        }
                    }
                }
                else if (_returnRequest == null && _returnPoll.ElapsedMilliseconds >= 500)
                {
                    _returnPoll.Restart();
                    _returnRequest = SendReturnMessage(ReturnDestination(_returnOffer), "return-sent", _returnOffer);
                }
                else if (_returnRequest != null && _returnRequest.IsCompleted)
                {
                    if (_returnRequest.Result == "complete:" + _returnOffer.Id) ResetReturn();
                    else _returnRequest = null;
                }
                if (_returnOffer != null && !_returnWaitReported && _returnPhase.Elapsed.TotalSeconds > 30)
                {
                    _returnWaitReported = true;
                    Logger.Warning("[CityBankers] Verified return still awaiting peer/accounting acknowledgement: " +
                        _returnOffer.Id + "; IPC retries remain active.");
                }
                return true;
            }
            if (!_returnOpened)
            {
                if (IsReturnReceiver)
                {
                    if (_returnPhase.Elapsed.TotalSeconds >= ServicePolicy.TradeTimeoutSeconds) ResetReturn();
                    return _returnOffer != null;
                }
                if (_returnOpenRequested)
                {
                    if (_returnPhase.Elapsed.TotalSeconds >= 20)
                    { TryDeclineTrade(); VerifyCancelledReceipt(); ResetReturn(); }
                    return true;
                }
                if (_returnRequest == null || !_returnRequest.IsCompleted) return true;
                string response = _returnRequest.Result;
                _returnRequest = null;
                var item = FindReturnSourceItem();
                var central = DynelManager.Players.FirstOrDefault(p => p != null &&
                    string.Equals(p.Name, ReturnDestination(_returnOffer), StringComparison.OrdinalIgnoreCase));
                if (response != "ready:" + _returnOffer.Id || _returnPhase.Elapsed.TotalSeconds >= ServicePolicy.TradeTimeoutSeconds ||
                    item == null || central == null || CensusReservedHere())
                { ResetReturn(); return false; }
                _returnPartner = central.Identity;
                PrepareReceipt("recovery-return-send", _returnOffer.TransactionId, _returnOffer.Id,
                    new List<TransferItemState> { _returnOffer.Item }, -1);
                _receipt.LedgerIds = IsLayoutReturn(_returnOffer) ? new List<string>() : new List<string> { _returnOffer.LedgerId };
                PersistReceipt("return-bound");
                _returnOpenRequested = true;
                _returnPhase.Restart();
                Trade.Open(central.Identity);
                // Keep waiting for TradeOpened instead of submitting the item here.
                return true;
            }
            if (_returnPhase.Elapsed.TotalSeconds >= 20)
            {
                TryDeclineTrade();
                VerifyCancelledReceipt();
                ResetReturn();
                return true;
            }
            if (!Trade.IsTrading)
            {
                if (DispatchClosedWithoutResult()) { VerifyCancelledReceipt(); ResetReturn(); }
                return true;
            }
            if (Trade.CurrentTarget != _returnPartner || !DispatchPeerReady("opened")) return true;
            var offered = SnapshotTradeItems(IsReturnReceiver ? Trade.TargetWindowCache?.Items : Trade.PlayerWindowCache?.Items);
            if (!InternalOfferSettled(offered)) return true;
            if (DispatchWindowsConsistent(CurrentInternalCommand()) &&
                (IsReturnReceiver ? DispatchPeerReady("accepted") :
                    offered.Count == 1 && CustodyKey(offered[0]) == CustodyKey(_returnOffer.Item)))
            {
                // Neither return side may give an unexpected reciprocal item.
                var reciprocal = IsReturnReceiver ? Trade.PlayerWindowCache?.Items : Trade.TargetWindowCache?.Items;
                if (reciprocal == null || reciprocal.Count != 0) return true;
                if (!_returnAccepted) { _returnAccepted = true; _localDispatchAcceptAge.Restart(); Trade.Accept(); }
                return true;
            }
            if (!IsReturnReceiver && offered.Count == 0 && _returnAddAttempts < ServicePolicy.InternalAddItemMaxAttempts &&
                (_returnAddAttempts == 0 || _returnAddWait.ElapsedMilliseconds >= ServicePolicy.InternalAddItemRetryMilliseconds))
            {
                var item = FindReturnSourceItem();
                if (item != null)
                {
                    Trade.AddItem(item.Slot);
                    _returnAddAttempts++;
                    _returnAddWait.Restart();
                }
            }
            return true;
        }

        private bool TryReturnTradeOpened(Identity target, string name)
        {
            if (_returnOffer == null) return false;
            string expected = IsReturnReceiver ? _returnOffer.Source : ReturnDestination(_returnOffer);
            if (_returnLocalVerified || !string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
            { Trade.Decline(); return true; }
            if (!IsReturnReceiver && (!_returnOpenRequested || _receipt == null))
            { Trade.Decline(); return true; }
            _returnPartner = target;
            _returnOpened = true;
            _internalOpenedAge.Restart();
            _returnPhase.Restart();
            if (IsReturnReceiver)
            {
                _returnBeforeSlots = new HashSet<int>(Inventory.Items.Where(i => i != null &&
                    i.Slot.Type == IdentityType.Inventory).Select(i => i.Slot.Instance));
                PrepareReceipt("recovery-return-receive", _returnOffer.TransactionId, _returnOffer.Id,
                    new List<TransferItemState> { _returnOffer.Item }, 1);
            }
            return true;
        }

        private bool TryReturnTradeStatus(TradeStatus status)
        {
            if (_returnOffer == null || (!_returnOpened && !_returnLocalVerified)) return false;
            if (_returnLocalVerified) return true;
            // Status callback identities are unreliable; use the partner captured
            // at TradeOpened, just as the donation bridge does.
            if (status == TradeStatus.Confirm) QueueInternalConfirmation(_returnPartner);
            else if (status == TradeStatus.Finished)
                AwaitPhysicalReceipt(new List<TransferItemState> { _returnOffer.Item }, () =>
                {
                    SaveReturn(_returnOffer, IsReturnReceiver ? "received" : "sent");
                    _returnLocalVerified = true;
                    _returnOpened = false;
                    _returnPhase.Restart();
                });
            else if (status == TradeStatus.Declined)
            {
                if (_afterReceipt != null)
                    StartupCensusGate.Block("Return reported Declined after Finished; retaining physical evidence.");
                else { VerifyCancelledReceipt(); ResetReturn(); }
            }
            return true;
        }

        private Item FindReturnSourceItem()
        {
            return Inventory.Items.FirstOrDefault(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                i.Slot.Instance == _returnOffer.SourceSlot && i.Id == _returnOffer.Item.AoId &&
                i.HighId == _returnOffer.Item.HighId && i.Ql == _returnOffer.Item.Ql &&
                (!IsReserveBag(_returnOffer.Item) || i.UniqueIdentity.ToString() == _returnOffer.Item.UniqueIdentity));
        }

        private void CommitReturnOnCentral()
        {
            var offer = _returnOffer;
            var arrived = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                !_returnBeforeSlots.Contains(i.Slot.Instance) && i.Id == offer.Item.AoId &&
                i.HighId == offer.Item.HighId && i.Ql == offer.Item.Ql).ToList();
            if (IsLayoutReturn(offer))
            {
                if (arrived.Count != 1 || arrived[0].UniqueIdentity.ToString() != offer.Item.UniqueIdentity || !ValidLayoutReturn(offer))
                    throw new InvalidOperationException("Surplus backpack receipt or source ownership is ambiguous.");
                CityDwellers.Shared.ManagerAccounting.Transaction("Receive verified empty surplus bag", () => {
                    RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, offer.Source, offer.Item.UniqueIdentity, null);
                    RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, offer.Item.UniqueIdentity,
                        new StorageBagState { Source = "inventory", OuterSlotType = "Inventory",
                            OuterSlotInstance = arrived[0].Slot.Instance & 65535, LastUniqueIdentity = offer.Item.UniqueIdentity,
                            Capacity = CityDwellers.Shared.BankerRoles.SmallBackpackCapacity });
                    var reserve = ReadReserve();
                    if (_isCentral && !reserve.Bags.Any(b => b.Identity == offer.Item.UniqueIdentity && !b.Delivered))
                    {
                        reserve.Bags.RemoveAll(b => b.Identity == offer.Item.UniqueIdentity);
                        reserve.Bags.Add(new ReserveBag { Identity = offer.Item.UniqueIdentity, Transaction = offer.TransactionId });
                    }
                    SaveReserve(reserve);
                    _reserveEmptyArrivals.Add(offer.Item.UniqueIdentity);
                    offer.CompletedUtc = DateTime.UtcNow;
                    SaveReturn(offer, "completed");
                    var operation = CityDwellers.Shared.ManagerMemory.Current.ReadReserveOperation(
                        CityDwellers.Shared.ManagerAccounting.TransactionId, offer.Source);
                    operation.Phase = "completed";
                    CityDwellers.Shared.ManagerMemory.Current.ChangeReserveOperation(
                        CityDwellers.Shared.ManagerAccounting.TransactionId, operation);
                });
                Logger.Information("[CityBankers] SURPLUS BAG received and registered " + offer.Item.UniqueIdentity +
                    " from " + offer.Source + " on " + Client.CharacterName + "; banking before further work.");
                ResetReturn();
                if (ReserveBankFreeSlots > 0) BeginReserveOperation(offer.Item.UniqueIdentity, "bank");
                return;
            }
            ActiveLedgerStore.RecordPhysicalReturn(_settingsDir, offer.LedgerId, offer.TransactionId,
                offer.Source, Client.CharacterName, offer.Item, arrived.Count == 1 ? (int?)arrived[0].Slot.Instance : null);
            string role;
            if (SymbiantCatalog.TryGetDestinationRole(_settingsDir, offer.Item.AoId, out role) ||
                SymbiantCatalog.TryGetDestinationRole(_settingsDir, offer.Item.HighId, out role))
            {
                RoleConfig destination;
                if (!string.Equals(role, "central", StringComparison.OrdinalIgnoreCase) && TryGetRole(role, out destination))
                {
                    var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                    string batch = "return-" + offer.Id;
                    if (!queue.Batches.Any(b => b.BatchId == batch))
                    {
                        queue.Batches.Add(new DispatchBatchState { BatchId = batch, TransactionId = offer.TransactionId,
                            Role = role, Character = destination.Character, Status = "queued", CreatedUtc = DateTime.UtcNow,
                            UpdatedUtc = DateTime.UtcNow, Items = new List<TransferItemState> { offer.Item } });
                        RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                    }
                }
            }
            offer.CompletedUtc = DateTime.UtcNow;
            SaveReturn(offer, "completed");
            ResetReturn();
            try
            {
                RuntimeStateStore.AppendActivity(_settingsDir, Client.CharacterName, _role,
                    "RETURN RECEIVED " + offer.Item.Name + " from " + offer.Source + "; ledger=" + offer.LedgerId +
                    "; destination=" + (role ?? "central review") + ".");
                if (role == null)
                    TellKavem("Central is holding " + offer.Item.Name + " QL" + offer.Item.Ql +
                        " returned by " + offer.Source + " for your review.");
            }
            catch (Exception ex) { Logger.Warning("[CityBankers] Return committed; notification unavailable: " + ex.Message); }
        }

        private void ResetReturn()
        {
            _returnOffer = null;
            _returnWaitReported = false;
            _returnCommitError = null;
            _returnRequest = null;
            _returnPartner = Identity.None;
            _returnOpened = _returnOpenRequested = _returnAccepted = _returnLocalVerified = _returnSenderVerified = false;
            _returnAddAttempts = 0;
            _returnBeforeSlots = null;
            _returnCandidate = null;
            _returnPoll.Restart();
        }

        private void TickLooseReturnRecovery()
        {
            if (_isCentral || _extraction != null || _returnOffer != null || _storageJob != null || _workerCommand != null ||
                _reservedDispatch != null || _receipt != null || _withdrawal != null || Trade.IsTrading ||
                _returnPoll.ElapsedMilliseconds < 1000) return;
            _returnPoll.Restart();
            if (CensusReservedHere()) return;
            var ledger = CityDwellers.Shared.BankerState.ReadLedgerForCharacter<ActiveLedgerState>(Client.CharacterName);
            if (ledger == null) return;
            var withdrawals = WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList();
            var inventory = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory).ToList();
            var rules = SymbiantCatalog.GetRulesFor(_settingsDir,
                inventory.SelectMany(item => new[] { item.Id, item.HighId }));
            foreach (var item in inventory)
            {
                var entries = ledger.Items.Where(e => e.AoId == item.Id &&
                    (!e.HighId.HasValue || e.HighId == item.HighId) && (!e.Ql.HasValue || e.Ql == item.Ql) &&
                    string.Equals(e.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    e.Location == "inventory" && !e.Bag.HasValue && e.Slot.HasValue &&
                    (e.Slot.Value & 65535) == (item.Slot.Instance & 65535)).ToList();
                if (entries.Count != 1) continue;
                SymbiantCatalog.AcceptanceRule rule;
                string destination = rules.TryGetValue(item.Id, out rule) ||
                    rules.TryGetValue(item.HighId, out rule) ? rule.Role : "central";
                if (string.Equals(destination, _role, StringComparison.OrdinalIgnoreCase)) continue;
                if (withdrawals.Any(w => w.ActiveLedgerId == entries[0].Id ||
                    (w.Item != null && w.Item.AoId == item.Id && w.Item.HighId == item.HighId && w.Item.Ql == item.Ql))) continue;
                string candidate = entries[0].Id + "/" + item.Slot.Instance;
                if (_returnCandidate != candidate)
                { _returnCandidate = candidate; _returnCandidateWait.Restart(); return; }
                if (_returnCandidateWait.ElapsedMilliseconds < 500) return;
                _returnOffer = new ReturnOffer { Id = Guid.NewGuid().ToString("N"), LedgerId = entries[0].Id,
                    TransactionId = entries[0].TransactionId, Source = Client.CharacterName,
                    SourceSlot = item.Slot.Instance, Item = SnapshotTradeItems(new[] { item })[0], StartedUtc = DateTime.UtcNow };
                _returnPhase.Restart();
                _returnRequest = SendReturnMessage(_centralCharacter, "return-prepare", _returnOffer);
                return;
            }
        }
    }
}
