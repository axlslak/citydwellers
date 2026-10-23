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
        private string _returnAdding;
        private readonly Dictionary<string, int> _returnPeerLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int ReturnPeerLimit(string character)
        {
            int limit;
            return character != null && _returnPeerLimits.TryGetValue(character, out limit) ? limit : ServicePolicy.MaxInternalTradeItems;
        }
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

        private static List<ExtractionSlot> ReturnSlots(ReturnOffer offer) => offer?.Bags?.Count > 0
            ? offer.Bags : new List<ExtractionSlot> { new ExtractionSlot { Slot = offer?.SourceSlot ?? 0, Item = offer?.Item } };

        private static List<TransferItemState> ReturnItems(ReturnOffer offer) => ReturnSlots(offer).Select(b => b?.Item).ToList();

        private static string ReturnSlotKey(ExtractionSlot slot) => slot?.Item == null ? "invalid" :
            slot.Slot + "/" + CustodyKey(slot.Item) + "/" + slot.Item.Quantity;

        private static bool SameReturn(ReturnOffer a, ReturnOffer b)
        {
            return a != null && b != null && a.Id == b.Id && a.LedgerId == b.LedgerId &&
                a.TransactionId == b.TransactionId && a.SourceSlot == b.SourceSlot &&
                string.Equals(a.Destination, b.Destination, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase) &&
                a.Item != null && b.Item != null && CustodyKey(a.Item) == CustodyKey(b.Item) &&
                ReturnSlots(a).Select(ReturnSlotKey).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(
                    ReturnSlots(b).Select(ReturnSlotKey).OrderBy(k => k, StringComparer.Ordinal));
        }

        private string ReturnDestination(ReturnOffer offer) => offer?.Destination ?? _centralCharacter;
        private bool IsReturnReceiver => _returnOffer != null &&
            string.Equals(ReturnDestination(_returnOffer), Client.CharacterName, StringComparison.OrdinalIgnoreCase);
        private bool ReturnHasPhysicalWork => _returnOpened || _returnOpenRequested || _returnLocalVerified;
        private static bool HasUnresolvedReturnReceipt(ReturnOffer offer) =>
            CityDwellers.Shared.ManagerMemory.Current.ReadReturnReceipts(
                CityDwellers.Shared.ManagerAccounting.TransactionId, offer.Id).Any(r => r.Phase != "applied" || r.Direction != 0);

        private static void SaveReturn(ReturnOffer offer, string phase)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction("Return", () => {
                var previous = CityDwellers.Shared.ManagerMemory.Current.ReadReturn(CityDwellers.Shared.ManagerAccounting.TransactionId, offer.Id);
                if (previous != null && !SameReturn(previous, offer))
                    throw new InvalidOperationException("Return ID changed its custody evidence.");
                offer.CompletedUtc = offer.CompletedUtc ?? previous?.CompletedUtc;
                offer.SenderReceiptVerified |= previous?.SenderReceiptVerified == true || phase == "sent";
                offer.ReceiverReceiptVerified |= previous?.ReceiverReceiptVerified == true || phase == "received";
                offer.Phase = previous?.CompletedUtc != null ? previous.Phase : phase;
                CityDwellers.Shared.ManagerMemory.Current.ChangeReturn(CityDwellers.Shared.ManagerAccounting.TransactionId, offer);
            });
        }

        private bool HandleReturnProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "return-prepare" && proposal.Kind != "return-sent" && proposal.Kind != "return-cancel") return false;
            var offer = proposal.Return;
            Guid id;
            if (offer?.Item == null || !Guid.TryParseExact(offer.Id, "N", out id) ||
                !string.Equals(ReturnDestination(offer), Client.CharacterName, StringComparison.OrdinalIgnoreCase))
            { proposal.Reply.TrySetResult("busy"); return true; }
            var recorded = CityDwellers.Shared.ManagerMemory.Current.ReadReturn(
                CityDwellers.Shared.ManagerAccounting.TransactionId, offer.Id);
            if (proposal.Kind == "return-cancel")
            {
                bool cancellable = IsLayoutReturn(offer) && SameReturn(recorded, offer) &&
                    !recorded.SenderReceiptVerified && !recorded.ReceiverReceiptVerified &&
                    !HasUnresolvedReturnReceipt(offer) &&
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
                    var identities = new HashSet<string>(ReturnItems(offer).Select(i => i.UniqueIdentity));
                    if (identities.All(identity => StorageBagPolicy.AllRecordsFor(identity, "inventory").Count == 1 &&
                        StorageBagPolicy.AllRecordsFor(identity, "bank").Count == 0))
                    {
                        _returnOffer = recorded;
                        _returnLocalVerified = _returnSenderVerified = true;
                        _returnBeforeSlots = new HashSet<int>(Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                            !identities.Contains(i.UniqueIdentity.ToString())).Select(i => i.Slot.Instance));
                    }
                }
                if (SameReturn(_returnOffer, offer) && recorded?.SenderReceiptVerified == true)
                    _returnSenderVerified = true;
                proposal.Reply.TrySetResult("pending");
                return true;
            }
            if (recorded != null && !SameReturn(recorded, offer))
            { proposal.Reply.TrySetResult("busy"); return true; }
            if (!ValidReturnSource(offer))
            { proposal.Reply.TrySetResult("busy"); return true; }
            if (recorded == null)
            {
                // Legacy single-item senders may not yet have published an intent.
                // Admission is durable, but does not reserve this banker's AO actor.
                if (ReturnQueue().Length >= 64) { proposal.Reply.TrySetResult("busy"); return true; }
                SaveReturn(offer, "queued");
            }
            TryClaimQueuedReturn();
            int room = ReturnReceiverRoom(offer);
            proposal.Reply.TrySetResult(SameReturn(_returnOffer, offer) && IsReturnReceiver
                ? "ready:" + offer.Id : ReturnReceiverIdle() && room > 0 && room < ReturnItems(offer).Count
                    ? "resize:" + room : "queued:" + offer.Id);
            return true;
        }

        private ReturnOffer[] ReturnQueue() => CityDwellers.Shared.ManagerMemory.Current.ReadCharacterReturns(
            CityDwellers.Shared.ManagerAccounting.TransactionId, Client.CharacterName, _isCentral)
            .Where(r => string.Equals(ReturnDestination(r), Client.CharacterName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.StartedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();

        private bool ValidReturnSource(ReturnOffer offer)
        {
            var items = ReturnItems(offer);
            if (items.Count == 0 || items.Count > ServicePolicy.MaxInternalTradeItems || items.Any(i => i == null) ||
                !_config.Roles.Any(pair => !string.Equals(pair.Key, "central", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(pair.Value?.Character, offer.Source, StringComparison.OrdinalIgnoreCase))) return false;
            if (IsLayoutReturn(offer)) return ValidLayoutReturn(offer);
            if (items.Count != 1 || offer.Bags?.Count > 0) return false;
            string role;
            bool routed = SymbiantCatalog.TryGetDestinationRole(_settingsDir, offer.Item.AoId, out role) ||
                SymbiantCatalog.TryGetDestinationRole(_settingsDir, offer.Item.HighId, out role);
            if (!_isCentral && (!routed || !string.Equals(role, _role, StringComparison.OrdinalIgnoreCase))) return false;
            var entry = ActiveLedgerStore.LoadLedger(_settingsDir)?.Items?.SingleOrDefault(e => e.Id == offer.LedgerId);
            return entry != null && entry.TransactionId == offer.TransactionId && entry.AoId == offer.Item.AoId &&
                (!entry.HighId.HasValue || entry.HighId == offer.Item.HighId) &&
                (!entry.Ql.HasValue || entry.Ql == offer.Item.Ql) &&
                string.Equals(entry.Character, offer.Source, StringComparison.OrdinalIgnoreCase) &&
                entry.Location == "inventory" && !entry.Bag.HasValue &&
                (!entry.Slot.HasValue || (entry.Slot.Value & 65535) == (offer.SourceSlot & 65535));
        }

        private bool ReturnReceiverIdle() => StartupCensusGate.IsOpen && Client.InPlay && Inventory.Bank.IsOpen &&
            !Trade.IsTrading && _receipt == null && _afterReceipt == null && _activeBatch == null && !_donationActive &&
            _workerCommand == null && _reservedDispatch == null && _storageJob == null && _stackOperation == null &&
            _donationCleanup == null && _withdrawal == null && _extraction == null && _bankLayout == null &&
            _reserveOperation == null && _bagShop == null && _returnOffer == null &&
            StorageBagPolicy.DuplicatedIdentities().Count == 0;

        private int ReturnReceiverRoom(ReturnOffer offer)
        {
            int room = ServicePolicy.MaxInternalTradeItems;
            if (IsLayoutReturn(offer)) room = Math.Min(ReserveBankFreeSlots, LayoutReturnRoom(Client.CharacterName));
            else if (!_isCentral)
            {
                var own = RuntimeStateStore.LoadStorageState(_settingsDir)?.Workers?.SingleOrDefault(w =>
                    string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
                room = own?.Bags?.Where(b => b.Source == "bank").Sum(b => Math.Max(0, b.Capacity - (b.Items?.Count ?? 0))) ?? 0;
            }
            return Math.Max(0, Math.Min(ServicePolicy.MaxInternalTradeItems, Math.Min(Inventory.NumFreeSlots - 1, room)));
        }

        private bool CanClaimReturn(ReturnOffer offer) => ReturnReceiverIdle() &&
            !WithdrawalStore.GetCensusCharacters(_settingsDir).Contains(offer.Source) &&
            ReturnReceiverRoom(offer) >= ReturnItems(offer).Count &&
            DynelManager.Players.Any(p => p != null && string.Equals(p.Name, offer.Source, StringComparison.OrdinalIgnoreCase)) &&
            !offer.SenderReceiptVerified && !offer.ReceiverReceiptVerified && !HasUnresolvedReturnReceipt(offer) &&
            ValidReturnSource(offer);

        private void TryClaimQueuedReturn()
        {
            if (_returnOffer != null) return;
            // Manager stores the records; this banker alone selects its eligible
            // queue head. A busy/offline peer cannot stall unrelated bankers.
            var next = ReturnQueue().FirstOrDefault(CanClaimReturn);
            if (next == null) return;
            _returnOffer = next;
            _returnPhase.Restart();
            SaveReturn(next, "prepared");
        }

        private bool TickReturnTransfer()
        {
            // A queued send does not occupy this banker's window. If two
            // bankers want to send to each other, the older offer goes first.
            if (_returnOffer != null && !IsReturnReceiver && !ReturnHasPhysicalWork && _receipt == null && !Trade.IsTrading)
            {
                var incoming = ReturnQueue().FirstOrDefault(offer => ValidReturnSource(offer) &&
                    ReturnReceiverRoom(offer) >= ReturnItems(offer).Count &&
                    DynelManager.Players.Any(p => p != null && string.Equals(p.Name, offer.Source, StringComparison.OrdinalIgnoreCase)));
                if (incoming != null && (incoming.StartedUtc < _returnOffer.StartedUtc ||
                    (incoming.StartedUtc == _returnOffer.StartedUtc && string.CompareOrdinal(incoming.Id, _returnOffer.Id) < 0)) &&
                    !_returnOffer.SenderReceiptVerified && !_returnOffer.ReceiverReceiptVerified && !HasUnresolvedReturnReceipt(_returnOffer))
                {
                    _returnOffer.CompletedUtc = DateTime.UtcNow;
                    SaveReturn(_returnOffer, "cancelled-unsent");
                    if (_bankLayout?.Phase == _returnOffer.Id) FinishBankLayout();
                    ResetReturn();
                }
            }
            if (_returnOffer == null) TryClaimQueuedReturn();
            if (_returnOffer == null) return false;
            // Both physical sides publish to the same Manager. A lost request/
            // reply must not hide a committed receipt or completed accounting.
            var recorded = CityDwellers.Shared.ManagerMemory.Current.ReadReturn(
                CityDwellers.Shared.ManagerAccounting.TransactionId, _returnOffer.Id);
            if (recorded != null && SameReturn(recorded, _returnOffer))
            {
                if (recorded.CompletedUtc.HasValue) { ResetReturn(); return true; }
                if (IsReturnReceiver) _returnSenderVerified |= recorded.SenderReceiptVerified;
            }
            if (_returnLocalVerified)
            {
                if (IsReturnReceiver)
                {
                    if (_returnSenderVerified)
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
                        _returnOffer.Id + "; Manager memory acknowledgement remains pending.");
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
                if (_returnRequest == null)
                {
                    if (_returnPoll.ElapsedMilliseconds >= 250)
                    {
                        _returnRequest = SendReturnMessage(ReturnDestination(_returnOffer), "return-prepare", _returnOffer);
                        _returnPoll.Restart();
                    }
                    return true;
                }
                if (!_returnRequest.IsCompleted) return true;
                string response = _returnRequest.Status == TaskStatus.RanToCompletion ? _returnRequest.Result : "busy";
                _returnRequest = null;
                _returnPoll.Restart();
                if (response != "ready:" + _returnOffer.Id)
                {
                    int limit;
                    if (IsLayoutReturn(_returnOffer) && response.StartsWith("resize:", StringComparison.Ordinal) &&
                        int.TryParse(response.Substring(7), out limit) && limit > 0 && limit < ReturnItems(_returnOffer).Count)
                    {
                        _returnPeerLimits[ReturnDestination(_returnOffer)] = limit;
                        ResetReturn(); return false; // cancel UNSENT manifest before making a smaller one
                    }
                    if (IsLayoutReturn(_returnOffer) && (!LayoutReturnSourceMatches(_returnOffer) ||
                        LayoutReturnRoom(ReturnDestination(_returnOffer)) < ReturnItems(_returnOffer).Count))
                    { ResetReturn(); return false; }
                    return true;
                }
                var sourceItems = ReturnSlots(_returnOffer).Select(FindReturnSourceItem).ToList();
                var central = DynelManager.Players.FirstOrDefault(p => p != null &&
                    string.Equals(p.Name, ReturnDestination(_returnOffer), StringComparison.OrdinalIgnoreCase));
                if (sourceItems.Any(i => i == null) || central == null || CensusReservedHere())
                { ResetReturn(); return false; }
                _returnPartner = central.Identity;
                PrepareReceipt("recovery-return-send", _returnOffer.TransactionId, _returnOffer.Id,
                    ReturnItems(_returnOffer), -1);
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
            if (DispatchWindowsConsistent(CurrentInternalCommand()) &&
                (IsReturnReceiver ? DispatchPeerReady("accepted") :
                    SameManifest(offered, ReturnItems(_returnOffer))))
            {
                // Neither return side may give an unexpected reciprocal item.
                var reciprocal = IsReturnReceiver ? Trade.PlayerWindowCache?.Items : Trade.TargetWindowCache?.Items;
                if (reciprocal == null || reciprocal.Count != 0) return true;
                if (!_returnAccepted) { _returnAccepted = true; _localDispatchAcceptAge.Restart(); Trade.Accept(); }
                return true;
            }
            if (!IsReturnReceiver)
            {
                var missing = ReturnSlots(_returnOffer).FirstOrDefault(slot => !offered.Any(i => CustodyKey(i) == CustodyKey(slot.Item)));
                if (missing != null)
                {
                    string key = ReturnSlotKey(missing);
                    if (_returnAdding != key) { _returnAdding = key; _returnAddAttempts = 0; }
                    if (_returnAddAttempts < ServicePolicy.InternalAddItemMaxAttempts &&
                        (_returnAddAttempts == 0 || _returnAddWait.ElapsedMilliseconds >= ServicePolicy.InternalAddItemRetryMilliseconds))
                    {
                        var item = FindReturnSourceItem(missing);
                        if (item != null)
                        {
                            Trade.AddItem(item.Slot);
                            _returnAddAttempts++;
                            _returnAddWait.Restart();
                        }
                    }
                }
            }
            return true;
        }

        private bool TryReturnTradeOpened(Identity target, string name)
        {
            if (_returnOffer == null) return false;
            if (_extraction?.LocalStorage == true || _storageJob?.LocalRecovery == true)
            { Trade.Decline(); return true; }
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
                    ReturnItems(_returnOffer), 1);
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
                AwaitPhysicalReceipt(ReturnItems(_returnOffer), () =>
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

        private Item FindReturnSourceItem(ExtractionSlot source)
        {
            var expected = source.Item;
            if (IsLayoutReturn(_returnOffer))
            {
                var bags = StorageBagPolicy.AllRecordsFor(expected.UniqueIdentity, "inventory");
                if (bags.Count != 1 || StorageBagPolicy.AllRecordsFor(expected.UniqueIdentity, "bank").Count != 0)
                    return null;
                var bag = bags[0];
                return bag.Id == expected.AoId && bag.HighId == expected.HighId && bag.Ql == expected.Ql &&
                    bag.Slot.Instance == source.Slot ? bag : null;
            }
            return Inventory.Items.FirstOrDefault(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                i.Slot.Instance == source.Slot && i.Id == expected.AoId && i.HighId == expected.HighId && i.Ql == expected.Ql &&
                (!IsReserveBag(expected) || i.UniqueIdentity.ToString() == expected.UniqueIdentity));
        }

        private void CommitReturnOnCentral()
        {
            var offer = _returnOffer;
            var arrived = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                !_returnBeforeSlots.Contains(i.Slot.Instance) && i.Id == offer.Item.AoId &&
                i.HighId == offer.Item.HighId && i.Ql == offer.Item.Ql).ToList();
            if (IsLayoutReturn(offer))
            {
                var manifest = ReturnItems(offer);
                var arrivals = manifest.Select(item => Inventory.Items.Where(i => i != null &&
                    i.Slot.Type == IdentityType.Inventory && !_returnBeforeSlots.Contains(i.Slot.Instance) &&
                    i.UniqueIdentity.ToString() == item.UniqueIdentity && i.Id == item.AoId &&
                    i.HighId == item.HighId && i.Ql == item.Ql).ToList()).ToList();
                if (arrivals.Any(items => items.Count != 1) || !ValidLayoutReturn(offer))
                    throw new InvalidOperationException("Surplus batch receipt or source ownership is ambiguous.");
                CityDwellers.Shared.ManagerAccounting.Transaction("Receive surplus bags", () => {
                    // Re-read under the accounting lock before transferring every
                    // shell together. No partial ownership publication is allowed.
                    if (!ValidLayoutReturn(offer)) throw new InvalidOperationException("Surplus source ownership changed.");
                    var reserve = ReadReserve();
                    for (int index = 0; index < manifest.Count; index++)
                    {
                        var item = manifest[index];
                        RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, offer.Source, item.UniqueIdentity, null);
                        RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, item.UniqueIdentity,
                            new StorageBagState { Source = "inventory", OuterSlotType = "Inventory",
                                OuterSlotInstance = arrivals[index][0].Slot.Instance & 65535, LastUniqueIdentity = item.UniqueIdentity,
                                Capacity = CityDwellers.Shared.BankerRoles.SmallBackpackCapacity });
                        if (_isCentral && !reserve.Bags.Any(b => b.Identity == item.UniqueIdentity && !b.Delivered))
                        {
                            reserve.Bags.RemoveAll(b => b.Identity == item.UniqueIdentity);
                            reserve.Bags.Add(new ReserveBag { Identity = item.UniqueIdentity, Transaction = offer.TransactionId });
                        }
                    }
                    SaveReserve(reserve);
                    offer.CompletedUtc = DateTime.UtcNow;
                    SaveReturn(offer, "completed");
                    var operation = CityDwellers.Shared.ManagerMemory.Current.ReadReserveOperation(
                        CityDwellers.Shared.ManagerAccounting.TransactionId, offer.Source);
                    if (operation?.Purpose != "layout-return" || operation.Phase != offer.Id)
                        throw new InvalidOperationException("Surplus batch lost its source intent.");
                    operation.Phase = "completed";
                    CityDwellers.Shared.ManagerMemory.Current.ChangeReserveOperation(
                        CityDwellers.Shared.ManagerAccounting.TransactionId, operation);
                });
                foreach (var item in manifest) _reserveEmptyArrivals.Add(item.UniqueIdentity);
                Logger.Information("[CityBankers] SURPLUS BATCH received and registered " + manifest.Count +
                    " bag(s) from " + offer.Source + " on " + Client.CharacterName + "; banking before further work.");
                ResetReturn();
                // Subsequent shells are selected by the durable local bank-layout
                // actor after this acknowledged move; restart never replays trade.
                if (ReserveBankFreeSlots > 0) BeginReserveOperation(manifest[0].UniqueIdentity, "bank");
                return;
            }
            if (arrived.Count != 1) throw new InvalidOperationException("Returned item does not have one arrival slot.");
            ActiveLedgerStore.RecordPhysicalReturn(_settingsDir, offer.LedgerId, offer.TransactionId,
                offer.Source, Client.CharacterName, offer.Item, arrived[0].Slot.Instance);
            if (!_isCentral)
            {
                offer.CompletedUtc = DateTime.UtcNow;
                SaveReturn(offer, "completed");
                ResetReturn();
                _storageJob = new StorageJob { LocalRecovery = true, RecoverySlot = arrived[0].Slot.Instance,
                    RecoveryLedgerId = offer.LedgerId, Command = new DispatchCommand {
                        BatchId = "local-" + offer.LedgerId, TransactionId = offer.TransactionId, Role = _role,
                        SourceCharacter = Client.CharacterName, DestinationCharacter = Client.CharacterName,
                        CreatedUtc = DateTime.UtcNow, Items = new List<TransferItemState> { offer.Item } },
                    Phase = StoragePhase.FindBag, PhaseStartedUtc = DateTime.UtcNow,
                    DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.ItemMoveTimeoutMs) };
                Logger.Information("[CityBankers] ITEM received directly from " + offer.Source + " on " + Client.CharacterName + ".");
                return;
            }
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
                    ReportBankerProblem("Central is holding " + offer.Item.Name + " QL" + offer.Item.Ql +
                        " returned by " + offer.Source + " and recorded it for automatic routing.");
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
            _returnAdding = null;
            _returnBeforeSlots = null;
            _returnCandidate = null;
            _returnPoll.Restart();
        }

        private string DirectItemDestination(string role)
        {
            RoleConfig destination;
            return TryGetRole(role, out destination) && IsRoleEnabled(role) &&
                !string.Equals(destination.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase)
                ? destination.Character : null;
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
                var expected = SnapshotTradeItems(new[] { item })[0];
                _returnOffer = CityDwellers.Shared.ManagerMemory.Current.ReadCharacterReturns(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, Client.CharacterName).Where(offer =>
                        string.Equals(offer.Source, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        offer.LedgerId == entries[0].Id && offer.TransactionId == entries[0].TransactionId &&
                        offer.SourceSlot == item.Slot.Instance && offer.Item != null &&
                        CustodyKey(offer.Item) == CustodyKey(expected))
                    .OrderBy(offer => offer.StartedUtc).ThenBy(offer => offer.Id, StringComparer.Ordinal).FirstOrDefault();
                string direct = DirectItemDestination(destination);
                if (_returnOffer != null && direct != null &&
                    !string.Equals(ReturnDestination(_returnOffer), direct, StringComparison.OrdinalIgnoreCase) &&
                    !_returnOffer.SenderReceiptVerified && !_returnOffer.ReceiverReceiptVerified && !HasUnresolvedReturnReceipt(_returnOffer))
                {
                    _returnOffer.CompletedUtc = DateTime.UtcNow;
                    SaveReturn(_returnOffer, "cancelled-unsent");
                    _returnOffer = null;
                }
                if (_returnOffer == null)
                {
                    _returnOffer = new ReturnOffer { Id = Guid.NewGuid().ToString("N"), LedgerId = entries[0].Id,
                        TransactionId = entries[0].TransactionId, Source = Client.CharacterName,
                        Destination = direct, SourceSlot = item.Slot.Instance, Item = expected, StartedUtc = DateTime.UtcNow };
                    if (_returnOffer.Destination == null) { _returnOffer = null; continue; }
                    SaveReturn(_returnOffer, "queued");
                }
                _returnLocalVerified = _returnOffer.SenderReceiptVerified;
                _returnPhase.Restart();
                _returnRequest = _returnLocalVerified ? null : SendReturnMessage(ReturnDestination(_returnOffer), "return-prepare", _returnOffer);
                return;
            }
        }
    }
}
