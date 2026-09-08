using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// One-shot physical reconciliation for managed items observed on the wrong banker.
    ///
    /// A clean bagaudit/physical census is the authority for creating a repair plan. During
    /// repair a sentinel makes BankingServiceAgent yield trade ownership. Wrong workers
    /// extract the exact AOID/HighId/QL item from the audited bag (or use an already-loose
    /// item), return it to Central, and Central verifies the physical return. Only after all
    /// misplaced items are physically on Central does Central replace superseded queue
    /// entries with fresh AOID-routed dispatch batches. Normal BankingServiceAgent dispatch
    /// then resumes under the hardened Identity.None-safe matcher.
    ///
    /// This intentionally does not rewrite the audited bag state while extracting. A final
    /// clean bagaudit after corrected redistribution is the authoritative state reset and
    /// post-redispatch completion proof.
    /// </summary>
    public class RouteRepairAgent : ClientlessPluginEntry
    {
        private const string RepairSentinelFile = "route-repair-active.json";
        private const string RepairPlanFile = "route-repair-plan.json";
        private const string RepairReceiptFile = "route-repair-last-completed.json";
        private const int PollMilliseconds = 250;
        private const int PhaseTimeoutSeconds = 15;
        private const int TradeTimeoutSeconds = 30;

        private string _settingsDir;
        private RepairConfig _config;
        private string _role;
        private string _centralCharacter;
        private bool _isCentral;
        private bool _enabled;
        private DateTime _nextPollUtc;

        // Central return-trade state.
        private string _centralTradeItemId;
        private bool _centralTradeOpened;
        private bool _centralAccepted;
        private DateTime _centralTradeStartedUtc;

        // Worker command state.
        private RepairCommand _workerCommand;
        private WorkerPhase _workerPhase;
        private DateTime _workerDeadlineUtc;
        private StorageBagState _workerBag;
        private string _workerBagLiveIdentity;
        private bool _workerStagedBankBag;
        private bool _workerTradeOpened;
        private bool _workerItemOffered;
        private bool _workerAccepted;

        private enum WorkerPhase
        {
            None,
            Locate,
            WaitBagToInventory,
            WaitBagOpen,
            WaitItemToInventory,
            WaitBagReturn,
            OpenTrade,
            WaitTrade
        }

        public override void Init(string pluginDir)
        {
            // Bagaudit owns physical observation and must never repair while observing.
            if (ServicePolicy.IsBagAuditMode())
            {
                return;
            }

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _config = LoadConfig();
            if (_config == null || _config.Roles == null)
            {
                Logger.Warning("ROUTE REPAIR disabled: the Bankers section could not be read.");
                return;
            }

            RepairRole central;
            if (!TryGetRole("central", out central) ||
                central == null ||
                string.IsNullOrWhiteSpace(central.Character))
            {
                Logger.Warning("ROUTE REPAIR disabled: Central mapping is unavailable.");
                return;
            }

            _centralCharacter = central.Character;
            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role))
                return;

            _isCentral = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);
            _enabled = true;
            _nextPollUtc = DateTime.MinValue;

            Trade.TradeOpened += OnTradeOpened;
            Trade.TradeStatusChanged += OnTradeStatusChanged;
            Client.OnUpdate += Tick;

            Logger.Information(
                $"[CityBankers] ROUTE REPAIR initialized character={Client.CharacterName} " +
                $"role={_role} central={_isCentral}.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Trade.TradeOpened -= OnTradeOpened;
            Trade.TradeStatusChanged -= OnTradeStatusChanged;
            Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (_isCentral)
                    TickCentral();
                else
                    TickWorker();
            }
            catch (Exception ex)
            {
                Logger.Error($"ROUTE REPAIR tick failed character={Client.CharacterName}: {ex}");
                FailPlanIfCentral("Route repair exception: " + ex.Message);
                FailWorkerIfActive("Route repair exception: " + ex.Message);
            }
        }

        // -----------------------------------------------------------------
        // Central coordinator
        // -----------------------------------------------------------------

        private void TickCentral()
        {
            RepairPlan plan = EnsureRepairPlan();
            if (plan == null ||
                string.Equals(plan.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plan.Status, "redispatch-queued", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EnsureSentinel(plan);

            // A command may have completed across a restart before Central observed the
            // TradeStatus.Finished callback. Physical Central inventory wins.
            foreach (RepairItem item in plan.Items.Where(i =>
                string.Equals(i.State, "commanded", StringComparison.OrdinalIgnoreCase)))
            {
                Item centralItem = FindExactNormalInventoryItem(item);
                if (centralItem != null)
                {
                    item.State = "returned-central";
                    item.UpdatedUtc = DateTime.UtcNow;
                    DeleteWorkerCommand(item.SourceCharacter);
                    ClearCentralTradeState();
                    SavePlan(plan);
                    TellKavem(
                        "Route repair verified return: " + item.Name + " from " +
                        item.SourceCharacter + " is physically on Central.");
                    return;
                }

                RepairResult result = ReadWorkerResult(item.SourceCharacter);
                if (result != null && string.Equals(result.ItemId, item.Id, StringComparison.Ordinal))
                {
                    if (!result.Success)
                    {
                        FailPlan(plan,
                            "Worker " + item.SourceCharacter + " repair failed: " + result.Error);
                        return;
                    }
                }
            }

            if (Trade.IsTrading)
            {
                TickCentralReturnTrade(plan);
                return;
            }

            RepairItem active = plan.Items.FirstOrDefault(i =>
                string.Equals(i.State, "commanded", StringComparison.OrdinalIgnoreCase));
            if (active != null)
            {
                // Wait for that worker to open its return trade.
                if (active.CommandedUtc != DateTime.MinValue &&
                    (DateTime.UtcNow - active.CommandedUtc).TotalSeconds > 90)
                {
                    FailPlan(plan,
                        "Timed out waiting for worker return from " + active.SourceCharacter +
                        " for " + active.Name + ".");
                }
                return;
            }

            RepairItem pending = plan.Items.FirstOrDefault(i =>
                string.Equals(i.State, "pending-return", StringComparison.OrdinalIgnoreCase));
            if (pending != null)
            {
                IssueWorkerCommand(plan, pending);
                return;
            }

            if (plan.Items.Any(i =>
                !string.Equals(i.State, "central-ready", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(i.State, "returned-central", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            QueueCorrectedDispatches(plan);
        }

        private RepairPlan EnsureRepairPlan()
        {
            RepairPlan existing = ReadPlan();
            if (existing != null &&
                !string.Equals(existing.Status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(
                        existing.Status,
                        "redispatch-queued",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }

                JObject verification = ReadPhysicalState();
                string verificationRunId = StringValue(verification, "RunId");
                if (verification == null || string.IsNullOrWhiteSpace(verificationRunId) ||
                    string.Equals(
                        verificationRunId,
                        existing.SourceRunId,
                        StringComparison.Ordinal))
                {
                    return existing;
                }

                string verificationError;
                if (!VerifyRedispatchedPlan(existing, verification, out verificationError))
                {
                    EnsureSentinel(existing);
                    FailPlan(
                        existing,
                        "Post-redispatch physical audit " + verificationRunId +
                        " did not verify the repaired placement: " + verificationError);
                    return existing;
                }

                existing.Status = "complete";
                existing.Error = null;
                existing.UpdatedUtc = DateTime.UtcNow;
                foreach (RepairItem item in existing.Items ?? new List<RepairItem>())
                {
                    item.State = "complete";
                    item.UpdatedUtc = DateTime.UtcNow;
                    DeleteWorkerCommand(item.SourceCharacter);
                    RuntimeStateStore.DeleteIfExists(GetWorkerResultPath(item.SourceCharacter));
                }
                SavePlan(existing);
                WriteJson(
                    GetDataPath(RepairReceiptFile),
                    new RepairReceipt
                    {
                        Format = "citybankers-route-repair-receipt-v1",
                        SourceRunId = existing.SourceRunId,
                        PlanId = existing.PlanId,
                        Utc = DateTime.UtcNow,
                        Status = "complete"
                    });
                RuntimeStateStore.DeleteIfExists(GetDataPath(RepairSentinelFile));
                Logger.Information(
                    $"[CityBankers] ROUTE REPAIR COMPLETE plan={existing.PlanId} " +
                    $"sourceRun={existing.SourceRunId} verifiedRun={verificationRunId}; " +
                    "all repaired items are physically on their routed workers.");
                TellKavem(
                    "Route repair complete: physical audit " + verificationRunId +
                    " verified all repaired items on their routed workers.");
            }

            JObject physical = ReadPhysicalState();
            if (physical == null)
                return null;

            string sourceRunId = StringValue(physical, "RunId");
            if (string.IsNullOrWhiteSpace(sourceRunId))
                return null;

            RepairReceipt receipt = ReadJson<RepairReceipt>(GetDataPath(RepairReceiptFile));
            if (receipt != null && string.Equals(
                    receipt.SourceRunId,
                    sourceRunId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            JArray locations = GetValue(physical, "Locations") as JArray;
            if (locations == null)
                return null;

            var items = new List<RepairItem>();
            foreach (JObject location in locations.OfType<JObject>())
            {
                if (!BoolValue(location, "Managed") ||
                    BoolValue(location, "RouteMatchesPhysicalRole"))
                {
                    continue;
                }

                string routedRole = StringValue(location, "RoutedRole");
                RepairRole destination;
                if (string.IsNullOrWhiteSpace(routedRole) ||
                    !TryGetRole(routedRole, out destination) ||
                    destination == null ||
                    string.IsNullOrWhiteSpace(destination.Character))
                {
                    Logger.Error(
                        "ROUTE REPAIR cannot resolve destination role for physical mismatch: " +
                        location.ToString(Formatting.None));
                    return null;
                }

                var item = new RepairItem
                {
                    Id = "repair-item-" + Guid.NewGuid().ToString("N"),
                    Name = StringValue(location, "Name") ?? string.Empty,
                    AoId = IntValue(location, "LowId"),
                    HighId = IntValue(location, "HighId"),
                    Ql = IntValue(location, "Ql"),
                    RoutedRole = routedRole,
                    DestinationCharacter = destination.Character,
                    SourceRole = StringValue(location, "Role"),
                    SourceCharacter = StringValue(location, "Character"),
                    SourceKind = StringValue(location, "LocationKind"),
                    SourceLocation = StringValue(location, "Location"),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };

                bool alreadyCentral = string.Equals(
                    item.SourceCharacter,
                    _centralCharacter,
                    StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        item.SourceKind,
                        "loose-inventory",
                        StringComparison.OrdinalIgnoreCase);
                item.State = alreadyCentral ? "central-ready" : "pending-return";
                items.Add(item);
            }

            if (items.Count == 0)
                return null;

            // Exact tuple must identify each repair item uniquely in the physical snapshot.
            foreach (RepairItem item in items)
            {
                int count = locations.OfType<JObject>().Count(location =>
                    BoolValue(location, "Managed") &&
                    IntValue(location, "LowId") == item.AoId &&
                    IntValue(location, "HighId") == item.HighId &&
                    IntValue(location, "Ql") == item.Ql);
                if (count != 1)
                {
                    Logger.Error(
                        $"ROUTE REPAIR refuses ambiguous physical item {item.Name}: " +
                        $"AOID={item.AoId} high={item.HighId} ql={item.Ql} occurrences={count}.");
                    return null;
                }
            }

            var plan = new RepairPlan
            {
                Format = "citybankers-route-repair-plan-v1",
                PlanId = "repair-" + Guid.NewGuid().ToString("N"),
                SourceRunId = sourceRunId,
                Status = "active",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Items = items
            };

            // Remove only fully-superseded queue batches before repair starts. A partial
            // overlap would be unsafe because we cannot infer the physical state of the
            // unrelated items in that batch.
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            foreach (DispatchBatchState batch in (queue.Batches ?? new List<DispatchBatchState>()).ToList())
            {
                List<TransferItemState> batchItems = batch.Items ?? new List<TransferItemState>();
                bool intersects = batchItems.Any(expected => items.Any(repair => SameTemplate(expected, repair)));
                if (!intersects)
                    continue;

                bool fullyCovered = batchItems.Count > 0 &&
                    batchItems.All(expected => items.Any(repair => SameTemplate(expected, repair)));
                if (!fullyCovered)
                {
                    Logger.Error(
                        "ROUTE REPAIR refuses to supersede a partially-overlapping dispatch batch " +
                        (batch.BatchId ?? "<unknown>") + ".");
                    return null;
                }
                queue.Batches.Remove(batch);
            }
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);

            SavePlan(plan);
            EnsureSentinel(plan);
            TellKavem(
                "Route repair armed from physical audit " + sourceRunId + ": mismatches=" +
                items.Count + ". Wrong workers will return exact items to Central before rerouting.");
            Logger.Information(
                $"[CityBankers] ROUTE REPAIR PLAN created plan={plan.PlanId} " +
                $"sourceRun={sourceRunId} mismatches={items.Count}.");
            return plan;
        }

        private bool VerifyRedispatchedPlan(RepairPlan plan, JObject physical, out string error)
        {
            error = null;
            JArray locations = GetValue(physical, "Locations") as JArray;
            if (locations == null)
            {
                error = "physical state has no Locations array";
                return false;
            }

            foreach (RepairItem item in plan?.Items ?? new List<RepairItem>())
            {
                List<JObject> matches = locations.OfType<JObject>()
                    .Where(location =>
                        BoolValue(location, "Managed") &&
                        IntValue(location, "LowId") == item.AoId &&
                        IntValue(location, "HighId") == item.HighId &&
                        IntValue(location, "Ql") == item.Ql)
                    .ToList();
                if (matches.Count != 1)
                {
                    error = item.Name + " occurrences=" + matches.Count;
                    return false;
                }

                JObject actual = matches[0];
                if (!BoolValue(actual, "RouteMatchesPhysicalRole"))
                {
                    error = item.Name + " still has routeMatch=False";
                    return false;
                }

                string character = StringValue(actual, "Character");
                if (!string.Equals(
                        character,
                        item.DestinationCharacter,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = item.Name + " is on " + (character ?? "<unknown>") +
                        " instead of " + item.DestinationCharacter;
                    return false;
                }
            }

            return true;
        }

        private void IssueWorkerCommand(RepairPlan plan, RepairItem item)
        {
            if (string.IsNullOrWhiteSpace(item.SourceCharacter) ||
                string.Equals(item.SourceCharacter, _centralCharacter, StringComparison.OrdinalIgnoreCase))
            {
                FailPlan(plan, "Repair item has invalid worker source: " + item.Name + ".");
                return;
            }

            var command = new RepairCommand
            {
                Format = "citybankers-route-repair-command-v1",
                PlanId = plan.PlanId,
                ItemId = item.Id,
                SourceRunId = plan.SourceRunId,
                SourceRole = item.SourceRole,
                SourceCharacter = item.SourceCharacter,
                CentralCharacter = _centralCharacter,
                Name = item.Name,
                AoId = item.AoId,
                HighId = item.HighId,
                Ql = item.Ql,
                RoutedRole = item.RoutedRole,
                SourceKind = item.SourceKind,
                SourceLocation = item.SourceLocation,
                CreatedUtc = DateTime.UtcNow
            };

            RuntimeStateStore.DeleteIfExists(GetWorkerResultPath(item.SourceCharacter));
            RuntimeStateStore.WriteJsonAtomic(GetWorkerCommandPath(item.SourceCharacter), command);
            item.State = "commanded";
            item.CommandedUtc = DateTime.UtcNow;
            item.UpdatedUtc = DateTime.UtcNow;
            SavePlan(plan);

            TellKavem(
                "Route repair return requested: " + item.SourceCharacter + " -> Central, " +
                item.Name + " AOID=" + item.AoId + ".");
        }

        private void TickCentralReturnTrade(RepairPlan plan)
        {
            RepairItem item = plan.Items.FirstOrDefault(i =>
                string.Equals(i.Id, _centralTradeItemId, StringComparison.Ordinal));
            if (item == null || !_centralTradeOpened)
                return;

            if ((DateTime.UtcNow - _centralTradeStartedUtc).TotalSeconds > TradeTimeoutSeconds)
            {
                TryDeclineTrade();
                FailPlan(plan, "Timed out receiving repair return from " + item.SourceCharacter + ".");
                return;
            }

            if (_centralAccepted)
                return;

            List<Item> remote = Trade.TargetWindowCache?.Items ?? new List<Item>();
            if (remote.Count > 0)
            {
                if (remote.Count != 1 || !SameTemplate(remote[0], item))
                {
                    TryDeclineTrade();
                    FailPlan(plan,
                        "Wrong item offered during repair return from " + item.SourceCharacter + ".");
                    return;
                }

                _centralAccepted = true;
                Trade.Accept();
                return;
            }

            // Same AOSharp empty-target-cache fallback proven by the normal handshake
            // bridge: the worker accepts only after its local offered window is exact.
            if (Trade.Status == TradeStatus.Accept)
            {
                _centralAccepted = true;
                Trade.Accept();
            }
        }

        private void QueueCorrectedDispatches(RepairPlan plan)
        {
            var liveItems = new List<KeyValuePair<RepairItem, Item>>();
            foreach (RepairItem repair in plan.Items)
            {
                Item live = FindExactNormalInventoryItem(repair);
                if (live == null)
                {
                    FailPlan(plan,
                        "Cannot queue corrected dispatch; item is not uniquely on Central: " +
                        repair.Name + " AOID=" + repair.AoId + ".");
                    return;
                }
                liveItems.Add(new KeyValuePair<RepairItem, Item>(repair, live));
            }

            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            // Repair-plan items were superseded on plan creation. Recheck before adding new
            // batches so an old/recovered copy cannot survive concurrently.
            foreach (DispatchBatchState batch in (queue.Batches ?? new List<DispatchBatchState>()).ToList())
            {
                List<TransferItemState> items = batch.Items ?? new List<TransferItemState>();
                if (items.Any(expected => plan.Items.Any(repair => SameTemplate(expected, repair))))
                    queue.Batches.Remove(batch);
            }

            string transactionId = "route-repair:" + plan.PlanId;
            foreach (IGrouping<string, KeyValuePair<RepairItem, Item>> group in liveItems.GroupBy(
                pair => pair.Key.RoutedRole,
                StringComparer.OrdinalIgnoreCase))
            {
                RepairRole destination;
                if (!TryGetRole(group.Key, out destination) ||
                    destination == null ||
                    string.IsNullOrWhiteSpace(destination.Character))
                {
                    FailPlan(plan, "No configured destination for repaired role " + group.Key + ".");
                    return;
                }

                queue.Batches.Add(new DispatchBatchState
                {
                    BatchId = "batch-" + Guid.NewGuid().ToString("N"),
                    TransactionId = transactionId,
                    Role = group.Key,
                    Character = destination.Character,
                    Status = "queued",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                    AttemptCount = 0,
                    Items = group.Select(pair => SnapshotLiveItem(pair.Value)).ToList()
                });
            }

            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);

            foreach (RepairItem item in plan.Items)
            {
                item.State = "redispatch-queued";
                item.UpdatedUtc = DateTime.UtcNow;
            }
            plan.Status = "redispatch-queued";
            plan.UpdatedUtc = DateTime.UtcNow;
            SavePlan(plan);

            WriteJson(
                GetDataPath(RepairReceiptFile),
                new RepairReceipt
                {
                    Format = "citybankers-route-repair-receipt-v1",
                    SourceRunId = plan.SourceRunId,
                    PlanId = plan.PlanId,
                    Utc = DateTime.UtcNow,
                    Status = "redispatch-queued"
                });

            RuntimeStateStore.DeleteIfExists(GetDataPath(RepairSentinelFile));
            ClearCentralTradeState();

            TellKavem(
                "Route repair returns verified. Queued " + plan.Items.Count +
                " corrected item(s) by exact AOID; normal dispatch is resuming. " +
                "Run a final bagaudit after queue becomes empty to reset canonical bag state.");
            Logger.Information(
                $"[CityBankers] ROUTE REPAIR REDISPATCH queued plan={plan.PlanId} " +
                $"items={plan.Items.Count}.");
        }

        private void FailPlanIfCentral(string error)
        {
            if (!_isCentral)
                return;
            RepairPlan plan = ReadPlan();
            if (plan != null && string.Equals(plan.Status, "active", StringComparison.OrdinalIgnoreCase))
                FailPlan(plan, error);
        }

        private void FailPlan(RepairPlan plan, string error)
        {
            if (plan == null)
                return;
            plan.Status = "failed";
            plan.Error = error;
            plan.UpdatedUtc = DateTime.UtcNow;
            SavePlan(plan);
            TryDeclineTrade();
            ClearCentralTradeState();
            TellKavem(
                "ROUTE REPAIR STOPPED: " + error +
                " No further repair item will be moved until the failure is reconciled.");
            Logger.Error("[CityBankers] ROUTE REPAIR STOPPED: " + error);
            // Leave the sentinel in place so normal banking stays frozen after a repair
            // failure. A clean bagaudit/new plan is the recovery boundary.
        }

        // -----------------------------------------------------------------
        // Worker extraction + return
        // -----------------------------------------------------------------

        private void TickWorker()
        {
            if (!File.Exists(GetDataPath(RepairSentinelFile)))
            {
                ResetWorkerState();
                return;
            }

            if (_workerCommand == null)
            {
                _workerCommand = ReadJson<RepairCommand>(GetWorkerCommandPath(Client.CharacterName));
                if (_workerCommand == null)
                    return;

                if (!string.Equals(
                        _workerCommand.SourceCharacter,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_workerCommand.SourceRole, _role, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        _workerCommand.CentralCharacter,
                        _centralCharacter,
                        StringComparison.OrdinalIgnoreCase))
                {
                    FailWorkerCommand("Repair command role/character mapping does not match this worker.");
                    return;
                }

                _workerPhase = WorkerPhase.Locate;
                SetWorkerDeadline(PhaseTimeoutSeconds);
                Logger.Information(
                    $"[CityBankers] ROUTE REPAIR worker={Client.CharacterName} item='{_workerCommand.Name}' " +
                    $"aoid={_workerCommand.AoId} starting physical return.");
            }

            if (_workerPhase != WorkerPhase.WaitTrade && DateTime.UtcNow >= _workerDeadlineUtc)
            {
                FailWorkerCommand("Timed out in repair phase " + _workerPhase + ".");
                return;
            }

            switch (_workerPhase)
            {
                case WorkerPhase.Locate:
                    WorkerLocateItem();
                    break;
                case WorkerPhase.WaitBagToInventory:
                    WorkerWaitBagToInventory();
                    break;
                case WorkerPhase.WaitBagOpen:
                    WorkerWaitBagOpen();
                    break;
                case WorkerPhase.WaitItemToInventory:
                    WorkerWaitItemToInventory();
                    break;
                case WorkerPhase.WaitBagReturn:
                    WorkerWaitBagReturn();
                    break;
                case WorkerPhase.OpenTrade:
                    WorkerOpenTrade();
                    break;
                case WorkerPhase.WaitTrade:
                    WorkerTickTrade();
                    break;
            }
        }

        private void WorkerLocateItem()
        {
            Item loose = FindExactNormalInventoryItem(_workerCommand);
            if (loose != null)
            {
                _workerPhase = WorkerPhase.OpenTrade;
                SetWorkerDeadline(PhaseTimeoutSeconds);
                return;
            }

            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            StorageWorkerState worker = (state?.Workers ?? new List<StorageWorkerState>())
                .FirstOrDefault(w =>
                    string.Equals(w.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (worker == null)
            {
                FailWorkerCommand("Current storage state has no matching worker entry.");
                return;
            }

            List<StorageBagState> candidates = (worker.Bags ?? new List<StorageBagState>())
                .Where(bag => bag != null &&
                    (bag.Items ?? new List<StoredItemState>()).Any(item =>
                        SameTemplate(item, _workerCommand)))
                .ToList();
            if (candidates.Count != 1)
            {
                FailWorkerCommand(
                    "Expected misplaced item is not in exactly one audited bag; candidates=" +
                    candidates.Count + ".");
                return;
            }

            _workerBag = candidates[0];
            _workerStagedBankBag = string.Equals(
                _workerBag.Source,
                "bank",
                StringComparison.OrdinalIgnoreCase);

            if (_workerStagedBankBag)
            {
                // Do not interpret a still-loading/closed bank view as a missing audited bag.
                if (Inventory.Bank == null || !Inventory.Bank.IsOpen)
                    return;

                List<Item> liveBags = FindLiveBagsForStoredBag(
                    Inventory.Bank.Items,
                    _workerBag,
                    false);
                if (liveBags.Count != 1)
                {
                    FailWorkerCommand(
                        "Audited bank bag is not uniquely visible live; matches=" +
                        liveBags.Count + ".");
                    return;
                }

                Item bag = liveBags[0];
                _workerBagLiveIdentity = bag.UniqueIdentity.ToString();
                // The live container identity is authoritative. If AO remapped the outer
                // slot since the audit, return verification should use this live slot.
                _workerBag.OuterSlotInstance = bag.Slot.Instance;
                bag.MoveToInventory();
                _workerPhase = WorkerPhase.WaitBagToInventory;
                SetWorkerDeadline(PhaseTimeoutSeconds);
                return;
            }

            List<Item> inventoryBags = FindLiveBagsForStoredBag(
                Inventory.Items,
                _workerBag,
                true);
            if (inventoryBags.Count != 1)
            {
                FailWorkerCommand(
                    "Audited inventory bag is not uniquely visible live; matches=" +
                    inventoryBags.Count + ".");
                return;
            }

            Item inventoryBag = inventoryBags[0];
            _workerBagLiveIdentity = inventoryBag.UniqueIdentity.ToString();
            _workerBag.OuterSlotInstance = inventoryBag.Slot.Instance;
            inventoryBag.Use();
            _workerPhase = WorkerPhase.WaitBagOpen;
            SetWorkerDeadline(PhaseTimeoutSeconds);
        }

        private static List<Item> FindLiveBagsForStoredBag(
            IEnumerable<Item> source,
            StorageBagState bag,
            bool requireInventorySlot)
        {
            IEnumerable<Item> candidates = (source ?? Enumerable.Empty<Item>())
                .Where(item => item != null &&
                    item.UniqueIdentity.Type == IdentityType.Container &&
                    (!requireInventorySlot || item.Slot.Type == IdentityType.Inventory));

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

        private void WorkerWaitBagToInventory()
        {
            Item bag = FindInventoryBagByIdentity(_workerBagLiveIdentity);
            if (bag == null)
                return;
            bag.Use();
            _workerPhase = WorkerPhase.WaitBagOpen;
            SetWorkerDeadline(PhaseTimeoutSeconds);
        }

        private void WorkerWaitBagOpen()
        {
            Container container = Inventory.Containers?.FirstOrDefault(c =>
                c != null &&
                string.Equals(c.Identity.ToString(), _workerBagLiveIdentity, StringComparison.Ordinal));
            if (container == null || !container.IsOpen || container.Items == null)
                return;

            List<Item> matches = container.Items.Where(item =>
                SameTemplate(item, _workerCommand)).ToList();
            if (matches.Count != 1)
            {
                FailWorkerCommand(
                    "Opened repair bag does not contain exactly one expected item; matches=" +
                    matches.Count + ".");
                return;
            }

            // AOSharp.Clientless bag retrieval uses ClientContainerAddItem from the
            // Backpack source identity to the local player. MoveToInventory() is the
            // AOSharp Core-style path and repeatedly failed on live Clientless AO.
            matches[0].MoveToContainer(DynelManager.LocalPlayer.Identity);
            _workerPhase = WorkerPhase.WaitItemToInventory;
            SetWorkerDeadline(PhaseTimeoutSeconds);
        }

        private void WorkerWaitItemToInventory()
        {
            Item item = FindExactNormalInventoryItem(_workerCommand);
            if (item == null)
                return;

            if (_workerStagedBankBag)
            {
                Item bag = FindInventoryBagByIdentity(_workerBagLiveIdentity);
                if (bag == null)
                {
                    FailWorkerCommand(
                        "Misplaced item was extracted, but staged bank bag is unavailable for return.");
                    return;
                }
                bag.MoveToBank();
                _workerPhase = WorkerPhase.WaitBagReturn;
                SetWorkerDeadline(PhaseTimeoutSeconds);
                return;
            }

            _workerPhase = WorkerPhase.OpenTrade;
            SetWorkerDeadline(PhaseTimeoutSeconds);
        }

        private void WorkerWaitBagReturn()
        {
            Item bag = Inventory.Bank.Items?.FirstOrDefault(item =>
                item != null &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                string.Equals(
                    item.UniqueIdentity.ToString(),
                    _workerBagLiveIdentity,
                    StringComparison.Ordinal));
            if (bag == null)
                return;
            if (bag.Slot.Instance != _workerBag.OuterSlotInstance)
            {
                FailWorkerCommand(
                    "Repair bank bag returned to unexpected outer slot " + bag.Slot.Instance +
                    " instead of " + _workerBag.OuterSlotInstance + ".");
                return;
            }

            _workerPhase = WorkerPhase.OpenTrade;
            SetWorkerDeadline(PhaseTimeoutSeconds);
        }

        private void WorkerOpenTrade()
        {
            if (Trade.IsTrading)
                return;

            Item item = FindExactNormalInventoryItem(_workerCommand);
            if (item == null)
            {
                FailWorkerCommand("Extracted repair item disappeared before return trade.");
                return;
            }

            PlayerChar central = DynelManager.Players.FirstOrDefault(player =>
                player != null && string.Equals(
                    player.Name,
                    _centralCharacter,
                    StringComparison.OrdinalIgnoreCase));
            if (central == null)
                return;

            _workerTradeOpened = false;
            _workerItemOffered = false;
            _workerAccepted = false;
            _workerPhase = WorkerPhase.WaitTrade;
            _workerDeadlineUtc = DateTime.UtcNow.AddSeconds(TradeTimeoutSeconds);
            Trade.Open(central.Identity);
        }

        private void WorkerTickTrade()
        {
            if (DateTime.UtcNow >= _workerDeadlineUtc)
            {
                TryDeclineTrade();
                FailWorkerCommand("Timed out returning repaired item to Central.");
                return;
            }

            if (!_workerTradeOpened || !Trade.IsTrading)
                return;

            if (!_workerItemOffered)
            {
                Item item = FindExactNormalInventoryItem(_workerCommand);
                if (item == null)
                {
                    FailWorkerCommand("Repair return item is not in worker normal inventory.");
                    return;
                }
                Trade.AddItem(item.Slot);
                _workerItemOffered = true;
                return;
            }

            if (_workerAccepted)
                return;

            List<Item> offered = Trade.PlayerWindowCache?.Items ?? new List<Item>();
            if (offered.Count != 1 || !SameTemplate(offered[0], _workerCommand))
                return;

            _workerAccepted = true;
            Trade.Accept();
        }

        private void FailWorkerIfActive(string error)
        {
            if (_workerCommand != null)
                FailWorkerCommand(error);
        }

        private void FailWorkerCommand(string error)
        {
            if (_workerCommand == null)
                return;

            WriteJson(
                GetWorkerResultPath(Client.CharacterName),
                new RepairResult
                {
                    Format = "citybankers-route-repair-result-v1",
                    PlanId = _workerCommand.PlanId,
                    ItemId = _workerCommand.ItemId,
                    Character = Client.CharacterName,
                    Utc = DateTime.UtcNow,
                    Success = false,
                    Error = error
                });
            Logger.Error(
                $"[CityBankers] ROUTE REPAIR worker={Client.CharacterName} FAILED: {error}");
            TryDeclineTrade();
            ResetWorkerState(false);
        }

        private void CompleteWorkerCommand()
        {
            if (_workerCommand == null)
                return;

            WriteJson(
                GetWorkerResultPath(Client.CharacterName),
                new RepairResult
                {
                    Format = "citybankers-route-repair-result-v1",
                    PlanId = _workerCommand.PlanId,
                    ItemId = _workerCommand.ItemId,
                    Character = Client.CharacterName,
                    Utc = DateTime.UtcNow,
                    Success = true
                });
            RuntimeStateStore.DeleteIfExists(GetWorkerCommandPath(Client.CharacterName));
            Logger.Information(
                $"[CityBankers] ROUTE REPAIR worker={Client.CharacterName} returned " +
                $"'{_workerCommand.Name}' AOID={_workerCommand.AoId} to Central.");
            ResetWorkerState(false);
        }

        private void ResetWorkerState(bool deleteCommand = false)
        {
            if (deleteCommand)
                RuntimeStateStore.DeleteIfExists(GetWorkerCommandPath(Client.CharacterName));
            _workerCommand = null;
            _workerPhase = WorkerPhase.None;
            _workerDeadlineUtc = DateTime.MinValue;
            _workerBag = null;
            _workerBagLiveIdentity = null;
            _workerStagedBankBag = false;
            _workerTradeOpened = false;
            _workerItemOffered = false;
            _workerAccepted = false;
        }

        private void SetWorkerDeadline(int seconds)
        {
            _workerDeadlineUtc = DateTime.UtcNow.AddSeconds(seconds);
        }

        // -----------------------------------------------------------------
        // Trade callbacks
        // -----------------------------------------------------------------

        private void OnTradeOpened(Identity target)
        {
            if (!_enabled || !Client.InPlay || !File.Exists(GetDataPath(RepairSentinelFile)))
                return;

            string targetName = FindPlayerName(target);
            if (_isCentral)
            {
                RepairPlan plan = ReadPlan();
                RepairItem commanded = plan?.Items?.FirstOrDefault(item =>
                    string.Equals(item.State, "commanded", StringComparison.OrdinalIgnoreCase));
                if (commanded == null || !string.Equals(
                        targetName,
                        commanded.SourceCharacter,
                        StringComparison.OrdinalIgnoreCase))
                {
                    TryDeclineTrade();
                    return;
                }

                _centralTradeItemId = commanded.Id;
                _centralTradeOpened = true;
                _centralAccepted = false;
                _centralTradeStartedUtc = DateTime.UtcNow;
                return;
            }

            if (_workerCommand != null && string.Equals(
                    targetName,
                    _centralCharacter,
                    StringComparison.OrdinalIgnoreCase))
            {
                _workerTradeOpened = true;
                return;
            }

            TryDeclineTrade();
        }

        private void OnTradeStatusChanged(Identity target, TradeStatus status)
        {
            if (!_enabled || !File.Exists(GetDataPath(RepairSentinelFile)))
                return;

            if (status == TradeStatus.Confirm)
            {
                if ((_isCentral && _centralTradeOpened) ||
                    (!_isCentral && _workerCommand != null && _workerTradeOpened))
                {
                    Trade.Confirm();
                }
                return;
            }

            if (status == TradeStatus.Finished)
            {
                if (_isCentral && _centralTradeOpened)
                {
                    RepairPlan plan = ReadPlan();
                    RepairItem item = plan?.Items?.FirstOrDefault(i =>
                        string.Equals(i.Id, _centralTradeItemId, StringComparison.Ordinal));
                    if (item == null)
                    {
                        FailPlanIfCentral("Repair return completed for unknown plan item.");
                        return;
                    }

                    Item received = FindExactNormalInventoryItem(item);
                    if (received == null)
                    {
                        FailPlan(plan,
                            "AO completed repair return but exact item is not uniquely visible on Central: " +
                            item.Name + ".");
                        return;
                    }

                    item.State = "returned-central";
                    item.UpdatedUtc = DateTime.UtcNow;
                    DeleteWorkerCommand(item.SourceCharacter);
                    SavePlan(plan);
                    TellKavem(
                        "Route repair received: " + item.Name + " from " +
                        item.SourceCharacter + " -> Central.");
                    ClearCentralTradeState();
                    return;
                }

                if (!_isCentral && _workerCommand != null && _workerTradeOpened)
                {
                    CompleteWorkerCommand();
                }
                return;
            }

            if (status == TradeStatus.Declined)
            {
                if (_isCentral && _centralTradeOpened)
                    FailPlanIfCentral("Repair return trade was declined.");
                else if (!_isCentral && _workerCommand != null && _workerTradeOpened)
                    FailWorkerCommand("Repair return trade was declined.");
            }
        }

        // -----------------------------------------------------------------
        // Exact matching + state helpers
        // -----------------------------------------------------------------

        private Item FindExactNormalInventoryItem(RepairItem expected)
        {
            if (expected == null)
                return null;
            List<Item> matches = (Inventory.Items ?? new List<Item>())
                .Where(item => item != null &&
                    item.Slot.Type == IdentityType.Inventory &&
                    SameTemplate(item, expected))
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private Item FindExactNormalInventoryItem(RepairCommand expected)
        {
            if (expected == null)
                return null;
            List<Item> matches = (Inventory.Items ?? new List<Item>())
                .Where(item => item != null &&
                    item.Slot.Type == IdentityType.Inventory &&
                    SameTemplate(item, expected))
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool SameTemplate(Item item, RepairItem expected)
        {
            return item != null && expected != null &&
                item.Id == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql;
        }

        private static bool SameTemplate(Item item, RepairCommand expected)
        {
            return item != null && expected != null &&
                item.Id == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql;
        }

        private static bool SameTemplate(StoredItemState item, RepairCommand expected)
        {
            return item != null && expected != null &&
                item.AoId == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql;
        }

        private static bool SameTemplate(TransferItemState item, RepairItem expected)
        {
            return item != null && expected != null &&
                item.AoId == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql;
        }

        private static TransferItemState SnapshotLiveItem(Item item)
        {
            string identity = item?.UniqueIdentity.ToString();
            if (string.Equals(identity, Identity.None.ToString(), StringComparison.Ordinal))
                identity = null;
            return new TransferItemState
            {
                UniqueIdentity = identity,
                AoId = item?.Id ?? 0,
                HighId = item?.HighId ?? 0,
                Ql = item?.Ql ?? 0,
                Name = item?.Name ?? string.Empty
            };
        }

        private static Item FindInventoryBagByIdentity(string identity)
        {
            return Inventory.Items?.FirstOrDefault(item =>
                item != null &&
                item.Slot.Type == IdentityType.Inventory &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                string.Equals(item.UniqueIdentity.ToString(), identity, StringComparison.Ordinal));
        }

        private string FindPlayerName(Identity identity)
        {
            PlayerChar player = DynelManager.Players.FirstOrDefault(p =>
                p != null && p.Identity == identity);
            return player?.Name;
        }

        private void ClearCentralTradeState()
        {
            _centralTradeItemId = null;
            _centralTradeOpened = false;
            _centralAccepted = false;
            _centralTradeStartedUtc = DateTime.MinValue;
        }

        private RepairConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<RepairConfig>();
            }
            catch
            {
                return null;
            }
        }

        private string ResolveCurrentRole()
        {
            foreach (KeyValuePair<string, RepairRole> pair in _config.Roles)
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

        private bool TryGetRole(string role, out RepairRole value)
        {
            foreach (KeyValuePair<string, RepairRole> pair in _config.Roles)
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

        private JObject ReadPhysicalState()
        {
            string path = GetDataPath("physical-state.json");
            if (!File.Exists(path))
                return null;
            try
            {
                return JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private RepairPlan ReadPlan()
        {
            return ReadJson<RepairPlan>(GetDataPath(RepairPlanFile));
        }

        private void SavePlan(RepairPlan plan)
        {
            if (plan == null)
                return;
            plan.UpdatedUtc = DateTime.UtcNow;
            WriteJson(GetDataPath(RepairPlanFile), plan);
        }

        private void EnsureSentinel(RepairPlan plan)
        {
            string path = GetDataPath(RepairSentinelFile);
            if (File.Exists(path))
                return;
            WriteJson(
                path,
                new RepairSentinel
                {
                    Format = "citybankers-route-repair-active-v1",
                    PlanId = plan?.PlanId,
                    SourceRunId = plan?.SourceRunId,
                    CreatedUtc = DateTime.UtcNow
                });
        }

        private string GetDataPath(string file)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir), file);
        }

        private string GetWorkerCommandPath(string character)
        {
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "citybankers-route-repair-command-" + SafeFileToken(character) + ".json");
        }

        private string GetWorkerResultPath(string character)
        {
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "citybankers-route-repair-result-" + SafeFileToken(character) + ".json");
        }

        private void DeleteWorkerCommand(string character)
        {
            RuntimeStateStore.DeleteIfExists(GetWorkerCommandPath(character));
        }

        private RepairResult ReadWorkerResult(string character)
        {
            return ReadJson<RepairResult>(GetWorkerResultPath(character));
        }

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path)
                    ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteJson(string path, object value)
        {
            RuntimeStateStore.WriteJsonAtomic(path, value);
        }

        private static JToken GetValue(JObject value, string property)
        {
            return value?.GetValue(property, StringComparison.OrdinalIgnoreCase);
        }

        private static string StringValue(JObject value, string property)
        {
            JToken token = GetValue(value, property);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static int IntValue(JObject value, string property)
        {
            JToken token = GetValue(value, property);
            int result;
            return token != null && int.TryParse(token.ToString(), out result) ? result : 0;
        }

        private static bool BoolValue(JObject value, string property)
        {
            JToken token = GetValue(value, property);
            bool result;
            return token != null && bool.TryParse(token.ToString(), out result) && result;
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        }

        private void TellKavem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            if (Client.Chat != null)
                Client.Chat.SendPrivateMessage(TrustedOperators.BootstrapAdmin, message, true);
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "ROUTE REPAIR: " + message);
        }

        private static void TryDeclineTrade()
        {
            try
            {
                if (Trade.IsTrading)
                    Trade.Decline();
            }
            catch
            {
            }
        }

        private sealed class RepairConfig
        {
            public Dictionary<string, RepairRole> Roles;
        }

        private sealed class RepairRole
        {
            public string Username;
            public string Character;
        }

        private sealed class RepairPlan
        {
            public string Format;
            public string PlanId;
            public string SourceRunId;
            public string Status;
            public DateTime CreatedUtc;
            public DateTime UpdatedUtc;
            public string Error;
            public List<RepairItem> Items = new List<RepairItem>();
        }

        private sealed class RepairItem
        {
            public string Id;
            public string Name;
            public int AoId;
            public int HighId;
            public int Ql;
            public string RoutedRole;
            public string DestinationCharacter;
            public string SourceRole;
            public string SourceCharacter;
            public string SourceKind;
            public string SourceLocation;
            public string State;
            public DateTime CreatedUtc;
            public DateTime UpdatedUtc;
            public DateTime CommandedUtc;
        }

        private sealed class RepairCommand
        {
            public string Format;
            public string PlanId;
            public string ItemId;
            public string SourceRunId;
            public string SourceRole;
            public string SourceCharacter;
            public string CentralCharacter;
            public string Name;
            public int AoId;
            public int HighId;
            public int Ql;
            public string RoutedRole;
            public string SourceKind;
            public string SourceLocation;
            public DateTime CreatedUtc;
        }

        private sealed class RepairResult
        {
            public string Format;
            public string PlanId;
            public string ItemId;
            public string Character;
            public DateTime Utc;
            public bool Success;
            public string Error;
        }

        private sealed class RepairSentinel
        {
            public string Format;
            public string PlanId;
            public string SourceRunId;
            public DateTime CreatedUtc;
        }

        private sealed class RepairReceipt
        {
            public string Format;
            public string SourceRunId;
            public string PlanId;
            public DateTime Utc;
            public string Status;
        }
    }
}
