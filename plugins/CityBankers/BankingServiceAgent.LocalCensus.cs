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
            public BagAuditAgent.BagAuditResult Census;
            public List<ActiveLedgerItem> Previous;
            public StorageWorkerState PreviousStorage;
            public PhysicalLedgerReconciliation.Plan Plan;
            public StorageWorkerState Storage;
            public DispatchQueueState PreviousQueue;
            public DispatchQueueState Queue;
        }

        private string _localCensus;
        private long _localCensusPause;
        private string _localCensusReason;
        private bool _localCensusIssued;
        private int _localCensusAttempt;
        private readonly Stopwatch _localCensusRetry = Stopwatch.StartNew();
        private BagAuditAgent.BagAuditResult _localCensusResult;
        private Task<string> _localCensusCommit;
        private readonly Stopwatch _localCensusPoll = Stopwatch.StartNew();
        private readonly Stopwatch _localCensusScan = Stopwatch.StartNew();
        private string _localCensusMismatch;
        private string _localCensusError;

        // A local bag move has no remote participant. Never use this path to
        // discard an unresolved trade, dispatch receipt or withdrawal.
        private bool CanStartLocalCensus() => StartupCensusGate.IsOpen &&
            !Trade.IsTrading && Inventory.Bank.IsOpen && _receipt == null && _afterReceipt == null &&
            _returnOffer == null && _workerCommand == null && _reservedDispatch == null &&
            _withdrawal == null && _activeBatch == null && !_donationActive && _donationCleanup == null &&
            _storageJob == null && _dispatchPreparation == null &&
            (_extractionCommit == null || _extractionCommit.IsCompleted);

        private bool HasPendingPeerWork(string character)
        {
            if (string.Equals(_returnOffer?.Source, character, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_activeBatch?.Character, character, StringComparison.OrdinalIgnoreCase)) return true;
            bool central = string.Equals(character, _centralCharacter, StringComparison.OrdinalIgnoreCase);
            return RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Any(b =>
                (central || string.Equals(b.Character, character, StringComparison.OrdinalIgnoreCase)) &&
                !(b.Status == "failed" && b.TransferNeverStarted) &&
                b.Status != "queued" && b.Status != "stored" && b.Status != "completed" && b.Status != "cancelled");
        }

        private bool StartLocalCensus(string reason)
        {
            if (_localCensus != null) return true;
            if (!CanStartLocalCensus() || HasPendingPeerWork(Client.CharacterName)) return false;
            string run = Guid.NewGuid().ToString("N");
            if (!WithdrawalStore.TryReserveCensus(_settingsDir, run, Client.CharacterName, _isCentral)) return false;
            _localCensus = run;
            _localCensusReason = reason;
            _localCensusAttempt = 0;
            _localCensusRetry.Restart();
            _localCensusPause = StartupCensusGate.PauseLocalCensus(reason);
            _localCensusPoll.Restart();
            return true;
        }

        private string LocalCensusDirectory(string run) => Path.Combine(
            StartupCensusGate.CensusDirectory(_settingsDir), "local-" + run);

        private bool TickLocalCensus()
        {
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
                    if (_localCensusAttempt > 0 && _localCensusRetry.ElapsedMilliseconds < 30000) return true;
                    _localCensusAttempt++;
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "intent.json"), new
                    { RunId = _localCensus, Character = Client.CharacterName, Reason = _localCensusReason, Extraction = _extraction });
                    RuntimeStateStore.DeleteIfExists(resultPath);
                    var command = new BagAuditAgent.BagAuditCommand { RunId = _localCensus, Role = _role };
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "request.json"), command);
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(collector, Client.CharacterName + ".command.json"), command);
                    _localCensusIssued = true;
                    return true;
                }
                if (_localCensusResult == null)
                {
                    if (!File.Exists(resultPath)) return true;
                    var result = CensusApplication.ReadExisting<BagAuditAgent.BagAuditResult>(resultPath);
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
        }

        private async Task<string> SendLocalCensus(BagAuditAgent.BagAuditResult census)
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
                var census = proposal.Census;
                Guid run;
                if (!_isCentral || census == null || !Guid.TryParseExact(census.RunId, "N", out run) ||
                    !_config.Roles.Any(p => p.Key == census.Role &&
                        (p.Key != "central" || census.RunId == _localCensus) &&
                        string.Equals(p.Value?.Character, census.Character, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Invalid local census source.");
                string directory = LocalCensusDirectory(census.RunId);
                string completed = Path.Combine(directory, "completed.json");
                if (File.Exists(completed))
                {
                    var old = CensusApplication.ReadExisting<BagAuditAgent.BagAuditResult>(completed);
                    if (JsonConvert.SerializeObject(old) != JsonConvert.SerializeObject(census))
                        throw new InvalidOperationException("Local census run was reused with different evidence.");
                }
                else
                {
                    if (!WithdrawalStore.OwnsCensus(_settingsDir, census.RunId, census.Character) || HasPendingPeerWork(census.Character))
                    { proposal.Reply.TrySetResult("pending"); return true; }
                    ApplyLocalCensus(directory, census);
                    RuntimeStateStore.WriteJsonAtomic(completed, census);
                }
                proposal.Reply.TrySetResult("complete:" + census.RunId);
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

        private void ApplyLocalCensus(string directory, BagAuditAgent.BagAuditResult census)
        {
            var observations = PhysicalLedgerReconciliation.ReadCensus(_settingsDir, census);
            string path = Path.Combine(directory, "application.json");
            var bundle = CensusApplication.ReadExisting<LocalCensusBundle>(path);
            if (bundle == null)
            {
                var ledger = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
                if (ledger?.Items == null) throw new InvalidOperationException("Local census requires the current ledger.");
                var storage = CensusApplication.ReadExisting<StorageState>(RuntimeStateStore.GetStorageStatePath(_settingsDir));
                var previousStorage = storage?.Workers?.SingleOrDefault(w => string.Equals(w.Character, census.Character, StringComparison.OrdinalIgnoreCase));
                var previous = ledger.Items.Where(e => string.Equals(e.Character, census.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var observation in observations.Where(o => o.Bag.HasValue && !string.IsNullOrWhiteSpace(o.BagIdentity)))
                {
                    var matches = previousStorage?.Bags?.Where(b => b.LastUniqueIdentity == observation.BagIdentity).ToList();
                    if (matches?.Count == 1 && census.Bags.Count(b => b.UniqueIdentity == observation.BagIdentity) == 1)
                    { observation.PreviousBag = matches[0].OuterSlotInstance & 65535; observation.PreviousLocation = matches[0].Source; }
                }
                var plan = PhysicalLedgerReconciliation.Build(previous, observations, new[] { census.Character });
                foreach (var item in plan.Items)
                    if (string.IsNullOrWhiteSpace(item.TransactionId)) item.TransactionId = "found-" + item.Id;
                bundle = new LocalCensusBundle { Census = census, Previous = previous, PreviousStorage = previousStorage,
                    Plan = plan, Storage = CensusApplication.BuildStorage(new[] { census }, plan, census.RunId).Workers.Single() };
                if (string.Equals(census.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase))
                {
                    bundle.PreviousQueue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
                    bundle.Queue = CensusApplication.BuildQueue(plan, observations,
                        _config.Roles.ToDictionary(p => p.Key, p => p.Value.Character, StringComparer.OrdinalIgnoreCase),
                        WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList(), "local-" + census.RunId);
                }
                RuntimeStateStore.WriteJsonAtomic(path, bundle);
            }
            if (JsonConvert.SerializeObject(bundle.Census) != JsonConvert.SerializeObject(census) || bundle.Plan == null || bundle.Storage == null)
                throw new InvalidOperationException("Local census application changed during retry.");
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(ActiveLedgerStore.GetHistoryDirectory(_settingsDir),
                "census-local-" + census.RunId + ".json"), new
                { census.RunId, census.Character, census.ObservedUtc, EventTimeKnown = false, bundle.Plan.Differences });
            RuntimeStorageStateTransactions.ReplaceCensusedWorker(_settingsDir, bundle.Storage);
            // Reload unaffected characters on EVERY retry. Never replay an old
            // global ledger/storage snapshot while other bankers are working.
            var current = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
            if (current?.Items == null) throw new InvalidOperationException("Current ledger is unavailable.");
            var merged = current.Items.Where(e => !string.Equals(e.Character, census.Character, StringComparison.OrdinalIgnoreCase))
                .Concat(bundle.Plan.Items).ToList();
            ActiveLedgerStore.ApplyCensus(_settingsDir, merged, observations.Select(o => new TransferItemState
                { AoId = o.Item.LowId, HighId = o.Item.HighId, Ql = o.Item.Ql, Name = o.Item.Name }));
            if (string.Equals(census.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase))
            {
                if (bundle.Queue == null) throw new InvalidOperationException("Central census has no physical dispatch plan.");
                var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir))
                    ?? new DispatchQueueState();
                var originalIds = new HashSet<string>((bundle.PreviousQueue?.Batches ?? new List<DispatchBatchState>())
                    .Select(b => b.BatchId), StringComparer.Ordinal);
                var plannedIds = new HashSet<string>(bundle.Queue.Batches.Select(b => b.BatchId), StringComparer.Ordinal);
                if (queue.Batches.Any(b => !originalIds.Contains(b.BatchId) && !plannedIds.Contains(b.BatchId)))
                    throw new InvalidOperationException("Unexpected dispatch was added while Central's census owned routing.");
                // Central has not resumed, so no planned batch can have traded.
                // Original records remain in the immutable application bundle.
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, bundle.Queue);
            }
        }

        private void DetectLocalInventoryDifference()
        {
            if (_extraction != null || !CanStartLocalCensus() || _localCensusScan.ElapsedMilliseconds < 5000) return;
            _localCensusScan.Restart();
            var ledger = ActiveLedgerStore.LoadLedger(_settingsDir);
            if (ledger?.Items == null) return;
            var expected = ledger.Items.Where(e => string.Equals(e.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                e.Location == "inventory" && !e.Bag.HasValue).Select(e =>
                (e.Slot.HasValue ? (e.Slot.Value & 65535).ToString() : "?") + "/" + e.AoId + "/" + e.HighId + "/" + e.Ql).OrderBy(k => k);
            var actual = Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                i.UniqueIdentity.Type != IdentityType.Container).Select(i =>
                (i.Slot.Instance & 65535) + "/" + i.Id + "/" + i.HighId + "/" + i.Ql).OrderBy(k => k);
            string difference = string.Join(";", expected) + "|" + string.Join(";", actual);
            if (expected.SequenceEqual(actual)) { _localCensusMismatch = null; return; }
            if (_localCensusMismatch != difference) { _localCensusMismatch = difference; return; }
            StartLocalCensus("Stable loose-inventory difference; refreshing this banker's complete physical inventory.");
        }
    }
}
