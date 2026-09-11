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
        private sealed class ReturnOffer
        {
            public string Id;
            public string LedgerId;
            public string TransactionId;
            public string Source;
            public int SourceSlot;
            public TransferItemState Item;
            public DateTime StartedUtc;
            public DateTime? CompletedUtc;
        }

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
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(central),
                    JsonConvert.SerializeObject(new DispatchProposal { Kind = kind, Return = offer }),
                    1000, 4000).ConfigureAwait(false);
            }
            catch (Exception) { return "busy"; }
        }

        private static bool SameReturn(ReturnOffer a, ReturnOffer b)
        {
            return a != null && b != null && a.Id == b.Id && a.LedgerId == b.LedgerId &&
                a.TransactionId == b.TransactionId && a.SourceSlot == b.SourceSlot &&
                string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase) &&
                a.Item != null && b.Item != null && CustodyKey(a.Item) == CustodyKey(b.Item);
        }

        private string ReturnDirectory(ReturnOffer offer)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir), "banker-returns", offer.Id);
        }

        private bool HandleReturnProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "return-prepare" && proposal.Kind != "return-sent") return false;
            var offer = proposal.Return;
            Guid id;
            if (!_isCentral || offer?.Item == null || !Guid.TryParseExact(offer.Id, "N", out id))
            { proposal.Reply.TrySetResult("busy"); return true; }
            string completed = Path.Combine(ReturnDirectory(offer), "completed.json");
            if (File.Exists(completed))
            {
                var recorded = JsonConvert.DeserializeObject<ReturnOffer>(File.ReadAllText(completed));
                proposal.Reply.TrySetResult(SameReturn(recorded, offer) ? "complete:" + offer.Id : "busy");
                return true;
            }
            if (proposal.Kind == "return-sent")
            {
                if (SameReturn(_returnOffer, offer))
                {
                    _returnSenderVerified = true;
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(ReturnDirectory(offer), "sender-acknowledged.json"), offer);
                }
                proposal.Reply.TrySetResult("pending");
                return true;
            }
            bool idle = StartupCensusGate.IsOpen && Client.InPlay && Inventory.Bank.IsOpen &&
                !WithdrawalStore.GetCensusCharacters(_settingsDir).Contains(offer.Source) &&
                !Trade.IsTrading && _receipt == null && _activeBatch == null && !_donationActive &&
                _donationCleanup == null && _withdrawal == null && _extraction == null && Inventory.NumFreeSlots >= 2 &&
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
            if (valid)
            {
                _returnOffer = offer;
                _returnPhase.Restart();
                RuntimeStateStore.WriteJsonAtomic(Path.Combine(ReturnDirectory(offer), "prepared.json"), offer);
            }
            proposal.Reply.TrySetResult(valid ? "ready:" + offer.Id : "busy");
            return true;
        }

        private bool TickReturnTransfer()
        {
            if (_returnOffer == null) return false;
            if (_returnLocalVerified)
            {
                if (_isCentral)
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
                    _returnRequest = SendReturnMessage(_centralCharacter, "return-sent", _returnOffer);
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
                if (_isCentral)
                {
                    if (_returnPhase.ElapsedMilliseconds >= 15000) ResetReturn();
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
                    string.Equals(p.Name, _centralCharacter, StringComparison.OrdinalIgnoreCase));
                if (response != "ready:" + _returnOffer.Id || item == null || central == null || CensusReservedHere())
                { ResetReturn(); return false; }
                _returnPartner = central.Identity;
                PrepareReceipt("recovery-return-send", _returnOffer.TransactionId, _returnOffer.Id,
                    new List<TransferItemState> { _returnOffer.Item }, -1);
                _receipt.LedgerIds = new List<string> { _returnOffer.LedgerId };
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
            if (!Trade.IsTrading || Trade.CurrentTarget != _returnPartner) return true;
            var offered = SnapshotTradeItems(_isCentral ? Trade.TargetWindowCache?.Items : Trade.PlayerWindowCache?.Items);
            if (!InternalOfferSettled(offered)) return true;
            if (offered.Count == 1 && CustodyKey(offered[0]) == CustodyKey(_returnOffer.Item))
            {
                // Neither return side may give an unexpected reciprocal item.
                var reciprocal = _isCentral ? Trade.PlayerWindowCache?.Items : Trade.TargetWindowCache?.Items;
                if (reciprocal == null || reciprocal.Count != 0) return true;
                if (!_returnAccepted) { _returnAccepted = true; Trade.Accept(); }
                return true;
            }
            if (!_isCentral && offered.Count == 0 && _returnAddAttempts < ServicePolicy.InternalAddItemMaxAttempts &&
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
            string expected = _isCentral ? _returnOffer.Source : _centralCharacter;
            if (_returnLocalVerified || !string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
            { Trade.Decline(); return true; }
            if (!_isCentral && (!_returnOpenRequested || _receipt == null))
            { Trade.Decline(); return true; }
            _returnPartner = target;
            _returnOpened = true;
            _returnPhase.Restart();
            if (_isCentral)
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
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(ReturnDirectory(_returnOffer),
                        _isCentral ? "received.json" : "sent.json"), new
                        { Offer = _returnOffer, Character = Client.CharacterName, RecordedUtc = DateTime.UtcNow, Evidence = _receipt });
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
                i.HighId == _returnOffer.Item.HighId && i.Ql == _returnOffer.Item.Ql);
        }

        private void CommitReturnOnCentral()
        {
            var offer = _returnOffer;
            var arrived = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                !_returnBeforeSlots.Contains(i.Slot.Instance) && i.Id == offer.Item.AoId &&
                i.HighId == offer.Item.HighId && i.Ql == offer.Item.Ql).ToList();
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
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(ReturnDirectory(offer), "completed.json"), offer);
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
            var ledger = ActiveLedgerStore.LoadLedger(_settingsDir);
            if (ledger == null) return;
            var withdrawals = WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList();
            foreach (var item in Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory))
            {
                var entries = ledger.Items.Where(e => e.AoId == item.Id &&
                    (!e.HighId.HasValue || e.HighId == item.HighId) && (!e.Ql.HasValue || e.Ql == item.Ql) &&
                    string.Equals(e.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    e.Location == "inventory" && !e.Bag.HasValue && e.Slot.HasValue &&
                    (e.Slot.Value & 65535) == (item.Slot.Instance & 65535)).ToList();
                if (entries.Count != 1) continue;
                string destination;
                if (!SymbiantCatalog.TryGetDestinationRole(_settingsDir, item.Id, out destination) &&
                    !SymbiantCatalog.TryGetDestinationRole(_settingsDir, item.HighId, out destination)) destination = "central";
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
