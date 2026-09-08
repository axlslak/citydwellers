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

namespace CityBankers
{
    /// <summary>
    /// Reconciles persisted storage-bag locators with the live AO container slots on each
    /// storage worker before the all-bankers readiness barrier may release dispatch.
    ///
    /// AO may remap a bank/inventory bag's outer slot across relog. The audited container
    /// identity remains the physical bag authority when present, matching the already-proven
    /// RouteRepairAgent behavior. The worker updates only its own persisted bag slot map.
    ///
    /// After readiness, this agent also recovers a narrow post-transfer failure class:
    /// a failed batch whose complete expected multiset is still loose in the destination
    /// worker's normal inventory and whose failure occurred before any bag insertion because
    /// the persisted outer slot was stale. Recovery stores locally; it never trades the item
    /// back through Central. Central remains the authority that removes the queue batch after
    /// a successful worker result is physically recorded.
    /// </summary>
    public class WorkerStorageLayoutRecoveryAgent : ClientlessPluginEntry
    {
        public const string LayoutReadyFilePrefix = "citybankers-storage-layout-ready-";

        private const string LayoutMutexName = "CityBankers.StorageLayoutRemap.v1";
        private const int PollMilliseconds = 100;

        private string _settingsDir;
        private RecoveryConfig _config;
        private string _role;
        private string _centralCharacter;
        private bool _isCentral;
        private bool _enabled;
        private bool _layoutReady;
        private DateTime _nextPollUtc;
        private string _lastLayoutProblem;
        private RecoveryStorageJob _job;
        private readonly Dictionary<string, string> _centralDecisionByBatch =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private enum RecoveryStoragePhase
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
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _config = LoadConfig();
            if (_config == null || _config.Roles == null)
                return;

            RecoveryRole central;
            if (!TryGetRole("central", out central) ||
                central == null ||
                string.IsNullOrWhiteSpace(central.Character))
            {
                return;
            }

