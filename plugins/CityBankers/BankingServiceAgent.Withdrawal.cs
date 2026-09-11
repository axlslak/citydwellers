using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private const int WithdrawalPhaseTimeoutSeconds = 30;
        private WithdrawalState _withdrawal;
        private WithdrawalWorkerPhase _withdrawalWorkerPhase;
        private DateTime _withdrawalDeadlineUtc;
        private StorageBagState _withdrawalBag;
        private string _withdrawalBagIdentity;
        private bool _withdrawalBankBag;
        private bool _withdrawalTradeOpened;
        private bool _withdrawalItemOffered;
        private bool _withdrawalAccepted;
        private Identity _withdrawalTradePartner = Identity.None;
        private bool _withdrawalPickupTrade;
        private int _withdrawalItemMoveAttempts;
        private DateTime _withdrawalNextItemMoveRetryUtc;
        private string _receiptWaitId;
        private readonly Stopwatch _receiptWait = new Stopwatch();
        private string _extractionWaitId;
        private readonly Stopwatch _extractionWait = new Stopwatch();
        private readonly Dictionary<string, Stopwatch> _withdrawalAccountingRetry = new Dictionary<string, Stopwatch>();
        private readonly Dictionary<string, string> _withdrawalAccountingError = new Dictionary<string, string>();

        private enum WithdrawalWorkerPhase
        {
            None,
            Locate,
            WaitBagInventory,
            WaitBagOpen,
            WaitItemInventory,
            WaitBagReturn,
            OpenTrade,
            WaitTrade
        }

        private List<WithdrawalState> _pickupItems = new List<WithdrawalState>();
        private readonly HashSet<string> _pickupOfferedIds = new HashSet<string>(StringComparer.Ordinal);

        private bool TickWithdrawalCentral()
        {
            List<WithdrawalState> rows = WithdrawalStore.LoadAll(_settingsDir);
            if (_withdrawal != null && !_withdrawalTradeOpened && _receipt == null &&
                !rows.Any(row => row.Id == _withdrawal.Id && WithdrawalStore.IsActive(row)))
                ResetWithdrawalLocal();
            if (_withdrawalTradeOpened && !Trade.IsTrading)
                ResetWithdrawalTrade(); // recover even if AO omitted a Declined callback
            if (_withdrawalTradeOpened)
            {
                if (_withdrawalPickupTrade && _pickupItems.Count > 0)
                    TickWithdrawalPickupTrade(_pickupItems[0]);
                else if (_extractionWait.IsRunning && _extractionWait.Elapsed.TotalSeconds >= 240)
                    FailWithdrawal(rows.First(row => row.Id == _withdrawal.Id),
                        "Worker return trade did not finish within four minutes; custody needs reconciliation.");
                return true;
            }

            foreach (WithdrawalState row in rows.Where(WithdrawalStore.IsActive))
            {
                if (WithdrawalStore.HasConfirmedDelivery(row))
                    RetryConfirmedWithdrawalAccounting(row);
                else if (WithdrawalStore.HasStatus(row, "returning"))
                    QueueWithdrawalReturn(row);
                else if (WithdrawalStore.HasStatus(row, "return-queued") && WithdrawalReturnIsStored(row))
                {
                    row.Status = "expired";
                    WithdrawalStore.Save(_settingsDir, row);
                }
                else if (WithdrawalStore.HasStatus(row, "pickup-trading") && !Trade.IsTrading)
                {
                    if (FindWithdrawalInventoryItem(row) == null)
                        FailWithdrawal(row, "Interrupted pickup requires custody reconciliation.");
                    else
                    {
                        row.Status = "central-ready";
                        WithdrawalStore.Save(_settingsDir, row);
                    }
                }
                else if (WithdrawalStore.HasStatus(row, "central-ready") && !Trade.IsTrading &&
                    row.PickupExpiresUtc.HasValue && DateTime.UtcNow >= row.PickupExpiresUtc.Value)
                {
                    // Claim expiry against the current revision: a concurrent #get may have extended it.
                    bool expired = WithdrawalStore.Update(_settingsDir, current =>
                    {
                        WithdrawalState fresh = current.First(value => value.Id == row.Id);
                        if (!WithdrawalStore.HasStatus(fresh, "central-ready") ||
                            !fresh.PickupExpiresUtc.HasValue || fresh.PickupExpiresUtc.Value > DateTime.UtcNow)
                            return false;
                        fresh.Status = "returning";
                        fresh.ReturnBatchId = "withdraw-return-" + fresh.Id;
                        WithdrawalStore.Touch(fresh);
                        return true;
                    });
                    if (expired) return false; // enqueue idempotently on next tick
                }
            }

            rows = WithdrawalStore.LoadAll(_settingsDir);
            WithdrawalState active = rows.FirstOrDefault(row =>
                WithdrawalStore.HasStatus(row, "extracting") || WithdrawalStore.HasStatus(row, "central-received"));
            if (active != null)
            {
                _withdrawal = active;
                if (_extractionWaitId != active.Id)
                {
                    _extractionWaitId = active.Id;
                    _extractionWait.Restart();
                }
                if (WithdrawalStore.HasStatus(active, "extracting") &&
                    _extractionWait.Elapsed.TotalSeconds >= 240)
                {
                    FailWithdrawal(active, "Worker extraction did not finish within four minutes; custody needs reconciliation.");
                    return true;
                }
                if (WithdrawalStore.HasStatus(active, "central-received") && !Trade.IsTrading)
                {
                    if (_receiptWaitId != active.Id)
                    {
                        _receiptWaitId = active.Id;
                        _receiptWait.Restart();
                    }
                    if (FindWithdrawalInventoryItem(active) != null)
                        OpenPickupWindow(active);
                    else if (_receiptWait.Elapsed.TotalSeconds >= 30)
                        FailWithdrawal(active, "AO finished the worker trade but Central cannot identify the received copy.");
                }
                return true; // only physical extraction owns Central, never a waiting reservation
            }

            if (_donationActive || _donationCleanup != null || _activeBatch != null || Trade.IsTrading)
                return false;
            // Existing dispatch drains before another extraction; waiting pickups do not gate it.
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            if ((queue.Batches ?? new List<DispatchBatchState>()).Any(batch =>
                batch != null &&
                !string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(batch.Status, CustodyHoldStatus, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (!TrustedOperators.IsAllBankersReady() || Inventory.NumFreeSlots < 12)
                return false; // retain room for a full donation even with waiting orders
            WithdrawalState next = rows.FirstOrDefault(row =>
                (WithdrawalStore.HasStatus(row, "requested") || CanRetryWithdrawalExtraction(row)) &&
                !rows.Any(other => other.Id != row.Id && WithdrawalStore.HasStatus(other, "failed") &&
                    string.Equals(other.SourceCharacter, row.SourceCharacter, StringComparison.OrdinalIgnoreCase)) &&
                !(queue.Batches ?? new List<DispatchBatchState>()).Any(batch =>
                    batch != null && !batch.TransferNeverStarted && string.Equals(batch.Character, row.SourceCharacter, StringComparison.OrdinalIgnoreCase)));
            if (next == null) return false;
            bool retry = WithdrawalStore.HasStatus(next, "failed");
            JToken liveInventoryMatch = retry
                ? FindUniqueLooseWorkerHeartbeatMatch(next)
                : null;
            bool liveInventoryRetry = liveInventoryMatch != null &&
                next.LiveInventoryAnchorAttempts == 0;
            if (liveInventoryRetry)
            {
                next.LiveInventoryAnchorAttempts++;
                next.LiveInventoryAnchorSlot =
                    (int?)(liveInventoryMatch["Slot"] ?? liveInventoryMatch["slot"]);
                next.LiveInventoryAnchorIdentity =
                    (string)(liveInventoryMatch["UniqueIdentity"] ??
                             liveInventoryMatch["uniqueIdentity"]);
            }
            else if (retry)
                next.RecoveryAttempts++;
            next.Status = "extracting";
            WithdrawalStore.Save(_settingsDir, next);
            _extractionWaitId = next.Id;
            _extractionWait.Restart();
            if (retry)
                Logger.Warning("[CityBankers] WITHDRAWAL RECOVERY RETRY " + next.Id +
                    (liveInventoryRetry
                        ? ": Central proved one exact-name loose item in the source worker's fresh inventory; resuming from live custody without replaying the old bag slot."
                        : ": Central scheduled one inventory-extraction retry; no delivery or pickup state is replayed."));
            _withdrawal = next;
            return true;
        }

        private bool CanRetryWithdrawalExtraction(WithdrawalState row)
        {
            if (!WithdrawalStore.HasStatus(row, "failed") || row.DeliveredUtc.HasValue ||
                !string.IsNullOrWhiteSpace(row.CentralItemIdentity))
                return false;

            if (row.LiveInventoryAnchorAttempts == 0 &&
                FindUniqueLooseWorkerHeartbeatMatch(row) != null)
                return true;

            return row.RecoveryAttempts == 0 &&
                !row.DeliveredUtc.HasValue && string.IsNullOrWhiteSpace(row.CentralItemIdentity) &&
                (row.Error ?? string.Empty).StartsWith(
                    "Timed out during worker extraction phase WaitItemInventory.", StringComparison.Ordinal);
        }

        private JToken FindUniqueLooseWorkerHeartbeatMatch(WithdrawalState row)
        {
            if (row?.Item == null || string.IsNullOrWhiteSpace(row.SourceCharacter))
                return null;
            string token = string.Concat(row.SourceCharacter.Where(char.IsLetterOrDigit));
            JObject heartbeat = RuntimeStateStore.ReadJson<JObject>(System.IO.Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "citybankers-health-" + token + ".json"));
            JArray items = heartbeat?["InventoryItems"] as JArray;
            if (items == null)
                return null;
            List<JToken> matches = items.Where(item =>
                item != null && !((bool?)(item["IsContainer"] ?? item["isContainer"]) ?? false) &&
                LiveInventorySnapshotMatches(item, row)).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool LiveInventorySnapshotMatches(JToken item, WithdrawalState row)
        {
            string identity = (string)(item["UniqueIdentity"] ?? item["uniqueIdentity"]);
            if (!string.IsNullOrWhiteSpace(row.SourceItemIdentity) &&
                !string.Equals(row.SourceItemIdentity, "(None:0000)", StringComparison.Ordinal) &&
                string.Equals(identity, row.SourceItemIdentity, StringComparison.Ordinal))
                return true;
            string name = (string)(item["Name"] ?? item["name"]);
            return string.Equals(
                name ?? string.Empty, row.Item.Name ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        private bool TickWithdrawalWorker()
        {
            WithdrawalState state = WithdrawalStore.LoadAll(_settingsDir).FirstOrDefault(row =>
                WithdrawalStore.HasStatus(row, "extracting") &&
                string.Equals(row.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (state == null)
            {
                ResetWithdrawalLocal();
                return false;
            }
            if (_withdrawal != null && _withdrawal.Id != state.Id)
                ResetWithdrawalLocal();
            _withdrawal = state;
            if (_withdrawalWorkerPhase == WithdrawalWorkerPhase.None)
            {
                _withdrawalWorkerPhase = WithdrawalWorkerPhase.Locate;
                SetWithdrawalDeadline();
            }
            WithdrawalWorkerPhase phaseAtStart = _withdrawalWorkerPhase;
            switch (_withdrawalWorkerPhase)
            {
                case WithdrawalWorkerPhase.Locate: WithdrawalLocate(); break;
                case WithdrawalWorkerPhase.WaitBagInventory: WithdrawalWaitBagInventory(); break;
                case WithdrawalWorkerPhase.WaitBagOpen: WithdrawalWaitBagOpen(); break;
                case WithdrawalWorkerPhase.WaitItemInventory: WithdrawalWaitItemInventory(); break;
                case WithdrawalWorkerPhase.WaitBagReturn: WithdrawalWaitBagReturn(); break;
                case WithdrawalWorkerPhase.OpenTrade: WithdrawalOpenCentralTrade(); break;
                case WithdrawalWorkerPhase.WaitTrade: WithdrawalTickWorkerTrade(); break;
            }

            // Consume the newest AO inventory/container update before declaring the phase
            // timed out. On Clientless, the item-arrival update can be delivered on the
            // same tick that reaches the deadline; checking first caused a false failure
            // followed immediately by a successful recovery retry.
            if (phaseAtStart != WithdrawalWorkerPhase.WaitTrade &&
                _withdrawalWorkerPhase == phaseAtStart &&
                DateTime.UtcNow >= _withdrawalDeadlineUtc)
            {
                FailWithdrawal(state, "Timed out during worker extraction phase " +
                    _withdrawalWorkerPhase + ". Physical state requires reconciliation.");
            }
            return true;
        }

        private bool TryHandleWithdrawalTradeOpened(Identity target, string targetName)
        {
            List<WithdrawalState> rows = WithdrawalStore.LoadAll(_settingsDir);
            WithdrawalState transfer = rows.FirstOrDefault(row => WithdrawalStore.HasStatus(row, "extracting") &&
                (_isCentral
                    ? string.Equals(row.SourceCharacter, targetName, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(row.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(targetName, _centralCharacter, StringComparison.OrdinalIgnoreCase)));
            if (transfer != null)
            {
                if (_receipt == null)
                    PrepareReceipt("withdrawal-transfer", transfer.DonationTransactionId,
                        transfer.Id, new List<TransferItemState> { transfer.Item }, _isCentral ? 1 : -1);
                _withdrawal = transfer;
                _withdrawalTradeOpened = true;
                _withdrawalTradePartner = target;
                return true;
            }
            if (!_isCentral || rows.Any(WithdrawalStore.OwnsCentralTrade) ||
                _activeBatch != null || _donationCleanup != null) return false;

            // A ready order belongs to this collector; everybody else can donate normally.
            List<WithdrawalState> ready = WithdrawalStore.Update(_settingsDir, current =>
            {
                WithdrawalState first = current.FirstOrDefault(row =>
                    WithdrawalStore.HasStatus(row, "central-ready") &&
                    WithdrawalStore.IsAllowedCollector(row, targetName) &&
                    row.PickupExpiresUtc.HasValue && row.PickupExpiresUtc.Value > DateTime.UtcNow);
                if (first == null) return new List<WithdrawalState>();
                List<WithdrawalState> claim = current.Where(row => row.OrderId == first.OrderId &&
                    WithdrawalStore.HasStatus(row, "central-ready") &&
                    row.PickupExpiresUtc.HasValue && row.PickupExpiresUtc.Value > DateTime.UtcNow).ToList();
                foreach (WithdrawalState row in claim)
                {
                    row.Status = "pickup-trading";
                    WithdrawalStore.Touch(row);
                }
                return claim;
            });
            if (ready.Count == 0) return false;
            PrepareReceipt("withdrawal-pickup", ready[0].DonationTransactionId,
                ready[0].OrderId, ready.Select(row => row.Item).ToList(), -1);
            _pickupItems = ready;
            _withdrawal = ready[0];
            _withdrawalPickupTrade = true;
            _withdrawalTradeOpened = true;
            _withdrawalTradePartner = target;
            _pickupOfferedIds.Clear();
            _withdrawalAccepted = false;
            return true;
        }

        private bool TryHandleWithdrawalTradeStatus(Identity target, TradeStatus status)
        {
            if (!_withdrawalTradeOpened || target != _withdrawalTradePartner || _withdrawal == null)
                return false;
            WithdrawalState state = WithdrawalStore.LoadAll(_settingsDir).First(row => row.Id == _withdrawal.Id);
            if (status == TradeStatus.Accept)
            {
                if (_isCentral && !_withdrawalPickupTrade)
                {
                    List<Item> offered = Trade.TargetWindowCache?.Items ?? new List<Item>();
                    if (offered.Count == 1 && MatchesWithdrawalItem(offered[0], state))
                    {
                        _withdrawalAccepted = true;
                        Trade.Accept();
                    }
                }
                return true;
            }
            if (status == TradeStatus.Confirm)
            {
                if (_isCentral && _withdrawalPickupTrade &&
                    (Trade.TargetWindowCache?.Items?.Count ?? 0) > 0)
                {
                    TryDeclineTrade();
                    return true;
                }
                if (_withdrawalAccepted) Trade.Confirm();
                return true;
            }
            if (status == TradeStatus.Finished)
            {
                var expected = _withdrawalPickupTrade
                    ? _pickupItems.Select(row => row.Item).ToList()
                    : new List<TransferItemState> { state.Item };
                AwaitPhysicalReceipt(expected, () =>
                {
                if (_isCentral && _withdrawalPickupTrade)
                {
                    if (!_withdrawalAccepted || _pickupOfferedIds.Count != _pickupItems.Count)
                    {
                        ResetWithdrawalTrade(); // persisted pickup-trading requires physical reconciliation
                        throw new InvalidOperationException("Pickup completion lacks accepted offer evidence.");
                    }
                    // Persist the entire confirmed delivery before per-item archival.
                    List<string> ids = _pickupItems.Select(row => row.Id).ToList();
                    WithdrawalStore.Update(_settingsDir, current =>
                    {
                        foreach (WithdrawalState row in current.Where(row => ids.Contains(row.Id) &&
                            WithdrawalStore.HasStatus(row, "pickup-trading")))
                        {
                            row.DeliveredUtc = DateTime.UtcNow;
                            row.Status = "delivery-confirmed";
                            WithdrawalStore.Touch(row);
                        }
                        return true;
                    });
                }
                else if (_isCentral)
                {
                    state.Status = "central-received";
                    WithdrawalStore.Save(_settingsDir, state);
                }
                ResetWithdrawalTrade();

                });
                return true;
            }
            if (status == TradeStatus.Declined)
            {
                if (_afterReceipt != null && _receipt != null && _receipt.Direction != 0)
                {
                    StartupCensusGate.Block("Conflicting withdrawal Declined after Finished; custody evidence retained.");
                    return true;
                }
                VerifyCancelledReceipt();
                if (_isCentral && _withdrawalPickupTrade)
                {
                    List<string> ids = _pickupItems.Select(row => row.Id).ToList();
                    WithdrawalStore.Update(_settingsDir, current =>
                    {
                        foreach (WithdrawalState row in current.Where(row => ids.Contains(row.Id) &&
                            WithdrawalStore.HasStatus(row, "pickup-trading")))
                        {
                            row.Status = "central-ready";
                            WithdrawalStore.Touch(row);
                        }
                        return true;
                    });
                }
                else if (!_isCentral)
                {
                    // Keep the existing deadline: a busy/declined trade cannot retry forever.
                    _withdrawalWorkerPhase = WithdrawalWorkerPhase.OpenTrade;
                }
                ResetWithdrawalTrade();
                return true;
            }
            return true;
        }

        private void WithdrawalLocate()
        {
            Item loose = FindWithdrawalInventoryItem(_withdrawal);
            if (loose != null)
            {
                Logger.Warning(
                    "[CityBankers] WITHDRAWAL LIVE INVENTORY RECOVERY " +
                    (_withdrawal?.Id ?? "?") + ": uniquely recognized " +
                    (loose.Name ?? "reserved item") + " in " + Client.CharacterName +
                    " normal inventory; the old audited source slot will not be replayed.");
                if (string.Equals(_withdrawal.SourceBag, "bank", StringComparison.OrdinalIgnoreCase))
                {
                    StorageState recoveryState = RuntimeStateStore.LoadStorageState(_settingsDir);
                    StorageWorkerState recoveryWorker = (recoveryState?.Workers ?? new List<StorageWorkerState>())
                        .FirstOrDefault(value => string.Equals(
                            value.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
                    StorageBagState recoveryBag = (recoveryWorker?.Bags ?? new List<StorageBagState>())
                        .FirstOrDefault(value => value != null &&
                            string.Equals(value.Source, "bank", StringComparison.OrdinalIgnoreCase) &&
                            value.OuterSlotInstance == _withdrawal.SourceBagOuterSlot);
                    Item staged = (Inventory.Items ?? new List<Item>()).FirstOrDefault(item =>
                        item != null && item.UniqueIdentity.Type == IdentityType.Container &&
                        !string.IsNullOrWhiteSpace(recoveryBag?.LastUniqueIdentity) &&
                        string.Equals(item.UniqueIdentity.ToString(), recoveryBag.LastUniqueIdentity, StringComparison.Ordinal));
                    if (staged != null)
                    {
                        _withdrawalBag = recoveryBag;
                        _withdrawalBagIdentity = staged.UniqueIdentity.ToString();
                        _withdrawalBankBag = true;
                        staged.MoveToBank();
                        _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitBagReturn;
                        SetWithdrawalDeadline();
                        return;
                    }
                }
                _withdrawalWorkerPhase = WithdrawalWorkerPhase.OpenTrade;
                SetWithdrawalDeadline();
                return;
            }
            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            StorageWorkerState worker = (state?.Workers ?? new List<StorageWorkerState>())
                .FirstOrDefault(value => string.Equals(
                    value.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
            _withdrawalBag = (worker?.Bags ?? new List<StorageBagState>())
                .FirstOrDefault(value => value != null &&
                    string.Equals(value.Source, _withdrawal.SourceBag, StringComparison.OrdinalIgnoreCase) &&
                    value.OuterSlotInstance == _withdrawal.SourceBagOuterSlot);
            if (_withdrawalBag == null)
            {
                FailWithdrawal(_withdrawal, "The audited source bag is missing.");
                return;
            }
            _withdrawalBankBag = string.Equals(_withdrawalBag.Source, "bank", StringComparison.OrdinalIgnoreCase);
            IEnumerable<Item> bagItems = _withdrawalBankBag
                ? (Inventory.Bank != null ? Inventory.Bank.Items : null)
                : Inventory.Items;
            Item bag = (bagItems ?? Enumerable.Empty<Item>()).FirstOrDefault(item => item != null &&
                    item.UniqueIdentity.Type == IdentityType.Container &&
                    ((!string.IsNullOrWhiteSpace(_withdrawalBag.LastUniqueIdentity) &&
                      string.Equals(item.UniqueIdentity.ToString(), _withdrawalBag.LastUniqueIdentity, StringComparison.Ordinal)) ||
                     (string.IsNullOrWhiteSpace(_withdrawalBag.LastUniqueIdentity) &&
                      item.Slot.Instance == _withdrawalBag.OuterSlotInstance)));
            if (bag == null)
            {
                if (_withdrawalBankBag)
                {
                    Item staged = (Inventory.Items ?? new List<Item>()).FirstOrDefault(item =>
                        item != null && item.UniqueIdentity.Type == IdentityType.Container &&
                        !string.IsNullOrWhiteSpace(_withdrawalBag.LastUniqueIdentity) &&
                        string.Equals(item.UniqueIdentity.ToString(), _withdrawalBag.LastUniqueIdentity, StringComparison.Ordinal));
                    if (staged != null)
                    {
                        _withdrawalBagIdentity = staged.UniqueIdentity.ToString();
                        staged.Use();
                        _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitBagOpen;
                        SetWithdrawalDeadline();
                    }
                }
                return;
            }
            _withdrawalBagIdentity = bag.UniqueIdentity.ToString();
            if (_withdrawalBankBag)
            {
                bag.MoveToInventory();
                _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitBagInventory;
            }
            else
            {
                bag.Use();
                _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitBagOpen;
            }
            SetWithdrawalDeadline();
        }

        private void WithdrawalWaitBagInventory()
        {
            Item bag = FindInventoryBagByIdentity(_withdrawalBagIdentity);
            if (bag == null) return;
            bag.Use();
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitBagOpen;
            SetWithdrawalDeadline();
        }

        private void WithdrawalWaitBagOpen()
        {
            Container container = FindContainerByIdentity(_withdrawalBagIdentity);
            if (container == null || !container.IsOpen || container.Items == null) return;
            Item item = container.Items.FirstOrDefault(value =>
                value != null &&
                (value.Slot.Instance & 0xFFFF) == _withdrawal.SourceInnerSlot &&
                MatchesWithdrawalItem(value, _withdrawal));
            if (item == null)
            {
                FailWithdrawal(_withdrawal, "The exact reserved item is not in its audited inner slot.");
                return;
            }
            _withdrawal.PreExtractionInventorySlots = (Inventory.Items ?? new List<Item>())
                .Where(value => value != null && value.Slot.Type == IdentityType.Inventory)
                .Select(value => value.Slot.Instance)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            WithdrawalStore.Save(_settingsDir, _withdrawal);
            item.MoveToContainer(DynelManager.LocalPlayer.Identity);
            _withdrawalItemMoveAttempts = 1;
            _withdrawalNextItemMoveRetryUtc = DateTime.UtcNow.AddSeconds(1);
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitItemInventory;
            SetWithdrawalDeadline();
        }

        private void WithdrawalWaitItemInventory()
        {
            Item extracted = FindWithdrawalInventoryItem(_withdrawal);
            if (extracted == null)
            {
                if (_withdrawalItemMoveAttempts < 3 &&
                    DateTime.UtcNow >= _withdrawalNextItemMoveRetryUtc)
                {
                    Container container = FindContainerByIdentity(_withdrawalBagIdentity);
                    Item stillInBag = container != null && container.IsOpen && container.Items != null
                        ? container.Items.FirstOrDefault(value =>
                            value != null &&
                            (value.Slot.Instance & 0xFFFF) == _withdrawal.SourceInnerSlot &&
                            MatchesWithdrawalItem(value, _withdrawal))
                        : null;
                    if (stillInBag != null)
                    {
                        _withdrawalItemMoveAttempts++;
                        _withdrawalNextItemMoveRetryUtc = DateTime.UtcNow.AddSeconds(2);
                        Logger.Warning(
                            "[CityBankers] WITHDRAWAL ITEM MOVE RETRY " +
                            (_withdrawal?.Id ?? "?") + ": attempt " +
                            _withdrawalItemMoveAttempts +
                            " reissued for the same exact reserved item still visible in its source slot.");
                        stillInBag.MoveToContainer(DynelManager.LocalPlayer.Identity);
                    }
                }
                return;
            }
            Logger.Information(
                "[CityBankers] WITHDRAWAL ITEM OBSERVED " + (_withdrawal?.Id ?? "?") +
                ": exact reserved item is now in " + Client.CharacterName +
                " normal inventory; continuing extraction.");
            if (!string.Equals(
                    _withdrawal.ExtractedItemIdentity,
                    extracted.UniqueIdentity.ToString(),
                    StringComparison.Ordinal))
            {
                _withdrawal.ExtractedItemIdentity = extracted.UniqueIdentity.ToString();
                WithdrawalStore.Save(_settingsDir, _withdrawal);
            }
            if (_withdrawalBankBag)
            {
                Item bag = FindInventoryBagByIdentity(_withdrawalBagIdentity);
                if (bag == null) return;
                bag.MoveToBank();
                _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitBagReturn;
            }
            else
                _withdrawalWorkerPhase = WithdrawalWorkerPhase.OpenTrade;
            SetWithdrawalDeadline();
        }

        private void WithdrawalWaitBagReturn()
        {
            Item bag = FindBankBagByIdentity(_withdrawalBagIdentity);
            if (bag == null) return;
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.OpenTrade;
            SetWithdrawalDeadline();
        }

        private void WithdrawalOpenCentralTrade()
        {
            if (Trade.IsTrading) return;
            PlayerChar central = DynelManager.Players.FirstOrDefault(player => player != null &&
                string.Equals(player.Name, _centralCharacter, StringComparison.OrdinalIgnoreCase));
            if (central == null) return;
            ResetWithdrawalTrade();
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitTrade;
            PrepareReceipt("withdrawal-transfer", _withdrawal.DonationTransactionId,
                _withdrawal.Id, new List<TransferItemState> { _withdrawal.Item }, -1);
            Trade.Open(central.Identity);
        }

        private void WithdrawalTickWorkerTrade()
        {
            if (DateTime.UtcNow >= _withdrawalDeadlineUtc)
            {
                TryDeclineTrade();
                FailWithdrawal(_withdrawal, "Timed out returning the reserved item to Central.");
                return;
            }
            if (!_withdrawalTradeOpened || !Trade.IsTrading) return;
            if (!_withdrawalItemOffered)
            {
                Item item = FindWithdrawalInventoryItem(_withdrawal);
                if (item == null) return;
                Trade.AddItem(item.Slot);
                _withdrawalItemOffered = true;
                return;
            }
            if (!_withdrawalAccepted)
            {
                List<Item> offered = Trade.PlayerWindowCache?.Items ?? new List<Item>();
                if (offered.Count == 1 && MatchesWithdrawalItem(offered[0], _withdrawal))
                {
                    _withdrawalAccepted = true;
                    Trade.Accept();
                }
            }
        }

        private void TickWithdrawalPickupTrade(WithdrawalState state)
        {
            if (!Trade.IsTrading) return;
            if ((Trade.TargetWindowCache?.Items?.Count ?? 0) > 0)
            {
                TellPlayer(state.RequestedBy, "This trade collects your ready order. Please donate in a separate trade after pickup.");
                TryDeclineTrade();
                return;
            }
            if (_pickupItems.Any(row => row.PickupExpiresUtc.HasValue &&
                    DateTime.UtcNow >= row.PickupExpiresUtc.Value))
            {
                TryDeclineTrade();
                return; // Declined callback restores all claimed items together
            }
            foreach (WithdrawalState row in _pickupItems)
            {
                if (_pickupOfferedIds.Contains(row.Id)) continue;
                Item item = FindWithdrawalInventoryItem(row);
                if (item == null)
                {
                    TryDeclineTrade();
                    return;
                }
                Trade.AddItem(item.Slot);
                _pickupOfferedIds.Add(row.Id);
                return;
            }
            if (!_withdrawalAccepted)
            {
                List<Item> offered = Trade.PlayerWindowCache?.Items ?? new List<Item>();
                var available = new List<Item>(offered);
                bool exact = offered.Count == _pickupItems.Count;
                foreach (WithdrawalState row in _pickupItems)
                {
                    Item match = available.FirstOrDefault(item => MatchesWithdrawalItem(item, row));
                    if (match == null) { exact = false; break; }
                    available.Remove(match);
                }
                if (exact)
                {
                    _withdrawalAccepted = true;
                    Trade.Accept();
                }
            }
        }

        private void OpenPickupWindow(WithdrawalState state)
        {
            string error;
            if (!RuntimeStateStore.RemoveStoredItem(
                    _settingsDir, state.SourceCharacter, state.SourceBag,
                    state.SourceBagOuterSlot, state.SourceInnerSlot,
                    state.SourceItemIdentity, state.Item.AoId, out error))
            {
                // Restart/idempotence: absence is acceptable only when Central visibly owns
                // the item and the exact old stock row is already gone.
                if (FindWithdrawalInventoryItem(state) == null ||
                    WithdrawalSourceStillPersisted(state))
                {
                    FailWithdrawal(state, "Central received the item but storage-state removal failed: " + error);
                    return;
                }
            }
            Item received = FindWithdrawalInventoryItem(state);
            if (received == null) return;
            WithdrawalStore.Update(_settingsDir, rows =>
            {
                WithdrawalState fresh = rows.First(row => row.Id == state.Id);
                fresh.CentralItemIdentity = received.UniqueIdentity.ToString();
                fresh.Status = "central-ready";
                DateTime expires = DateTime.UtcNow.AddSeconds(WithdrawalStore.PickupSeconds);
                foreach (WithdrawalState row in rows.Where(row => row.OrderId == fresh.OrderId &&
                    WithdrawalStore.HasStatus(row, "central-ready")))
                {
                    row.PickupExpiresUtc = expires;
                    WithdrawalStore.Touch(row);
                }
                return true;
            });
            TellPlayer(state.RequestedBy,
                (state.Item?.Name ?? "Your item") + " is ready on Kbcentral. " +
                "Your order pickup clock is now three minutes. Open trade to collect all ready items.");
        }

        private void RetryConfirmedWithdrawalAccounting(WithdrawalState state)
        {
            Stopwatch retry;
            if (!_withdrawalAccountingRetry.TryGetValue(state.Id, out retry))
                _withdrawalAccountingRetry[state.Id] = retry = Stopwatch.StartNew();
            if (retry.ElapsedMilliseconds < 1000) return;
            retry.Restart();
            try
            {
                if (!state.DeliveredUtc.HasValue)
                    throw new InvalidOperationException("Confirmed pickup has no recorded delivery time; original evidence retained.");
                CompleteWithdrawalAccounting(state);
                _withdrawalAccountingRetry.Remove(state.Id);
                _withdrawalAccountingError.Remove(state.Id);
            }
            catch (Exception ex)
            {
                string previous;
                if (!_withdrawalAccountingError.TryGetValue(state.Id, out previous) || previous != ex.Message)
                    Logger.Warning("[CityBankers] Confirmed withdrawal accounting retry " + state.Id + ": " + ex.Message);
                _withdrawalAccountingError[state.Id] = ex.Message;
            }
        }

        private void CompleteWithdrawalAccounting(WithdrawalState state)
        {
            if (!ActiveLedgerStore.ArchiveActiveItemById(
                    _settingsDir, state.ActiveLedgerId,
                    state.DeliveredUtc ?? DateTime.UtcNow,
                    "withdrawn", state.RecipientMain))
            {
                throw new InvalidOperationException("AO delivered the item, but active-ledger archival is still pending.");
            }
            state.Status = "completed";
            WithdrawalStore.Save(_settingsDir, state);
            RuntimeStateStore.AppendLedger(_settingsDir, new LedgerRecord
            {
                Utc = state.DeliveredUtc ?? DateTime.UtcNow,
                Event = "member_withdrawal_completed",
                TransactionId = state.Id,
                Actor = Client.CharacterName,
                Source = Client.CharacterName,
                Destination = state.RecipientMain,
                Message = "AO Finished confirmed reserved item delivery to the canonical AP member.",
                Items = new List<LedgerItem> { ToLedgerItem(state.Item, state.SourceRole, null, null, null) }
            });
            TellPlayer(state.RequestedBy,
                "Pickup complete: " + (state.Item?.Name ?? ("AOID " + state.Item?.AoId)) +
                " was recorded as given to " + state.RecipientMain + ".");
            ResetWithdrawalLocal();
        }

        private void QueueWithdrawalReturn(WithdrawalState state)
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            string batchId = state.ReturnBatchId ?? ("withdraw-return-" + state.Id);
            if (!queue.Batches.Any(batch => batch.BatchId == batchId))
                queue.Batches.Add(new DispatchBatchState
            {
                BatchId = batchId,
                TransactionId = state.DonationTransactionId,
                Role = state.SourceRole,
                Character = state.SourceCharacter,
                Status = "queued",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Items = new List<TransferItemState> { new TransferItemState {
                    UniqueIdentity = state.CentralItemIdentity ?? state.Item.UniqueIdentity,
                    AoId = state.Item.AoId, HighId = state.Item.HighId, Ql = state.Item.Ql, Name = state.Item.Name } }
            });
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            state.ReturnBatchId = batchId;
            state.Status = "return-queued";
            WithdrawalStore.Save(_settingsDir, state);
            TellPlayer(state.RequestedBy,
                "Your three-minute pickup expired. The item is being returned safely to storage.");
            ResetWithdrawalTrade();
        }

        private bool WithdrawalReturnIsStored(WithdrawalState state)
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            if ((queue.Batches ?? new List<DispatchBatchState>()).Any(batch =>
                    string.Equals(batch.BatchId, state.ReturnBatchId, StringComparison.Ordinal)))
                return false;

            bool restored = (RuntimeStateStore.LoadCurrentStock(_settingsDir).Items ??
                    new List<StockItemState>())
                .Any(item => item != null && item.AoId == state.Item.AoId &&
                    string.Equals(item.TransactionId, state.DonationTransactionId, StringComparison.Ordinal) &&
                    string.Equals(item.Character, state.SourceCharacter, StringComparison.OrdinalIgnoreCase));
            if (!restored)
                return false;

            // The normal dispatch consumer removes a successful batch and then
            // deletes its worker result.  The queue absence plus this exact
            // transaction's restored canonical stock row is the durable proof;
            // retain a matching result only as an additional failure check when
            // Central happens to observe it before that ordinary cleanup.
            StorageBatchResult result = RuntimeStateStore.ReadStorageResult(_settingsDir, state.SourceCharacter);
            return result == null || result.BatchId != state.ReturnBatchId ||
                (result.Success && result.StoredCount == 1);
        }

        private bool WithdrawalSourceStillPersisted(WithdrawalState state)
        {
            return (RuntimeStateStore.LoadCurrentStock(_settingsDir).Items ?? new List<StockItemState>())
                .Any(item => item != null && item.AoId == state.Item.AoId &&
                    string.Equals(item.TransactionId, state.DonationTransactionId, StringComparison.Ordinal) &&
                    string.Equals(item.Character, state.SourceCharacter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.BagSource, state.SourceBag, StringComparison.OrdinalIgnoreCase) &&
                    item.BagOuterSlot == state.SourceBagOuterSlot &&
                    item.InnerSlot == state.SourceInnerSlot);
        }

        private static bool MatchesWithdrawalItem(Item item, WithdrawalState state)
        {
            if (item == null || state?.Item == null) return false;
            if (!string.IsNullOrWhiteSpace(state.SourceItemIdentity) &&
                !string.Equals(state.SourceItemIdentity, "(None:0000)", StringComparison.Ordinal) &&
                string.Equals(item.UniqueIdentity.ToString(), state.SourceItemIdentity, StringComparison.Ordinal))
                return true;
            return item.Id == state.Item.AoId && item.HighId == state.Item.HighId && item.Ql == state.Item.Ql;
        }

        private bool IsReservedForPickup(Item item, List<WithdrawalState> reservations)
        {
            if (!_isCentral || item == null) return false;
            return reservations.Any(row =>
                WithdrawalStore.IsActive(row) && !WithdrawalStore.HasStatus(row, "return-queued") &&
                (row.CentralItemIdentity == item.UniqueIdentity.ToString() ||
                 row.ExtractedItemIdentity == item.UniqueIdentity.ToString() ||
                 row.SourceItemIdentity == item.UniqueIdentity.ToString() ||
                 (string.IsNullOrWhiteSpace(row.CentralItemIdentity) &&
                  (WithdrawalStore.HasStatus(row, "failed") ||
                   WithdrawalStore.HasStatus(row, "central-received") ||
                   WithdrawalStore.HasStatus(row, "central-ready") ||
                   WithdrawalStore.HasStatus(row, "pickup-trading") ||
                   WithdrawalStore.HasStatus(row, "returning")) &&
                  MatchesWithdrawalItem(item, row))));
        }

        private Item FindWithdrawalInventoryItem(WithdrawalState state)
        {
            List<Item> inventory = (Inventory.Items ?? new List<Item>()).Where(item =>
                item != null && item.Slot.Type == IdentityType.Inventory &&
                item.UniqueIdentity.Type != IdentityType.Container).ToList();

            if (state?.LiveInventoryAnchorSlot.HasValue == true)
            {
                List<Item> anchored = inventory.Where(item =>
                    item.Slot.Instance == state.LiveInventoryAnchorSlot.Value &&
                    (string.Equals(
                         item.UniqueIdentity.ToString(),
                         state.LiveInventoryAnchorIdentity,
                         StringComparison.Ordinal) ||
                     string.Equals(
                         item.Name ?? string.Empty,
                         state.Item?.Name ?? string.Empty,
                         StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (anchored.Count == 1)
                    return anchored[0];
            }

            if (_isCentral && !string.IsNullOrWhiteSpace(state?.CentralItemIdentity))
            {
                List<Item> exactCentral = inventory.Where(item =>
                    item.UniqueIdentity.ToString() == state.CentralItemIdentity).ToList();
                return exactCentral.Count == 1 ? exactCentral[0] : null;
            }

            if (!string.IsNullOrWhiteSpace(state?.ExtractedItemIdentity))
            {
                List<Item> exactExtracted = inventory.Where(item => string.Equals(
                    item.UniqueIdentity.ToString(),
                    state.ExtractedItemIdentity,
                    StringComparison.Ordinal)).ToList();
                if (exactExtracted.Count == 1)
                    return exactExtracted[0];
            }

            if (!string.IsNullOrWhiteSpace(state?.SourceItemIdentity) &&
                !string.Equals(state.SourceItemIdentity, "(None:0000)", StringComparison.Ordinal))
            {
                List<Item> exact = inventory.Where(item => string.Equals(
                    item.UniqueIdentity.ToString(),
                    state.SourceItemIdentity,
                    StringComparison.Ordinal)).ToList();
                if (exact.Count == 1)
                    return exact[0];
            }

            // Exclude every other reservation, so identical templates cannot share one physical item.
            List<WithdrawalState> others = WithdrawalStore.LoadAll(_settingsDir)
                .Where(row => WithdrawalStore.IsActive(row) && row.Id != state.Id).ToList();
            List<Item> matches = inventory.Where(item => MatchesWithdrawalItem(item, state) &&
                !others.Any(row => !string.IsNullOrWhiteSpace(row.CentralItemIdentity) &&
                    row.CentralItemIdentity == item.UniqueIdentity.ToString())).ToList();
            if (matches.Count == 1)
                return matches[0];

            // AO can assign a different live identity/template representation while moving
            // a bag item into normal inventory. The pre-move slot census still proves which
            // single new slot appeared, and name+QL ties that occurrence to the reservation.
            // This is deliberately unique-or-nothing so an unrelated inventory change can
            // never be guessed as the withdrawn item.
            var priorSlots = new HashSet<int>(state.PreExtractionInventorySlots ?? new List<int>());
            List<Item> named = inventory.Where(item =>
                    string.Equals(item.Name ?? string.Empty, state.Item.Name ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase) &&
                    (priorSlots.Count == 0 || !priorSlots.Contains(item.Slot.Instance)) &&
                    !others.Any(row => !string.IsNullOrWhiteSpace(row.CentralItemIdentity) &&
                        row.CentralItemIdentity == item.UniqueIdentity.ToString()))
                .ToList();
            return named.Count == 1 ? named[0] : null;
        }

        private void FailWithdrawal(WithdrawalState state, string error)
        {
            if (state == null) return;
            state.Status = "failed";
            state.Error = error;
            WithdrawalStore.Save(_settingsDir, state);
            Logger.Error("[CityBankers] WITHDRAWAL FAILED " + state.Id + ": " + error);
            TellPlayer(state.RequestedBy, "Withdrawal stopped safely: " + error);
            TryDeclineTrade();
            ResetWithdrawalLocal();
        }

        private void SetWithdrawalDeadline()
        {
            _withdrawalDeadlineUtc = DateTime.UtcNow.AddSeconds(WithdrawalPhaseTimeoutSeconds);
        }

        private void ResetWithdrawalTrade()
        {
            _withdrawalTradeOpened = false;
            _withdrawalItemOffered = false;
            _withdrawalAccepted = false;
            _withdrawalTradePartner = Identity.None;
            _withdrawalPickupTrade = false;
            _pickupItems.Clear();
            _pickupOfferedIds.Clear();
        }

        private void ResetWithdrawalLocal()
        {
            _withdrawal = null;
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.None;
            _withdrawalBag = null;
            _withdrawalBagIdentity = null;
            _withdrawalBankBag = false;
            _withdrawalItemMoveAttempts = 0;
            _withdrawalNextItemMoveRetryUtc = DateTime.MinValue;
            ResetWithdrawalTrade();
        }
    }
}
