using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;

namespace CityBankers
{
    /// <summary>
    /// Post-login reconciliation for failed Central -> worker batches.
    ///
    /// This agent never guesses across worker custody. It only repairs failures that are
    /// provably pre-transfer after the all-bankers readiness barrier has opened. The full
    /// expected multiset must be physically visible loose on Central, and no same-batch
    /// storage result may show receipt/storage. Stale same-batch command files and exact
    /// pre-receipt failure results are bookkeeping debris and may then be cleared safely.
    ///
    /// Worker-local storage failures owned by a narrower recovery agent are deliberately
    /// ignored here so Central does not emit a contradictory RECONCILE HOLD while that
    /// worker is about to recover its physically retained items.
    ///
    /// Any other unsafe/ambiguous condition remains failed and is reported to Kavem instead
    /// of silently returning forever.
    /// </summary>
    public class PostLoginFailedBatchReconciliationAgent : ClientlessPluginEntry
    {
        private const int PollMilliseconds = 750;
        private const string CustodyHoldStatus = "custody-hold";
        private const string WorkerLocalFullBagFailure =
            "Live bag is full even though persisted state expected free space. Reconcile before continuing.";
        private const string WorkerLocalPlacementPersistenceFailurePrefix =
            "AO placement succeeded but persistent state update failed:";

        private readonly Dictionary<string, string> _lastDecisionByBatch =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _startupFailedBatchIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _startupTradingBatchIds =
            new HashSet<string>(StringComparer.Ordinal);

        private string _settingsDir;
        private bool _enabled;
        private DateTime _nextPollUtc;

        public override void Init(string pluginDir)
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            if (!CentralCharacterGuard.IsCurrentCharacterCentral(
                _settingsDir,
                Client.CharacterName))
            {
                return;
            }

