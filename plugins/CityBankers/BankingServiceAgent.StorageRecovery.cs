using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class StorageRecoveryRequest
        {
            public string RunId;
            public ReceiptEvidence Received;
        }

        private sealed class StorageRecoveryGrant
        {
            public StorageRecoveryRequest Request;
            public ReceiptEvidence Sent;
            public DispatchBatchState OriginalBatch;
        }

        private readonly Dictionary<string, ReceiptEvidence> _appliedDispatchReceipts =
            new Dictionary<string, ReceiptEvidence>(StringComparer.Ordinal);
        private StorageRecoveryRequest _storageRecovery;
        private Task<string> _storageRecoveryReply;
        private readonly Stopwatch _storageRecoveryRetry = Stopwatch.StartNew();
        private string _storageRecoveryError;

        private static bool IsAppliedDispatchProof(ReceiptEvidence proof, bool sender)
        {
            Guid attempt;
            if (proof == null || !Guid.TryParseExact(proof.AttemptId, "N", out attempt) ||
                proof.Kind != (sender ? "dispatch-send" : "dispatch-receive") || proof.Phase != "applied" ||
                proof.Direction != (sender ? -1 : 1) || proof.Expected == null || proof.Expected.Count == 0 ||
                !SameManifest(proof.Expected, proof.PreparedItems) || proof.Before == null || proof.Observed == null ||
                proof.Before.Any(i => i == null) || proof.Observed.Any(i => i == null) ||
                string.IsNullOrWhiteSpace(proof.BatchId) || string.IsNullOrWhiteSpace(proof.TransactionId)) return false;
            // Full inventory equality, including multiplicity; a trade-window
            // Finished event or a storage count alone is not custody proof.
            return sender ? SameManifest(proof.Before, proof.Observed.Concat(proof.Expected))
                : SameManifest(proof.Before.Concat(proof.Expected), proof.Observed);
        }

        private void RetainAppliedDispatchReceipt(ReceiptEvidence proof)
        {
            if (proof.Direction == 0 || (proof.Kind != "dispatch-send" && proof.Kind != "dispatch-receive")) return;
            if (!IsAppliedDispatchProof(proof, _isCentral))
                throw new InvalidOperationException("Applied dispatch has incomplete physical custody evidence.");
            _appliedDispatchReceipts[proof.AttemptId] = proof;
        }

        private void RequestStorageRecovery(StorageJob job)
        {
            ReceiptEvidence received;
            if (job.Command?.AttemptId == null ||
                !_appliedDispatchReceipts.TryGetValue(job.Command.AttemptId, out received)) return;
            _storageRecovery = new StorageRecoveryRequest { RunId = Guid.NewGuid().ToString("N"), Received = received };
            _storageRecoveryReply = null;
            _storageRecoveryRetry.Restart();
        }

        private bool TickStorageRecovery()
        {
            if (_storageRecovery == null) return false;
            if (_storageRecoveryRetry.ElapsedMilliseconds < 1000) return true;
            _storageRecoveryRetry.Restart();
            try
            {
                if (_storageRecoveryReply == null)
                    _storageRecoveryReply = SendStorageRecovery(_storageRecovery);
                else if (_storageRecoveryReply.IsCompleted)
                {
                    string reply = _storageRecoveryReply.Status == TaskStatus.RanToCompletion
                        ? _storageRecoveryReply.Result : "pending";
                    _storageRecoveryReply = null;
                    if (reply == "ready:" + _storageRecovery.RunId)
                        StartLocalCensus("Verified batch received; storage failed. Refresh all physical custody before continuing.",
                            _storageRecovery.RunId);
                }
            }
            catch (Exception ex)
            {
                if (_storageRecoveryError != ex.Message)
                    Logger.Warning("[CityBankers] Storage recovery retry: " + ex.Message);
                _storageRecoveryError = ex.Message;
            }
            return true;
        }

        private async Task<string> SendStorageRecovery(StorageRecoveryRequest request)
        {
            try
            {
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(_centralCharacter),
                    JsonConvert.SerializeObject(new DispatchProposal { Kind = "storage-recovery", StorageRecovery = request }),
                    1000, 4000).ConfigureAwait(false);
            }
            catch (Exception) { return "pending"; }
        }

        private bool HandleStorageRecoveryProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "storage-recovery") return false;
            var request = proposal.StorageRecovery;
            Guid run;
            if (!_isCentral || request == null || !Guid.TryParseExact(request.RunId, "N", out run) ||
                !IsAppliedDispatchProof(request.Received, false) ||
                !_config.Roles.Any(p => p.Key != "central" &&
                    string.Equals(p.Value?.Character, request.Received.Character, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Invalid storage recovery source or receipt.");
            string path = Path.Combine(LocalCensusDirectory(request.RunId), "dispatch-grant.json");
            var grant = CensusApplication.ReadExisting<StorageRecoveryGrant>(path);
            if (grant == null)
            {
                ReceiptEvidence sent;
                if (!_appliedDispatchReceipts.TryGetValue(request.Received.AttemptId, out sent))
                { proposal.Reply.TrySetResult("pending"); return true; }
                var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
                var batch = queue?.Batches?.SingleOrDefault(b => b.BatchId == request.Received.BatchId &&
                    b.AttemptId == request.Received.AttemptId);
                grant = new StorageRecoveryGrant { Request = request, Sent = sent, OriginalBatch = batch };
                ValidateStorageGrant(grant, request.RunId, request.Received.Character);
                // Sender accounting must already have transferred these exact
                // occurrence IDs; partial storage may have changed their slots.
                var ledger = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
                var ids = sent.LedgerIds;
                var owned = ledger?.Items?.Where(e => ids != null && ids.Contains(e.Id)).ToList();
                if (ids == null || ids.Count != sent.Expected.Count || ids.Distinct().Count() != ids.Count ||
                    owned == null || owned.Count != ids.Count || owned.Any(e => !e.HighId.HasValue || !e.Ql.HasValue ||
                        !string.Equals(e.Character, request.Received.Character, StringComparison.OrdinalIgnoreCase)) ||
                    !SameManifest(owned.Select(e => new TransferItemState { AoId = e.AoId, HighId = e.HighId.Value, Ql = e.Ql.Value }), sent.Expected))
                    throw new InvalidOperationException("Transferred ledger occurrences do not match storage recovery proof.");
                RuntimeStateStore.WriteJsonAtomic(path, grant);
            }
            ValidateStorageGrant(grant, request.RunId, request.Received.Character);
            if (JsonConvert.SerializeObject(grant.Request) != JsonConvert.SerializeObject(request))
                throw new InvalidOperationException("Storage recovery run was reused with changed evidence.");
            if (File.Exists(Path.Combine(LocalCensusDirectory(request.RunId), "completed.json")))
            { proposal.Reply.TrySetResult("complete:" + request.RunId); return true; }
            if (HasPendingPeerWork(request.Received.Character, request.RunId) ||
                !WithdrawalStore.TryReserveCensus(_settingsDir, request.RunId, request.Received.Character))
            { proposal.Reply.TrySetResult("pending"); return true; }
            proposal.Reply.TrySetResult("ready:" + request.RunId);
            return true;
        }

        private void ValidateStorageGrant(StorageRecoveryGrant grant, string run, string character)
        {
            var received = grant?.Request?.Received;
            var sent = grant?.Sent;
            var batch = grant?.OriginalBatch;
            if (grant?.Request?.RunId != run || !IsAppliedDispatchProof(sent, true) ||
                !IsAppliedDispatchProof(received, false) || batch == null ||
                !string.Equals(sent.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(received.Character, character, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(batch.Character, character, StringComparison.OrdinalIgnoreCase) ||
                (batch.Status != "failed" && batch.Status != "transferred") ||
                sent.AttemptId != received.AttemptId || sent.AttemptId != batch.AttemptId ||
                sent.BatchId != received.BatchId || sent.BatchId != batch.BatchId ||
                sent.TransactionId != received.TransactionId || sent.TransactionId != batch.TransactionId ||
                !SameManifest(sent.Expected, received.Expected) || !SameManifest(sent.Expected, batch.Items))
                throw new InvalidOperationException("Storage recovery requires matching sender removal and receiver arrival proofs.");
        }

        private StorageRecoveryGrant ReadStorageGrant(string run, string character)
        {
            if (run == null) return null;
            var grant = CensusApplication.ReadExisting<StorageRecoveryGrant>(
                Path.Combine(LocalCensusDirectory(run), "dispatch-grant.json"));
            if (grant != null) ValidateStorageGrant(grant, run, character);
            return grant;
        }

        private void CompleteStorageRecoveryCensus(StorageRecoveryGrant grant)
        {
            if (grant == null) return;
            var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
            if (queue?.Batches == null) throw new InvalidOperationException("Dispatch queue unavailable during recovery.");
            var batch = queue.Batches.SingleOrDefault(b => b.BatchId == grant.OriginalBatch.BatchId);
            if (batch != null)
            {
                if (batch.AttemptId != grant.OriginalBatch.AttemptId ||
                    (batch.Status != "failed" && batch.Status != "transferred"))
                    throw new InvalidOperationException("Recovered dispatch changed while the worker was auditing.");
                queue.Batches.Remove(batch);
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            }
            // No old manifest is retried from Central. Census has established
            // the worker's real items; normal storage/return routing takes over.
            _appliedDispatchReceipts.Remove(grant.Sent.AttemptId);
        }
    }
}
