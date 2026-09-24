using Directory = System.IO.Directory;
using TradeTrace = CityDwellers.Shared.TradeTrace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private ReceiptEvidence _receipt;
        private List<Item> _receiptLiveBeforeItems;
        private Action _afterReceipt;
        private bool _lateReceiptDeclineReported;
        private Stopwatch _custodyVerificationWait;
        private string _settledOffer;
        private readonly Stopwatch _offerStableFor = Stopwatch.StartNew();
        private string _settledInventory;
        private readonly Stopwatch _inventoryStableFor = Stopwatch.StartNew();
        private Identity _pendingConfirmation = Identity.None;
        private readonly Stopwatch _confirmationWait = Stopwatch.StartNew();

        private bool InternalOfferSettled(IEnumerable<TransferItemState> items)
        {
            string signature = string.Join(";", items.Select(CustodyKey).OrderBy(key => key));
            if (signature != _settledOffer)
            {
                _settledOffer = signature;
                _offerStableFor.Restart();
                return false;
            }
            return _offerStableFor.ElapsedMilliseconds >= 500;
        }

        private void QueueInternalConfirmation(Identity target)
        {
            if (_pendingConfirmation == target) return;
            _pendingConfirmation = target;
            _confirmationWait.Restart();
        }

        private void TickInternalConfirmation()
        {
            // Player pickup owns its modal handshake; this path is for bot peers.
            if (_withdrawalPickupTrade) { _pendingConfirmation = Identity.None; return; }
            if (_pendingConfirmation == Identity.None) return;
            if (!Trade.IsTrading || Trade.CurrentTarget != _pendingConfirmation)
            { _pendingConfirmation = Identity.None; return; }
            if (_dispatchConfirmed) { _pendingConfirmation = Identity.None; return; }
            if (!LocalInternalAccepted()) return;

            bool cautiousRecovery =
                _receipt != null &&
                (_receipt.Kind == "recovery-return-send" || _receipt.Kind == "recovery-return-receive");
            if (cautiousRecovery)
            {
                if (_confirmationWait.ElapsedMilliseconds < 500 ||
                    _localDispatchAcceptAge.ElapsedMilliseconds < 500 ||
                    !DispatchWindowsConsistent(CurrentInternalCommand()) ||
                    !DispatchPeerReady("accepted")) return;
            }
            else if (!DispatchWindowsExact(CurrentInternalCommand())) return;

            PersistReceipt("accepted-confirming");
            _dispatchConfirmed = true;
            _pendingConfirmation = Identity.None;
            Trade.Confirm();
        }



        private List<TransferItemState> PhysicalInventory()
        {
            if (Inventory.Items == null) throw new InvalidOperationException("Inventory unavailable.");
            return SnapshotTradeItems(Inventory.Items.Where(i => i != null &&
                i.Slot.Type == IdentityType.Inventory).ToList());
        }

        private static string CustodyKey(TransferItemState item)
        {
            return item.AoId + "/" + item.HighId + "/" + item.Ql +
                (IsReserveBag(item) ? "/" + item.UniqueIdentity : "");
        }

        private List<CustodySlot> PhysicalSlots()
        {
            return Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory)
                .Select(i => new CustodySlot { Slot = i.Slot.ToString(), Identity = i.UniqueIdentity.ToString(),
                    AoId = i.Id, HighId = i.HighId, Ql = i.Ql, Name = i.Name, Quantity = StackableItems.Quantity(i) }).ToList();
        }

        private void RecordDonationOffer()
        {
            if (_receipt == null) throw new InvalidOperationException("Donation has no durable preparation.");
            _receipt.Expected = new List<TransferItemState>(_donationSnapshot);
            EnrollReserveOffer();
            PersistReceipt("offer-validated");
        }

        private void PrepareReceipt(string kind, string transaction, string batch,
            List<TransferItemState> expected, int direction)
        {
            if (_receipt != null)
                throw new InvalidOperationException("Unresolved custody evidence prevents another trade.");
            _dispatchConfirmed = false;
            _dispatchClosedAge = null;
            _tradeStages.Clear();
            _recordedTradeStages.Clear();
            _settledOffer = null;
            _settledInventory = null;
            _pendingConfirmation = Identity.None;
            _receiptLiveBeforeItems = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory).ToList();
            _reserveReceiptObservationAfter = SharedBagRecovery.ContainerObservationSequence;
            if (kind == "dispatch-send") BindReserveProofToAttempt(expected, batch, transaction);
            _receipt = new ReceiptEvidence { Id = Guid.NewGuid().ToString("N"), Kind = kind, TransactionId = transaction,
                BatchId = batch, Character = Client.CharacterName, Before = PhysicalInventory(), BeforeSlots = PhysicalSlots(),
                Expected = expected == null ? null : new List<TransferItemState>(expected), Direction = direction,
                PreparedItems = expected == null ? null : new List<TransferItemState>(expected),
                AttemptId = kind == "dispatch-send" ? _activeBatch?.AttemptId :
                    kind == "dispatch-receive" ? _workerCommand?.AttemptId :
                    kind == "withdrawal-transfer" ? _withdrawal?.TransferAttemptId :
                    kind == "recovery-return-send" || kind == "recovery-return-receive" ? _returnOffer?.Id : null };
            if (kind == "dispatch-send" && !IsReserveBatch(expected))
            {
                var ledger = ActiveLedgerStore.LoadLedger(_settingsDir);
                _receipt.LedgerIds = new List<string>();
                foreach (var group in expected.GroupBy(CustodyKey))
                {
                    var template = group.First();
                    var matches = (ledger?.Items ?? new List<ActiveLedgerItem>()).Where(item =>
                        item.AoId == template.AoId && item.HighId == template.HighId && item.Ql == template.Ql &&
                        item.TransactionId == transaction &&
                        string.Equals(item.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        item.Location == "inventory" && item.Bag == null)
                        .OrderBy(item => item.Id, StringComparer.Ordinal).Take(group.Count()).ToList();
                    if (matches.Count != group.Count() || matches.Any(item => string.IsNullOrWhiteSpace(item.Id)))
                        throw new InvalidOperationException("Dispatch has no matching sender-held ledger occurrences.");
                    _receipt.LedgerIds.AddRange(matches.Select(item => item.Id));
                }
                if (_receipt.LedgerIds.Distinct().Count() != expected.Count)
                    throw new InvalidOperationException("Dispatch ledger occurrence IDs are not unique.");
            }
            PersistReceipt("prepared");
        }

        private void PersistReceipt(string phase)
        {
            _receipt.Phase = phase;
            CityDwellers.Shared.ManagerAccounting.Transaction("Custody", () =>
                CityDwellers.Shared.ManagerMemory.Current.ChangeReceipt(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, _receipt));
            CityDwellers.Shared.IncidentJournal.Record(RuntimeStateStore.GetDataDirectory(_settingsDir),
                _receipt.TransactionId ?? _receipt.BatchId, Client.CharacterName, "receipt." + phase, _receipt,
                CityDwellers.Shared.IncidentJournal.IsProblem(phase), new[] { _receipt.BatchId, _tradeTrace });
        }

        private void AwaitPhysicalReceipt(List<TransferItemState> expected, Action apply)
        {
            if (_afterReceipt != null) return;
            if (_receipt == null) { StartupCensusGate.Block("Finished without prepared custody evidence."); return; }
            _receipt.Expected = new List<TransferItemState>(expected ?? new List<TransferItemState>());
            PersistReceipt("finished-awaiting-inventory");
            _custodyVerificationWait = Stopwatch.StartNew();
            _lateReceiptDeclineReported = false;
            _afterReceipt = apply;
        }

        private bool TickPhysicalReceipt()
        {
            if (_afterReceipt == null) return false;
            // A cancellation request is not a closed trade. Wait for AO to
            // close it before treating an unchanged inventory as returned custody.
            if (_receipt.Direction == 0 && Trade.IsTrading)
            {
                _settledInventory = null;
                _inventoryStableFor.Restart();
                return true;
            }
            _receipt.Observed = PhysicalInventory();
            _receipt.ObservedSlots = PhysicalSlots();
            string observedSignature = string.Join(";", _receipt.Observed.Select(i => CustodyKey(i) + "/" + i.Quantity).OrderBy(key => key));
            if (_settledInventory != observedSignature)
            {
                _settledInventory = observedSignature;
                _inventoryStableFor.Restart();
            }
            bool includesCru = _receipt.Expected.Any(i => CruPolicy.IsCru(i.AoId));
            // Background quantity updates to the supply stack are irrelevant to an
            // ordinary symbiant receipt. A CRU pickup/donation verifies unit deltas.
            Func<TransferItemState, bool> relevant = i => includesCru || !CruPolicy.IsCru(i.AoId);
            var expectedCounts = _receipt.Before.Where(relevant).GroupBy(CustodyKey)
                .ToDictionary(group => group.Key, group => group.Sum(i => i.Quantity));
            foreach (var group in _receipt.Expected.GroupBy(CustodyKey))
            {
                int count;
                expectedCounts.TryGetValue(group.Key, out count);
                expectedCounts[group.Key] = count + _receipt.Direction * group.Sum(i => i.Quantity);
            }
            var actualCounts = _receipt.Observed.Where(relevant).GroupBy(CustodyKey)
                .ToDictionary(group => group.Key, group => group.Sum(i => i.Quantity));
            bool knownCru = !includesCru || _receipt.Before.Concat(_receipt.Observed).Concat(_receipt.Expected)
                .Where(i => CruPolicy.IsCru(i.AoId)).All(i => i.Quantity > 0);
            bool exact = knownCru && (_receipt.Direction == 0 || _receipt.Expected.Count > 0) &&
                expectedCounts.Where(pair => pair.Value != 0).OrderBy(pair => pair.Key)
                    .SequenceEqual(actualCounts.OrderBy(pair => pair.Key));
            if (!exact)
            {
                if (_custodyVerificationWait.Elapsed.TotalSeconds >= 10)
                {
                    if ((IsDispatchEvidence(_receipt) || WithdrawalReceipt(_receipt)) &&
                        !Trade.IsTrading && _inventoryStableFor.ElapsedMilliseconds < 1000)
                        return true;
                    PersistReceipt("inventory-mismatch");
                    if (BeginDispatchDispute()) return true;
                    if (BeginWithdrawalDispute("Withdrawal inventory differs from its receipt; refreshing affected custody.")) return true;
                    StartupCensusGate.Block("Physical inventory delta did not match " +
                        _receipt.Kind + " transaction " + _receipt.TransactionId +
                        ". Evidence retained; no receipt inferred from the trade window.");
                }
                TradeTrace.Wait(_dispatchSpan, "inventory-delta-exact");
                TradeTrace.Wait(_storageSpan, "inventory-delta-exact");
                return true;
            }
            // An artificial settle delay, named with its constant so the trace says
            // what it is. The inventory delta already matched; this is 500ms of
            // deliberate waiting on top of that, per receipt.
            if (_inventoryStableFor.ElapsedMilliseconds < 500)
            {
                TradeTrace.Wait(_dispatchSpan, "settle-timer-500ms");
                TradeTrace.Wait(_storageSpan, "settle-timer-500ms");
                return true;
            }
            // Stage 17: post-trade physical evidence is complete on this side.
            TradeTrace.Mark(_dispatchSpan, "posttrade.evidence.complete");
            TradeTrace.Mark(_storageSpan, "posttrade.evidence.complete");
            PersistReceipt("inventory-verified");
            Action apply = _afterReceipt;
            // An exception after an AO action must not automatically replay it next tick.
            _afterReceipt = null;
            try
            {
                apply();
                PersistReceipt("applied");
                RetainCancellationForPeer(_receipt);
                RetainAppliedDispatchReceipt(_receipt);
                if (_receipt.Direction == 0 && _receipt.Kind == "donation")
                    ReportTransferProgress("CANCELLATION VERIFIED", _receipt.BatchId,
                        "transaction=" + _receipt.TransactionId + "; inventory unchanged; no donation received and no audit required.");
                _receipt = null;
            }
            catch (Exception ex)
            {
                if (BeginDispatchDispute()) return true;
                if (BeginWithdrawalDispute("Withdrawal accounting was interrupted; evidence retained: " + ex.Message)) return true;
                StartupCensusGate.Block("Verified custody could not be committed: " + ex);
            }
            return true;
        }

        private void VerifyCancelledReceipt()
        {
            if (_receipt == null || _afterReceipt != null) return;
            _receipt.Direction = 0;
            _receipt.Expected = new List<TransferItemState>();
            PersistReceipt("cancelled-awaiting-inventory");
            _custodyVerificationWait = Stopwatch.StartNew();
            _afterReceipt = () => { };
        }
    }
}
