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
        private string _receiptDirectory;
        private Action _afterReceipt;
        private Stopwatch _custodyVerificationWait;
        private int _receiptSequence;
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
            if (_confirmationWait.ElapsedMilliseconds < 500) return;
            if (_dispatchConfirmed) { _pendingConfirmation = Identity.None; return; }
            if (_localDispatchAcceptAge.ElapsedMilliseconds < 500 || !LocalInternalAccepted()) return;
            if (_withdrawalPickupTrade)
            {
                if (Trade.TargetWindowCache?.Items == null || Trade.TargetWindowCache.Items.Count != 0 ||
                    Trade.PlayerWindowCache?.Items == null ||
                    !SameManifest(SnapshotTradeItems(Trade.PlayerWindowCache.Items), _pickupItems.Select(r => r.Item))) return;
            }
            else if (!DispatchWindowsConsistent(CurrentInternalCommand()) || !DispatchPeerReady("accepted")) return;
            PersistReceipt("accepted-confirming");
            _dispatchConfirmed = true;
            _pendingConfirmation = Identity.None;
            Trade.Confirm();
        }

        private sealed class ReceiptEvidence
        {
            public string Kind;
            public string TransactionId;
            public string BatchId;
            public string AttemptId;
            public List<TransferItemState> PreparedItems;
            public string Character;
            public string Phase;
            public List<TransferItemState> Before;
            public List<TransferItemState> Expected;
            public List<TransferItemState> Observed;
            // Live object references are valid only in this actor/cache lifetime.
            // Persisted slot evidence remains separate; never deserialize references.
            [Newtonsoft.Json.JsonIgnore]
            public List<Item> LiveBeforeItems;
            public int? WithdrawalArrivalSlot;
            public object BeforeSlots;
            public object ObservedSlots;
            public int Direction;
            public List<string> LedgerIds;
        }

        private List<TransferItemState> PhysicalInventory()
        {
            if (Inventory.Items == null) throw new InvalidOperationException("Inventory unavailable.");
            return SnapshotTradeItems(Inventory.Items.Where(i => i != null &&
                i.Slot.Type == IdentityType.Inventory).ToList());
        }

        private static string CustodyKey(TransferItemState item)
        {
            return item.AoId + "/" + item.HighId + "/" + item.Ql;
        }

        private object PhysicalSlots()
        {
            return Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory)
                .Select(i => new { Slot = i.Slot.ToString(), Identity = i.UniqueIdentity.ToString(),
                    AoId = i.Id, HighId = i.HighId, Ql = i.Ql, Name = i.Name }).ToList();
        }

        private void RecordDonationOffer()
        {
            if (_receipt == null) throw new InvalidOperationException("Donation has no durable preparation.");
            _receipt.Expected = new List<TransferItemState>(_donationSnapshot);
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
            _receiptDirectory = Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir),
                "custody-transactions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_receiptDirectory);
            _receiptSequence = 0;
            _receipt = new ReceiptEvidence { Kind = kind, TransactionId = transaction,
                BatchId = batch, Character = Client.CharacterName, Before = PhysicalInventory(), BeforeSlots = PhysicalSlots(),
                LiveBeforeItems = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory).ToList(),
                Expected = expected == null ? null : new List<TransferItemState>(expected), Direction = direction,
                PreparedItems = expected == null ? null : new List<TransferItemState>(expected),
                AttemptId = kind == "dispatch-send" ? _activeBatch?.AttemptId :
                    kind == "dispatch-receive" ? _workerCommand?.AttemptId :
                    kind == "withdrawal-transfer" ? _withdrawal?.TransferAttemptId :
                    kind == "recovery-return-send" || kind == "recovery-return-receive" ? _returnOffer?.Id : null };
            if (kind == "dispatch-send")
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
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_receipt, Formatting.Indented));
            string path = Path.Combine(_receiptDirectory,
                (_receiptSequence++).ToString("D6") + "-" + phase + ".json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private void AwaitPhysicalReceipt(List<TransferItemState> expected, Action apply)
        {
            if (_afterReceipt != null) return;
            if (_receipt == null) { StartupCensusGate.Block("Finished without prepared custody evidence."); return; }
            _receipt.Expected = new List<TransferItemState>(expected ?? new List<TransferItemState>());
            PersistReceipt("finished-awaiting-inventory");
            _custodyVerificationWait = Stopwatch.StartNew();
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
            string observedSignature = string.Join(";", _receipt.Observed.Select(CustodyKey).OrderBy(key => key));
            if (_settledInventory != observedSignature)
            {
                _settledInventory = observedSignature;
                _inventoryStableFor.Restart();
            }
            var expectedCounts = _receipt.Before.GroupBy(CustodyKey)
                .ToDictionary(group => group.Key, group => group.Count());
            foreach (var group in _receipt.Expected.GroupBy(CustodyKey))
            {
                int count;
                expectedCounts.TryGetValue(group.Key, out count);
                expectedCounts[group.Key] = count + _receipt.Direction * group.Count();
            }
            var actualCounts = _receipt.Observed.GroupBy(CustodyKey)
                .ToDictionary(group => group.Key, group => group.Count());
            bool exact = (_receipt.Direction == 0 || _receipt.Expected.Count > 0) &&
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
                return true;
            }
            if (_inventoryStableFor.ElapsedMilliseconds < 500) return true;
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