            _centralCharacter = central.Character;
            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role))
                return;

            _isCentral = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);
            _enabled = true;
            _nextPollUtc = DateTime.UtcNow;

            if (!_isCentral)
                DeleteIfExists(GetLayoutReadyPath(Client.CharacterName));

            Client.OnUpdate += Tick;

            Logger.Information(
                "[CityBankers] STORAGE LAYOUT/RECOVERY initialized character=" +
                Client.CharacterName + " role=" + _role + " central=" + _isCentral + ".");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Client.OnUpdate -= Tick;
            if (!_isCentral)
                DeleteIfExists(GetLayoutReadyPath(Client.CharacterName));
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (_isCentral)
                    TickCentralCoordinator();
                else
                    TickWorker();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "[CityBankers] STORAGE LAYOUT/RECOVERY tick failed character=" +
                    Client.CharacterName + ": " + ex);
            }
        }

        // -----------------------------------------------------------------
        // Worker startup live-layout reconciliation
        // -----------------------------------------------------------------

        private void TickWorker()
        {
            if (!_layoutReady)
            {
                if (!TryReconcileLiveBagSlots())
                    return;
            }

            if (!TrustedOperators.IsAllBankersReady())
                return;

            if (_job != null)
            {
                TickRecoveryStorageJob();
                return;
            }

            if (Trade.IsTrading)
                return;

            TryStartFailedStorageRecovery();
        }

        private bool TryReconcileLiveBagSlots()
        {
            if (Inventory.Bank == null || !Inventory.Bank.IsOpen ||
                Inventory.Bank.Items == null || Inventory.Items == null)
            {
                return false;
            }

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
                        ReportLayoutProblem("Timed out waiting for storage-layout state lock.");
                        return false;
                    }

                    StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
                    StorageWorkerState worker = (state?.Workers ?? new List<StorageWorkerState>())
                        .FirstOrDefault(candidate =>
                            candidate != null &&
                            string.Equals(candidate.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(
                                candidate.Character,
                                Client.CharacterName,
                                StringComparison.OrdinalIgnoreCase));
                    if (worker == null)
                    {
                        ReportLayoutProblem(
                            "Persistent storage state has no worker entry for " +
                            Client.CharacterName + "/" + _role + ".");
                        return false;
                    }

                    int remapped = 0;
                    foreach (StorageBagState bag in worker.Bags ?? new List<StorageBagState>())
                    {
                        if (bag == null)
                            continue;

                        List<Item> live = FindLiveBagsForStoredBag(bag);
                        if (live.Count != 1)
                        {
                            ReportLayoutProblem(
                                "Cannot uniquely reconcile persisted " + (bag.Source ?? "?") +
                                " bag identity=" + (bag.LastUniqueIdentity ?? "<none>") +
                                " oldSlot=" + bag.OuterSlotInstance +
                                "; liveMatches=" + live.Count + ".");
                            return false;
                        }

                        Item actual = live[0];
                        if (actual.Slot.Instance != bag.OuterSlotInstance)
                        {
                            int oldSlot = bag.OuterSlotInstance;
                            bag.OuterSlotInstance = actual.Slot.Instance;
                            remapped++;
                            Logger.Information(
                                "[CityBankers] LIVE BAG SLOT remap worker=" + Client.CharacterName +
                                " source=" + bag.Source + " identity=" +
                                actual.UniqueIdentity + " " + oldSlot + " -> " +
                                bag.OuterSlotInstance + ".");
                        }

                        bag.LastUniqueIdentity = actual.UniqueIdentity.ToString();
                    }

                    worker.ObservedUtc = DateTime.UtcNow;
                    state.UpdatedUtc = DateTime.UtcNow;
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetStorageStatePath(_settingsDir),
                        state);
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetCurrentStockPath(_settingsDir),
                        RuntimeStateStore.BuildCurrentStock(state));

                    WriteLayoutReadyMarker(state.BaselineRunId, remapped);
                    _layoutReady = true;
                    _lastLayoutProblem = null;

                    string notice =
                        "Storage layout ready on " + Client.CharacterName +
                        ": reconciled " + (worker.Bags?.Count ?? 0) +
                        " persisted bag(s) to live AO slots; remapped=" + remapped + ".";
                    Logger.Information("[CityBankers] " + notice);
                    if (remapped > 0)
                        TellKavem(notice);
                    return true;
                }
                finally
                {
                    if (entered)
                        mutex.ReleaseMutex();
                }
            }
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

        private void ReportLayoutProblem(string problem)
        {
            if (string.Equals(problem, _lastLayoutProblem, StringComparison.Ordinal))
                return;

            _lastLayoutProblem = problem;
            Logger.Warning(
                "[CityBankers] STORAGE LAYOUT NOT READY " + Client.CharacterName +
                ": " + problem);
        }

        private void WriteLayoutReadyMarker(string baselineRunId, int remapped)
        {
            string path = GetLayoutReadyPath(Client.CharacterName);
            string temp = path + ".tmp";
            var marker = new LayoutReadyMarker
            {
                Format = "citybankers-storage-layout-ready-v1",
                Role = _role,
                Character = Client.CharacterName,
                BaselineRunId = baselineRunId,
                ReadyUtc = DateTime.UtcNow,
                RemappedBagCount = remapped
            };

            File.WriteAllText(temp, JsonConvert.SerializeObject(marker, Formatting.Indented));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        public static string GetLayoutReadyPath(string settingsDir, string character)
        {
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(settingsDir),
                LayoutReadyFilePrefix + SafeFileToken(character) + ".json");
        }

        private string GetLayoutReadyPath(string character)
        {
            return GetLayoutReadyPath(_settingsDir, character);
        }

        // -----------------------------------------------------------------
        // Worker-local recovery after completed Central -> worker transfer
        // -----------------------------------------------------------------

        private void TryStartFailedStorageRecovery()
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
                    IsRecoverableWorkerStorageFailure(candidate.LastError));
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
                return;
            }

            List<Item> physical = FindDistinctInventoryItems(batch.Items);
            if (physical.Count != batch.Items.Count)
                return;

            _job = new RecoveryStorageJob
            {
                Command = new DispatchCommand
                {
                    BatchId = batch.BatchId,
                    TransactionId = batch.TransactionId,
                    Role = batch.Role,
                    SourceCharacter = _centralCharacter,
                    DestinationCharacter = Client.CharacterName,
                    CreatedUtc = DateTime.UtcNow,
                    Items = new List<TransferItemState>(batch.Items)
                },
                Index = 0,
                StoredCount = 0,
                Phase = RecoveryStoragePhase.FindBag,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.ItemMoveTimeoutMs)
            };

            string notice =
                "WORKER RECOVERY " + _role + " batch " + ShortId(batch.BatchId) +
                ": verified " + batch.Items.Count + "/" + batch.Items.Count +
                " expected item(s) loose on " + Client.CharacterName +
                "; resuming bag placement locally without another Central trade.";
            Logger.Information("[CityBankers] " + notice);
            TellKavem(notice);
            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "worker_storage_recovery_started",
                    TransactionId = batch.TransactionId,
                    BatchId = batch.BatchId,
                    Actor = Client.CharacterName,
                    Role = _role,
                    Character = Client.CharacterName,
                    Source = "normal-inventory",
                    Destination = "storage-bags",
                    Message =
                        "Post-transfer recovery started after full expected multiset was " +
                        "verified loose on the destination worker. No new trade is used.",
                    Items = batch.Items.Select(ToLedgerItem).ToList()
                });
        }

        private void TickRecoveryStorageJob()
        {
            if (_job == null)
                return;

            if (_job.Command == null || _job.Command.Items == null)
            {
                FailRecoveryStorageJob("Recovery storage command is empty.");
                return;
            }

            if (_job.Index >= _job.Command.Items.Count)
            {
                CompleteRecoveryStorageJob();
                return;
            }

            switch (_job.Phase)
            {
                case RecoveryStoragePhase.FindBag:
                    StartRecoveryStorageItem();
                    break;
                case RecoveryStoragePhase.MovingBagToInventory:
                    ProcessRecoveryBagMoveToInventory();
                    break;
                case RecoveryStoragePhase.OpeningBag:
                    ProcessRecoveryBagOpen();
                    break;
                case RecoveryStoragePhase.MovingItemIntoBag:
                    ProcessRecoveryItemMoveIntoBag();
                    break;
                case RecoveryStoragePhase.ReturningBag:
                    ProcessRecoveryBagReturn();
                    break;
                default:
                    FailRecoveryStorageJob("Invalid recovery storage phase " + _job.Phase + ".");
                    break;
            }
        }

        private void StartRecoveryStorageItem()
        {
            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (state == null)
            {
                FailRecoveryStorageJob("Persistent storage state is missing.");
                return;
            }

            TransferItemState expected = _job.Command.Items[_job.Index];
            Item inventoryItem = FindInventoryItem(expected);
            if (inventoryItem == null)
            {
                FailRecoveryStorageJob(
                    "Expected recovery item is not visible loose in worker inventory: " +
                    expected.Name + " AOID=" + expected.AoId + ".");
                return;
            }

            StorageBagState bag = RuntimeStateStore.FindNextFreeBag(
                state,
                _role,
                Client.CharacterName);
            if (bag == null)
            {
                FailRecoveryStorageJob("No persisted storage bag with a free inner slot remains.");
                return;
            }

            List<Item> live = FindLiveBagsForStoredBag(bag);
            if (live.Count != 1)
            {
                FailRecoveryStorageJob(
                    "Persisted target bag is not uniquely visible live; identity=" +
                    (bag.LastUniqueIdentity ?? "<none>") + ", matches=" + live.Count + ".");
                return;
            }

            Item liveBag = live[0];
            _job.Expected = expected;
            _job.ActualItemIdentity = IsUsableIdentity(inventoryItem.UniqueIdentity.ToString())
                ? inventoryItem.UniqueIdentity.ToString()
                : null;
            _job.Bag = bag;
            _job.BagLiveIdentity = liveBag.UniqueIdentity.ToString();
            _job.LiveOuterSlotInstance = liveBag.Slot.Instance;
            _job.InnerSlot = -1;

            if (string.Equals(bag.Source, "bank", StringComparison.OrdinalIgnoreCase))
            {
                _job.Phase = RecoveryStoragePhase.MovingBagToInventory;
                SetRecoveryDeadline(ServicePolicy.BagMoveTimeoutMs);
                liveBag.MoveToInventory();
                return;
            }

            _job.Phase = RecoveryStoragePhase.OpeningBag;
            SetRecoveryDeadline(ServicePolicy.BagOpenTimeoutMs);
            liveBag.Use();
        }

        private void ProcessRecoveryBagMoveToInventory()
        {
            Item bag = FindInventoryBagByIdentity(_job.BagLiveIdentity);
            if (bag != null)
            {
                _job.Phase = RecoveryStoragePhase.OpeningBag;
                SetRecoveryDeadline(ServicePolicy.BagOpenTimeoutMs);
                bag.Use();
                return;
            }

            if (DateTime.UtcNow >= _job.DeadlineUtc)
            {
                FailRecoveryStorageJob(
                    "Recovery bank bag did not arrive in normal inventory before staging timeout.");
            }
        }

        private void ProcessRecoveryBagOpen()
        {
            Container container = FindContainerByIdentity(_job.BagLiveIdentity);
            if (container != null && container.IsOpen)
            {
                Item item = FindInventoryItem(_job.Expected);
                if (item == null)
                {
                    FailRecoveryStorageJob(
                        "Recovery item disappeared from normal inventory before bag insertion.");
                    return;
                }

                if (container.IsFull)
                {
                    FailRecoveryStorageJob(
                        "Live recovery bag is full even though persisted state expected free space.");
                    return;
                }

                _job.Phase = RecoveryStoragePhase.MovingItemIntoBag;
                SetRecoveryDeadline(ServicePolicy.ItemMoveTimeoutMs);
                item.MoveToContainer(container);
                return;
            }

            if (DateTime.UtcNow >= _job.DeadlineUtc)
                FailRecoveryStorageJob("Recovery bag did not open/materialize before timeout.");
        }

        private void ProcessRecoveryItemMoveIntoBag()
        {
            Container container = FindContainerByIdentity(_job.BagLiveIdentity);
            if (container != null && container.Items != null)
            {
                var occupiedInnerSlots = new HashSet<int>(
                    (_job.Bag?.Items ?? new List<StoredItemState>())
                        .Where(item => item != null)
                        .Select(item => item.InnerSlot));
                Item observed = container.Items.FirstOrDefault(item =>
                    MatchesItem(item, _job.Expected, _job.ActualItemIdentity) &&
                    !occupiedInnerSlots.Contains(item.Slot.Instance & 0xFFFF));
                if (observed != null)
                {
                    _job.InnerSlot = observed.Slot.Instance & 0xFFFF;
                    _job.ObservedStoredItemIdentity = IsUsableIdentity(
                        observed.UniqueIdentity.ToString())
                            ? observed.UniqueIdentity.ToString()
                            : null;
                    _job.BagHandle = container.Handle;

                    if (string.Equals(
                            _job.Bag.Source,
                            "bank",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Item liveBag = FindInventoryBagByIdentity(_job.BagLiveIdentity);
                        if (liveBag == null)
                        {
                            FailRecoveryStorageJob(
                                "Recovery item is in the staged bank bag, but the bag is " +
                                "not visible for return.");
                            return;
                        }

                        _job.Phase = RecoveryStoragePhase.ReturningBag;
                        SetRecoveryDeadline(ServicePolicy.BagMoveTimeoutMs);
                        liveBag.MoveToBank();
                        return;
                    }

                    CommitRecoveryStoredItem();
                    return;
                }
            }

            if (DateTime.UtcNow >= _job.DeadlineUtc)
            {
                FailRecoveryStorageJob(
                    "AO did not confirm the recovery item in a newly occupied bag slot before timeout.");
            }
        }

        private void ProcessRecoveryBagReturn()
        {
            Item bankBag = FindBankBagByIdentity(_job.BagLiveIdentity);
            if (bankBag != null)
            {
                if (bankBag.Slot.Instance != _job.LiveOuterSlotInstance)
                {
                    FailRecoveryStorageJob(
                        "Recovery bank bag returned to unexpected live outer slot " +
                        bankBag.Slot.Instance + " instead of " +
                        _job.LiveOuterSlotInstance + ".");
                    return;
                }

                CommitRecoveryStoredItem();
                return;
            }

            if (DateTime.UtcNow >= _job.DeadlineUtc)
                FailRecoveryStorageJob("Recovery staged bank bag did not return before timeout.");
        }

        private void CommitRecoveryStoredItem()
        {
            string error;
            if (!RuntimeStateStore.RecordPlacement(
                    _settingsDir,
                    _job.Command.TransactionId,
                    _role,
                    Client.CharacterName,
                    _job.Bag.Source,
                    _job.LiveOuterSlotInstance,
                    _job.BagLiveIdentity,
                    _job.BagHandle,
                    _job.Expected,
                    _job.ObservedStoredItemIdentity,
                    _job.InnerSlot,
                    out error))
            {
                FailRecoveryStorageJob(
                    "AO recovery placement succeeded but persistent state update failed: " +
                    error);
                return;
            }

            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "item_stored_recovery",
                    TransactionId = _job.Command.TransactionId,
                    BatchId = _job.Command.BatchId,
                    Actor = Client.CharacterName,
                    Role = _role,
                    Character = Client.CharacterName,
                    Source = "normal-inventory",
                    Destination =
                        _job.Bag.Source + ":" + _job.LiveOuterSlotInstance +
                        "/inner:" + _job.InnerSlot,
                    Message =
                        "AO confirmed worker-local failed-batch recovery placement and any " +
                        "required bank-bag return.",
                    Items = new List<LedgerItem> { ToLedgerItem(_job.Expected) }
                });

            TellKavem(
                "Recovered storage: " + _job.Expected.Name + " QL" + _job.Expected.Ql +
                " on " + Client.CharacterName + " -> " + _job.Bag.Source +
                " bag " + _job.LiveOuterSlotInstance + ", slot " + _job.InnerSlot + ".");

            _job.StoredCount++;
            _job.Index++;
            _job.Expected = null;
            _job.Bag = null;
            _job.BagLiveIdentity = null;
            _job.ActualItemIdentity = null;
            _job.ObservedStoredItemIdentity = null;
            _job.InnerSlot = -1;
            _job.BagHandle = 0;
            _job.LiveOuterSlotInstance = -1;
            _job.Phase = RecoveryStoragePhase.FindBag;
            SetRecoveryDeadline(ServicePolicy.ItemMoveTimeoutMs);
        }

        private void CompleteRecoveryStorageJob()
        {
            RecoveryStorageJob job = _job;
            RuntimeStateStore.WriteStorageResult(
                _settingsDir,
                new StorageBatchResult
                {
                    BatchId = job.Command.BatchId,
                    TransactionId = job.Command.TransactionId,
                    Role = _role,
                    Character = Client.CharacterName,
                    CompletedUtc = DateTime.UtcNow,
                    Success = true,
                    ExpectedCount = job.Command.Items?.Count ?? 0,
                    StoredCount = job.StoredCount
                });

            string notice =
                "WORKER RECOVERY COMPLETE " + _role + " batch " +
                ShortId(job.Command.BatchId) + ": stored=" + job.StoredCount +
                "/" + (job.Command.Items?.Count ?? 0) +
                "; Central may now clear the failed queue batch.";
            Logger.Information("[CityBankers] " + notice);
            TellKavem(notice);
            _job = null;
        }

        private void FailRecoveryStorageJob(string error)
        {
            if (_job == null)
                return;

            RecoveryStorageJob job = _job;
            RuntimeStateStore.WriteStorageResult(
                _settingsDir,
                new StorageBatchResult
                {
                    BatchId = job.Command?.BatchId,
                    TransactionId = job.Command?.TransactionId,
                    Role = _role,
                    Character = Client.CharacterName,
                    CompletedUtc = DateTime.UtcNow,
                    Success = false,
                    ExpectedCount = job.Command?.Items?.Count ?? 0,
                    StoredCount = job.StoredCount,
                    Error = error
                });

            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "worker_storage_recovery_failed",
                    TransactionId = job.Command?.TransactionId,
                    BatchId = job.Command?.BatchId,
                    Actor = Client.CharacterName,
                    Role = _role,
                    Character = Client.CharacterName,
                    Message = error
                });

            Logger.Error(
                "[CityBankers] WORKER RECOVERY FAILED " + _role + " batch " +
                ShortId(job.Command?.BatchId) + ": " + error);
            TellKavem(
                "WORKER RECOVERY FAILED " + _role + ": " + error +
                " Queue remains failed; physical state was not guessed.");
            _job = null;
        }

        // -----------------------------------------------------------------
        // Central completion authority for worker-local recovery
        // -----------------------------------------------------------------

        private void TickCentralCoordinator()
        {
            if (!TrustedOperators.IsAllBankersReady())
                return;

            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            List<DispatchBatchState> failed = (queue?.Batches ?? new List<DispatchBatchState>())
                .Where(batch =>
                    batch != null &&
                    string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                    IsRecoverableWorkerStorageFailure(batch.LastError))
                .ToList();

            foreach (DispatchBatchState batch in failed)
            {
                StorageBatchResult result = RuntimeStateStore.ReadStorageResult(
                    _settingsDir,
                    batch.Character);
                if (result == null || !string.Equals(
                        result.BatchId,
                        batch.BatchId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                int expected = batch.Items?.Count ?? 0;
                if (result.Success && expected > 0 &&
                    result.ExpectedCount == expected &&
                    result.StoredCount == expected)
                {
                    queue.Batches.Remove(batch);
                    RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                    RuntimeStateStore.DeleteIfExists(
                        RuntimeStateStore.GetStorageResultPath(_settingsDir, batch.Character));

                    RuntimeStateStore.AppendLedger(
                        _settingsDir,
                        new LedgerRecord
                        {
                            Utc = DateTime.UtcNow,
                            Event = "dispatch_storage_recovered",
                            TransactionId = batch.TransactionId,
                            BatchId = batch.BatchId,
                            Actor = Client.CharacterName,
                            Role = batch.Role,
                            Character = batch.Character,
                            Source = "worker-normal-inventory",
                            Destination = "storage-bags",
                            Message =
                                "Central accepted worker-local recovery result only after the " +
                                "worker reported full expected-count physical placement.",
                            Items = (batch.Items ?? new List<TransferItemState>())
                                .Select(ToLedgerItem).ToList()
                        });

                    string notice =
                        "RECOVERED QUEUE " + batch.Role + " batch " +
                        ShortId(batch.BatchId) + ": worker stored " + expected +
                        "/" + expected + " item(s); failed batch cleared.";
                    Logger.Information("[CityBankers] " + notice);
                    TellKavem(notice);
                    _centralDecisionByBatch.Remove(batch.BatchId ?? string.Empty);
                    return;
                }

                if (!result.Success)
                {
                    batch.LastError =
                        "Worker-local storage recovery failed after readiness: " +
                        (result.Error ?? "<no error>");
                    batch.UpdatedUtc = DateTime.UtcNow;
                    RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                    RuntimeStateStore.DeleteIfExists(
                        RuntimeStateStore.GetStorageResultPath(_settingsDir, batch.Character));
                    ReportCentralDecisionOnce(
                        batch,
                        "failed",
                        "RECOVERY HOLD " + batch.Role + " batch " +
                        ShortId(batch.BatchId) + ": worker-local storage recovery failed: " +
                        (result.Error ?? "<no error>") + ".");
                    return;
                }

                ReportCentralDecisionOnce(
                    batch,
                    "inconsistent-result",
                    "RECOVERY HOLD " + batch.Role + " batch " +
                    ShortId(batch.BatchId) +
                    ": worker recovery result count is inconsistent; expected=" + expected +
                    ", resultExpected=" + result.ExpectedCount +
                    ", stored=" + result.StoredCount + ".");
            }
        }

        private void ReportCentralDecisionOnce(
            DispatchBatchState batch,
            string key,
            string message)
        {
            string batchId = batch?.BatchId ?? "<unknown>";
            string prior;
            if (_centralDecisionByBatch.TryGetValue(batchId, out prior) &&
                string.Equals(prior, key, StringComparison.Ordinal))
            {
                return;
            }

            _centralDecisionByBatch[batchId] = key;
            Logger.Warning("[CityBankers] " + message);
            TellKavem(message);
        }

        // -----------------------------------------------------------------
        // Common physical helpers
        // -----------------------------------------------------------------

        private static bool IsRecoverableWorkerStorageFailure(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return error.StartsWith(
                       "Persisted bank bag is not present at expected outer slot ",
                       StringComparison.Ordinal) ||
                   error.StartsWith(
                       "Persisted inventory bag is not present at expected outer slot ",
                       StringComparison.Ordinal);
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

        private static bool MatchesItem(
            Item item,
            TransferItemState expected,
            string observedIdentity)
        {
            if (item == null || expected == null)
                return false;
            if (IsUsableIdentity(observedIdentity) && string.Equals(
                    item.UniqueIdentity.ToString(),
                    observedIdentity,
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (IsUsableIdentity(expected.UniqueIdentity) && string.Equals(
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

        private static LedgerItem ToLedgerItem(TransferItemState item)
        {
            return new LedgerItem
            {
                UniqueIdentity = item?.UniqueIdentity,
                AoId = item?.AoId ?? 0,
                HighId = item?.HighId ?? 0,
                Ql = item?.Ql ?? 0,
                Name = item?.Name
            };
        }

        private void SetRecoveryDeadline(int milliseconds)
        {
            if (_job == null)
                return;
            _job.DeadlineUtc = DateTime.UtcNow.AddMilliseconds(milliseconds);
        }

        private RecoveryConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<RecoveryConfig>();
            }
            catch
            {
                return null;
            }
        }

        private string ResolveCurrentRole()
        {
            foreach (KeyValuePair<string, RecoveryRole> pair in _config.Roles)
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

        private bool TryGetRole(string role, out RecoveryRole value)
        {
            foreach (KeyValuePair<string, RecoveryRole> pair in _config.Roles)
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

        private sealed class RecoveryConfig
        {
            public Dictionary<string, RecoveryRole> Roles;
        }

        private sealed class RecoveryRole
        {
            public string Character;
        }

        private sealed class LayoutReadyMarker
        {
            public string Format;
            public string Role;
            public string Character;
            public string BaselineRunId;
            public DateTime ReadyUtc;
            public int RemappedBagCount;
        }

        private sealed class RecoveryStorageJob
        {
            public DispatchCommand Command;
            public int Index;
            public int StoredCount;
            public RecoveryStoragePhase Phase;
            public DateTime DeadlineUtc;
            public TransferItemState Expected;
            public StorageBagState Bag;
            public string BagLiveIdentity;
            public int LiveOuterSlotInstance;
            public string ActualItemIdentity;
            public string ObservedStoredItemIdentity;
            public int InnerSlot;
            public int BagHandle;
        }
    }
}
