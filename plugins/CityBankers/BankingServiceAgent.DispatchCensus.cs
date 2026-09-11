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
        private sealed class DispatchCensusGrant
        {
            public DispatchBatchState Batch;
            public ReceiptEvidence Initiator;
            public ReceiptEvidence CentralEvidence;
            public string CentralRun;
            public string WorkerRun;
        }

        private sealed class DispatchCensusBundle
        {
            public DispatchCensusGrant Grant;
            public List<BagAuditAgent.BagAuditResult> Censuses;
            public List<ActiveLedgerItem> Previous;
            public StorageState PreviousStorage;
            public DispatchQueueState PreviousQueue;
            public List<WithdrawalState> Requests;
            public PhysicalLedgerReconciliation.Plan Plan;
            public StorageState Storage;
            public DispatchQueueState Queue;
        }

        private ReceiptEvidence _dispatchDispute;
        private long _dispatchDisputePause;
        private DispatchCensusGrant _dispatchCensus;
        private Task<string> _dispatchCensusMessage;
        private readonly Stopwatch _dispatchCensusPoll = Stopwatch.StartNew();
        private string _dispatchCensusError;
        private ReceiptEvidence _lastStoredDispatchReceipt;
        private string _dispatchCensusInventory;
        private readonly Stopwatch _dispatchCensusSettling = Stopwatch.StartNew();
        private readonly Stopwatch _failedDispatchCensusPoll = Stopwatch.StartNew();

        private static bool UnresolvedDispatch(DispatchBatchState batch) => batch != null &&
            batch.Status != "queued" && batch.Status != "completed" && batch.Status != "stored" &&
            batch.Status != "cancelled" && !(batch.Status == "failed" && batch.TransferNeverStarted);

        private bool CensusReservedHere() => WithdrawalStore.GetCensusCharacters(_settingsDir).Contains(Client.CharacterName);

        private bool DispatchCensusInventorySettled()
        {
            if (_dispatchCensus == null) return true;
            if (Inventory.Items == null || Inventory.Bank.Items == null) return false;
            string signature = string.Join(";", Inventory.Items.Concat(Inventory.Bank.Items).Where(i => i != null)
                .Select(i => i.Slot + "/" + i.UniqueIdentity + "/" + i.Id + "/" + i.HighId + "/" + i.Ql).OrderBy(s => s));
            if (_dispatchCensusInventory != signature)
            { _dispatchCensusInventory = signature; _dispatchCensusSettling.Restart(); return false; }
            return _dispatchCensusSettling.ElapsedMilliseconds >= 1000;
        }

        private string DispatchCensusDirectory(string attempt) => Path.Combine(
            StartupCensusGate.CensusDirectory(_settingsDir), "dispatch-" + attempt);

        private static bool IsDispatchEvidence(ReceiptEvidence proof)
        {
            Guid id;
            return proof != null && Guid.TryParseExact(proof.AttemptId, "N", out id) &&
                (proof.Kind == "dispatch-send" || proof.Kind == "dispatch-receive") &&
                !string.IsNullOrWhiteSpace(proof.BatchId) && !string.IsNullOrWhiteSpace(proof.TransactionId) &&
                proof.PreparedItems?.Count > 0 && proof.PreparedItems.All(i => i != null) && proof.Before != null;
        }

        private bool BeginDispatchDispute()
        {
            if (!IsDispatchEvidence(_receipt) || Trade.IsTrading || !StartupCensusGate.IsOpen) return false;
            PersistReceipt("dispatch-census-required");
            _dispatchDispute = JsonConvert.DeserializeObject<ReceiptEvidence>(JsonConvert.SerializeObject(_receipt));
            _dispatchDisputePause = StartupCensusGate.PauseLocalCensus(
                "Dispatch inventory differs from its receipt; coordinating full physical audits of both participants.");
            return true;
        }

        private ReceiptEvidence DispatchEvidence(string attempt)
        {
            if (_receipt?.AttemptId == attempt) return _receipt;
            ReceiptEvidence proof;
            if (_appliedDispatchReceipts.TryGetValue(attempt, out proof)) return proof;
            CancellationPending cancelled;
            if (_cancellationOutbox.TryGetValue(attempt, out cancelled)) return cancelled.Proof;
            return _lastStoredDispatchReceipt?.AttemptId == attempt ? _lastStoredDispatchReceipt : null;
        }

        private bool CanJoinDispatchCensus(DispatchCensusGrant grant)
        {
            string attempt = grant.Batch.AttemptId;
            return Client.InPlay && !Trade.IsTrading && Inventory.Bank.IsOpen &&
                (StartupCensusGate.IsOpen || (_dispatchDispute?.AttemptId == attempt &&
                    StartupCensusGate.OwnsLocalPause(_dispatchDisputePause))) &&
                _localCensus == null && _returnOffer == null && _extraction == null && _withdrawal == null &&
                !_donationActive && _donationCleanup == null && _dispatchPreparation == null &&
                (_receipt == null || _receipt.AttemptId == attempt) &&
                (_activeBatch == null || _activeBatch.AttemptId == attempt) &&
                (_workerCommand == null || _workerCommand.AttemptId == attempt) &&
                (_reservedDispatch == null || _reservedDispatch.AttemptId == attempt) &&
                (_storageJob == null || (!_storageJob.LocalRecovery && _storageJob.Command?.AttemptId == attempt)) &&
                (_storageRecovery == null || _storageRecovery.Received.AttemptId == attempt) &&
                (DispatchEvidence(attempt) == null ||
                    EvidenceMatchesDispatch(DispatchEvidence(attempt), grant.Batch, Client.CharacterName));
        }

        private static bool EvidenceMatchesDispatch(ReceiptEvidence proof, DispatchBatchState batch, string character) =>
            IsDispatchEvidence(proof) && proof.AttemptId == batch.AttemptId && proof.BatchId == batch.BatchId &&
            proof.TransactionId == batch.TransactionId && SameManifest(proof.PreparedItems, batch.Items) &&
            string.Equals(proof.Character, character, StringComparison.OrdinalIgnoreCase);

        private void ValidateDispatchCensusGrant(DispatchCensusGrant grant)
        {
            Guid id;
            if (grant?.Batch == null || !Guid.TryParseExact(grant.Batch.AttemptId, "N", out id) ||
                !Guid.TryParseExact(grant.CentralRun, "N", out id) || !Guid.TryParseExact(grant.WorkerRun, "N", out id) ||
                grant.CentralRun == grant.WorkerRun || string.IsNullOrWhiteSpace(grant.Batch.BatchId) ||
                string.IsNullOrWhiteSpace(grant.Batch.TransactionId) || grant.Batch.Items == null ||
                grant.Batch.Items.Count == 0 || grant.Batch.Items.Any(i => i == null) ||
                !_config.Roles.Any(p => p.Key != "central" && p.Key == grant.Batch.Role &&
                    string.Equals(p.Value?.Character, grant.Batch.Character, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Invalid paired dispatch census grant.");
            if (grant.CentralEvidence != null && grant.CentralEvidence.Kind != "dispatch-send")
                throw new InvalidOperationException("Central retained a non-sender dispatch receipt.");
            // Missing receipts are an explicit absence of evidence, never an
            // invented unchanged/received proof. The retained queue identifies
            // the attempt; BOTH fresh full audits establish present custody.
            foreach (var proof in new[] { grant.Initiator, grant.CentralEvidence }.Where(p => p != null))
                if (!IsDispatchEvidence(proof) || proof.AttemptId != grant.Batch.AttemptId || proof.BatchId != grant.Batch.BatchId ||
                    proof.TransactionId != grant.Batch.TransactionId || !SameManifest(proof.PreparedItems, grant.Batch.Items) ||
                    !string.Equals(proof.Character, proof.Kind == "dispatch-send" ? _centralCharacter : grant.Batch.Character,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Paired census evidence does not match the dispatch participants.");
        }

        private bool OtherDispatchPending(DispatchCensusGrant grant)
        {
            var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
            if (queue?.Batches == null) throw new InvalidOperationException("Paired census requires the dispatch queue.");
            // Other workers' unresolved records remain in the queue. Only
            // another attempt involving this same worker exceeds this pair's
            // scope; do not turn two independent failures into a global deadlock.
            return queue.Batches.Any(b => b.AttemptId != grant.Batch.AttemptId && UnresolvedDispatch(b) &&
                string.Equals(b.Character, grant.Batch.Character, StringComparison.OrdinalIgnoreCase));
        }

        private bool HandleDispatchCensusProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "dispatch-census-request" && proposal.Kind != "dispatch-census-prepare") return false;
            DispatchCensusGrant grant;
            if (proposal.Kind == "dispatch-census-request")
            {
                var proof = proposal.Cancellation;
                if (!_isCentral || (proof != null && !IsDispatchEvidence(proof)) ||
                    (proof == null && string.IsNullOrWhiteSpace(proposal.BatchId)))
                    throw new InvalidOperationException("Invalid dispatch census request.");
                var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
                var batch = queue?.Batches?.SingleOrDefault(b => b.BatchId == (proof?.BatchId ?? proposal.BatchId) &&
                    (proof == null || b.AttemptId == proof.AttemptId));
                string attempt = proof?.AttemptId ?? batch?.AttemptId;
                Guid attemptId;
                if (!Guid.TryParseExact(attempt, "N", out attemptId))
                { proposal.Reply.TrySetResult("pending"); return true; }
                string path = Path.Combine(DispatchCensusDirectory(attempt), "grant.json");
                grant = CensusApplication.ReadExisting<DispatchCensusGrant>(path);
                if (grant == null)
                {
                    if (batch == null || (batch.Status != "failed" && batch.Status != "trading" && batch.Status != "transferred"))
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    grant = new DispatchCensusGrant { Batch = batch, Initiator = proof,
                        CentralEvidence = DispatchEvidence(attempt),
                        CentralRun = Guid.NewGuid().ToString("N"), WorkerRun = Guid.NewGuid().ToString("N") };
                    ValidateDispatchCensusGrant(grant);
                    if (!CanJoinDispatchCensus(grant) || OtherDispatchPending(grant))
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    RuntimeStateStore.WriteJsonAtomic(path, grant);
                }
                ValidateDispatchCensusGrant(grant);
                if (proof != null && !EvidenceMatchesDispatch(proof, grant.Batch,
                    proof.Kind == "dispatch-send" ? _centralCharacter : grant.Batch.Character))
                    throw new InvalidOperationException("Dispatch census request changed its participants or manifest.");
                if (File.Exists(Path.Combine(DispatchCensusDirectory(grant.Batch.AttemptId), "completed.json")))
                { proposal.Reply.TrySetResult("complete"); return true; }
                if (_dispatchCensus == null)
                {
                    // A retained grant may have been written before admission
                    // succeeded. Recheck the live queue before freezing anyone;
                    // ordinary recovery may already have retired that attempt.
                    if (batch == null || batch.AttemptId != grant.Batch.AttemptId || !UnresolvedDispatch(batch) ||
                        batch.TransactionId != grant.Batch.TransactionId || !SameManifest(batch.Items, grant.Batch.Items) ||
                        !string.Equals(batch.Character, grant.Batch.Character, StringComparison.OrdinalIgnoreCase))
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    if (!CanJoinDispatchCensus(grant) || OtherDispatchPending(grant) ||
                        !WithdrawalStore.TryReserveDispatchCensus(_settingsDir, grant.CentralRun, _centralCharacter,
                            grant.WorkerRun, grant.Batch.Character))
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    JoinDispatchCensus(grant);
                }
                if (_dispatchCensus.Batch.AttemptId != grant.Batch.AttemptId)
                { proposal.Reply.TrySetResult("pending"); return true; }
                proposal.Reply.TrySetResult(JsonConvert.SerializeObject(grant));
            }
            else
            {
                grant = proposal.DispatchCensus;
                ValidateDispatchCensusGrant(grant);
                if (_isCentral || !string.Equals(grant.Batch.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected paired census recipient.");
                var retained = CensusApplication.ReadExisting<DispatchCensusGrant>(
                    Path.Combine(DispatchCensusDirectory(grant.Batch.AttemptId), "grant.json"));
                if (JsonConvert.SerializeObject(retained) != JsonConvert.SerializeObject(grant))
                    throw new InvalidOperationException("Paired census grant differs from Central's retained record.");
                if (File.Exists(Path.Combine(DispatchCensusDirectory(grant.Batch.AttemptId), "completed.json")))
                { proposal.Reply.TrySetResult("complete"); return true; }
                if (_dispatchCensus == null)
                {
                    if (!CanJoinDispatchCensus(grant) || !WithdrawalStore.OwnsCensus(_settingsDir, grant.WorkerRun, Client.CharacterName))
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    JoinDispatchCensus(grant);
                }
                proposal.Reply.TrySetResult(_dispatchCensus.Batch.AttemptId == grant.Batch.AttemptId ? "joined" : "pending");
            }
            return true;
        }

        private void JoinDispatchCensus(DispatchCensusGrant grant)
        {
            string run = _isCentral ? grant.CentralRun : grant.WorkerRun;
            var evidence = DispatchEvidence(grant.Batch.AttemptId);
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(LocalCensusDirectory(run), "dispatch-pair.json"), grant);
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(LocalCensusDirectory(run), "superseded-operation.json"), new
            { Evidence = evidence, ReceiptMissing = evidence == null, TransferInferred = false,
                Batch = _activeBatch, Command = _workerCommand, Storage = _storageJob, Physical = PhysicalSlots() });
            long pause = _dispatchDispute != null ? _dispatchDisputePause : StartupCensusGate.PauseLocalCensus(
                "Paired dispatch recovery: refreshing both participants before rebuilding physical custody.");
            _dispatchCensus = grant;
            _dispatchCensusInventory = null;
            // Evidence is durable and AO is closed. No old receipt callback or
            // storage action may execute after the audit owns this character.
            _receipt = null; _afterReceipt = null; _activeBatch = null; _workerCommand = null;
            _reservedDispatch = null; _storageJob = null; _storageRecovery = null;
            _outgoingOpened = false; _outgoingAccepted = false; _workerAccepted = false;
            _pendingConfirmation = Identity.None;
            _localCensus = run; _localCensusPause = pause;
            _localCensusReason = "Paired dispatch census for " + grant.Batch.BatchId;
            _localCensusAttempt = 0; _localCensusIssued = false; _localCensusResult = null; _localCensusCommit = null;
            _localCensusRetry.Restart(); _localCensusPoll.Restart();
        }

        private bool TryRecoverFailedDispatch()
        {
            if (!_isCentral || _dispatchDispute != null || _dispatchCensus != null ||
                !CanStartLocalCensus() || _failedDispatchCensusPoll.ElapsedMilliseconds < 5000) return false;
            _failedDispatchCensusPoll.Restart();
            // Give ordinary paired cancellation and verified storage recovery
            // their first opportunity. A failed attempt without one peer's
            // receipt still needs physical resolution instead of an eternal wait.
            var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
            foreach (var batch in (queue?.Batches ?? new List<DispatchBatchState>()).Where(b =>
                b.Status == "failed" && !b.TransferNeverStarted && !string.IsNullOrWhiteSpace(b.AttemptId)))
            {
                var proposal = new DispatchProposal { Kind = "dispatch-census-request", BatchId = batch.BatchId,
                    Cancellation = DispatchEvidence(batch.AttemptId) };
                HandleDispatchCensusProposal(proposal);
                if (_dispatchCensus != null) return true;
            }
            return false;
        }

        private bool TickDispatchCensus()
        {
            if (_dispatchDispute == null && _dispatchCensus == null) return false;
            try
            {
                if (!Client.InPlay) return true;
                TickBankerIpc();
                if (_isCentral && _localCensus == null)
                    foreach (var batch in RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Where(b => b.Status == "transferred").ToList())
                        PollStorageResult(batch);
                if (_dispatchCensusPoll.ElapsedMilliseconds < 1000) return _localCensus == null;
                _dispatchCensusPoll.Restart();
                if (_dispatchCensusMessage != null && !_dispatchCensusMessage.IsCompleted) return _localCensus == null;
                _dispatchCensusMessage = null;
                if (_isCentral && _dispatchCensus == null)
                {
                    var proposal = new DispatchProposal { Kind = "dispatch-census-request", Cancellation = _dispatchDispute };
                    HandleDispatchCensusProposal(proposal);
                }
                if (_isCentral && _dispatchCensus != null)
                    _dispatchCensusMessage = SendDispatchCensus(_dispatchCensus.Batch.Character,
                        new DispatchProposal { Kind = "dispatch-census-prepare", DispatchCensus = _dispatchCensus });
                else if (!_isCentral && _dispatchCensus == null)
                    _dispatchCensusMessage = SendDispatchCensus(_centralCharacter,
                        new DispatchProposal { Kind = "dispatch-census-request", Cancellation = _dispatchDispute });
            }
            catch (Exception ex)
            {
                if (_dispatchCensusError != ex.Message) Logger.Warning("[CityBankers] Paired census retry: " + ex.Message);
                _dispatchCensusError = ex.Message;
            }
            return _localCensus == null;
        }

        private async Task<string> SendDispatchCensus(string character, DispatchProposal proposal)
        {
            try { return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(character),
                JsonConvert.SerializeObject(proposal), 1000, 4000).ConfigureAwait(false); }
            catch (Exception) { return "pending"; }
        }

        private bool HandlePairedCensusResult(BagAuditAgent.BagAuditResult census)
        {
            var grant = CensusApplication.ReadExisting<DispatchCensusGrant>(
                Path.Combine(LocalCensusDirectory(census.RunId), "dispatch-pair.json"));
            if (grant == null) return false;
            ValidateDispatchCensusGrant(grant);
            string expectedRun = string.Equals(census.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase)
                ? grant.CentralRun : grant.WorkerRun;
            if (census.RunId != expectedRun || (census.RunId == grant.WorkerRun &&
                !string.Equals(census.Character, grant.Batch.Character, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Paired census participant changed.");
            string directory = DispatchCensusDirectory(grant.Batch.AttemptId);
            string completed = Path.Combine(directory, "completed.json");
            string evidencePath = Path.Combine(directory, census.RunId + ".json");
            var old = CensusApplication.ReadExisting<BagAuditAgent.BagAuditResult>(evidencePath);
            if (old != null && JsonConvert.SerializeObject(old) != JsonConvert.SerializeObject(census))
                throw new InvalidOperationException("Paired census evidence changed during retry.");
            if (old == null)
            {
                PhysicalLedgerReconciliation.ReadCensus(_settingsDir, census);
                RuntimeStateStore.WriteJsonAtomic(evidencePath, census);
            }
            var completion = CensusApplication.ReadExisting<DispatchCensusGrant>(completed);
            if (completion != null)
            {
                if (JsonConvert.SerializeObject(completion) != JsonConvert.SerializeObject(grant))
                    throw new InvalidOperationException("Paired census completion belongs to different evidence.");
                return true;
            }
            if (!WithdrawalStore.OwnsCensus(_settingsDir, grant.CentralRun, _centralCharacter) ||
                !WithdrawalStore.OwnsCensus(_settingsDir, grant.WorkerRun, grant.Batch.Character))
                throw new InvalidOperationException("Both participants must remain reserved during paired application.");
            var central = CensusApplication.ReadExisting<BagAuditAgent.BagAuditResult>(Path.Combine(directory, grant.CentralRun + ".json"));
            var worker = CensusApplication.ReadExisting<BagAuditAgent.BagAuditResult>(Path.Combine(directory, grant.WorkerRun + ".json"));
            if (central == null || worker == null) throw new InvalidOperationException("Waiting for the other participant's complete physical audit.");
            ApplyDispatchCensus(directory, grant, new List<BagAuditAgent.BagAuditResult> { central, worker });
            RuntimeStateStore.WriteJsonAtomic(completed, grant);
            return true;
        }

        private void ApplyDispatchCensus(string directory, DispatchCensusGrant grant, List<BagAuditAgent.BagAuditResult> censuses)
        {
            if (censuses.Count != 2 || censuses[0].RunId != grant.CentralRun || censuses[1].RunId != grant.WorkerRun ||
                !string.Equals(censuses[0].Character, _centralCharacter, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(censuses[1].Character, grant.Batch.Character, StringComparison.OrdinalIgnoreCase) ||
                censuses[0].Role != "central" || censuses[1].Role != grant.Batch.Role)
                throw new InvalidOperationException("Paired audit results do not match their reserved participants.");
            var scope = new HashSet<string>(new[] { _centralCharacter, grant.Batch.Character }, StringComparer.OrdinalIgnoreCase);
            var observations = censuses.SelectMany(c => PhysicalLedgerReconciliation.ReadCensus(_settingsDir, c)).ToList();
            string path = Path.Combine(directory, "application.json");
            var bundle = CensusApplication.ReadExisting<DispatchCensusBundle>(path);
            if (bundle == null)
            {
                if (OtherDispatchPending(grant)) throw new InvalidOperationException("Another dispatch still has unresolved custody.");
                var ledger = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
                if (ledger?.Items == null) throw new InvalidOperationException("Current ledger unavailable for paired census.");
                var storage = CensusApplication.ReadExisting<StorageState>(RuntimeStateStore.GetStorageStatePath(_settingsDir));
                foreach (var observation in observations.Where(o => o.Bag.HasValue && !string.IsNullOrWhiteSpace(o.BagIdentity)))
                {
                    var matches = storage?.Workers?.Where(w => string.Equals(w.Character, observation.Character, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(w => w.Bags).Where(b => b.LastUniqueIdentity == observation.BagIdentity).ToList();
                    if (matches?.Count == 1 && censuses.Single(c => c.Character == observation.Character).Bags.Count(b => b.UniqueIdentity == observation.BagIdentity) == 1)
                    { observation.PreviousBag = matches[0].OuterSlotInstance & 65535; observation.PreviousLocation = matches[0].Source; }
                }
                var previous = ledger.Items.Where(i => scope.Contains(i.Character)).ToList();
                var plan = PhysicalLedgerReconciliation.Build(previous, observations, scope);
                foreach (var item in plan.Items)
                    if (string.IsNullOrWhiteSpace(item.TransactionId)) item.TransactionId = "found-" + item.Id;
                var requests = WithdrawalStore.LoadAll(_settingsDir).Where(r => WithdrawalStore.IsActive(r) && scope.Contains(r.SourceCharacter)).ToList();
                if (requests.Any(r => !WithdrawalStore.HasStatus(r, "requested")))
                    throw new InvalidOperationException("Paired dispatch census cannot supersede an active withdrawal transfer.");
                bundle = new DispatchCensusBundle
                {
                    Grant = grant, Censuses = censuses, Previous = previous, PreviousStorage = storage,
                    PreviousQueue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir)),
                    Requests = requests, Plan = plan, Storage = CensusApplication.BuildStorage(censuses, plan, grant.CentralRun),
                    Queue = CensusApplication.BuildQueue(plan, observations,
                        _config.Roles.ToDictionary(p => p.Key, p => p.Value.Character, StringComparer.OrdinalIgnoreCase),
                        new List<WithdrawalState>(), "pair-" + grant.CentralRun)
                };
                RuntimeStateStore.WriteJsonAtomic(path, bundle);
            }
            if (JsonConvert.SerializeObject(bundle.Grant) != JsonConvert.SerializeObject(grant) ||
                JsonConvert.SerializeObject(bundle.Censuses) != JsonConvert.SerializeObject(censuses) ||
                bundle.Plan == null || bundle.Storage == null || bundle.Queue?.Batches == null ||
                bundle.PreviousQueue?.Batches == null || bundle.Requests == null)
                throw new InvalidOperationException("Paired census application changed during retry.");
            var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
            var allowedIds = new HashSet<string>(bundle.PreviousQueue.Batches.Concat(bundle.Queue.Batches).Select(b => b.BatchId));
            if (queue?.Batches == null || queue.Batches.Any(b => !allowedIds.Contains(b.BatchId)) ||
                queue.Batches.Any(b => b.BatchId == grant.Batch.BatchId && b.AttemptId != grant.Batch.AttemptId))
                throw new InvalidOperationException("Dispatch queue changed while Central was auditing.");
            var mergedQueue = JsonConvert.DeserializeObject<DispatchQueueState>(JsonConvert.SerializeObject(bundle.Queue));
            foreach (var pending in queue.Batches.Where(b => b.AttemptId != grant.Batch.AttemptId && UnresolvedDispatch(b)))
            {
                pending.RequiresPairedCensus = true;
                mergedQueue.Batches.Add(pending);
            }
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(ActiveLedgerStore.GetHistoryDirectory(_settingsDir),
                "census-dispatch-" + grant.Batch.AttemptId + ".json"), new
                { Grant = grant, EventTimeKnown = false, TransferInferred = false, bundle.Plan.Differences,
                    Withdrawals = bundle.Requests.Select(r => new { Original = r, Disposition = "reconciled" }) });
            foreach (var worker in bundle.Storage.Workers)
                RuntimeStorageStateTransactions.ReplaceCensusedWorker(_settingsDir, worker);
            var current = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
            if (current?.Items == null) throw new InvalidOperationException("Current ledger unavailable during paired census application.");
            ActiveLedgerStore.ApplyCensus(_settingsDir, current.Items.Where(i => !scope.Contains(i.Character)).Concat(bundle.Plan.Items).ToList(),
                observations.Select(o => new TransferItemState { AoId = o.Item.LowId, HighId = o.Item.HighId, Ql = o.Item.Ql, Name = o.Item.Name }));
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, mergedQueue);
            WithdrawalStore.ReconcileQueuedRequestsAfterLocalCensus(_settingsDir, grant.CentralRun, _centralCharacter,
                bundle.Requests.Where(r => string.Equals(r.SourceCharacter, _centralCharacter, StringComparison.OrdinalIgnoreCase)).ToList());
            WithdrawalStore.ReconcileQueuedRequestsAfterLocalCensus(_settingsDir, grant.WorkerRun, grant.Batch.Character,
                bundle.Requests.Where(r => string.Equals(r.SourceCharacter, grant.Batch.Character, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        private void FinishDispatchCensus()
        {
            if (_dispatchCensus == null) return;
            string attempt = _dispatchCensus.Batch.AttemptId;
            _appliedDispatchReceipts.Remove(attempt);
            _cancellationOutbox.Remove(attempt);
            if (_lastStoredDispatchReceipt?.AttemptId == attempt) _lastStoredDispatchReceipt = null;
            _dispatchDispute = null; _dispatchCensus = null; _dispatchCensusMessage = null;
        }
    }
}
