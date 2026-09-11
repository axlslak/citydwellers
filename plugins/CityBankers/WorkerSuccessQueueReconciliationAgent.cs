using System;
using System.Collections.Generic;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;

namespace CityBankers
{
    /// <summary>
    /// Repairs one narrow split-brain state: Central marked a dispatch batch failed while
    /// the destination worker had already completed and durably reported full physical bag
    /// placement for that exact batch.
    ///
    /// A successful StorageBatchResult is produced only after the worker has committed every
    /// expected item to persistent storage state and, for bank bags, verified the staged bag
    /// returned to bank. Therefore exact same-batch/full-count success is stronger custody
    /// evidence than a stale Central pre-transfer failure classification.
    /// </summary>
    public class WorkerSuccessQueueReconciliationAgent : ClientlessPluginEntry
    {
        private const int PollMilliseconds = 100;

        private string _settingsDir;
        private bool _enabled;
        private DateTime _nextPollUtc;

        public override void Init(string pluginDir)
        {
            // A census must not run accounting, handshake, or recovery writers.
            if (ServicePolicy.IsBagAuditMode())
                return;

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
            _nextPollUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;
            Logger.Information(
                "[CityBankers] WORKER-SUCCESS QUEUE RECONCILIATION initialized on Central.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (!TrustedOperators.IsAllBankersReady())
                    return;

                DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                List<DispatchBatchState> failed = (queue?.Batches ?? new List<DispatchBatchState>())
                    .Where(batch =>
                        batch != null &&
                        string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                        batch.Items != null &&
                        batch.Items.Count > 0 &&
                        !string.IsNullOrWhiteSpace(batch.Character))
                    .ToList();

                foreach (DispatchBatchState batch in failed)
                {
                    StorageBatchResult result = RuntimeStateStore.ReadStorageResult(
                        _settingsDir,
                        batch.Character);
                    if (!IsExactFullWorkerSuccess(batch, result))
                        continue;

                    int expected = batch.Items.Count;
                    queue.Batches.Remove(batch);
                    RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);

                    RuntimeStateStore.DeleteIfExists(
                        RuntimeStateStore.GetStorageResultPath(
                            _settingsDir,
                            batch.Character));

                    DispatchCommand command = RuntimeStateStore.ReadDispatchCommand(
                        _settingsDir,
                        batch.Character);
                    if (command != null && string.Equals(
                            command.BatchId,
                            batch.BatchId,
                            StringComparison.Ordinal))
                    {
                        RuntimeStateStore.DeleteIfExists(
                            RuntimeStateStore.GetDispatchCommandPath(
                                _settingsDir,
                                batch.Character));
                    }

                    RuntimeStateStore.AppendLedger(
                        _settingsDir,
                        new LedgerRecord
                        {
                            Utc = DateTime.UtcNow,
                            Event = "dispatch_reconciled_from_worker_storage_success",
                            TransactionId = batch.TransactionId,
                            BatchId = batch.BatchId,
                            Actor = Client.CharacterName,
                            Role = batch.Role,
                            Character = batch.Character,
                            Source = "worker-storage-result",
                            Destination = "queue-cleared",
                            Message =
                                "Central cleared a stale failed dispatch classification only " +
                                "after the destination worker reported exact same-batch full " +
                                "physical storage success " + expected + "/" + expected + ".",
                            Items = batch.Items.Select(item => new LedgerItem
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
                        "RECONCILED QUEUE " + (batch.Role ?? "?") + " batch " +
                        ShortId(batch.BatchId) + ": " + batch.Character +
                        " already reported full physical storage success " + expected +
                        "/" + expected + "; stale failed batch cleared.";
                    Logger.Information("[CityBankers] " + notice);
                    TellKavem(notice);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "[CityBankers] WORKER-SUCCESS QUEUE RECONCILIATION failed: " + ex);
            }
        }

        private static bool IsExactFullWorkerSuccess(
            DispatchBatchState batch,
            StorageBatchResult result)
        {
            if (batch == null || result == null || !result.Success ||
                batch.Items == null || batch.Items.Count == 0)
            {
                return false;
            }

            int expected = batch.Items.Count;
            return string.Equals(
                       result.BatchId,
                       batch.BatchId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       result.Character,
                       batch.Character,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       result.Role,
                       batch.Role,
                       StringComparison.OrdinalIgnoreCase) &&
                   result.ExpectedCount == expected &&
                   result.StoredCount == expected;
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
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                "central",
                "TELL -> " + TrustedOperators.BootstrapAdmin + ": " + message);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";
            return value.Length <= 12 ? value : value.Substring(value.Length - 8);
        }
    }
}
