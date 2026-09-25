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
        private sealed class LocalCensusBundle
        {
            public BagAuditResult Census;
            public List<ActiveLedgerItem> Previous;
            public StorageWorkerState PreviousStorage;
            public PhysicalLedgerReconciliation.Plan Plan;
            public StorageWorkerState Storage;
            public DispatchQueueState PreviousQueue;
            public DispatchQueueState Queue;
            public List<WithdrawalState> QueuedRequests;
            public StorageRecoveryGrant StorageGrant;
        }

        private string _localCensus;
        private long _localCensusPause;
        private string _localCensusReason;
        private bool _localCensusIssued;
        private int _localCensusAttempt;
        private readonly Stopwatch _localCensusRetry = Stopwatch.StartNew();
        private BagAuditResult _localCensusResult;
        private Task<string> _localCensusCommit;
        private readonly Stopwatch _localCensusPoll = Stopwatch.StartNew();
        private readonly Stopwatch _localCensusScan = Stopwatch.StartNew();
        private string _localCensusMismatch;
        private string _localCensusError;

        // Does not require InPlay: the disconnect callback runs after the SDK
        // has already dropped the connection. Retained operation state is what
        // decides whether an idle session can be resumed without recounting bags.
        internal static bool CanResumeIdleConnection()
        {
            var a = _ipcOwner;
            return a != null && a._enabled && !Trade.IsTrading &&
                a._reserveOperation == null && a._stackOperation == null && a._receipt == null && a._afterReceipt == null &&
                a._returnOffer == null && a._returnRequest == null &&
                a._workerCommand == null && a._reservedDispatch == null &&
                a._withdrawal == null && a._activeBatch == null && !a._donationActive &&
                a._donationCleanup == null && a._storageJob == null && a._dispatchPreparation == null &&
                a._extraction == null && (a._extractionCommit == null || a._extractionCommit.IsCompleted) &&
                a._localCensus == null && a._dispatchCensus == null && a._dispatchDispute == null &&
                a._withdrawalCensus == null && !a._withdrawalDispute && a._storageRecovery == null &&
                a._pickupItems.Count == 0 && a._centralWithdrawalItems.Count == 0 &&
                !a.HasPendingPeerWork(Client.CharacterName) && !a.CensusReservedHere();
        }

        internal static bool ReconcileIdleReconnect() => _ipcOwner != null &&
            _ipcOwner.StartLocalCensus("Idle reconnect inventory differs from the last settled snapshot; reconcile only this banker.");

        // Called only by Central's census coordinator on its update thread.
        // Reserve before granting an excluded worker permission to scan: a local
        // census cannot resolve an outstanding transfer involving another peer.
        internal static bool ReserveStartupAdmission(string run, string character)
        {
            var actor = _ipcOwner;
            return actor != null && actor._enabled && actor._isCentral && StartupCensusGate.IsOpen &&
                !string.Equals(character, actor._centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                !actor.HasPendingPeerWork(character, run) &&
                WithdrawalStore.TryReserveCensus(actor._settingsDir, run, character);
        }

        internal static bool ApplyStartupAdmission(BagAuditResult census)
        {
            var actor = _ipcOwner;
            if (actor == null || !actor._enabled || !actor._isCentral || !StartupCensusGate.IsOpen)
                return false;
            // Use the same atomic accounting scope, reservation checks and
            // character-scoped ledger/storage merge as operational local censuses.
            var proposal = new DispatchProposal { Kind = "local-census", Census = census };
            actor.HandleLocalCensusProposal(proposal);
            return proposal.Reply.Task.IsCompleted && proposal.Reply.Task.Result == "complete:" + census.RunId;
        }

        // A local move needs no peer. Failed dispatch storage additionally
        // requires a grant backed by both peers' applied physical receipts.
        private bool CanStartLocalCensus() => StartupCensusGate.IsOpen &&
            !Trade.IsTrading && _reserveOperation == null && _stackOperation == null && Inventory.Bank.IsOpen && _receipt == null && _afterReceipt == null &&
            _returnOffer == null && _workerCommand == null && _reservedDispatch == null &&
            _withdrawal == null && _activeBatch == null && !_donationActive && _donationCleanup == null &&
            _storageJob == null && _dispatchPreparation == null &&
            (_extractionCommit == null || _extractionCommit.IsCompleted);

        private bool HasPendingPeerWork(string character, string run = null)
        {
            if (string.Equals(_returnOffer?.Source, character, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_activeBatch?.Character, character, StringComparison.OrdinalIgnoreCase)) return true;
            var grant = ReadStorageGrant(run, character);
            bool central = string.Equals(character, _centralCharacter, StringComparison.OrdinalIgnoreCase);
            var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            if (queue?.Batches == null) throw new InvalidOperationException("Dispatch queue unavailable for local census admission.");
            return queue.Batches.Any(b =>
                (central || string.Equals(b.Character, character, StringComparison.OrdinalIgnoreCase)) &&
                !(grant != null && b.BatchId == grant.OriginalBatch.BatchId && b.AttemptId == grant.OriginalBatch.AttemptId &&
                    (b.Status == "failed" || b.Status == "transferred")) &&
                !(b.Status == "failed" && b.TransferNeverStarted) &&
                b.Status != "queued" && b.Status != "stored" && b.Status != "completed" && b.Status != "cancelled");
        }

        private bool StartLocalCensus(string reason, string requestedRun = null)
        {
            StartupCensusGate.Block(reason);
            return false;
#if false // Owner policy: retained legacy automatic audit path.

            if (_localCensus != null) return true;
            if (!CanStartLocalCensus() || HasPendingPeerWork(Client.CharacterName, requestedRun)) return false;
            string run = requestedRun ?? Guid.NewGuid().ToString("N");
            if (!WithdrawalStore.TryReserveCensus(_settingsDir, run, Client.CharacterName, _isCentral)) return false;
            _localCensus = run;
            _localCensusReason = reason;
            CityDwellers.Shared.IncidentJournal.Record(RuntimeStateStore.GetDataDirectory(_settingsDir),
                "recovery:history/census-local-" + run + ".json", Client.CharacterName, "recovery.started", new { Reason = reason, Run = run }, true);
            TraceTrade("recovery.local", new { Reason = reason, Run = run }, true, "recovery:history/census-local-" + run + ".json");
            _localCensusAttempt = 0;
            _localCensusRetry.Restart();
            _localCensusPause = StartupCensusGate.PauseLocalCensus(reason);
            Logger.Warning("[CityBankers] LOCAL CENSUS START " + Client.CharacterName + ": " + reason);
            CityDwellers.Shared.ServiceEvents.Report("census.started", "warning", reason, new { Run = run });
            _localCensusPoll.Restart();
            return true;
        #endif
        }

        private string LocalCensusDirectory(string run) => Path.Combine(
            StartupCensusGate.CensusDirectory(_settingsDir), "local-" + run);

        private bool TickLocalCensus()
        {
            return false;
#if false // Retain implementation; exclude automatic audit workers from runtime.

            if (_localCensus == null) return false;
            // Recovery IPC performs no AO movement. Other workers can finish
            // their accounting while Central's own bags are being observed.
            if (_isCentral && Client.InPlay)
            {
                try { TickBankerIpc(); }
                catch (Exception ex)
                {
                    if (_localCensusError != ex.Message)
                        Logger.Error("[CityBankers] Census-time IPC retry: " + ex.Message);
                    _localCensusError = ex.Message;
                }
            }
            if (!StartupCensusGate.OwnsLocalPause(_localCensusPause) || !Client.InPlay || Trade.IsTrading ||
                !Inventory.Bank.IsOpen || _localCensusPoll.ElapsedMilliseconds < 1000) return true;
            _localCensusPoll.Restart();
            try
            {
                string directory = LocalCensusDirectory(_localCensus);
                string collector = StartupCensusGate.CensusDirectory(_settingsDir);
                string resultPath = Path.Combine(collector, Client.CharacterName + ".result.json");
                if (!_localCensusIssued)
                {
                    if (!DispatchCensusInventorySettled()) return true;
                    if (_localCensusAttempt > 0 && _localCensusRetry.ElapsedMilliseconds < 30000) return true;
                    _localCensusAttempt++;
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "intent.json"), new
                    { RunId = _localCensus, Character = Client.CharacterName, Reason = _localCensusReason, Extraction = _extraction });
                    RuntimeStateStore.DeleteIfExists(resultPath);
                    var command = new BagAuditCommand { RunId = _localCensus, Role = _role, BagMoveTimeoutMs = 15000 };
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "request.json"), command);
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(collector, Client.CharacterName + ".command.json"), command);
                    _localCensusIssued = true;
                    return true;
                }
                if (_localCensusResult == null)
                {
                    if (!File.Exists(resultPath)) return true;
                    var result = CensusApplication.ReadExisting<BagAuditResult>(resultPath);
                    if (result == null || result.RunId != _localCensus || result.Character != Client.CharacterName || result.Role != _role)
                        throw new InvalidOperationException("Local census result does not match its request.");
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "evidence-" + _localCensusAttempt + ".json"), result);
                    try { PhysicalLedgerReconciliation.ReadCensus(_settingsDir, result); }
                    catch
                    {
                        _localCensusIssued = false;
                        _localCensusRetry.Restart();
                        throw;
                    }
                    _localCensusResult = result;
                }
                if (_localCensusCommit == null)
                {
                    if (_isCentral)
                    {
                        var proposal = new DispatchProposal { Kind = "local-census", Census = _localCensusResult };
                        HandleLocalCensusProposal(proposal);
                        _localCensusCommit = proposal.Reply.Task;
                    }
                    else _localCensusCommit = SendLocalCensus(_localCensusResult);
                }
                else if (_localCensusCommit.IsCompleted)
                {
                    string reply = _localCensusCommit.Result;
                    _localCensusCommit = null;
                    if (reply != "complete:" + _localCensus) return true;
                    // The complete census supersedes only this local move. Its
                    // original intent/proofs remain in their evidence directory.
                    if (_extraction != null)
                    {
                        WithdrawalStore.ReleaseRecovery(_settingsDir, _extraction.Id);
                        FinishExtraction();
                    }
                    WithdrawalStore.ReleaseRecovery(_settingsDir, _localCensus);
                    if (!StartupCensusGate.ResumeLocalCensus(_localCensusPause)) return true;
                    if (_storageRecovery != null && _storageRecovery.RunId == _localCensus)
                    {
                        _appliedDispatchReceipts.Remove(_storageRecovery.Received.AttemptId);
                        _storageRecovery = null;
                        _storageRecoveryReply = null;
                    }
                    FinishDispatchCensus();
                    FinishWithdrawalCensus();
                    CityDwellers.Shared.ServiceEvents.Report("census.applied", "info", "Local census applied.", new { Run = _localCensus });
                    _localCensus = null;
                    _localCensusResult = null;
                    _localCensusIssued = false;
                    _localCensusMismatch = null;
                    _localCensusScan.Restart();
                    Logger.Information("[CityBankers] LOCAL CENSUS APPLIED " + Client.CharacterName +
                        "; all differences retained; ordinary routing resumes.");
                }
            }
            catch (Exception ex)
            {
                // Keep the owner and its snapshot frozen; another banker's work
                // is not a reason to replay this snapshot over the whole ledger.
                if (_localCensusError != ex.Message)
                    Logger.Error("[CityBankers] Local census retry on " + Client.CharacterName + ": " + ex.Message);
                _localCensusError = ex.Message;
            }
            return true;
        #endif
        }

        private async Task<string> SendLocalCensus(BagAuditResult census)
        {
            try
            {
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(_centralCharacter),
                    JsonConvert.SerializeObject(new DispatchProposal { Kind = "local-census", Census = census }), 1000, 4000).ConfigureAwait(false);
            }
            catch (Exception) { return "pending"; }
        }

        private bool HandleLocalCensusProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "local-census") return false;
            try
            {
                string reply = CityDwellers.Shared.ManagerAccounting.Transaction("Local census admission", () =>
                {
                    var census = proposal.Census;
                    Guid run;
                    if (!_isCentral || census == null || !Guid.TryParseExact(census.RunId, "N", out run) ||
                        !_config.Roles.Any(p => p.Key == census.Role &&
                            (p.Key != "central" || census.RunId == _localCensus) &&
                            string.Equals(p.Value?.Character, census.Character, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Invalid local census source.");
                    var owner = CityDwellers.Shared.ManagerMemory.Current;
                    string transaction = CityDwellers.Shared.ManagerAccounting.TransactionId;
                    var old = owner.ReadAppliedCensus(transaction, census.RunId);
                    if (old != null)
                    {
                        if (JsonConvert.SerializeObject(old) != JsonConvert.SerializeObject(census))
                            throw new InvalidOperationException("Local census run was reused with different evidence.");
                    }
                    else
                    {
                        if (!HandleWithdrawalCensusResult(census) && !HandlePairedCensusResult(census))
                        {
                            if (!WithdrawalStore.OwnsCensus(_settingsDir, census.RunId, census.Character) ||
                                HasPendingPeerWork(census.Character, census.RunId)) return "pending";
                            ApplyLocalCensus(census);
                        }
                        owner.CompleteCensus(transaction, census);
                    }
                    return "complete:" + census.RunId;
                });
                // Admission is acknowledged only after the complete accounting commit.
                proposal.Reply.TrySetResult(reply);
            }
            catch (Exception ex)
            {
                // Source remains paused/reserved. Failed source persistence or a
                // rejected proposal must not invalidate Central's own census.
                proposal.Reply.TrySetResult("pending");
                if (_localCensusError != ex.Message)
                    Logger.Error("[CityBankers] Local census commit retry: " + ex.Message);
                _localCensusError = ex.Message;
            }
            return true;
        }

        private void ApplyLocalCensus(BagAuditResult census)
        {
            var observations = PhysicalLedgerReconciliation.ReadCensus(_settingsDir, census);
            LocalCensusBundle bundle;
            {
                var ledger = CityDwellers.Shared.BankerState.ReadLedger<ActiveLedgerState>();
                if (ledger?.Items == null) throw new InvalidOperationException("Local census requires the current ledger.");
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                var previousStorage = storage?.Workers?.SingleOrDefault(w => string.Equals(w.Character, census.Character, StringComparison.OrdinalIgnoreCase));
                var previous = ledger.Items.Where(e => string.Equals(e.Character, census.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var observation in observations.Where(o => o.Bag.HasValue && !string.IsNullOrWhiteSpace(o.BagIdentity)))
                {
                    var matches = previousStorage?.Bags?.Where(b => b.LastUniqueIdentity == observation.BagIdentity).ToList();
                    if (matches?.Count == 1 && census.Bags.Count(b => b.UniqueIdentity == observation.BagIdentity) == 1)
                    { observation.PreviousBag = matches[0].OuterSlotInstance & 65535; observation.PreviousLocation = matches[0].Source; }
                }
                SharedBagRecovery.ApplyProvenance(_settingsDir, observations, previous);
                var plan = PhysicalLedgerReconciliation.Build(previous, observations, new[] { census.Character });
                foreach (var item in plan.Items)
                    if (string.IsNullOrWhiteSpace(item.TransactionId)) item.TransactionId = "found-" + item.Id;
                bundle = new LocalCensusBundle { Census = census, Previous = previous, PreviousStorage = previousStorage,
                    Plan = plan, Storage = CensusApplication.BuildStorage(new[] { census }, plan, census.RunId).Workers.Single(),
                    StorageGrant = ReadStorageGrant(census.RunId, census.Character),
                    QueuedRequests = WithdrawalStore.LoadAll(_settingsDir).Where(r => WithdrawalStore.IsActive(r) &&
                        string.Equals(r.SourceCharacter, census.Character, StringComparison.OrdinalIgnoreCase)).ToList() };
                if (bundle.QueuedRequests.Any(r => !WithdrawalStore.HasStatus(r, "requested")))
                    throw new InvalidOperationException("Local census cannot supersede an active withdrawal transfer.");
                if (string.Equals(census.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase))
                {
                    bundle.PreviousQueue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                    bundle.Queue = CensusApplication.BuildQueue(plan, observations,
                        _config.Roles.ToDictionary(p => p.Key, p => p.Value.Character, StringComparer.OrdinalIgnoreCase),
                        WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList(), "local-" + census.RunId);
                }
            }
            if (bundle.Plan == null || bundle.Storage == null)
                throw new InvalidOperationException("Local census plan is incomplete.");
            foreach (var difference in bundle.Plan.Differences)
                CityDwellers.Shared.ManagerMemory.Current.RecordItemHistory(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, new ActiveHistoryRecord
                    {
                        LeftUtc = census.ObservedUtc, Reason = "census-" + difference.Kind,
                        Item = difference.Previous, CurrentItem = difference.Current,
                        ItemName = difference.Physical?.Item?.Name,
                        Source = "census-local:" + census.RunId, EventTimeKnown = false
                    });
            RuntimeStorageStateTransactions.ReplaceCensusedWorker(_settingsDir, bundle.Storage);
            // Reload unaffected characters on EVERY retry. Never replay an old
            // global ledger/storage snapshot while other bankers are working.
            var current = CityDwellers.Shared.BankerState.ReadLedger<ActiveLedgerState>();
            if (current?.Items == null) throw new InvalidOperationException("Current ledger is unavailable.");
            var merged = current.Items.Where(e => !string.Equals(e.Character, census.Character, StringComparison.OrdinalIgnoreCase))
                .Concat(bundle.Plan.Items).ToList();
            ActiveLedgerStore.ApplyCensus(_settingsDir, merged, observations.Select(o => new TransferItemState
                { AoId = o.Item.LowId, HighId = o.Item.HighId, Ql = o.Item.Ql, Name = o.Item.Name }),
                "history/census-local-" + census.RunId + ".json");
            if (string.Equals(census.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase))
            {
                if (bundle.Queue == null) throw new InvalidOperationException("Central census has no physical dispatch plan.");
                var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir)
                    ?? new DispatchQueueState();
                var originalIds = new HashSet<string>((bundle.PreviousQueue?.Batches ?? new List<DispatchBatchState>())
                    .Select(b => b.BatchId), StringComparer.Ordinal);
                var plannedIds = new HashSet<string>(bundle.Queue.Batches.Select(b => b.BatchId), StringComparer.Ordinal);
                if (queue.Batches.Any(b => !originalIds.Contains(b.BatchId) && !plannedIds.Contains(b.BatchId)))
                    throw new InvalidOperationException("Unexpected dispatch was added while Central's census owned routing.");
                // Central has not resumed, so no planned batch can have traded.
                // The current plan and original records remain in working memory.
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, bundle.Queue);
            }
            WithdrawalStore.ReconcileQueuedRequestsAfterLocalCensus(_settingsDir, census.RunId,
                census.Character, bundle.QueuedRequests);
            CompleteStorageRecoveryCensus(bundle.StorageGrant);
            SharedBagRecovery.MarkReconciled(_settingsDir, new[] { census.Character });
            CityDwellers.Shared.IncidentJournal.Record(RuntimeStateStore.GetDataDirectory(_settingsDir),
                "recovery:history/census-local-" + census.RunId + ".json", census.Character, "recovery.applied",
                new { census.RunId, RemainingClaims = bundle.Plan.Items.Count,
                    Differences = bundle.Plan.Differences.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count()),
                    Outcome = "Local reconciliation applied; discrepancy cause remains unproven." }, false, null, true);
        }

        private void DetectLocalInventoryDifference()
        {
            if (_extraction != null || !CanStartLocalCensus() || _localCensusScan.ElapsedMilliseconds < 5000) return;
            _localCensusScan.Restart();
            // Cancellation receipts own this inventory until both peers have
            // agreed. A relog here would close the IPC gate that completes them.
            if (_cancellationOutbox.Count != 0) return;
            if (_isCentral && RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Any(b =>
                b.Status == "cancelled" && !string.IsNullOrWhiteSpace(b.LastCancelledAttempt)))
            {
                StartLocalCensus("Both dispatch peers confirmed cancellation; refresh remaining Central custody before routing.");
                return;
            }
            var ledger = CityDwellers.Shared.BankerState.ReadLedgerForCharacter<ActiveLedgerState>(Client.CharacterName);
            if (ledger?.Items == null) return;

            // A withdrawal can deliberately leave its exact reserved occurrence
            // loose in normal inventory while return-to-Central is retrying. That
            // item is already named by positive custody evidence and must not be
            // rediscovered as an unexplained mismatch that triggers a relog.
            var knownWithdrawalLoose = new HashSet<string>(
                WithdrawalStore.LoadAll(_settingsDir)
                    .Where(row => WithdrawalStore.IsActive(row) &&
                        string.Equals(row.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        IsUsableIdentity(row.ExtractedItemIdentity) &&
                        !IsUsableIdentity(row.CentralItemIdentity))
                    .Select(row => row.ExtractedItemIdentity),
                StringComparer.Ordinal);

            var expected = ledger.Items.Where(e => string.Equals(e.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                e.Location == "inventory" && !e.Bag.HasValue && !CruPolicy.IsCru(e.AoId) &&
                !BankerPersonalItems.IsPersonal(e.AoId, e.HighId ?? e.AoId)).Select(e =>
                e.AoId + "/" + e.HighId + "/" + e.Ql).OrderBy(k => k).ToList();
            var actual = Inventory.Items.Where(i => StorageBagPolicy.IsNormalInventory(i) &&
                i.UniqueIdentity.Type != IdentityType.Container &&
                !knownWithdrawalLoose.Contains(i.UniqueIdentity.ToString()) &&
                !CruPolicy.IsCru(i.Id) &&
                !BankerPersonalItems.IsPersonal(i.Id, i.HighId)).Select(i =>
                i.Id + "/" + i.HighId + "/" + i.Ql).OrderBy(k => k).ToList();
            // Donation ledger slots are intentionally null, and cancellation can
            // return an item to a different slot. Compare custody multiplicities,
            // not optional addresses; this does not reassign any donor or ledger ID.
            string difference = string.Join(";", expected) + "|" + string.Join(";", actual);
            if (expected.SequenceEqual(actual)) { _localCensusMismatch = null; return; }
            if (_localCensusMismatch != difference) { _localCensusMismatch = difference; return; }
            Logger.Error("[CityBankers] LOOSE INVENTORY MISMATCH character=" + Client.CharacterName +
                "; expected=" + string.Join(";", expected) + "; observed=" + string.Join(";", actual) +
                "; entries are AOID/highID/QL, including duplicate occurrences; slots are not custody evidence.");
            StartLocalCensus("Stable loose-inventory item-count/template difference; refreshing this banker's inventory by relog.");
        }
    }
}
