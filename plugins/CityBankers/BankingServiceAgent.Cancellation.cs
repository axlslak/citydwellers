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
        private sealed class CancellationPending
        {
            public ReceiptEvidence Proof;
            public Task<string> Request;
            public readonly Stopwatch Retry = Stopwatch.StartNew();
        }

        private sealed class CancellationPair
        {
            public ReceiptEvidence Sender;
            public ReceiptEvidence Receiver;
            public DispatchBatchState OriginalBatch;
        }

        private readonly Dictionary<string, CancellationPending> _cancellationOutbox =
            new Dictionary<string, CancellationPending>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _cancellationSignatures = new Dictionary<string, string>();
        private readonly Dictionary<string, Stopwatch> _cancellationSettling = new Dictionary<string, Stopwatch>();
        private string _cancellationError;

        private static bool SameManifest(IEnumerable<TransferItemState> a, IEnumerable<TransferItemState> b) =>
            a != null && b != null && a.All(i => i != null) && b.All(i => i != null) &&
            a.Select(CustodyKey).OrderBy(k => k).SequenceEqual(b.Select(CustodyKey).OrderBy(k => k));

        private static bool IsCancellationProof(ReceiptEvidence proof)
        {
            Guid attempt;
            return proof != null && Guid.TryParseExact(proof.AttemptId, "N", out attempt) &&
                (proof.Kind == "dispatch-send" || proof.Kind == "dispatch-receive") &&
                proof.Phase == "applied" && proof.Direction == 0 && proof.Expected?.Count == 0 &&
                proof.PreparedItems?.Count > 0 && proof.PreparedItems.All(i => i != null) &&
                !string.IsNullOrWhiteSpace(proof.BatchId) && !string.IsNullOrWhiteSpace(proof.TransactionId) &&
                SameManifest(proof.Before, proof.Observed);
        }

        private void RetainCancellationForPeer(ReceiptEvidence proof)
        {
            if (proof.Direction != 0 || (proof.Kind != "dispatch-send" && proof.Kind != "dispatch-receive")) return;
            if (!IsCancellationProof(proof))
                throw new InvalidOperationException("Cancelled dispatch lacks attempt-bound physical evidence.");
            // The applied receipt is already flushed to its durable custody
            // directory. Keep retrying its IPC acknowledgement without reserving
            // unrelated transfers on this banker.
            _cancellationOutbox[proof.AttemptId] = new CancellationPending { Proof = proof };
        }

        private string CancellationDirectory(string attempt) => Path.Combine(
            RuntimeStateStore.GetDataDirectory(_settingsDir), "custody-transactions", "dispatch-cancel-" + attempt);

        private void TickCancellationOutbox()
        {
            foreach (var entry in _cancellationOutbox.ToList())
            {
                var pending = entry.Value;
                if (pending.Request == null && pending.Retry.ElapsedMilliseconds >= 1000)
                {
                    pending.Retry.Restart();
                    if (_isCentral)
                    {
                        var proposal = new DispatchProposal { Kind = "dispatch-cancelled", Cancellation = pending.Proof };
                        HandleCancellationProposal(proposal);
                        pending.Request = proposal.Reply.Task;
                    }
                    else pending.Request = SendCancellation(pending.Proof);
                }
                else if (pending.Request != null && pending.Request.IsCompleted)
                {
                    if (pending.Request.Status == TaskStatus.RanToCompletion && pending.Request.Result == "complete:" + entry.Key)
                        _cancellationOutbox.Remove(entry.Key);
                    else pending.Retry.Restart();
                    pending.Request = null;
                }
            }
        }

        private async Task<string> SendCancellation(ReceiptEvidence proof)
        {
            try
            {
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(_centralCharacter),
                    JsonConvert.SerializeObject(new DispatchProposal { Kind = "dispatch-cancelled", Cancellation = proof }),
                    1000, 4000).ConfigureAwait(false);
            }
            catch (Exception) { return "pending"; }
        }

        private bool HandleCancellationProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "dispatch-cancelled") return false;
            try
            {
                if (!_isCentral || !IsCancellationProof(proposal.Cancellation))
                    throw new InvalidOperationException("Invalid dispatch cancellation proof.");
                var proof = proposal.Cancellation;
                bool sender = proof.Kind == "dispatch-send";
                if (sender ? !string.Equals(proof.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase) :
                    !_config.Roles.Any(p => p.Key != "central" &&
                        string.Equals(p.Value?.Character, proof.Character, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Cancellation proof came from an unexpected participant.");
                string directory = CancellationDirectory(proof.AttemptId);
                string path = Path.Combine(directory, sender ? "sender.json" : "receiver.json");
                var retained = CensusApplication.ReadExisting<ReceiptEvidence>(path);
                if (retained != null && JsonConvert.SerializeObject(retained) != JsonConvert.SerializeObject(proof))
                    throw new InvalidOperationException("A cancellation attempt was reused with different evidence.");
                if (retained == null) RuntimeStateStore.WriteJsonAtomic(path, proof);
                string completed = Path.Combine(directory, "completed.json");
                if (!File.Exists(completed))
                {
                    var source = CensusApplication.ReadExisting<ReceiptEvidence>(Path.Combine(directory, "sender.json"));
                    var receiver = CensusApplication.ReadExisting<ReceiptEvidence>(Path.Combine(directory, "receiver.json"));
                    if (source == null || receiver == null || !StartupCensusGate.IsOpen || Trade.IsTrading ||
                        _receipt != null || _activeBatch != null || _donationActive || _donationCleanup != null ||
                        _returnOffer != null || _extraction != null || _withdrawalTradeOpened)
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    if (!IsCancellationProof(source) || !IsCancellationProof(receiver) ||
                        source.AttemptId != receiver.AttemptId || source.BatchId != receiver.BatchId ||
                        source.TransactionId != receiver.TransactionId || !SameManifest(source.PreparedItems, receiver.PreparedItems))
                        throw new InvalidOperationException("Cancellation participants disagree about the attempted transfer.");
                    string pairPath = Path.Combine(directory, "pair.json");
                    var pair = CensusApplication.ReadExisting<CancellationPair>(pairPath);
                    var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
                    if (queue?.Batches == null) throw new InvalidOperationException("Cancellation requires the current dispatch queue.");
                    var batch = queue.Batches.SingleOrDefault(b => b.BatchId == source.BatchId);
                    if (batch?.RequiresPairedCensus == true)
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    if (pair == null)
                    {
                        if (batch == null || batch.AttemptId != source.AttemptId || batch.Status != "failed" ||
                            batch.TransactionId != source.TransactionId ||
                            !string.Equals(batch.Character, receiver.Character, StringComparison.OrdinalIgnoreCase) ||
                            !SameManifest(batch.Items, source.PreparedItems))
                        { proposal.Reply.TrySetResult("pending"); return true; }
                        pair = new CancellationPair { Sender = source, Receiver = receiver,
                            OriginalBatch = JsonConvert.DeserializeObject<DispatchBatchState>(JsonConvert.SerializeObject(batch)) };
                        RuntimeStateStore.WriteJsonAtomic(pairPath, pair);
                    }
                    if (JsonConvert.SerializeObject(pair.Sender) != JsonConvert.SerializeObject(source) ||
                        JsonConvert.SerializeObject(pair.Receiver) != JsonConvert.SerializeObject(receiver))
                        throw new InvalidOperationException("Retained cancellation participants changed during retry.");
                    string outcome = "superseded";
                    if (batch != null && batch.LastCancelledAttempt == source.AttemptId)
                        outcome = "already-reconciled";
                    else if (batch != null && batch.AttemptId == source.AttemptId)
                    {
                        if (batch.Status != "failed") { proposal.Reply.TrySetResult("pending"); return true; }
                        string signature = string.Join(";", PhysicalInventory().Select(CustodyKey).OrderBy(k => k));
                        string previous;
                        if (!_cancellationSignatures.TryGetValue(source.AttemptId, out previous) || previous != signature)
                        {
                            _cancellationSignatures[source.AttemptId] = signature;
                            _cancellationSettling[source.AttemptId] = Stopwatch.StartNew();
                            proposal.Reply.TrySetResult("pending"); return true;
                        }
                        if (_cancellationSettling[source.AttemptId].ElapsedMilliseconds < 500)
                        { proposal.Reply.TrySetResult("pending"); return true; }
                        bool available = CancelledSourceStillAvailable(source);
                        outcome = available ? "retry-queued" : "source-census-required";
                        if (available)
                        {
                            string retryId = "retry-" + source.AttemptId;
                            if (queue.Batches.Any(b => b.BatchId == retryId))
                                throw new InvalidOperationException("Cancellation retry already exists alongside its original batch.");
                            var retry = JsonConvert.DeserializeObject<DispatchBatchState>(JsonConvert.SerializeObject(batch));
                            retry.BatchId = retryId;
                            retry.LastCancelledAttempt = source.AttemptId;
                            retry.AttemptId = null;
                            retry.Status = "queued";
                            retry.TransferNeverStarted = false;
                            retry.LastError = null;
                            retry.UpdatedUtc = DateTime.UtcNow;
                            queue.Batches.Remove(batch);
                            queue.Batches.Add(retry);
                        }
                        else
                        {
                            batch.LastCancelledAttempt = source.AttemptId;
                            batch.AttemptId = null;
                            batch.Status = "cancelled";
                            batch.TransferNeverStarted = false;
                            batch.LastError = "Both peers confirmed cancellation; refresh current source custody before routing.";
                            batch.UpdatedUtc = DateTime.UtcNow;
                        }
                        RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                        _workerRetries[batch.Character] = Stopwatch.StartNew();
                    }
                    // A retry uses a new batch ID as well as a new attempt ID:
                    // old storage replies cannot affect it. A completion-write
                    // retry never recreates an original batch that has gone away.
                    RuntimeStateStore.WriteJsonAtomic(completed, new
                    { source.AttemptId, source.BatchId, source.TransactionId, Outcome = outcome, Pair = pair });
                    _cancellationSignatures.Remove(source.AttemptId);
                    _cancellationSettling.Remove(source.AttemptId);
                    Logger.Information("[CityBankers] DISPATCH CANCELLATION RECONCILED " + source.BatchId +
                        "; attempt=" + source.AttemptId + "; " + outcome + ". Both inventories remained unchanged.");
                }
                proposal.Reply.TrySetResult("complete:" + proof.AttemptId);
            }
            catch (Exception ex)
            {
                proposal.Reply.TrySetResult("pending");
                if (_cancellationError != ex.Message)
                    Logger.Warning("[CityBankers] Cancellation acknowledgement retry: " + ex.Message);
                _cancellationError = ex.Message;
            }
            return true;
        }

        private bool CancelledSourceStillAvailable(ReceiptEvidence source)
        {
            if (source.LedgerIds == null || source.LedgerIds.Count != source.PreparedItems.Count ||
                source.LedgerIds.Any(string.IsNullOrWhiteSpace) || source.LedgerIds.Distinct().Count() != source.LedgerIds.Count)
                return false;
            var ledger = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
            var entries = ledger?.Items?.Where(i => source.LedgerIds.Contains(i.Id)).ToList();
            if (entries == null || entries.Count != source.LedgerIds.Count || entries.Any(i =>
                !string.Equals(i.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase) ||
                i.TransactionId != source.TransactionId || i.Location != "inventory" || i.Bag.HasValue)) return false;
            if (!entries.Select(i => i.AoId + "/" + i.HighId + "/" + i.Ql).OrderBy(k => k)
                .SequenceEqual(source.PreparedItems.Select(CustodyKey).OrderBy(k => k))) return false;
            var actual = PhysicalInventory().GroupBy(CustodyKey).ToDictionary(g => g.Key, g => g.Count());
            return source.PreparedItems.GroupBy(CustodyKey).All(g => actual.ContainsKey(g.Key) && actual[g.Key] >= g.Count());
        }
    }
}
