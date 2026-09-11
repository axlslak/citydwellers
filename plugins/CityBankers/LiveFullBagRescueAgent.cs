using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Repairs the exact runtime failure where BankingService stages a bank bag into normal
    /// inventory, opens it, discovers the live bag is full, and fails before returning the
    /// staged bag. The existing full-bag recovery then cannot find the persisted bank bag
    /// and reports matches=0.
    ///
    /// This agent acts only after all-bankers readiness, only for the exact zero-item failure
    /// sequence, and only when the complete expected batch is still loose on the destination
    /// worker. A stranded full bag is reconciled against physical contents transactionally,
    /// returned to bank, and only the zero-item recovery artifact is cleared. Queue ownership
    /// remains unchanged; the existing worker-local recovery must still perform and prove the
    /// actual item placement before Central may clear the batch.
    /// </summary>
    public class LiveFullBagRescueAgent : ClientlessPluginEntry
    {
        private const int PollMilliseconds = 100;
        private const string FullBagFailure =
            "Live bag is full even though persisted state expected free space. Reconcile before continuing.";
        private const string MissingTargetFailure =
            "Recovery target bag is not uniquely visible live; matches=0.";

        private string _settingsDir;
        private string _role;
        private bool _enabled;
        private DateTime _nextPollUtc;
        private RescueJob _job;
        private string _lastBlocked;

        private enum RescuePhase
        {
            None,
            OpeningStagedBag,
            ReturningStagedBag
        }

        public override void Init(string pluginDir)
        {
            // Full physical census and BankingService now own normal-mode recovery.
            if (StartupCensusGate.UsesPhysicalRecovery) return;

            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            if (IsBagAuditMode())
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role) ||
                string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _enabled = true;
            _nextPollUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;
            Logger.Information(
                "[CityBankers] LIVE FULL-BAG RESCUE initialized character=" +
                Client.CharacterName + " role=" + _role + ".");
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (!TrustedOperators.IsAllBankersReady() || Trade.IsTrading)
                    return;

                if (_job != null)
                {
                    TickJob();
                    return;
                }

                TryStart();
            }
            catch (Exception ex)
            {
                ReportBlocked("LIVE FULL-BAG RESCUE exception: " + ex.Message);
            }
        }

        private void TryStart()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            DispatchBatchState batch = (queue?.Batches ?? new List<DispatchBatchState>())
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.LastError, FullBagFailure, StringComparison.Ordinal) &&
                    candidate.Items != null && candidate.Items.Count > 0);
            if (batch == null)
                return;

            if (FindDistinctLooseItems(batch.Items).Count != batch.Items.Count)
                return;

            StorageBatchResult result = RuntimeStateStore.ReadStorageResult(
                _settingsDir,
                Client.CharacterName);
            if (result == null ||
                !string.Equals(result.BatchId, batch.BatchId, StringComparison.Ordinal) ||
                result.Success ||
                result.StoredCount != 0 ||
                !string.Equals(result.Error, MissingTargetFailure, StringComparison.Ordinal))
            {
                return;
            }

            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            StorageWorkerState worker = (state?.Workers ?? new List<StorageWorkerState>())
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (worker == null)
            {
                ReportBlocked("LIVE FULL-BAG RESCUE BLOCKED: persisted worker state is missing.");
                return;
            }

            var stranded = new List<StrandedBag>();
            foreach (StorageBagState persisted in worker.Bags ?? new List<StorageBagState>())
            {
                if (persisted == null ||
                    !string.Equals(persisted.Source, "bank", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(persisted.LastUniqueIdentity))
                {
                    continue;
                }

                Item live = Inventory.Items?.FirstOrDefault(item =>
                    item != null &&
                    item.Slot.Type == IdentityType.Inventory &&
                    item.UniqueIdentity.Type == IdentityType.Container &&
                    string.Equals(
                        item.UniqueIdentity.ToString(),
                        persisted.LastUniqueIdentity,
                        StringComparison.Ordinal));
                if (live != null)
                    stranded.Add(new StrandedBag { Persisted = persisted, Live = live });
            }

            if (stranded.Count == 0)
            {
                // On a clean restart StartupWriteFrontWatchdogAgent may already have returned
                // the stranded bag before readiness, and write-front reconciliation has then
                // proved a fresh writable target. The exact zero-item matches=0 result is now
                // only stale bookkeeping and can be cleared without moving physical items.
                RuntimeStateStore.DeleteIfExists(
                    RuntimeStateStore.GetStorageResultPath(_settingsDir, Client.CharacterName));
                string notice =
                    "LIVE FULL-BAG RESCUE " + _role + " batch " + ShortId(batch.BatchId) +
                    ": no persisted bank bag remains stranded after fresh startup write-front " +
                    "readiness; cleared the exact zero-item matches=0 recovery artifact so " +
                    "worker-local recovery can retry against the proven writable front.";
                Logger.Information("[CityBankers] " + notice);
                TellKavem(notice);
                _lastBlocked = null;
                return;
            }

            if (stranded.Count != 1)
            {
                ReportBlocked(
                    "LIVE FULL-BAG RESCUE BLOCKED " + _role + " batch " +
                    ShortId(batch.BatchId) + ": expected one stranded persisted bank bag, found " +
                    stranded.Count + ". No physical state was guessed.");
                return;
            }

            StrandedBag target = stranded[0];
            _job = new RescueJob
            {
                BatchId = batch.BatchId,
                TransactionId = batch.TransactionId,
                BagIdentity = target.Live.UniqueIdentity.ToString(),
                PersistedOuterSlot = target.Persisted.OuterSlotInstance,
                Phase = RescuePhase.OpeningStagedBag,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.BagOpenTimeoutMs)
            };

            target.Live.Use();
            Logger.Information(
                "[CityBankers] LIVE FULL-BAG RESCUE inspecting stranded bank bag " +
                _job.BagIdentity + " for " + _role + " batch " + ShortId(batch.BatchId) + ".");
        }

        private void TickJob()
        {
            if (_job == null)
                return;

            if (DateTime.UtcNow >= _job.DeadlineUtc)
            {
                FailJob("Timed out in phase " + _job.Phase + ".");
                return;
            }

            if (_job.Phase == RescuePhase.OpeningStagedBag)
            {
                Container container = Inventory.Containers?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Identity.ToString(), _job.BagIdentity, StringComparison.Ordinal));
                if (container == null || !container.IsOpen || container.Items == null)
                    return;

                if (!container.IsFull && container.NumFreeSlots > 0)
                {
                    FailJob(
                        "The stranded bag is not physically full anymore; refusing to rewrite occupancy from an unexpected state.");
                    return;
                }

                List<RuntimeStorageStateTransactions.LiveBagItemSnapshot> liveItems =
                    container.Items
                        .Where(item => item != null)
                        .Select(item => new RuntimeStorageStateTransactions.LiveBagItemSnapshot
                        {
                            UniqueIdentity = IsUsableIdentity(item.UniqueIdentity.ToString())
                                ? item.UniqueIdentity.ToString()
                                : null,
                            AoId = item.Id,
                            HighId = item.HighId,
                            Ql = item.Ql,
                            Name = item.Name ?? string.Empty,
                            InnerSlot = item.Slot.Instance & 0xFFFF
                        })
                        .ToList();

                int imported;
                List<LedgerItem> importedLedger;
                string error;
                if (!RuntimeStorageStateTransactions.TryReconcileBagContents(
                        _settingsDir,
                        _role,
                        Client.CharacterName,
                        "bank",
                        _job.BagIdentity,
                        _job.PersistedOuterSlot,
                        container.Handle,
                        liveItems,
                        "runtime-fullbag-rescue-" + _job.BatchId,
                        out imported,
                        out importedLedger,
                        out error))
                {
                    FailJob("Physical full-bag reconciliation refused: " + error);
                    return;
                }

                _job.ImportedExtras = imported;
                if (importedLedger.Count > 0)
                {
                    RuntimeStateStore.AppendLedger(
                        _settingsDir,
                        new LedgerRecord
                        {
                            Utc = DateTime.UtcNow,
                            Event = "runtime_full_bag_contents_reconciled",
                            TransactionId = _job.TransactionId,
                            BatchId = _job.BatchId,
                            Actor = Client.CharacterName,
                            Role = _role,
                            Character = Client.CharacterName,
                            Source = "live-staged-bank-bag",
                            Destination = "storage-state",
                            Message =
                                "Runtime full-bag rescue matched every persisted occurrence and " +
                                "imported " + importedLedger.Count +
                                " physically present same-role managed extra occurrence(s).",
                            Items = importedLedger
                        });
                }

                Item staged = Inventory.Items?.FirstOrDefault(item =>
                    item != null &&
                    item.Slot.Type == IdentityType.Inventory &&
                    item.UniqueIdentity.Type == IdentityType.Container &&
                    string.Equals(item.UniqueIdentity.ToString(), _job.BagIdentity, StringComparison.Ordinal));
                if (staged == null)
                {
                    FailJob("Reconciled bag disappeared before it could be returned to bank.");
                    return;
                }

                staged.MoveToBank();
                _job.Phase = RescuePhase.ReturningStagedBag;
                _job.DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.BagMoveTimeoutMs);
                return;
            }

            if (_job.Phase == RescuePhase.ReturningStagedBag)
            {
                Item returned = Inventory.Bank.Items?.FirstOrDefault(item =>
                    item != null &&
                    item.UniqueIdentity.Type == IdentityType.Container &&
                    string.Equals(item.UniqueIdentity.ToString(), _job.BagIdentity, StringComparison.Ordinal));
                if (returned == null)
                    return;

                string slotError;
                if (!RuntimeStorageStateTransactions.TryUpdateBankBagOuterSlot(
                        _settingsDir,
                        _role,
                        Client.CharacterName,
                        _job.BagIdentity,
                        returned.Slot.Instance,
                        out slotError))
                {
                    FailJob("Returned bag slot reconciliation failed: " + slotError);
                    return;
                }

                StorageBatchResult result = RuntimeStateStore.ReadStorageResult(
                    _settingsDir,
                    Client.CharacterName);
                if (result == null ||
                    !string.Equals(result.BatchId, _job.BatchId, StringComparison.Ordinal) ||
                    result.Success ||
                    result.StoredCount != 0 ||
                    !string.Equals(result.Error, MissingTargetFailure, StringComparison.Ordinal))
                {
                    FailJob(
                        "Zero-item recovery artifact changed while the bag was being rescued; leaving it untouched.");
                    return;
                }

                RuntimeStateStore.DeleteIfExists(
                    RuntimeStateStore.GetStorageResultPath(_settingsDir, Client.CharacterName));

                string notice =
                    "LIVE FULL-BAG RESCUE " + _role + " batch " + ShortId(_job.BatchId) +
                    ": reconciled the stranded physically-full bank bag, returned it to bank" +
                    (_job.ImportedExtras > 0
                        ? ", importedExtras=" + _job.ImportedExtras
                        : string.Empty) +
                    ", and cleared only the zero-item matches=0 recovery artifact. " +
                    "Worker-local recovery may now choose the next persisted free bag.";
                Logger.Information("[CityBankers] " + notice);
                TellKavem(notice);
                _job = null;
                _lastBlocked = null;
            }
        }

        private void FailJob(string error)
        {
            string batch = _job != null ? ShortId(_job.BatchId) : "?";
            _job = null;
            ReportBlocked(
                "LIVE FULL-BAG RESCUE BLOCKED " + _role + " batch " + batch +
                ": " + error + " No physical state was guessed.");
        }

        private void ReportBlocked(string message)
        {
            if (string.Equals(message, _lastBlocked, StringComparison.Ordinal))
                return;
            _lastBlocked = message;
            Logger.Warning("[CityBankers] " + message);
            TellKavem(message);
        }

        private List<Item> FindDistinctLooseItems(IEnumerable<TransferItemState> expectedItems)
        {
            List<Item> available = Inventory.Items == null
                ? new List<Item>()
                : Inventory.Items
                    .Where(item => item != null && item.Slot.Type == IdentityType.Inventory &&
                        item.UniqueIdentity.Type != IdentityType.Container)
                    .OrderBy(item => item.Slot.Instance)
                    .ToList();
            var selected = new List<Item>();

            foreach (TransferItemState expected in expectedItems ?? Enumerable.Empty<TransferItemState>())
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

        private string ResolveCurrentRole()
        {
            try
            {
                JObject root = SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = root.GetValue("Roles", StringComparison.OrdinalIgnoreCase) as JObject;
                if (roles == null)
                    return null;

                foreach (JProperty property in roles.Properties())
                {
                    JObject role = property.Value as JObject;
                    string character = role?.GetValue(
                        "Character",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (string.Equals(character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                        return property.Name;
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool IsBagAuditMode()
        {
            return ServicePolicy.IsBagAuditMode();
        }

        private static bool IsUsableIdentity(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, Identity.None.ToString(), StringComparison.Ordinal);
        }

        private void TellKavem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                TrustedOperators.BootstrapAdmin,
                CityBankersChatPalette.WhiteBaseMarkup(message));
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

        private sealed class StrandedBag
        {
            public StorageBagState Persisted;
            public Item Live;
        }

        private sealed class RescueJob
        {
            public string BatchId;
            public string TransactionId;
            public string BagIdentity;
            public int PersistedOuterSlot;
            public int ImportedExtras;
            public RescuePhase Phase;
            public DateTime DeadlineUtc;
        }
    }
}
