using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Worker-side write-front reconciliation and narrow recovery for a completed
    /// Central -> worker transfer that stopped before insertion because the selected live
    /// bag was already full.
    ///
    /// Startup does not audit every bag. After the existing layout agent has reconciled
    /// persisted bag identities to current AO outer slots, this agent stages/opens only the
    /// next persisted writable bag. Its live contents are matched multiplicity-aware against
    /// persisted contents. Persisted items may never disappear during this reconciliation;
    /// extra physical items are imported only when they are accepted items routed to this
    /// exact worker. If the candidate is physically full, that truth is persisted and the
    /// agent advances until it proves one genuinely writable bag. Readiness waits for this
    /// marker, so normal dispatch cannot race stale write-front occupancy.
    ///
    /// After readiness, narrowly proven worker-local failures may be resumed without a second
    /// Central trade. Full-bag failures require the complete expected multiset to remain loose.
    /// Post-placement persistence failures use the original transaction id to subtract exact
    /// persisted occurrences from the expected batch, then require the complete remaining
    /// multiset to be loose in worker inventory. Unique identities are preferred when present;
    /// identity-less occurrences use AO id/high-id/QL multiplicity. Only the loose remainder is
    /// moved. A normal StorageBatchResult is published only after the complete original batch
    /// is durably accounted for; Central's worker-success reconciler remains queue-removal
    /// authority.
    /// </summary>
    public class StorageWriteFrontReconciliationAgent : ClientlessPluginEntry
    {
        public const string ReadyFilePrefix = "citybankers-storage-writefront-ready-";

        private const string LayoutMutexName = "CityBankers.StorageLayoutRemap.v1";
        private const int PollMilliseconds = 100;
        private const string FullBagFailure =
            "Live bag is full even though persisted state expected free space. Reconcile before continuing.";
        private const string PlacementPersistenceFailurePrefix =
            "AO placement succeeded but persistent state update failed:";

        private string _settingsDir;
        private WriteFrontConfig _config;
        private string _role;
        private string _centralCharacter;
        private bool _enabled;
        private bool _preflightReady;
        private DateTime _nextPollUtc;
        private string _runId;
        private string _lastProblem;

        private PreflightPhase _preflightPhase;
        private DateTime _preflightDeadlineUtc;
        private StorageBagState _preflightBag;
        private string _preflightBagIdentity;
        private string _preflightBagSource;
        private int _preflightOuterSlot;
        private int _preflightLiveItemCount;
        private int _preflightFreeSlots;
        private int _preflightImportedExtras;
        private bool _preflightCandidateFull;

        private RecoveryJob _recovery;

        private enum PreflightPhase
        {
            None,
            MovingBagToInventory,
            OpeningBag,
            ReturningFullBag,
            ReturningReadyBag
        }

        private enum RecoveryPhase
        {
            None,
            FindBag,
            MovingBagToInventory,
            OpeningBag,
            MovingItemIntoBag,
            ReturningBag
        }

        public override void Init(string pluginDir)
        {
            if (ServicePolicy.IsBagAuditMode())
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _config = LoadConfig();
            if (_config == null || _config.Roles == null)
                return;

            WriteFrontRole central;
            if (!TryGetRole("central", out central) ||
                central == null ||
                string.IsNullOrWhiteSpace(central.Character))
            {
                return;
            }

            _centralCharacter = central.Character;
            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role) ||
                string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _enabled = true;
            _runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8);
            _nextPollUtc = DateTime.UtcNow;
            _preflightPhase = PreflightPhase.None;

            DeleteIfExists(GetReadyPath(_settingsDir, Client.CharacterName));
            Client.OnUpdate += Tick;

            Logger.Information(
                "[CityBankers] STORAGE WRITE-FRONT initialized character=" +
                Client.CharacterName + " role=" + _role +
                "; waiting for live layout reconciliation before write-front proof.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Client.OnUpdate -= Tick;
            DeleteIfExists(GetReadyPath(_settingsDir, Client.CharacterName));
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (!_preflightReady)
                {
                    TickPreflight();
                    return;
                }

                if (!TrustedOperators.IsAllBankersReady())
                    return;

                if (_recovery != null)
                {
                    TickRecovery();
                    return;
                }

                if (Trade.IsTrading)
                    return;

                TryStartPartialPlacementRecovery();
                if (_recovery == null)
                    TryStartFullBagRecovery();
            }
            catch (Exception ex)
            {
                ReportProblem("Storage write-front tick failed: " + ex);
            }
        }

        // -----------------------------------------------------------------
        // Startup: prove and reconcile only the next actual write target.
        // -----------------------------------------------------------------

        private void TickPreflight()
        {
            string layoutPath = WorkerStorageLayoutRecoveryAgent.GetLayoutReadyPath(
                _settingsDir,
                Client.CharacterName);
            if (!File.Exists(layoutPath))
                return;

            if (_preflightPhase != PreflightPhase.None &&
                DateTime.UtcNow >= _preflightDeadlineUtc)
            {
                ReportProblem(
                    "WRITE FRONT NOT READY " + Client.CharacterName +
                    ": timed out in phase " + _preflightPhase + ".");
                ResetPreflightCandidate();
                return;
            }

            switch (_preflightPhase)
            {
                case PreflightPhase.None:
                    BeginPreflightCandidate();
                    break;
                case PreflightPhase.MovingBagToInventory:
                    TickPreflightMoveToInventory();
                    break;
                case PreflightPhase.OpeningBag:
                    TickPreflightOpen();
                    break;
                case PreflightPhase.ReturningFullBag:
                    TickPreflightReturn(true);
                    break;
                case PreflightPhase.ReturningReadyBag:
                    TickPreflightReturn(false);
                    break;
            }
        }

        private void BeginPreflightCandidate()
        {
            if (Inventory.Bank == null || !Inventory.Bank.IsOpen)
                return;

            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            StorageBagState bag = RuntimeStateStore.FindNextFreeBag(
                state,
                _role,
                Client.CharacterName);
            if (bag == null)
            {
                ReportProblem(
                    "WRITE FRONT NOT READY " + Client.CharacterName +
                    ": no persisted storage bag with free capacity remains.");
                return;
            }

            List<Item> live = FindLiveBagsForStoredBag(bag);
            if (live.Count != 1)
            {
                ReportProblem(
                    "WRITE FRONT NOT READY " + Client.CharacterName +
                    ": next persisted bag is not uniquely visible live; source=" +
                    (bag.Source ?? "?") + " identity=" +
                    (bag.LastUniqueIdentity ?? "<none>") + " slot=" +
                    bag.OuterSlotInstance + " matches=" + live.Count + ".");
                return;
            }

            Item liveBag = live[0];
            _preflightBag = bag;
            _preflightBagIdentity = liveBag.UniqueIdentity.ToString();
            _preflightBagSource = bag.Source;
            _preflightOuterSlot = liveBag.Slot.Instance;
            _preflightLiveItemCount = -1;
            _preflightFreeSlots = -1;
            _preflightImportedExtras = 0;
            _preflightCandidateFull = false;

            if (string.Equals(bag.Source, "bank", StringComparison.OrdinalIgnoreCase))
            {
                liveBag.MoveToInventory();
                _preflightPhase = PreflightPhase.MovingBagToInventory;
                _preflightDeadlineUtc = DateTime.UtcNow.AddMilliseconds(
                    ServicePolicy.BagMoveTimeoutMs);
                return;
            }

            liveBag.Use();
            _preflightPhase = PreflightPhase.OpeningBag;
            _preflightDeadlineUtc = DateTime.UtcNow.AddMilliseconds(
                ServicePolicy.BagOpenTimeoutMs);
        }

        private void TickPreflightMoveToInventory()
        {
            Item bag = FindInventoryBagByIdentity(_preflightBagIdentity);
            if (bag == null)
                return;

            bag.Use();
            _preflightPhase = PreflightPhase.OpeningBag;
            _preflightDeadlineUtc = DateTime.UtcNow.AddMilliseconds(
                ServicePolicy.BagOpenTimeoutMs);
        }

        private void TickPreflightOpen()
        {
            Container container = FindContainerByIdentity(_preflightBagIdentity);
            if (container == null || !container.IsOpen || container.Items == null)
                return;

            int imported;
            string error;
            if (!TryReconcileOpenBagContents(container, out imported, out error))
            {
                ReportProblem(
                    "WRITE FRONT NOT READY " + Client.CharacterName + ": " + error);
                return;
            }

            _preflightImportedExtras = imported;
            _preflightLiveItemCount = container.Items.Count;
            _preflightFreeSlots = Math.Max(0, container.NumFreeSlots);
            _preflightCandidateFull = container.IsFull || _preflightFreeSlots <= 0;

            if (string.Equals(
                    _preflightBagSource,
                    "bank",
                    StringComparison.OrdinalIgnoreCase))
            {
                Item bag = FindInventoryBagByIdentity(_preflightBagIdentity);
                if (bag == null)
                {
                    ReportProblem(
                        "WRITE FRONT NOT READY " + Client.CharacterName +
                        ": staged bank bag disappeared before return.");
                    return;
                }

                bag.MoveToBank();
                _preflightPhase = _preflightCandidateFull
                    ? PreflightPhase.ReturningFullBag
                    : PreflightPhase.ReturningReadyBag;
                _preflightDeadlineUtc = DateTime.UtcNow.AddMilliseconds(
                    ServicePolicy.BagMoveTimeoutMs);
                return;
            }

            if (_preflightCandidateFull)
            {
                Logger.Information(
                    "[CityBankers] WRITE FRONT reconciled physically full inventory bag " +
                    _preflightBagIdentity + " on " + Client.CharacterName +
                    "; advancing to the next persisted free bag.");
                ResetPreflightCandidate();
                return;
            }

            PublishReadyMarker();
        }

        private void TickPreflightReturn(bool candidateWasFull)
        {
            Item bag = FindBankBagByIdentity(_preflightBagIdentity);
            if (bag == null)
                return;

            if (bag.Slot.Instance != _preflightOuterSlot)
            {
                string error;
                if (!TryUpdatePersistedBagSlot(
                        _preflightBagIdentity,
                        _preflightBagSource,
                        bag.Slot.Instance,
                        out error))
                {
                    ReportProblem(
                        "WRITE FRONT NOT READY " + Client.CharacterName +
                        ": returned bank bag slot update failed: " + error);
                    return;
                }
                _preflightOuterSlot = bag.Slot.Instance;
            }

            if (candidateWasFull)
            {
                Logger.Information(
                    "[CityBankers] WRITE FRONT reconciled physically full bank bag " +
                    _preflightBagIdentity + " on " + Client.CharacterName +
                    "; advancing to the next persisted free bag.");
                ResetPreflightCandidate();
                return;
            }

            PublishReadyMarker();
        }

        private bool TryReconcileOpenBagContents(
            Container container,
            out int importedExtras,
            out string error)
        {
            importedExtras = 0;
            error = null;

            if (container == null || container.Items == null || _preflightBag == null)
            {
                error = "opened bag contents are unavailable";
                return false;
            }

            List<Item> liveItems = container.Items
                .Where(item => item != null)
                .OrderBy(item => item.Slot.Instance)
                .ToList();

            using (var mutex = new Mutex(false, LayoutMutexName))
            {
                bool entered = false;
                try
                {
                    try
                    {
                        entered = mutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        entered = true;
                    }

                    if (!entered)
                    {
                        error = "timed out waiting for storage-state reconciliation lock";
                        return false;
                    }

                    StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
                    StorageWorkerState worker = FindWorker(state);
                    if (worker == null)
                    {
                        error = "persistent storage worker entry is missing";
                        return false;
                    }

                    List<StorageBagState> bagMatches = (worker.Bags ?? new List<StorageBagState>())
                        .Where(bag => bag != null &&
                            string.Equals(
                                bag.Source,
                                _preflightBagSource,
                                StringComparison.OrdinalIgnoreCase) &&
                            ((!string.IsNullOrWhiteSpace(_preflightBagIdentity) &&
                              string.Equals(
                                  bag.LastUniqueIdentity,
                                  _preflightBagIdentity,
                                  StringComparison.Ordinal)) ||
                             bag.OuterSlotInstance == _preflightOuterSlot))
                        .ToList();
                    if (bagMatches.Count != 1)
                    {
                        error = "persistent target bag is not uniquely resolvable; matches=" +
                            bagMatches.Count;
                        return false;
                    }

                    StorageBagState persistedBag = bagMatches[0];
                    if (liveItems.Count > persistedBag.Capacity)
                    {
                        error = "live bag contains " + liveItems.Count +
                            " item(s), exceeding persisted capacity " +
                            persistedBag.Capacity;
                        return false;
                    }

                    var remainingLive = new List<Item>(liveItems);
                    var merged = new List<StoredItemState>();
                    foreach (StoredItemState persisted in
                        persistedBag.Items ?? new List<StoredItemState>())
                    {
                        if (persisted == null)
                            continue;

                        int index = FindMatchingLiveItemIndex(remainingLive, persisted);
                        if (index < 0)
                        {
                            error =
                                "persisted item is missing from the live bag; refusing to " +
                                "erase physical/accounting history: " +
                                (persisted.Name ?? "<unnamed>") + " AOID=" +
                                persisted.AoId + " QL" + persisted.Ql;
                            return false;
                        }

                        Item actual = remainingLive[index];
                        remainingLive.RemoveAt(index);
                        persisted.UniqueIdentity = IsUsableIdentity(
                            actual.UniqueIdentity.ToString())
                                ? actual.UniqueIdentity.ToString()
                                : null;
                        persisted.AoId = actual.Id;
                        persisted.HighId = actual.HighId;
                        persisted.Ql = actual.Ql;
                        persisted.Name = actual.Name ?? persisted.Name ?? string.Empty;
                        persisted.InnerSlot = actual.Slot.Instance & 0xFFFF;
                        persisted.ObservedUtc = DateTime.UtcNow;
                        merged.Add(persisted);
                    }

                    var importedLedgerItems = new List<LedgerItem>();
                    foreach (Item extra in remainingLive)
                    {
                        string routedRole;
                        bool managed = SymbiantCatalog.TryGetDestinationRole(
                            _settingsDir,
                            extra.Id,
                            out routedRole);
                        if (!managed && extra.HighId != extra.Id)
                        {
                            managed = SymbiantCatalog.TryGetDestinationRole(
                                _settingsDir,
                                extra.HighId,
                                out routedRole);
                        }

                        if (!managed || !string.Equals(
                                routedRole,
                                _role,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            error =
                                "live bag contains an unaccounted item that is unmanaged or " +
                                "routed elsewhere; refusing automatic import: " +
                                (extra.Name ?? "<unnamed>") + " AOID=" + extra.Id +
                                " QL" + extra.Ql + " routed=" +
                                (routedRole ?? "unmanaged");
                            return false;
                        }

                        var imported = new StoredItemState
                        {
                            UniqueIdentity = IsUsableIdentity(extra.UniqueIdentity.ToString())
                                ? extra.UniqueIdentity.ToString()
                                : null,
                            AoId = extra.Id,
                            HighId = extra.HighId,
                            Ql = extra.Ql,
                            Name = extra.Name ?? string.Empty,
                            InnerSlot = extra.Slot.Instance & 0xFFFF,
                            ObservedUtc = DateTime.UtcNow,
                            TransactionId = "writefront-reconcile-" + _runId
                        };
                        merged.Add(imported);
                        importedExtras++;
                        importedLedgerItems.Add(new LedgerItem
                        {
                            UniqueIdentity = imported.UniqueIdentity,
                            AoId = imported.AoId,
                            HighId = imported.HighId,
                            Ql = imported.Ql,
                            Name = imported.Name,
                            Role = _role,
                            BagSource = persistedBag.Source,
                            BagOuterSlot = _preflightOuterSlot,
                            InnerSlot = imported.InnerSlot
                        });
                    }

                    persistedBag.Items = merged
                        .OrderBy(item => item.InnerSlot)
                        .ToList();
                    persistedBag.LastUniqueIdentity = _preflightBagIdentity;
                    persistedBag.LastHandle = container.Handle;
                    persistedBag.OuterSlotInstance = _preflightOuterSlot;
                    worker.ObservedUtc = DateTime.UtcNow;
                    state.UpdatedUtc = DateTime.UtcNow;

                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetStorageStatePath(_settingsDir),
                        state);
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetCurrentStockPath(_settingsDir),
                        RuntimeStateStore.BuildCurrentStock(_settingsDir, state));

                    if (importedLedgerItems.Count > 0)
                    {
                        RuntimeStateStore.AppendLedger(
                            _settingsDir,
                            new LedgerRecord
                            {
                                Utc = DateTime.UtcNow,
                                Event = "write_front_live_contents_reconciled",
                                TransactionId = "writefront-reconcile-" + _runId,
                                Actor = Client.CharacterName,
                                Role = _role,
                                Character = Client.CharacterName,
                                Source = "live-bag",
                                Destination = "storage-state",
                                Message =
                                    "Startup write-front reconciliation imported " +
                                    importedLedgerItems.Count +
                                    " physically present managed item(s) that were absent " +
                                    "from persisted bag occupancy. Existing persisted items " +
                                    "were all matched before import.",
                                Items = importedLedgerItems
                            });
                    }

                    return true;
                }
                finally
                {
                    if (entered)
                        mutex.ReleaseMutex();
                }
            }
        }

        private bool TryUpdatePersistedBagSlot(
            string identity,
            string source,
            int liveSlot,
            out string error)
        {
            error = null;
            using (var mutex = new Mutex(false, LayoutMutexName))
            {
                bool entered = false;
                try
                {
                    try
                    {
                        entered = mutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        entered = true;
                    }

                    if (!entered)
                    {
                        error = "timed out waiting for bag-slot update lock";
                        return false;
                    }

                    StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
                    StorageWorkerState worker = FindWorker(state);
                    if (worker == null)
                    {
                        error = "persistent storage worker entry is missing";
                        return false;
                    }

                    List<StorageBagState> matches = (worker.Bags ?? new List<StorageBagState>())
                        .Where(bag => bag != null &&
                            string.Equals(bag.Source, source, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(
                                bag.LastUniqueIdentity,
                                identity,
                                StringComparison.Ordinal))
                        .ToList();
                    if (matches.Count != 1)
                    {
                        error = "returned bag identity is not unique in persisted state; matches=" +
                            matches.Count;
                        return false;
                    }

                    matches[0].OuterSlotInstance = liveSlot;
                    worker.ObservedUtc = DateTime.UtcNow;
                    state.UpdatedUtc = DateTime.UtcNow;
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetStorageStatePath(_settingsDir),
                        state);
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetCurrentStockPath(_settingsDir),
                        RuntimeStateStore.BuildCurrentStock(_settingsDir, state));
                    return true;
                }
                finally
                {
                    if (entered)
                        mutex.ReleaseMutex();
                }
            }
        }

        private void PublishReadyMarker()
        {
            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            string path = GetReadyPath(_settingsDir, Client.CharacterName);
            string temp = path + ".tmp";
            var marker = new WriteFrontReadyMarker
            {
                Format = "citybankers-storage-writefront-ready-v1",
                Role = _role,
                Character = Client.CharacterName,
                BaselineRunId = state?.BaselineRunId,
                ReadyUtc = DateTime.UtcNow,
                BagSource = _preflightBagSource,
                BagUniqueIdentity = _preflightBagIdentity,
                BagOuterSlot = _preflightOuterSlot,
                LiveItemCount = _preflightLiveItemCount,
                FreeSlots = _preflightFreeSlots,
                ImportedExtraItems = _preflightImportedExtras
            };

            File.WriteAllText(temp, JsonConvert.SerializeObject(marker, Formatting.Indented));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);

            _preflightReady = true;
            _lastProblem = null;
            Logger.Information(
                "[CityBankers] STORAGE WRITE FRONT READY " + Client.CharacterName +
                ": " + (_preflightBagSource ?? "?") + " bag " +
                _preflightOuterSlot + " liveItems=" + _preflightLiveItemCount +
                " freeSlots=" + _preflightFreeSlots +
                " importedExtras=" + _preflightImportedExtras + ".");
            if (_preflightImportedExtras > 0)
            {
                TellKavem(
                    "Storage write-front reconciled on " + Client.CharacterName +
                    ": imported " + _preflightImportedExtras +
                    " physically present managed item(s) into persisted occupancy; next " +
                    "write bag has " + _preflightFreeSlots + " free slot(s).");
            }
        }

        private void ResetPreflightCandidate()
        {
            _preflightPhase = PreflightPhase.None;
            _preflightDeadlineUtc = DateTime.MinValue;
            _preflightBag = null;
            _preflightBagIdentity = null;
            _preflightBagSource = null;
            _preflightOuterSlot = -1;
            _preflightLiveItemCount = -1;
            _preflightFreeSlots = -1;
            _preflightImportedExtras = 0;
            _preflightCandidateFull = false;
        }

        // -----------------------------------------------------------------
        // Post-readiness local recovery for this exact pre-insertion failure.
        // -----------------------------------------------------------------

        private void TryStartPartialPlacementRecovery()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            DispatchBatchState batch = (queue?.Batches ?? new List<DispatchBatchState>())
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        candidate.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase) &&
                    candidate.LastError != null &&
                    candidate.LastError.StartsWith(
                        PlacementPersistenceFailurePrefix,
                        StringComparison.Ordinal));
            if (batch == null || batch.Items == null || batch.Items.Count == 0)
                return;

            StorageBatchResult existing = RuntimeStateStore.ReadStorageResult(
                _settingsDir,
                Client.CharacterName);
            if (existing != null && string.Equals(
                    existing.BatchId,
                    batch.BatchId,
                    StringComparison.Ordinal))
            {
                bool exactSuccess =
                    existing.Success &&
                    existing.ExpectedCount == batch.Items.Count &&
                    existing.StoredCount == batch.Items.Count &&
                    string.Equals(existing.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        existing.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase);
                if (exactSuccess)
                    return;

                if (existing.Success)
                {
                    ReportProblem(
                        "PARTIAL PLACEMENT RECOVERY BLOCKED " + _role + " batch " +
                        ShortId(batch.BatchId) +
                        ": an existing success result does not exactly match the failed " +
                        "batch. Waiting for operator reconciliation.");
                    return;
                }
            }

            List<TransferItemState> remaining;
            int alreadyStored;
            string partitionError;
            if (!TryBuildPartialPlacementPartition(
                    batch,
                    out remaining,
                    out alreadyStored,
                    out partitionError))
            {
                ReportProblem(
                    "PARTIAL PLACEMENT RECOVERY BLOCKED " + _role + " batch " +
                    ShortId(batch.BatchId) + ": " + partitionError +
                    " No physical state was guessed.");
                return;
            }

            _recovery = new RecoveryJob
            {
                BatchId = batch.BatchId,
                TransactionId = batch.TransactionId,
                Items = remaining,
                ExpectedCount = batch.Items.Count,
                Index = 0,
                StoredCount = alreadyStored,
                RecoveryKind = "PARTIAL PLACEMENT",
                Phase = RecoveryPhase.FindBag,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.ItemMoveTimeoutMs)
            };

            string notice =
                "PARTIAL PLACEMENT RECOVERY " + _role + " batch " +
                ShortId(batch.BatchId) + ": transaction-bound stock proves " +
                alreadyStored + " already persisted and " + remaining.Count +
                " still loose on " + Client.CharacterName +
                "; resuming only the loose remainder. No Central re-trade.";
            Logger.Information("[CityBankers] " + notice);
            TellKavem(notice);
        }

        private bool TryBuildPartialPlacementPartition(
            DispatchBatchState batch,
            out List<TransferItemState> looseRemainder,
            out int alreadyStored,
            out string error)
        {
            looseRemainder = new List<TransferItemState>();
            alreadyStored = 0;
            error = null;

            if (batch == null || batch.Items == null || batch.Items.Count == 0 ||
                string.IsNullOrWhiteSpace(batch.TransactionId))
            {
                error = "the failed batch has no transaction-bound expected item set.";
                return false;
            }

            CurrentStockState stock = RuntimeStateStore.LoadCurrentStock(_settingsDir);
            var unmatchedExpected = new List<TransferItemState>(batch.Items);
            List<StockItemState> persistedForTransaction =
                (stock?.Items ?? new List<StockItemState>())
                    .Where(item =>
                        item != null &&
                        string.Equals(
                            item.TransactionId,
                            batch.TransactionId,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            item.PhysicalRole,
                            _role,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            item.Character,
                            Client.CharacterName,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            foreach (StockItemState persisted in persistedForTransaction)
            {
                int expectedIndex = FindExpectedMatchIndex(unmatchedExpected, persisted);
                if (expectedIndex < 0)
                {
                    error =
                        "persisted transaction " + batch.TransactionId +
                        " contains an occurrence not present in the failed batch: " +
                        (persisted.Name ?? "<unnamed>") + " AOID=" + persisted.AoId +
                        " QL" + persisted.Ql + ".";
                    return false;
                }

                unmatchedExpected.RemoveAt(expectedIndex);
                alreadyStored++;
            }

            if (alreadyStored == 0)
            {
                error =
                    "no destination-worker stock occurrence carries the failed batch " +
                    "transaction id after write-front reconciliation.";
                return false;
            }

            List<Item> availableLoose = Inventory.Items == null
                ? new List<Item>()
                : Inventory.Items
                    .Where(item =>
                        item != null &&
                        item.Slot.Type == IdentityType.Inventory &&
                        item.UniqueIdentity.Type != IdentityType.Container)
                    .OrderBy(item => item.Slot.Instance)
                    .ToList();

            foreach (TransferItemState expected in unmatchedExpected)
            {
                int looseIndex = FindLooseMatchIndex(availableLoose, expected);
                if (looseIndex < 0)
                {
                    error =
                        "the transaction-bound persisted occurrences account for " +
                        alreadyStored + "/" + batch.Items.Count +
                        " item(s), but the remaining expected multiset is not completely " +
                        "loose on " + Client.CharacterName + "; missing " +
                        (expected?.Name ?? "<unnamed>") + " AOID=" +
                        (expected?.AoId ?? 0) + " QL" + (expected?.Ql ?? 0) + ".";
                    return false;
                }

                availableLoose.RemoveAt(looseIndex);
                looseRemainder.Add(expected);
            }

            if (alreadyStored + looseRemainder.Count != batch.Items.Count)
            {
                error =
                    "persisted and loose multiplicities do not account for the complete " +
                    "original batch.";
                return false;
            }

            return true;
        }

        private static int FindExpectedMatchIndex(
            List<TransferItemState> expectedItems,
            StockItemState persisted)
        {
            if (expectedItems == null || persisted == null)
                return -1;

            if (IsUsableIdentity(persisted.UniqueIdentity))
            {
                int identityIndex = expectedItems.FindIndex(expected =>
                    expected != null &&
                    IsUsableIdentity(expected.UniqueIdentity) &&
                    string.Equals(
                        expected.UniqueIdentity,
                        persisted.UniqueIdentity,
                        StringComparison.Ordinal));
                if (identityIndex >= 0)
                    return identityIndex;
            }

            return expectedItems.FindIndex(expected =>
                expected != null &&
                !IsUsableIdentity(expected.UniqueIdentity) &&
                expected.AoId == persisted.AoId &&
                expected.HighId == persisted.HighId &&
                expected.Ql == persisted.Ql);
        }

        private static int FindLooseMatchIndex(
            List<Item> looseItems,
            TransferItemState expected)
        {
            if (looseItems == null || expected == null)
                return -1;

            if (IsUsableIdentity(expected.UniqueIdentity))
            {
                return looseItems.FindIndex(item =>
                    item != null && string.Equals(
                        item.UniqueIdentity.ToString(),
                        expected.UniqueIdentity,
                        StringComparison.Ordinal));
            }

            return looseItems.FindIndex(item =>
                item != null &&
                item.Id == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql);
        }

        private void TryStartFullBagRecovery()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            DispatchBatchState batch = (queue?.Batches ?? new List<DispatchBatchState>())
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        candidate.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.LastError, FullBagFailure, StringComparison.Ordinal));
            if (batch == null || batch.Items == null || batch.Items.Count == 0)
                return;

            List<Item> physical = FindDistinctInventoryItems(batch.Items);
            if (physical.Count != batch.Items.Count)
                return;

            StorageBatchResult existing = RuntimeStateStore.ReadStorageResult(
                _settingsDir,
                Client.CharacterName);
            if (existing != null && string.Equals(
                    existing.BatchId,
                    batch.BatchId,
                    StringComparison.Ordinal))
            {
                if (existing.Success)
                    return;

                if (existing.StoredCount != 0 ||
                    !string.Equals(existing.Error, FullBagFailure, StringComparison.Ordinal))
                {
                    return;
                }

                RuntimeStateStore.DeleteIfExists(
                    RuntimeStateStore.GetStorageResultPath(
                        _settingsDir,
                        Client.CharacterName));
            }

            _recovery = new RecoveryJob
            {
                BatchId = batch.BatchId,
                TransactionId = batch.TransactionId,
                Items = new List<TransferItemState>(batch.Items),
                ExpectedCount = batch.Items.Count,
                Index = 0,
                StoredCount = 0,
                RecoveryKind = "FULL-BAG",
                Phase = RecoveryPhase.FindBag,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.ItemMoveTimeoutMs)
            };

            string notice =
                "WORKER FULL-BAG RECOVERY " + _role + " batch " +
                ShortId(batch.BatchId) + ": verified " + batch.Items.Count + "/" +
                batch.Items.Count + " expected item(s) loose on " +
                Client.CharacterName + "; resuming local placement after write-front " +
                "occupancy reconciliation. No Central re-trade.";
            Logger.Information("[CityBankers] " + notice);
            TellKavem(notice);
        }

        private void TickRecovery()
        {
            if (_recovery == null)
                return;

            if (_recovery.Index >= (_recovery.Items?.Count ?? 0))
            {
                CompleteRecovery();
                return;
            }

            if (_recovery.Phase != RecoveryPhase.FindBag &&
                DateTime.UtcNow >= _recovery.DeadlineUtc)
            {
                FailRecovery("Timed out in recovery phase " + _recovery.Phase + ".");
                return;
            }

            switch (_recovery.Phase)
            {
                case RecoveryPhase.FindBag:
                    StartRecoveryItem();
                    break;
                case RecoveryPhase.MovingBagToInventory:
                    TickRecoveryMoveToInventory();
                    break;
                case RecoveryPhase.OpeningBag:
                    TickRecoveryOpen();
                    break;
                case RecoveryPhase.MovingItemIntoBag:
                    TickRecoveryMoveItem();
                    break;
                case RecoveryPhase.ReturningBag:
                    TickRecoveryReturnBag();
                    break;
            }
        }

        private void StartRecoveryItem()
        {
            TransferItemState expected = _recovery.Items[_recovery.Index];
            Item loose = FindInventoryItem(expected);
            if (loose == null)
            {
                FailRecovery(
                    "Expected recovery item is not loose in worker inventory: " +
                    expected.Name + " AOID=" + expected.AoId + ".");
                return;
            }

            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            StorageBagState bag = RuntimeStateStore.FindNextFreeBag(
                state,
                _role,
                Client.CharacterName);
            if (bag == null)
            {
                FailRecovery("No persisted storage bag with free capacity remains.");
                return;
            }

            List<Item> live = FindLiveBagsForStoredBag(bag);
            if (live.Count != 1)
            {
                FailRecovery(
                    "Recovery target bag is not uniquely visible live; matches=" +
                    live.Count + ".");
                return;
            }

            Item liveBag = live[0];
            _recovery.Expected = expected;
            _recovery.Bag = bag;
            _recovery.BagIdentity = liveBag.UniqueIdentity.ToString();
            _recovery.BagOuterSlot = liveBag.Slot.Instance;
            _recovery.ObservedItemIdentity = IsUsableIdentity(loose.UniqueIdentity.ToString())
                ? loose.UniqueIdentity.ToString()
                : null;
            _recovery.InnerSlot = -1;
            _recovery.PreInsertSlots = null;

            if (string.Equals(bag.Source, "bank", StringComparison.OrdinalIgnoreCase))
            {
                liveBag.MoveToInventory();
                _recovery.Phase = RecoveryPhase.MovingBagToInventory;
                SetRecoveryDeadline(ServicePolicy.BagMoveTimeoutMs);
                return;
            }

            liveBag.Use();
            _recovery.Phase = RecoveryPhase.OpeningBag;
            SetRecoveryDeadline(ServicePolicy.BagOpenTimeoutMs);
        }

        private void TickRecoveryMoveToInventory()
        {
            Item bag = FindInventoryBagByIdentity(_recovery.BagIdentity);
            if (bag == null)
                return;

            bag.Use();
            _recovery.Phase = RecoveryPhase.OpeningBag;
            SetRecoveryDeadline(ServicePolicy.BagOpenTimeoutMs);
        }

        private void TickRecoveryOpen()
        {
            Container container = FindContainerByIdentity(_recovery.BagIdentity);
            if (container == null || !container.IsOpen || container.Items == null)
                return;

            if (container.IsFull || container.NumFreeSlots <= 0)
            {
                FailRecovery(
                    "Write-front proof became stale before recovery: selected live bag is full.");
                return;
            }

            Item loose = FindInventoryItem(_recovery.Expected);
            if (loose == null)
            {
                FailRecovery("Recovery item disappeared before bag insertion.");
                return;
            }

            _recovery.PreInsertSlots = new HashSet<int>(
                container.Items
                    .Where(item => item != null)
                    .Select(item => item.Slot.Instance & 0xFFFF));
            _recovery.BagHandle = container.Handle;
            loose.MoveToContainer(container);
            _recovery.Phase = RecoveryPhase.MovingItemIntoBag;
            SetRecoveryDeadline(ServicePolicy.ItemMoveTimeoutMs);
        }

        private void TickRecoveryMoveItem()
        {
            Container container = FindContainerByIdentity(_recovery.BagIdentity);
            if (container == null || container.Items == null)
                return;

            Item observed = container.Items.FirstOrDefault(item =>
                MatchesTransferItem(item, _recovery.Expected) &&
                (_recovery.PreInsertSlots == null ||
                 !_recovery.PreInsertSlots.Contains(item.Slot.Instance & 0xFFFF)));
            if (observed == null)
                return;

            _recovery.InnerSlot = observed.Slot.Instance & 0xFFFF;
            _recovery.StoredItemIdentity = IsUsableIdentity(observed.UniqueIdentity.ToString())
                ? observed.UniqueIdentity.ToString()
                : null;
            _recovery.BagHandle = container.Handle;

            if (string.Equals(
                    _recovery.Bag.Source,
                    "bank",
                    StringComparison.OrdinalIgnoreCase))
            {
                Item bag = FindInventoryBagByIdentity(_recovery.BagIdentity);
                if (bag == null)
                {
                    FailRecovery(
                        "Recovery item is stored, but staged bank bag is unavailable for return.");
                    return;
                }

                bag.MoveToBank();
                _recovery.Phase = RecoveryPhase.ReturningBag;
                SetRecoveryDeadline(ServicePolicy.BagMoveTimeoutMs);
                return;
            }

            CommitRecoveryPlacement();
        }

        private void TickRecoveryReturnBag()
        {
            Item bag = FindBankBagByIdentity(_recovery.BagIdentity);
            if (bag == null)
                return;

            if (bag.Slot.Instance != _recovery.BagOuterSlot)
            {
                FailRecovery(
                    "Recovery bank bag returned to unexpected outer slot " +
                    bag.Slot.Instance + " instead of " + _recovery.BagOuterSlot + ".");
                return;
            }

            CommitRecoveryPlacement();
        }

        private void CommitRecoveryPlacement()
        {
            string error;
            if (!RuntimeStateStore.RecordPlacement(
                    _settingsDir,
                    _recovery.TransactionId,
                    _role,
                    Client.CharacterName,
                    _recovery.Bag.Source,
                    _recovery.BagOuterSlot,
                    _recovery.BagIdentity,
                    _recovery.BagHandle,
                    _recovery.Expected,
                    _recovery.StoredItemIdentity,
                    _recovery.InnerSlot,
                    out error))
            {
                FailRecovery(
                    "AO recovery placement succeeded but persistent state update failed: " +
                    error);
                return;
            }

            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "item_stored_worker_local_recovery",
                    TransactionId = _recovery.TransactionId,
                    BatchId = _recovery.BatchId,
                    Actor = Client.CharacterName,
                    Role = _role,
                    Character = Client.CharacterName,
                    Source = "normal-inventory",
                    Destination = _recovery.Bag.Source + ":" +
                        _recovery.BagOuterSlot + "/inner:" + _recovery.InnerSlot,
                    Message =
                        "AO confirmed worker-local placement during " +
                        (_recovery.RecoveryKind ?? "storage") + " recovery after startup " +
                        "write-front occupancy reconciliation.",
                    Items = new List<LedgerItem>
                    {
                        new LedgerItem
                        {
                            UniqueIdentity = _recovery.Expected?.UniqueIdentity,
                            AoId = _recovery.Expected?.AoId ?? 0,
                            HighId = _recovery.Expected?.HighId ?? 0,
                            Ql = _recovery.Expected?.Ql ?? 0,
                            Name = _recovery.Expected?.Name,
                            Role = _role,
                            BagSource = _recovery.Bag.Source,
                            BagOuterSlot = _recovery.BagOuterSlot,
                            InnerSlot = _recovery.InnerSlot
                        }
                    }
                });

            TellKavem(
                "Recovered storage: " + _recovery.Expected.Name + " QL" +
                _recovery.Expected.Ql + " on " + Client.CharacterName + " -> " +
                _recovery.Bag.Source + " bag " + _recovery.BagOuterSlot +
                ", slot " + _recovery.InnerSlot + ".");

            _recovery.StoredCount++;
            _recovery.Index++;
            _recovery.Expected = null;
            _recovery.Bag = null;
            _recovery.BagIdentity = null;
            _recovery.ObservedItemIdentity = null;
            _recovery.StoredItemIdentity = null;
            _recovery.InnerSlot = -1;
            _recovery.BagHandle = 0;
            _recovery.BagOuterSlot = -1;
            _recovery.PreInsertSlots = null;
            _recovery.Phase = RecoveryPhase.FindBag;
            SetRecoveryDeadline(ServicePolicy.ItemMoveTimeoutMs);
        }

        private void CompleteRecovery()
        {
            RecoveryJob job = _recovery;
            RuntimeStateStore.WriteStorageResult(
                _settingsDir,
                new StorageBatchResult
                {
                    BatchId = job.BatchId,
                    TransactionId = job.TransactionId,
                    Role = _role,
                    Character = Client.CharacterName,
                    CompletedUtc = DateTime.UtcNow,
                    Success = true,
                    ExpectedCount = job.ExpectedCount,
                    StoredCount = job.StoredCount
                });

            string notice =
                "WORKER " + (job.RecoveryKind ?? "LOCAL") +
                " RECOVERY COMPLETE " + _role + " batch " +
                ShortId(job.BatchId) + ": stored=" + job.StoredCount + "/" +
                job.ExpectedCount +
                "; exact worker success is ready for Central queue reconciliation.";
            Logger.Information("[CityBankers] " + notice);
            TellKavem(notice);
            _recovery = null;
        }

        private void FailRecovery(string error)
        {
            if (_recovery == null)
                return;

            RecoveryJob job = _recovery;
            RuntimeStateStore.WriteStorageResult(
                _settingsDir,
                new StorageBatchResult
                {
                    BatchId = job.BatchId,
                    TransactionId = job.TransactionId,
                    Role = _role,
                    Character = Client.CharacterName,
                    CompletedUtc = DateTime.UtcNow,
                    Success = false,
                    ExpectedCount = job.ExpectedCount,
                    StoredCount = job.StoredCount,
                    Error = error
                });

            Logger.Error(
                "[CityBankers] WORKER " + (job.RecoveryKind ?? "LOCAL") +
                " RECOVERY FAILED " + _role +
                " batch " + ShortId(job.BatchId) + ": " + error);
            TellKavem(
                "WORKER " + (job.RecoveryKind ?? "LOCAL") +
                " RECOVERY FAILED " + _role + ": " + error +
                " Queue remains failed; no physical state was guessed.");
            _recovery = null;
        }

        // -----------------------------------------------------------------
        // Helpers.
        // -----------------------------------------------------------------

        private StorageWorkerState FindWorker(StorageState state)
        {
            return (state?.Workers ?? new List<StorageWorkerState>())
                .FirstOrDefault(worker =>
                    worker != null &&
                    string.Equals(worker.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        worker.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase));
        }

        private List<Item> FindLiveBagsForStoredBag(StorageBagState bag)
        {
            bool inventory = string.Equals(
                bag?.Source,
                "inventory",
                StringComparison.OrdinalIgnoreCase);
            IEnumerable<Item> source = inventory
                ? (Inventory.Items ?? Enumerable.Empty<Item>())
                : (Inventory.Bank.Items ?? Enumerable.Empty<Item>());
            IEnumerable<Item> candidates = source.Where(item =>
                item != null &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                (!inventory || item.Slot.Type == IdentityType.Inventory));

            if (!string.IsNullOrWhiteSpace(bag?.LastUniqueIdentity))
            {
                return candidates.Where(item => string.Equals(
                        item.UniqueIdentity.ToString(),
                        bag.LastUniqueIdentity,
                        StringComparison.Ordinal))
                    .ToList();
            }

            return candidates.Where(item =>
                    item.Slot.Instance == (bag?.OuterSlotInstance ?? -1))
                .ToList();
        }

        private static int FindMatchingLiveItemIndex(
            List<Item> live,
            StoredItemState persisted)
        {
            if (live == null || persisted == null)
                return -1;

            if (IsUsableIdentity(persisted.UniqueIdentity))
            {
                int identityIndex = live.FindIndex(item =>
                    item != null && string.Equals(
                        item.UniqueIdentity.ToString(),
                        persisted.UniqueIdentity,
                        StringComparison.Ordinal));
                if (identityIndex >= 0)
                    return identityIndex;
            }

            return live.FindIndex(item =>
                item != null &&
                item.Id == persisted.AoId &&
                item.HighId == persisted.HighId &&
                item.Ql == persisted.Ql);
        }

        private List<Item> FindDistinctInventoryItems(
            IEnumerable<TransferItemState> expectedItems)
        {
            List<Item> available = Inventory.Items == null
                ? new List<Item>()
                : Inventory.Items
                    .Where(item => item != null && item.Slot.Type == IdentityType.Inventory)
                    .OrderBy(item => item.Slot.Instance)
                    .ToList();
            var selected = new List<Item>();

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
                    return new List<Item>();

                selected.Add(available[index]);
                available.RemoveAt(index);
            }

            return selected;
        }

        private Item FindInventoryItem(TransferItemState expected)
        {
            return FindDistinctInventoryItems(
                expected == null
                    ? Enumerable.Empty<TransferItemState>()
                    : new[] { expected }).FirstOrDefault();
        }

        private static bool MatchesTransferItem(Item item, TransferItemState expected)
        {
            if (item == null || expected == null)
                return false;
            if (IsUsableIdentity(expected.UniqueIdentity) &&
                string.Equals(
                    item.UniqueIdentity.ToString(),
                    expected.UniqueIdentity,
                    StringComparison.Ordinal))
            {
                return true;
            }
            return item.Id == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql;
        }

        private static Item FindInventoryBagByIdentity(string identity)
        {
            return Inventory.Items?.FirstOrDefault(item =>
                item != null &&
                item.Slot.Type == IdentityType.Inventory &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                string.Equals(item.UniqueIdentity.ToString(), identity, StringComparison.Ordinal));
        }

        private static Item FindBankBagByIdentity(string identity)
        {
            return Inventory.Bank.Items?.FirstOrDefault(item =>
                item != null &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                string.Equals(item.UniqueIdentity.ToString(), identity, StringComparison.Ordinal));
        }

        private static Container FindContainerByIdentity(string identity)
        {
            return Inventory.Containers?.FirstOrDefault(container =>
                container != null &&
                string.Equals(container.Identity.ToString(), identity, StringComparison.Ordinal));
        }

        private static bool IsUsableIdentity(string identity)
        {
            return !string.IsNullOrWhiteSpace(identity) &&
                !string.Equals(identity, Identity.None.ToString(), StringComparison.Ordinal);
        }

        public static string GetReadyPath(string settingsDir, string character)
        {
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(settingsDir),
                ReadyFilePrefix + SafeFileToken(character) + ".json");
        }

        private void SetRecoveryDeadline(int milliseconds)
        {
            if (_recovery != null)
                _recovery.DeadlineUtc = DateTime.UtcNow.AddMilliseconds(milliseconds);
        }

        private void ReportProblem(string problem)
        {
            if (string.Equals(problem, _lastProblem, StringComparison.Ordinal))
                return;
            _lastProblem = problem;
            Logger.Warning("[CityBankers] " + problem);
            TellKavem(problem);
        }

        private WriteFrontConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<WriteFrontConfig>();
            }
            catch
            {
                return null;
            }
        }

        private string ResolveCurrentRole()
        {
            foreach (KeyValuePair<string, WriteFrontRole> pair in _config.Roles)
            {
                if (pair.Value != null && string.Equals(
                        pair.Value.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }
            return null;
        }

        private bool TryGetRole(string role, out WriteFrontRole value)
        {
            foreach (KeyValuePair<string, WriteFrontRole> pair in _config.Roles)
            {
                if (string.Equals(pair.Key, role, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = null;
            return false;
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
                _role,
                "TELL -> " + TrustedOperators.BootstrapAdmin + ": " + message);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";
            return value.Length <= 12 ? value : value.Substring(value.Length - 8);
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token;
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (File.Exists(path + ".tmp"))
                    File.Delete(path + ".tmp");
            }
            catch
            {
            }
        }

        private sealed class WriteFrontConfig
        {
            public Dictionary<string, WriteFrontRole> Roles;
        }

        private sealed class WriteFrontRole
        {
            public string Character;
        }

        private sealed class WriteFrontReadyMarker
        {
            public string Format;
            public string Role;
            public string Character;
            public string BaselineRunId;
            public DateTime ReadyUtc;
            public string BagSource;
            public string BagUniqueIdentity;
            public int BagOuterSlot;
            public int LiveItemCount;
            public int FreeSlots;
            public int ImportedExtraItems;
        }

        private sealed class RecoveryJob
        {
            public string BatchId;
            public string TransactionId;
            public List<TransferItemState> Items;
            public int ExpectedCount;
            public int Index;
            public int StoredCount;
            public string RecoveryKind;
            public RecoveryPhase Phase;
            public DateTime DeadlineUtc;
            public TransferItemState Expected;
            public StorageBagState Bag;
            public string BagIdentity;
            public int BagOuterSlot;
            public string ObservedItemIdentity;
            public string StoredItemIdentity;
            public int InnerSlot;
            public int BagHandle;
            public HashSet<int> PreInsertSlots;
        }
    }
}
