using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class WithdrawalCensusGrant
        {
            public string Id;
            public string Reason;
            public Dictionary<string, string> Runs;
            public List<WithdrawalState> Requests;
            public List<DispatchBatchState> Dispatches;
            public List<string> ExpiredRequests;
        }

        private sealed class WithdrawalCensusBundle
        {
            public WithdrawalCensusGrant Grant;
            public List<BagAuditAgent.BagAuditResult> Censuses;
            public List<ActiveLedgerItem> Previous;
            public List<ActiveLedgerItem> MatchingAnchors;
            public StorageState PreviousStorage;
            public DispatchQueueState PreviousQueue;
            public PhysicalLedgerReconciliation.Plan Plan;
            public StorageState Storage;
            public DispatchQueueState Queue;
            public List<WithdrawalState> Dispositions;
        }

        private WithdrawalCensusGrant _withdrawalCensus;
        private bool _withdrawalDispute;
        private long _withdrawalDisputePause;
        private readonly Stopwatch _withdrawalCensusPoll = Stopwatch.StartNew();
        private readonly Stopwatch _withdrawalCensusClose = Stopwatch.StartNew();
        private string _withdrawalCensusError;

        private string WithdrawalCensusDirectory(string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) throw new InvalidOperationException("Invalid withdrawal census ID.");
            return Path.Combine(StartupCensusGate.CensusDirectory(_settingsDir), "withdrawal-" + id);
        }

        private static bool WithdrawalReceipt(ReceiptEvidence receipt) => receipt != null &&
            (receipt.Kind == "withdrawal-transfer" || receipt.Kind == "withdrawal-pickup");

        private bool BeginWithdrawalDispute(string reason)
        {
            if (_withdrawalDispute) return true;
            if (!WithdrawalReceipt(_receipt) || !StartupCensusGate.IsOpen) return false;
            var rows = WithdrawalStore.LoadAll(_settingsDir).Where(r => WithdrawalStore.IsActive(r) &&
                (r.Id == _receipt.BatchId || r.OrderId == _receipt.BatchId)).ToList();
            if (rows.Count == 0) return false;
            PersistReceipt("withdrawal-census-required");
            // Failed is a request for physical resolution, not a delivery claim.
            WithdrawalStore.Update(_settingsDir, current =>
            {
                foreach (var row in current.Where(r => rows.Any(old => old.Id == r.Id) &&
                    WithdrawalStore.IsActive(r) && !WithdrawalStore.HasConfirmedDelivery(r) && r.RecoveryCensusId == null))
                {
                    row.Status = "failed"; row.Error = reason; WithdrawalStore.Touch(row);
                }
                return true;
            });
            _withdrawalDisputePause = StartupCensusGate.PauseLocalCensus(reason);
            _withdrawalDispute = true;
            return true;
        }

        private bool CanOwnWithdrawalCensus() => Client.InPlay && Inventory.Bank.IsOpen && !Trade.IsTrading &&
            _localCensus == null && _dispatchCensus == null && _activeBatch == null &&
            _returnOffer == null && _extraction == null && !_donationActive && _donationCleanup == null &&
            _dispatchPreparation == null && (_receipt == null || WithdrawalReceipt(_receipt)) &&
            (_afterReceipt == null || _withdrawalDispute) &&
            (StartupCensusGate.IsOpen || (_withdrawalDispute && StartupCensusGate.OwnsLocalPause(_withdrawalDisputePause)));

        private bool TryStartWithdrawalCensus()
        {
            if (!_isCentral || _withdrawalCensus != null || !CanOwnWithdrawalCensus()) return false;
            var rows = WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList();
            if (!_withdrawalDispute && !rows.Any(r => WithdrawalStore.HasStatus(r, "failed") &&
                !WithdrawalStore.HasConfirmedDelivery(r))) return false;
            // All staged requests share Central's inventory. Include their source
            // bankers, but leave merely queued requests on other workers alone.
            var characters = new HashSet<string>(rows.Where(r => !WithdrawalStore.HasStatus(r, "requested"))
                .Select(r => r.SourceCharacter), StringComparer.OrdinalIgnoreCase) { _centralCharacter };
            var grant = new WithdrawalCensusGrant
            {
                Id = Guid.NewGuid().ToString("N"), Reason = "Interrupted withdrawal custody requires complete physical observation.",
                Runs = characters.ToDictionary(c => c, c => Guid.NewGuid().ToString("N"), StringComparer.OrdinalIgnoreCase),
                Requests = rows.Where(r => characters.Contains(r.SourceCharacter)).ToList(),
                Dispatches = RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Where(b => characters.Contains(b.Character)).ToList(),
                ExpiredRequests = rows.Where(r => r.PickupExpiresUtc.HasValue && !WithdrawalStore.PickupWindowOpen(r))
                    .Select(r => r.Id).ToList()
            };
            ValidateWithdrawalCensus(grant);
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(WithdrawalCensusDirectory(grant.Id), "grant.json"), grant);
            _withdrawalCensus = grant; // retain before either of the admission writes
            return true;
        }

        private void ValidateWithdrawalCensus(WithdrawalCensusGrant grant)
        {
            Guid parsed;
            if (grant?.Runs != null) grant.Runs = new Dictionary<string, string>(grant.Runs, StringComparer.OrdinalIgnoreCase);
            if (grant == null || !Guid.TryParseExact(grant.Id, "N", out parsed) || grant.Runs == null ||
                !grant.Runs.ContainsKey(_centralCharacter) || grant.Requests == null || grant.Requests.Count == 0 ||
                grant.Dispatches == null || grant.ExpiredRequests == null || grant.Runs.Values.Distinct().Count() != grant.Runs.Count ||
                grant.Runs.Any(r => !Guid.TryParseExact(r.Value, "N", out parsed) ||
                    !_config.Roles.Any(p => string.Equals(p.Value?.Character, r.Key, StringComparison.OrdinalIgnoreCase))) ||
                grant.Requests.Any(r => r?.Item == null || !WithdrawalStore.IsActive(r) ||
                    !grant.Runs.ContainsKey(r.SourceCharacter)) ||
                grant.Requests.Select(r => r.Id).Distinct().Count() != grant.Requests.Count ||
                grant.Dispatches.Any(b => b == null || !grant.Runs.ContainsKey(b.Character)))
                throw new InvalidOperationException("Invalid withdrawal census participants or requests.");
        }

        private bool WithdrawalCensusOwnsReceipt(WithdrawalCensusGrant grant)
        {
            if (_receipt == null) return true;
            if (WithdrawalReceipt(_receipt))
                return grant.Requests.Any(r => r.Id == _receipt.BatchId || r.OrderId == _receipt.BatchId);
            if (IsDispatchEvidence(_receipt))
                return grant.Dispatches.Any(b => EvidenceMatchesDispatch(_receipt, b, Client.CharacterName));
            return _returnOffer != null && _receipt.BatchId == _returnOffer.Id &&
                (_receipt.Kind == "recovery-return-send" || _receipt.Kind == "recovery-return-receive");
        }

        private void JoinWithdrawalCensus(WithdrawalCensusGrant grant)
        {
            string run = grant.Runs[Client.CharacterName];
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(LocalCensusDirectory(run), "withdrawal-group.json"), grant);
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(LocalCensusDirectory(run), "superseded-operation.json"), new
            {
                Receipt = _receipt, Withdrawal = _withdrawal, Pickups = _pickupItems,
                Storage = _storageJob, Return = _returnOffer, Extraction = _extraction,
                Command = _workerCommand, Physical = PhysicalSlots(), DeliveryInferred = false
            });
            long pause = _withdrawalDispute ? _withdrawalDisputePause :
                _dispatchDispute != null ? _dispatchDisputePause : StartupCensusGate.PauseLocalCensus(grant.Reason);
            _receipt = null; _afterReceipt = null; _workerCommand = null; _reservedDispatch = null;
            _storageJob = null; _storageRecovery = null;
            _pendingConfirmation = Identity.None;
            ResetReturn(); ResetWithdrawalLocal();
            _receiptWaitId = null; _receiptWait.Reset();
            _extractionWaitId = null; _extractionWait.Reset();
            // Keep a local extraction's reservation until the new census commits;
            // TickLocalCensus already releases it after durable completion.
            _localCensus = run; _localCensusPause = pause; _localCensusReason = grant.Reason;
            _localCensusAttempt = 0; _localCensusIssued = false; _localCensusResult = null; _localCensusCommit = null;
            _dispatchCensusInventory = null;
            _localCensusRetry.Restart(); _localCensusPoll.Restart();
        }

        private bool TickWithdrawalCensus()
        {
            if (ServicePolicy.IsBagAuditMode() || !_enabled || !Client.InPlay) return false;
            if (_withdrawalCensusPoll.ElapsedMilliseconds < 1000)
                return (_withdrawalDispute || _withdrawalCensus != null) && _localCensus == null;
            _withdrawalCensusPoll.Restart();
            try
            {
                if (_withdrawalCensus == null)
                {
                    string id = WithdrawalStore.GetWithdrawalCensusId(_settingsDir, Client.CharacterName);
                    if (id != null)
                    {
                        _withdrawalCensus = CensusApplication.ReadExisting<WithdrawalCensusGrant>(
                            Path.Combine(WithdrawalCensusDirectory(id), "grant.json"));
                        ValidateWithdrawalCensus(_withdrawalCensus);
                    }
                    else if (_isCentral) TryStartWithdrawalCensus();
                }
                if (_withdrawalCensus == null)
                {
                    if (_withdrawalDispute)
                    {
                        TickBankerIpc();
                        if (Trade.IsTrading && _withdrawalCensusClose.ElapsedMilliseconds >= 1000)
                        { _withdrawalCensusClose.Restart(); TryDeclineTrade(); }
                    }
                    return _withdrawalDispute;
                }
                var grant = _withdrawalCensus;
                string directory = WithdrawalCensusDirectory(grant.Id);
                string frozen = Path.Combine(directory, "frozen.json");
                if (_isCentral && !File.Exists(frozen))
                {
                    if (!WithdrawalStore.TryReserveWithdrawalCensus(_settingsDir, grant.Id, _centralCharacter, grant.Runs, grant.Requests))
                    {
                        // No lease means the captured revisions became stale or
                        // another census still owns a peer. Retry from fresh rows.
                        if (WithdrawalStore.GetWithdrawalCensusId(_settingsDir, Client.CharacterName) != grant.Id)
                            _withdrawalCensus = null;
                        return _withdrawalDispute || _withdrawalCensus != null;
                    }
                    RuntimeStateStore.WriteJsonAtomic(frozen, grant);
                }
                TickBankerIpc(); // accounting remains responsive while movement is frozen
                if (!File.Exists(frozen)) return true;
                if (_localCensus == null)
                {
                    if (Trade.IsTrading)
                    {
                        if (_withdrawalCensusClose.ElapsedMilliseconds >= 1000)
                        { _withdrawalCensusClose.Restart(); TryDeclineTrade(); }
                        return true;
                    }
                    bool ownPause = (_withdrawalDispute && StartupCensusGate.OwnsLocalPause(_withdrawalDisputePause)) ||
                        (_dispatchDispute != null && StartupCensusGate.OwnsLocalPause(_dispatchDisputePause));
                    if ((!StartupCensusGate.IsOpen && !ownPause) || !Inventory.Bank.IsOpen || _dispatchCensus != null ||
                        _donationActive || _donationCleanup != null || _activeBatch != null || !WithdrawalCensusOwnsReceipt(grant)) return true;
                    JoinWithdrawalCensus(grant);
                }
            }
            catch (Exception ex)
            {
                if (_withdrawalCensusError != ex.Message) Logger.Warning("[CityBankers] Withdrawal census retry: " + ex.Message);
                _withdrawalCensusError = ex.Message;
                return true;
            }
            return _localCensus == null;
        }

        private bool HandleWithdrawalCensusResult(BagAuditAgent.BagAuditResult census)
        {
            var grant = CensusApplication.ReadExisting<WithdrawalCensusGrant>(
                Path.Combine(LocalCensusDirectory(census.RunId), "withdrawal-group.json"));
            if (grant == null) return false;
            ValidateWithdrawalCensus(grant);
            string run;
            if (!grant.Runs.TryGetValue(census.Character, out run) || run != census.RunId)
                throw new InvalidOperationException("Withdrawal census participant changed.");
            string directory = WithdrawalCensusDirectory(grant.Id);
            string completed = Path.Combine(directory, "completed.json");
            string path = Path.Combine(directory, census.RunId + ".json");
            var old = CensusApplication.ReadExisting<BagAuditAgent.BagAuditResult>(path);
            if (old != null && JsonConvert.SerializeObject(old) != JsonConvert.SerializeObject(census))
                throw new InvalidOperationException("Withdrawal census evidence changed during retry.");
            var done = CensusApplication.ReadExisting<WithdrawalCensusGrant>(completed);
            if (done != null)
            {
                if (old == null || JsonConvert.SerializeObject(done) != JsonConvert.SerializeObject(grant))
                    throw new InvalidOperationException("Withdrawal census completion changed.");
                return true;
            }
            if (grant.Runs.Any(r => !WithdrawalStore.OwnsCensus(_settingsDir, r.Value, r.Key)))
                throw new InvalidOperationException("Withdrawal census lost a participant.");
            if (old == null)
            {
                PhysicalLedgerReconciliation.ReadCensus(_settingsDir, census);
                RuntimeStateStore.WriteJsonAtomic(path, census);
            }
            var censuses = grant.Runs.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r =>
                CensusApplication.ReadExisting<BagAuditAgent.BagAuditResult>(Path.Combine(directory, r.Value + ".json"))).ToList();
            if (censuses.Any(c => c == null)) throw new InvalidOperationException("Waiting for all affected withdrawal inventories.");
            ApplyWithdrawalCensus(directory, grant, censuses);
            RuntimeStateStore.WriteJsonAtomic(completed, grant);
            return true;
        }

        private WithdrawalState WithdrawalDisposition(WithdrawalState original, PhysicalLedgerReconciliation.Plan plan,
            List<PhysicalLedgerReconciliation.Observation> observations, bool expired)
        {
            var row = JsonConvert.DeserializeObject<WithdrawalState>(JsonConvert.SerializeObject(original));
            if (WithdrawalStore.HasConfirmedDelivery(original)) { row.Status = "completed"; return row; }
            row.Status = "reconciled";
            row.Error = "Physical custody refreshed; no delivery inferred. Please request again if still needed.";
            var entry = plan.Items.SingleOrDefault(i => i.Id == original.ActiveLedgerId);
            if (entry == null || expired || !string.IsNullOrWhiteSpace(original.ReturnBatchId) || WithdrawalStore.HasStatus(original, "returning") ||
                WithdrawalStore.HasStatus(original, "return-queued")) return row;
            var physical = observations.SingleOrDefault(o => string.Equals(o.Character, entry.Character, StringComparison.OrdinalIgnoreCase) &&
                o.Location == entry.Location && o.Bag == entry.Bag && o.Slot == entry.Slot);
            if (physical == null || entry.AoId != original.Item.AoId || entry.HighId != original.Item.HighId || entry.Ql != original.Item.Ql)
                return row;
            if (string.Equals(entry.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                entry.Location == "inventory" && !entry.Bag.HasValue && !string.IsNullOrWhiteSpace(physical.Item.UniqueIdentity) &&
                physical.Item.UniqueIdentity != "(None:0000)")
            {
                row.Status = "central-ready"; row.CentralItemIdentity = physical.Item.UniqueIdentity;
                row.Error = "Exact pickup item recovered by complete physical census; pickup window renewed.";
            }
            else if (entry.Bag.HasValue && string.Equals(entry.Character, original.SourceCharacter, StringComparison.OrdinalIgnoreCase))
            {
                row.Status = "requested"; row.SourceBag = entry.Location;
                row.SourceBagOuterSlot = entry.Bag.Value; row.SourceInnerSlot = entry.Slot.Value;
                row.SourceItemIdentity = physical.Item.UniqueIdentity; row.CentralItemIdentity = null;
                row.ExtractedItemIdentity = null; row.PreExtractionInventorySlots = new List<int>();
                row.LiveInventoryAnchorSlot = null; row.LiveInventoryAnchorIdentity = null;
                row.PickupExpiresUtc = null;
                row.Error = "Exact source item recovered by complete physical census; extraction requeued from its current slot.";
            }
            return row;
        }

        private void ApplyWithdrawalCensus(string directory, WithdrawalCensusGrant grant, List<BagAuditAgent.BagAuditResult> censuses)
        {
            if (censuses.Count != grant.Runs.Count || censuses.Any(c => !grant.Runs.ContainsKey(c.Character) ||
                grant.Runs[c.Character] != c.RunId || !_config.Roles.Any(p => p.Key == c.Role &&
                    string.Equals(p.Value?.Character, c.Character, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Withdrawal audit scope changed.");
            var scope = new HashSet<string>(grant.Runs.Keys, StringComparer.OrdinalIgnoreCase);
            var observations = censuses.SelectMany(c => PhysicalLedgerReconciliation.ReadCensus(_settingsDir, c)).ToList();
            string path = Path.Combine(directory, "application.json");
            var bundle = CensusApplication.ReadExisting<WithdrawalCensusBundle>(path);
            if (bundle == null)
            {
                var ledger = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
                var storage = CensusApplication.ReadExisting<StorageState>(RuntimeStateStore.GetStorageStatePath(_settingsDir));
                if (ledger?.Items == null || storage?.Workers == null) throw new InvalidOperationException("Withdrawal census needs current ledger/storage.");
                foreach (var observation in observations.Where(o => o.Bag.HasValue && !string.IsNullOrWhiteSpace(o.BagIdentity)))
                {
                    var matches = storage.Workers.Where(w => string.Equals(w.Character, observation.Character, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(w => w.Bags).Where(b => b.LastUniqueIdentity == observation.BagIdentity).ToList();
                    if (matches.Count == 1 && censuses.Single(c => c.Character == observation.Character).Bags.Count(b => b.UniqueIdentity == observation.BagIdentity) == 1)
                    { observation.PreviousBag = matches[0].OuterSlotInstance & 65535; observation.PreviousLocation = matches[0].Source; }
                }
                var previous = ledger.Items.Where(i => scope.Contains(i.Character)).ToList();
                var delivered = new HashSet<string>(grant.Requests.Where(WithdrawalStore.HasConfirmedDelivery).Select(r => r.ActiveLedgerId));
                var anchors = JsonConvert.DeserializeObject<List<ActiveLedgerItem>>(JsonConvert.SerializeObject(
                    previous.Where(i => !delivered.Contains(i.Id)).ToList()));
                foreach (var request in grant.Requests.Where(r => !WithdrawalStore.HasStatus(r, "requested")))
                {
                    var anchor = anchors.SingleOrDefault(i => i.Id == request.ActiveLedgerId);
                    if (anchor == null) continue;
                    // Once extraction may have started, the old source slot is
                    // not an identity proof: another copy may now occupy it.
                    // Keep provenance eligible for unique-occurrence matching,
                    // but do not let the generic exact-slot pass claim it.
                    anchor.Location = "withdrawal-uncertain"; anchor.Bag = null; anchor.Slot = null;
                }
                foreach (var request in grant.Requests.Where(r => !string.IsNullOrWhiteSpace(r.CentralItemIdentity) &&
                    r.CentralItemIdentity != "(None:0000)" && !delivered.Contains(r.ActiveLedgerId)))
                {
                    var exact = observations.Where(o => string.Equals(o.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                        o.Location == "inventory" && !o.Bag.HasValue && o.Item.UniqueIdentity == request.CentralItemIdentity &&
                        o.Item.LowId == request.Item.AoId && o.Item.HighId == request.Item.HighId && o.Item.Ql == request.Item.Ql).ToList();
                    var anchor = anchors.SingleOrDefault(i => i.Id == request.ActiveLedgerId);
                    if (anchor == null || exact.Count != 1 || grant.Requests.Count(r => r.CentralItemIdentity == request.CentralItemIdentity) != 1) continue;
                    // A confirmed Central arrival is a stronger anchor than the
                    // historical source bag, which may have since been reused.
                    anchor.Character = _centralCharacter; anchor.Location = "inventory"; anchor.Bag = null; anchor.Slot = exact[0].Slot;
                }
                var plan = PhysicalLedgerReconciliation.Build(anchors, observations, scope);
                foreach (var item in plan.Items)
                    if (string.IsNullOrWhiteSpace(item.TransactionId)) item.TransactionId = "found-" + item.Id;
                var dispositions = grant.Requests.Select(r => WithdrawalDisposition(r, plan, observations,
                    grant.ExpiredRequests.Contains(r.Id))).ToList();
                bundle = new WithdrawalCensusBundle { Grant = grant, Censuses = censuses, Previous = previous,
                    MatchingAnchors = anchors, PreviousStorage = storage,
                    PreviousQueue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir)),
                    Plan = plan, Dispositions = dispositions, Storage = CensusApplication.BuildStorage(censuses, plan, grant.Id),
                    Queue = CensusApplication.BuildQueue(plan, observations,
                        _config.Roles.ToDictionary(p => p.Key, p => p.Value.Character, StringComparer.OrdinalIgnoreCase),
                        dispositions.Where(WithdrawalStore.IsActive).ToList(), "withdrawal-" + grant.Id) };
                RuntimeStateStore.WriteJsonAtomic(path, bundle);
            }
            if (JsonConvert.SerializeObject(bundle.Grant) != JsonConvert.SerializeObject(grant) ||
                JsonConvert.SerializeObject(bundle.Censuses) != JsonConvert.SerializeObject(censuses) || bundle.Plan == null ||
                bundle.Storage == null || bundle.Queue?.Batches == null || bundle.PreviousQueue?.Batches == null || bundle.Dispositions == null)
                throw new InvalidOperationException("Withdrawal census application changed during retry.");
            var queue = CensusApplication.ReadExisting<DispatchQueueState>(RuntimeStateStore.GetDispatchQueuePath(_settingsDir));
            var allowed = new HashSet<string>(bundle.PreviousQueue.Batches.Concat(bundle.Queue.Batches).Select(b => b.BatchId));
            if (queue?.Batches == null || queue.Batches.Any(b => !allowed.Contains(b.BatchId)))
                throw new InvalidOperationException("Dispatch queue changed during withdrawal census.");
            var mergedQueue = JsonConvert.DeserializeObject<DispatchQueueState>(JsonConvert.SerializeObject(bundle.Queue));
            foreach (var pending in queue.Batches.Where(b => !scope.Contains(b.Character) && UnresolvedDispatch(b)))
            { pending.RequiresPairedCensus = true; mergedQueue.Batches.Add(pending); }
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(ActiveLedgerStore.GetHistoryDirectory(_settingsDir),
                "census-withdrawal-" + grant.Id + ".json"), new
                { Grant = grant, EventTimeKnown = false, DeliveryInferred = false, bundle.Plan.Differences, bundle.Dispositions });
            foreach (var request in grant.Requests.Where(WithdrawalStore.HasConfirmedDelivery))
                ActiveLedgerStore.RecordCensusConfirmedDelivery(_settingsDir, request, bundle.Previous.SingleOrDefault(i => i.Id == request.ActiveLedgerId));
            foreach (var worker in bundle.Storage.Workers) RuntimeStorageStateTransactions.ReplaceCensusedWorker(_settingsDir, worker);
            var current = CensusApplication.ReadExisting<ActiveLedgerState>(ActiveLedgerStore.GetActiveLedgerPath(_settingsDir));
            if (current?.Items == null) throw new InvalidOperationException("Current ledger unavailable during withdrawal census application.");
            ActiveLedgerStore.ApplyCensus(_settingsDir, current.Items.Where(i => !scope.Contains(i.Character)).Concat(bundle.Plan.Items).ToList(),
                observations.Select(o => new TransferItemState { AoId = o.Item.LowId, HighId = o.Item.HighId, Ql = o.Item.Ql, Name = o.Item.Name }),
                "history/census-withdrawal-" + grant.Id + ".json",
                grant.Requests.Where(w => WithdrawalStore.HasConfirmedDelivery(w)).Select(w => w.ActiveLedgerId));
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, mergedQueue);
            WithdrawalStore.RestoreRequestsAfterWithdrawalCensus(_settingsDir, grant.Id, grant.Runs, grant.Requests, bundle.Dispositions);
        }

        private void FinishWithdrawalCensus()
        {
            if (_withdrawalCensus == null) return;
            foreach (var batch in _withdrawalCensus.Dispatches.Where(b => b.AttemptId != null))
            {
                _appliedDispatchReceipts.Remove(batch.AttemptId); _cancellationOutbox.Remove(batch.AttemptId);
                if (_lastStoredDispatchReceipt?.AttemptId == batch.AttemptId) _lastStoredDispatchReceipt = null;
            }
            if (_isCentral)
            {
                foreach (var name in _withdrawalCensus.Requests.Select(r => r.RequestedBy).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
                {
                    try { TellPlayer(name, "Your withdrawal inventory check is complete. Exact ready items have a fresh three-minute pickup window; " +
                        "verified bag items are queued again. Any unresolved request was closed without claiming delivery; check your order or request again."); }
                    catch (Exception ex) { Logger.Warning("[CityBankers] Withdrawal recovery notice unavailable: " + ex.Message); }
                }
            }
            foreach (var request in _withdrawalCensus.Requests)
            { _withdrawalAccountingRetry.Remove(request.Id); _withdrawalAccountingError.Remove(request.Id); }
            _withdrawalDispute = false; _withdrawalCensus = null;
            _dispatchDispute = null;
        }
    }
}
