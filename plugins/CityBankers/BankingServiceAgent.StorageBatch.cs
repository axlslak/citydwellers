using System;
using System.Collections.Generic;
using System.Linq;
using AOSharp.Clientless;
using CityBankers.Shared;
using CityDwellers.Shared;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private bool _storageIntentResumeChecked;
        private bool ResumeStorageBagIntent()
        {
            if (_storageIntentResumeChecked) return false;
            _storageIntentResumeChecked = true;
            if (_storageJob != null) return false;
            var intent = ManagerMemory.Current.ReadCharacterReceipts(ManagerAccounting.TransactionId, Client.CharacterName)
                .FirstOrDefault(r => r.Kind == "storage-bag" && r.Phase != "applied");
            if (intent == null) return false;
            StartupCensusGate.Block("Resolve interrupted working bag " + intent.Id + " from native custody.");
            return true;
        }

        private sealed class PendingStoragePlacement
        {
            public TransferItemState Expected;
            public string ObservedStoredItemIdentity;
            public int InnerSlot, BagHandle;
        }

        private void RecordStorageBagIntent(Item bag)
        {
            var job = _storageJob;
            job.BagIntent = "storage-bag-" + Guid.NewGuid().ToString("N");
            ManagerAccounting.Transaction("Storage working bag", () =>
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, new ReceiptEvidence {
                    Id = job.BagIntent, Kind = "storage-bag", Character = Client.CharacterName,
                    TransactionId = job.Command.TransactionId, BatchId = job.Command.BatchId,
                    AttemptId = job.Command.AttemptId, Phase = "staging",
                    Before = SnapshotTradeItems(new[] { bag }),
                    Expected = job.Command.Items.Skip(job.Index).ToList() }));
        }

        private void QueueStoragePlacement(Container container)
        {
            var job = _storageJob;
            job.PendingPlacements.Add(new PendingStoragePlacement { Expected = job.Expected,
                ObservedStoredItemIdentity = job.ObservedStoredItemIdentity,
                InnerSlot = job.InnerSlot, BagHandle = job.BagHandle });
            job.Index++;
            if (job.Index < job.Command.Items.Count && !container.IsFull &&
                job.Bag.Items.Count + job.PendingPlacements.Count < job.Bag.Capacity)
            {
                var expected = job.Command.Items[job.Index];
                var item = FindStorageInventoryItem(expected);
                if (item == null) { FailStorageJob("Next received item is unavailable while filling its working bag."); return; }
                job.Expected = expected;
                job.ActualItemIdentity = IsUsableIdentity(item.UniqueIdentity.ToString()) ? item.UniqueIdentity.ToString() : null;
                job.ObservedStoredItemIdentity = null;
                job.InnerSlot = -1;
                SendStorageItemMove(item, container);
                return;
            }
            var liveBag = FindInventoryBagByIdentity(job.BagLiveIdentity);
            if (liveBag == null) { FailStorageJob("Filled working bag is unavailable for bank return."); return; }
            job.Phase = StoragePhase.ReturningBag;
            SetStorageDeadline(ServicePolicy.BagMoveTimeoutMs);
            job.BankReturn = BeginWorkingBagReturn(liveBag);
        }

        private void ApplyStoredPlacement(PendingStoragePlacement placement)
        {
            string placementError;
            if (!RuntimeStateStore.RecordPlacement(
                _settingsDir,
                _storageJob.Command.TransactionId,
                _role,
                Client.CharacterName,
                _storageJob.Bag.Source,
                _storageJob.Bag.OuterSlotInstance,
                _storageJob.BagLiveIdentity,
                placement.BagHandle,
                placement.Expected,
                placement.ObservedStoredItemIdentity,
                placement.InnerSlot,
                out placementError))
                throw new InvalidOperationException(
                    "Storage state update failed: " + (placementError ?? "unknown error"));

            string sourceCharacter;
            IEnumerable<string> ledgerIds;
            if (_storageJob.LocalRecovery)
            {
                if (string.IsNullOrWhiteSpace(_storageJob.RecoveryLedgerId))
                    throw new InvalidOperationException(
                        "Verified local-recovery storage placement has no exact ledger occurrence.");

                // Local recovery starts from one exact active-ledger
                // occurrence already observed loose on this worker. It
                // has no Central dispatch attempt or sender receipt.
                sourceCharacter = Client.CharacterName;
                ledgerIds = new[] { _storageJob.RecoveryLedgerId };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_storageJob.Command.AttemptId))
                    throw new InvalidOperationException(
                        "Verified storage placement has no dispatch attempt identity.");

                ReceiptEvidence senderReceipt =
                    CityDwellers.Shared.ManagerMemory.Current.ReadCancellationReceipt(
                        CityDwellers.Shared.ManagerAccounting.TransactionId,
                        _storageJob.Command.AttemptId,
                        "dispatch-send");
                if (senderReceipt == null ||
                    !string.Equals(senderReceipt.AttemptId, _storageJob.Command.AttemptId, StringComparison.Ordinal) ||
                    !string.Equals(senderReceipt.BatchId, _storageJob.Command.BatchId, StringComparison.Ordinal) ||
                    !string.Equals(senderReceipt.TransactionId, _storageJob.Command.TransactionId, StringComparison.Ordinal) ||
                    !string.Equals(senderReceipt.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase) ||
                    !SameManifest(senderReceipt.PreparedItems, _storageJob.Command.Items) ||
                    senderReceipt.LedgerIds == null ||
                    senderReceipt.LedgerIds.Count != (_storageJob.Command.Items?.Count ?? 0) ||
                    senderReceipt.LedgerIds.Any(string.IsNullOrWhiteSpace) ||
                    senderReceipt.LedgerIds.Distinct().Count() != senderReceipt.LedgerIds.Count)
                    throw new InvalidOperationException(
                        "Verified storage placement lacks the exact sender ledger receipt for this dispatch attempt.");

                sourceCharacter = senderReceipt.Character;
                ledgerIds = senderReceipt.LedgerIds;
            }

            ActiveLedgerStore.RecordStoredPlacement(
                _settingsDir,
                _storageJob.Command.TransactionId,
                sourceCharacter,
                Client.CharacterName,
                _storageJob.Bag.Source,
                _storageJob.Bag.OuterSlotInstance,
                placement.InnerSlot,
                placement.Expected,
                ledgerIds);
        }

        private void RecordStoragePlacementHistory(PendingStoragePlacement placement)
        {
            LedgerRecord record = new LedgerRecord
            {
                Utc = DateTime.UtcNow,
                Event = "item_stored",
                TransactionId = _storageJob.Command.TransactionId,
                BatchId = _storageJob.Command.BatchId,
                Actor = Client.CharacterName,
                Role = _role,
                Character = Client.CharacterName,
                Source = "normal-inventory",
                Destination =
                    _storageJob.Bag.Source + ":" + _storageJob.Bag.OuterSlotInstance +
                    "/inner:" + placement.InnerSlot,
                Message = "AO confirmed item inside storage bag and required bank-bag return was verified.",
                Items = new List<LedgerItem>
                {
                    ToLedgerItem(
                        placement.Expected,
                        _role,
                        _storageJob.Bag.Source,
                        _storageJob.Bag.OuterSlotInstance,
                        placement.InnerSlot)
                }
            };
            RuntimeStateStore.AppendLedger(_settingsDir, record);
        }

        private void CommitStorageBag(Item returned)
        {
            var job = _storageJob;
            try
            {
                ManagerAccounting.Transaction("Commit filled storage bag", () =>
                {
                    RuntimeStorageStateTransactions.RecordBankReturn(_settingsDir, Client.CharacterName,
                        job.BagLiveIdentity, returned.Slot.Instance);
                    job.Bag.Source = "bank";
                    job.Bag.OuterSlotType = "BankByRef";
                    job.Bag.OuterSlotInstance = returned.Slot.Instance & 65535;
                    foreach (var placement in job.PendingPlacements)
                    {
                        ApplyStoredPlacement(placement);
                        RecordStoragePlacementHistory(placement);
                    }
                    var intent = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, job.BagIntent);
                    if (intent == null) throw new InvalidOperationException("Working bag intent was lost before accounting.");
                    intent.Phase = "applied";
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, intent);
                });
            }
            catch (Exception ex)
            {
                FailStorageJob("AO placements succeeded but atomic storage/ledger update failed: " + ex.Message);
                return;
            }
            _localCensusMismatch = null;
            _localCensusScan.Restart();
            foreach (var placement in job.PendingPlacements)
                ReportTransferProgress("STORED VERIFIED", job.Command.BatchId,
                    "transaction=" + job.Command.TransactionId + "; destination=" + Client.CharacterName +
                    "; " + placement.Expected.Name + " QL" + placement.Expected.Ql + " AOID=" + placement.Expected.AoId +
                    "; bank:" + job.Bag.OuterSlotInstance + "/inner:" + placement.InnerSlot);
            job.StoredCount += job.PendingPlacements.Count;
            job.PendingPlacements.Clear();
            job.BagIntent = null;
            job.Expected = null; job.Bag = null; job.BagLiveIdentity = null; job.BankReturn = null;
            job.ActualItemIdentity = null; job.ObservedStoredItemIdentity = null;
            job.InnerSlot = -1; job.BagHandle = 0; job.Move = null;
            job.Phase = StoragePhase.FindBag;
            SetStorageDeadline(ServicePolicy.ItemMoveTimeoutMs);
        }
    }
}