            _enabled = true;
            DispatchQueueState startupQueue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            foreach (DispatchBatchState batch in startupQueue?.Batches ?? new List<DispatchBatchState>())
            {
                if (batch == null || string.IsNullOrWhiteSpace(batch.BatchId))
                    continue;

                if (string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase))
                    _startupFailedBatchIds.Add(batch.BatchId);
                else if (string.Equals(batch.Status, "trading", StringComparison.OrdinalIgnoreCase))
                    _startupTradingBatchIds.Add(batch.BatchId);
            }
            _nextPollUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;
            Logger.Information(
                "[CityBankers] POST-LOGIN FAILED-BATCH RECONCILIATION initialized on Central.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Client.OnUpdate -= Tick;
            _lastDecisionByBatch.Clear();
            _startupFailedBatchIds.Clear();
            _startupTradingBatchIds.Clear();
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (!TrustedOperators.IsAllBankersReady() || Trade.IsTrading)
                    return;

                DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                List<DispatchBatchState> candidates = (queue?.Batches ?? new List<DispatchBatchState>())
                    .Where(batch =>
                        batch != null &&
                        (IsStartupFailedBatch(batch) ||
                         IsRestartOrphanedTradingBatch(batch)))
                    .ToList();

                foreach (DispatchBatchState batch in candidates)
                {
                    if (TryReconcile(queue, batch, IsRestartOrphanedTradingBatch(batch)))
                        return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "[CityBankers] POST-LOGIN FAILED-BATCH RECONCILIATION failed: " + ex);
            }
        }

        private bool TryReconcile(
            DispatchQueueState queue,
            DispatchBatchState batch,
            bool restartOrphan)
        {
            if (batch == null)
                return false;

            if (string.Equals(
                    batch.LastError,
                    WorkerLocalFullBagFailure,
                    StringComparison.Ordinal) ||
                (batch.LastError != null && batch.LastError.StartsWith(
                    WorkerLocalPlacementPersistenceFailurePrefix,
                    StringComparison.Ordinal)))
            {
                // StorageWriteFrontReconciliationAgent owns these exact post-transfer cases.
                // The items are expected to be stored and/or loose on the destination worker,
                // not Central.
                return false;
            }

            if (!restartOrphan && !IsRecoverablePreTransferFailure(batch.LastError))
            {
                ReportDecisionOnce(
                    batch,
                    "hard",
                    "RECONCILE HOLD " + BatchLabel(batch) +
                    ": failure is not classified as pre-transfer; leaving it failed. " +
                    "Reason: " + Compact(batch.LastError));
                return false;
            }

            int expectedCount = batch.Items?.Count ?? 0;
            if (expectedCount == 0)
            {
                ReportDecisionOnce(
                    batch,
                    "empty",
                    "RECONCILE BLOCKED " + BatchLabel(batch) +
                    ": failed batch has no expected item list; leaving it failed.");
                return false;
            }

            int matchedCount = CountExpectedMultisetInCentral(batch.Items);
            if (matchedCount != expectedCount)
            {
                return PlaceOnCustodyHold(queue, batch, matchedCount, expectedCount);
            }

            string resultPath = RuntimeStateStore.GetStorageResultPath(
                _settingsDir,
                batch.Character);
            bool resultFileExists = File.Exists(resultPath);
            StorageBatchResult result = RuntimeStateStore.ReadStorageResult(
                _settingsDir,
                batch.Character);

            if (resultFileExists && result == null)
            {
                ReportDecisionOnce(
                    batch,
                    "worker-result-unreadable",
                    "RECONCILE BLOCKED " + BatchLabel(batch) +
                    ": worker storage-result file exists but cannot be parsed; leaving it " +
                    "failed rather than guessing about custody.");
                return false;
            }

            bool sameBatchResult = result != null && string.Equals(
                result.BatchId,
                batch.BatchId,
                StringComparison.Ordinal);

            if (sameBatchResult && HasPostTransferCustodyEvidence(result))
            {
                ReportDecisionOnce(
                    batch,
                    "worker-custody-" + result.StoredCount + "-" + result.Success,
                    "RECONCILE BLOCKED " + BatchLabel(batch) +
                    ": worker result contains possible post-transfer custody/storage evidence " +
                    "(success=" + result.Success + ", stored=" + result.StoredCount +
                    "); leaving it failed for physical reconciliation.");
                return false;
            }

            if (sameBatchResult && !IsWorkerPreReceiptFailure(result))
            {
                ReportDecisionOnce(
                    batch,
                    "worker-result-ambiguous",
                    "RECONCILE BLOCKED " + BatchLabel(batch) +
                    ": same-batch worker result is not a recognized pre-receipt failure; " +
                    "leaving it failed. Worker reason: " + Compact(result.Error));
                return false;
            }

            string commandPath = RuntimeStateStore.GetDispatchCommandPath(
                _settingsDir,
                batch.Character);
            bool commandFileExists = File.Exists(commandPath);
            DispatchCommand command = RuntimeStateStore.ReadDispatchCommand(
                _settingsDir,
                batch.Character);

            if (commandFileExists && command == null)
            {
                ReportDecisionOnce(
                    batch,
                    "worker-command-unreadable",
                    "RECONCILE BLOCKED " + BatchLabel(batch) +
                    ": worker dispatch-command file exists but cannot be parsed; leaving it " +
                    "failed rather than guessing about worker intent.");
                return false;
            }

            if (command != null && !string.Equals(
                    command.BatchId,
                    batch.BatchId,
                    StringComparison.Ordinal))
            {
                ReportDecisionOnce(
                    batch,
                    "different-command-" + (command.BatchId ?? "unknown"),
                    "RECONCILE BLOCKED " + BatchLabel(batch) +
                    ": worker has a dispatch command for a different batch " +
                    ShortId(command.BatchId) + "; leaving this batch failed.");
                return false;
            }

            string priorError = restartOrphan
                ? "Host restarted after persisting trading state; no current process owns this trade."
                : batch.LastError;
            bool clearedCommand = command != null;
            bool clearedResult = sameBatchResult;

            if (clearedCommand)
                RuntimeStateStore.DeleteIfExists(commandPath);

            if (clearedResult)
                RuntimeStateStore.DeleteIfExists(resultPath);

            batch.Status = "queued";
            batch.UpdatedUtc = DateTime.UtcNow;
            batch.LastError =
                "Post-login reconciliation verified " + expectedCount + "/" + expectedCount +
                " expected item(s) loose on Central and no post-transfer custody evidence. " +
                "Prior failure: " + priorError;
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);

            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "dispatch_reconciled_after_readiness",
                    TransactionId = batch.TransactionId,
                    BatchId = batch.BatchId,
                    Actor = Client.CharacterName,
                    Role = batch.Role,
                    Character = Client.CharacterName,
                    Source = "central-normal-inventory",
                    Destination = "dispatch-queue",
                    Message =
                        "Post-login reconciliation verified the complete expected multiset " +
                        "on Central and no post-transfer custody evidence. Cleared same-batch " +
                        "staleCommand=" + clearedCommand + ", stalePreReceiptResult=" +
                        clearedResult + ". Prior failure: " + priorError,
                    Items = (batch.Items ?? new List<TransferItemState>())
                        .Select(item => new LedgerItem
                        {
                            UniqueIdentity = item?.UniqueIdentity,
                            AoId = item?.AoId ?? 0,
                            HighId = item?.HighId ?? 0,
                            Ql = item?.Ql ?? 0,
                            Name = item?.Name,
                            Role = batch.Role
                        }).ToList()
                });

            string notice =
                (restartOrphan ? "RESTART ORPHAN RECONCILED " : "RECONCILED ") +
                BatchLabel(batch) + ": Central verified " +
                expectedCount + "/" + expectedCount +
                " expected item(s) loose; no worker custody evidence; " +
                (clearedCommand || clearedResult
                    ? "cleared stale pre-transfer state and "
                    : string.Empty) +
                "requeued for normal dispatch.";
            Logger.Information("[CityBankers] " + notice);
            TellKavem(notice);
            _lastDecisionByBatch.Remove(batch.BatchId ?? string.Empty);
            _startupFailedBatchIds.Remove(batch.BatchId ?? string.Empty);
            _startupTradingBatchIds.Remove(batch.BatchId ?? string.Empty);
            return true;
        }

        private bool PlaceOnCustodyHold(
            DispatchQueueState queue,
            DispatchBatchState batch,
            int matchedCount,
            int expectedCount)
        {
            string priorStatus = batch.Status;
            string priorError = batch.LastError;
            batch.Status = CustodyHoldStatus;
            batch.UpdatedUtc = DateTime.UtcNow;
            batch.LastError =
                "Custody hold: Central physically sees " + matchedCount + "/" +
                expectedCount + " expected item(s) after restart. The retained batch is " +
                "not dispatchable and does not block unrelated donations. Prior status=" +
                priorStatus + "; prior failure: " + priorError;
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);

            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "dispatch_custody_hold",
                    TransactionId = batch.TransactionId,
                    BatchId = batch.BatchId,
                    Actor = Client.CharacterName,
                    Role = batch.Role,
                    Character = Client.CharacterName,
                    Source = "dispatch-queue",
                    Destination = "custody-hold",
                    Message =
                        "Retained incomplete batch as non-dispatchable custody evidence: " +
                        "Central physically sees " + matchedCount + "/" + expectedCount +
                        " expected item(s). Unrelated donations may continue. Prior status=" +
                        priorStatus + "; prior failure: " + priorError,
                    Items = (batch.Items ?? new List<TransferItemState>())
                        .Select(item => new LedgerItem
                        {
                            UniqueIdentity = item?.UniqueIdentity,
                            AoId = item?.AoId ?? 0,
                            HighId = item?.HighId ?? 0,
                            Ql = item?.Ql ?? 0,
                            Name = item?.Name,
                            Role = batch.Role
                        }).ToList()
                });

            string notice =
                "CUSTODY HOLD " + BatchLabel(batch) + ": Central physically sees " +
                matchedCount + "/" + expectedCount +
                " expected item(s). The batch and evidence remain retained, but unrelated " +
                "donations and dispatch may continue.";
            Logger.Warning("[CityBankers] " + notice);
            TellKavem(notice);
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                "central",
                notice);
            _lastDecisionByBatch.Remove(batch.BatchId ?? string.Empty);
            _startupFailedBatchIds.Remove(batch.BatchId ?? string.Empty);
            _startupTradingBatchIds.Remove(batch.BatchId ?? string.Empty);
            return true;
        }

        private bool IsStartupFailedBatch(DispatchBatchState batch)
        {
            return batch != null &&
                   !string.IsNullOrWhiteSpace(batch.BatchId) &&
                   string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                   _startupFailedBatchIds.Contains(batch.BatchId);
        }

        private bool IsRestartOrphanedTradingBatch(DispatchBatchState batch)
        {
            if (batch == null || string.IsNullOrWhiteSpace(batch.BatchId) || !string.Equals(
                    batch.Status, "trading", StringComparison.OrdinalIgnoreCase))
                return false;

            return _startupTradingBatchIds.Contains(batch.BatchId);
        }

        private static bool IsRecoverablePreTransferFailure(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return Contains(error,
                       "Internal worker trade was declined. AO should have returned the offered items to Central inventory.") ||
                   Contains(error,
                       "Internal worker trade timed out. Any incomplete outgoing AO trade is declined so offered items return to Central inventory.") ||
                   Contains(error,
                       "Routed item multiset changed before it could be added to worker trade.") ||
                   Contains(error,
                       "Expected routed item multiset is not present in Central normal inventory; refusing to guess across missing copies.");
        }

        private static bool IsWorkerPreReceiptFailure(StorageBatchResult result)
        {
            if (result == null || result.Success || result.StoredCount != 0 ||
                string.IsNullOrWhiteSpace(result.Error))
            {
                return false;
            }

            return string.Equals(
                       result.Error,
                       "Internal Central trade was declined before completion.",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       result.Error,
                       "Timed out waiting for expected Central trade contents.",
                       StringComparison.Ordinal);
        }

        private static bool HasPostTransferCustodyEvidence(StorageBatchResult result)
        {
            return result != null && (result.Success || result.StoredCount > 0);
        }

        private static int CountExpectedMultisetInCentral(
            IEnumerable<TransferItemState> expectedItems)
        {
            List<Item> available = Inventory.Items == null
                ? new List<Item>()
                : Inventory.Items
                    .Where(item => item != null && item.Slot.Type == IdentityType.Inventory)
                    .OrderBy(item => item.Slot.Instance)
                    .ToList();

            int matched = 0;
            foreach (TransferItemState expected in
                expectedItems ?? Enumerable.Empty<TransferItemState>())
            {
                int index = -1;
                if (IsUsableIdentity(expected?.UniqueIdentity))
                {
                    index = available.FindIndex(item => string.Equals(
                        item.UniqueIdentity.ToString(),
                        expected.UniqueIdentity,
                        StringComparison.Ordinal));
                }

                if (index < 0 && expected != null)
                {
                    index = available.FindIndex(item =>
                        item.Id == expected.AoId &&
                        item.HighId == expected.HighId &&
                        item.Ql == expected.Ql);
                }

                if (index < 0)
                    continue;

                matched++;
                available.RemoveAt(index);
            }

            return matched;
        }

        private void ReportDecisionOnce(
            DispatchBatchState batch,
            string decisionKey,
            string message)
        {
            string batchId = batch?.BatchId ?? "<unknown>";
            string key = decisionKey ?? string.Empty;
            string previous;
            if (_lastDecisionByBatch.TryGetValue(batchId, out previous) &&
                string.Equals(previous, key, StringComparison.Ordinal))
            {
                return;
            }

            _lastDecisionByBatch[batchId] = key;
            Logger.Warning("[CityBankers] " + message);
            TellKavem(message);
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                "central",
                message);
        }

        private void TellKavem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                TrustedOperators.BootstrapAdmin,
                message);
        }

        private static string BatchLabel(DispatchBatchState batch)
        {
            return (batch?.Role ?? "?") + " batch " + ShortId(batch?.BatchId);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";
            return value.Length <= 12 ? value : value.Substring(value.Length - 8);
        }

        private static bool Contains(string text, string value)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                !string.IsNullOrWhiteSpace(value) &&
                text.IndexOf(value, StringComparison.Ordinal) >= 0;
        }

        private static bool IsUsableIdentity(string identity)
        {
            return !string.IsNullOrWhiteSpace(identity) &&
                !string.Equals(identity, Identity.None.ToString(), StringComparison.Ordinal);
        }

        private static string Compact(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "<none>";

            string compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return compact.Length <= 180 ? compact : compact.Substring(0, 177) + "...";
        }
    }
}
