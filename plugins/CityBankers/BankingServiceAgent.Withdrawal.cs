using System;
using System.Threading.Tasks;
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
        private readonly Stopwatch _withdrawalPhaseAge = Stopwatch.StartNew();
        private Stopwatch _withdrawalClosedAge;
        private StorageBagState _withdrawalBag;
        private string _withdrawalBagIdentity;
        private bool _withdrawalBankBag;
        private bool _withdrawalTradeOpened;
        private bool _withdrawalItemOffered;
        private bool _withdrawalAccepted;
        private Identity _withdrawalTradePartner = Identity.None;
        private bool _withdrawalPickupTrade;
        private bool _pickupDeclineSent;
        private bool _pickupPlayerAccepted;
        private bool _pickupConfirmSent;
        private bool _pickupPlayerConfirmed;
        private bool _pickupFinalAcceptSent;
        private readonly Stopwatch _pickupConfirmAge = new Stopwatch();
        // Clientless keeps the same Item object when offering/returning a local item,
        // even if cancellation returns it to a different inventory slot. A new cache
        // or actor cannot inherit these bindings; existing census recovery owns that case.
        private readonly Dictionary<string, Item> _centralWithdrawalItems = new Dictionary<string, Item>();
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
            {
                if (CheckClosedWithdrawalTrade()) return true;
                ResetWithdrawalTrade();
            }
            if (_withdrawalTradeOpened)
            {
                if (_withdrawalPickupTrade && _pickupItems.Count > 0)
                    TickWithdrawalPickupTrade(_pickupItems[0]);
                else if (!_withdrawalPickupTrade) TickWithdrawalReceiver();
                if (!_withdrawalPickupTrade && _extractionWait.IsRunning && _extractionWait.Elapsed.TotalSeconds >= 240)
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
                    !WithdrawalStore.PickupWindowOpen(row))
                {
                    // Claim expiry against the current revision: a concurrent #get may have extended it.
                    bool expired = WithdrawalStore.Update(_settingsDir, current =>
                    {
                        WithdrawalState fresh = current.First(value => value.Id == row.Id);
                        if (!WithdrawalStore.HasStatus(fresh, "central-ready") ||
                            WithdrawalStore.PickupWindowOpen(fresh))
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
            if (StartupCensusGate.UsesPhysicalRecovery)
            {
                if (!WithdrawalStore.TryBeginExtraction(_settingsDir, next)) return false;
            }
            else
            {
                next.Status = "extracting";
                WithdrawalStore.Save(_settingsDir, next);
            }
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
            // A cached heartbeat/name match is not a new physical observation
            // or a peer acknowledgement. Paired custody recovery owns retries
            // in normal mode; keep the legacy path out of that runtime.
            if (StartupCensusGate.UsesPhysicalRecovery) return false;
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
                _withdrawalPhaseAge.Elapsed.TotalSeconds >= WithdrawalPhaseTimeoutSeconds)
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
                _withdrawal = transfer;
                _internalOpenedAge.Restart();
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
                    WithdrawalStore.PickupWindowOpen(row));
                if (first == null) return new List<WithdrawalState>();
                List<WithdrawalState> claim = current.Where(row => row.OrderId == first.OrderId &&
                    WithdrawalStore.HasStatus(row, "central-ready") &&
                    WithdrawalStore.PickupWindowOpen(row)).ToList();
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
            ReportTransferProgress("PICKUP OPENED", ready[0].OrderId,
                "collector=" + targetName + "; items=" + ready.Count, ready.Select(row => row.Item));
            return true;
        }

        private bool TryHandleWithdrawalTradeStatus(Identity target, TradeStatus status)
        {
            if (!_withdrawalTradeOpened || _withdrawal == null)
                return false;
            WithdrawalState state = WithdrawalStore.LoadAll(_settingsDir).First(row => row.Id == _withdrawal.Id);
            if (_isCentral && _withdrawalPickupTrade &&
                (status == TradeStatus.Accept || status == TradeStatus.Confirm))
            {
                // The callback target is unreliable in Clientless. Use the partner
                // captured at Opened, as the proven player donation bridge does.
                if (!Trade.IsTrading || Trade.CurrentTarget != _withdrawalTradePartner || _pickupDeclineSent)
                    return true;
                if (status == TradeStatus.Accept && !_pickupConfirmSent)
                {
                    _pickupPlayerAccepted = PickupOfferExact();
                    ReportTransferProgress("PICKUP HANDSHAKE", state.OrderId,
                        _pickupPlayerAccepted ? "player Accept observed; preparing Central Confirm" :
                        "player Accept arrived before the complete pickup offer; waiting for a fresh Accept");
                }
                else if (status == TradeStatus.Confirm && _pickupConfirmSent && !_pickupPlayerConfirmed)
                {
                    _pickupPlayerConfirmed = true;
                    _pickupConfirmAge.Restart();
                    ReportTransferProgress("PICKUP HANDSHAKE", state.OrderId,
                        "player Confirm observed; preparing Central final Accept");
                }
                return true;
            }
            if (status == TradeStatus.Accept) return true; // Internal bot trade acknowledgement.
            if (status == TradeStatus.Confirm)
            {
                QueueInternalConfirmation(_withdrawalTradePartner);
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
                    if (!_withdrawalAccepted || !_pickupFinalAcceptSent || !_pickupPlayerConfirmed ||
                        _pickupOfferedIds.Count != _pickupItems.Count)
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
                    foreach (string id in ids) _centralWithdrawalItems.Remove(id);
                }
                else if (_isCentral)
                {
                    BindVerifiedWithdrawalArrival(state);
                    state.Status = "central-received";
                    WithdrawalStore.Save(_settingsDir, state);
                    ReportTransferProgress("WITHDRAWAL RECEIVED VERIFIED", state.Id,
                        state.SourceCharacter + " -> " + Client.CharacterName + "; receipt verified; preparing pickup",
                        new[] { state.Item });
                }
                ResetWithdrawalTrade();

                });
                return true;
            }
            if (status == TradeStatus.Declined)
            {
                if (_afterReceipt != null && _receipt != null && _receipt.Direction != 0)
                {
                    if (!BeginWithdrawalDispute("Conflicting withdrawal Declined after Finished; custody evidence retained."))
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
            ReportTransferProgress("WITHDRAWAL ITEM MOVE", _withdrawal.Id,
                "attempt=1; requester=" + _withdrawal.RequestedBy + "; source=" + Client.CharacterName +
                "; bag=" + _withdrawal.SourceBag + "; innerSlot=" + _withdrawal.SourceInnerSlot +
                "; sending move to normal inventory", new[] { _withdrawal.Item });
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
                            "; requester=" + _withdrawal.RequestedBy +
                            "; still awaiting inventory confirmation; source bag slot remains visible; reissuing move.");
                        stillInBag.MoveToContainer(DynelManager.LocalPlayer.Identity);
                    }
                }
                return;
            }
            Logger.Information(
                "[CityBankers] WITHDRAWAL ITEM OBSERVED " + (_withdrawal?.Id ?? "?") +
                ": exact reserved item is now in " + Client.CharacterName +
                " normal inventory; continuing extraction.");
            ReportTransferProgress("WITHDRAWAL EXTRACTED", _withdrawal.Id,
                "source=" + Client.CharacterName + "; reserved item observed in normal inventory; preparing return to " + _centralCharacter,
                new[] { _withdrawal.Item });
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
            if (_withdrawalPreparation == null)
            {
                if (_withdrawalPreparationAge.ElapsedMilliseconds < 500) return;
                _withdrawal.TransferAttemptId = Guid.NewGuid().ToString("N");
                WithdrawalStore.Save(_settingsDir, _withdrawal);
                _withdrawalPreparationAge.Restart();
                _withdrawalPreparation = AskWithdrawalPreparation(WithdrawalCommand(_withdrawal));
                return;
            }
            if (!_withdrawalPreparation.IsCompleted) return;
            bool ready = _withdrawalPreparation.Status == TaskStatus.RanToCompletion &&
                _withdrawalPreparationAge.ElapsedMilliseconds < 1500 &&
                _withdrawalPreparation.Result == "ready:" + _withdrawal.TransferAttemptId + ":prepare";
            _withdrawalPreparation = null;
            if (!ready || CensusReservedHere()) return;
            ResetWithdrawalTrade();
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitTrade;
            PrepareReceipt("withdrawal-transfer", _withdrawal.DonationTransactionId,
                _withdrawal.Id, new List<TransferItemState> { _withdrawal.Item }, -1);
            ReportTransferProgress("WITHDRAWAL TRANSFER OPENING", _withdrawal.Id,
                "attempt=" + _withdrawal.TransferAttemptId + "; " + Client.CharacterName + " -> " + _centralCharacter,
                new[] { _withdrawal.Item });
            Trade.Open(central.Identity);
        }

        private async System.Threading.Tasks.Task<string> AskWithdrawalPreparation(DispatchCommand command)
        {
            try
            {
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(_centralCharacter),
                    Newtonsoft.Json.JsonConvert.SerializeObject(new DispatchProposal { Kind = "withdrawal-prepare", Command = command }),
                    1000, 4000).ConfigureAwait(false);
            }
            catch (Exception) { return "pending"; }
        }

        private void WithdrawalTickWorkerTrade()
        {
            if (CheckClosedWithdrawalTrade()) return;
            if (_withdrawalPhaseAge.Elapsed.TotalSeconds >= WithdrawalPhaseTimeoutSeconds)
            {
                TryDeclineTrade();
                FailWithdrawal(_withdrawal, "Timed out returning the reserved item to Central.");
                return;
            }
            if (!_withdrawalTradeOpened || !Trade.IsTrading || !DispatchPeerReady("opened")) return;
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
                if (offered.Count == 1 && MatchesWithdrawalItem(offered[0], _withdrawal) &&
                    DispatchWindowsConsistent(CurrentInternalCommand()) && InternalOfferSettled(SnapshotTradeItems(offered)))
                {
                    _withdrawalAccepted = true;
                    _localDispatchAcceptAge.Restart();
                    Trade.Accept();
                }
            }
        }

        private void DeclinePickup(WithdrawalState state, string reason)
        {
            if (_pickupDeclineSent) { TryDeclineTrade(); return; }
            _pickupDeclineSent = true;
            ReportTransferProgress("PICKUP DECLINED", state.OrderId, "request=" + state.Id + "; " + reason);
            try { TellDirectPlayer(state.RequestedBy, reason); }
            catch (Exception ex) { Logger.Warning("Pickup decline notice unavailable: " + ex.Message); }
            TryDeclineTrade();
        }

        private void TickWithdrawalPickupTrade(WithdrawalState state)
        {
            if (!Trade.IsTrading) return;
            if (_pickupDeclineSent) { TryDeclineTrade(); return; }
            if ((Trade.TargetWindowCache?.Items?.Count ?? 0) > 0)
            {
                DeclinePickup(state, "This trade collects your ready order. Please donate in a separate trade after pickup.");
                return;
            }
            if (_pickupItems.Any(row => !WithdrawalStore.PickupWindowOpen(row)))
            {
                DeclinePickup(state, "Your pickup window expired while the trade was open. Please request the item again after it returns to storage.");
                return; // Declined callback restores all claimed items together
            }
            foreach (WithdrawalState row in _pickupItems)
            {
                if (_pickupOfferedIds.Contains(row.Id)) continue;
                Item item = FindWithdrawalInventoryItem(row);
                if (item == null)
                {
                    DeclinePickup(state, "Central cannot uniquely identify a reserved item for this pickup. Nothing will be handed out; your order remains pending.");
                    return;
                }
                if (_managedAddWait.ElapsedMilliseconds < 500) return;
                Trade.AddItem(item.Slot);
                _managedAddWait.Restart();
                _pickupOfferedIds.Add(row.Id);
                return;
            }
            if (!PickupOfferExact())
            {
                _pickupPlayerAccepted = false;
                if (_pickupConfirmSent)
                    DeclinePickup(state, "The pickup offer changed during confirmation. Please reopen trade to collect your order.");
                return;
            }
            if (!_withdrawalAccepted)
            {
                if (!InternalOfferSettled(SnapshotTradeItems(Trade.PlayerWindowCache.Items))) return;
                _withdrawalAccepted = true;
                _localDispatchAcceptAge.Restart();
                Trade.Accept();
                ReportTransferProgress("PICKUP HANDSHAKE", state.OrderId,
                    "Central initial Accept sent; waiting for player Accept");
                return;
            }
            if (!_pickupPlayerAccepted) return;
            if (!_pickupConfirmSent)
            {
                PersistReceipt("pickup-player-accepted-confirming");
                _pickupConfirmSent = true;
                Trade.Confirm();
                ReportTransferProgress("PICKUP HANDSHAKE", state.OrderId,
                    "Central Confirm sent; waiting for player's confirmation dialog");
                return;
            }
            // Never echo Confirm or re-Accept while the player's modal is open.
            if (!_pickupPlayerConfirmed || _pickupFinalAcceptSent || _pickupConfirmAge.ElapsedMilliseconds < 150)
                return;
            PersistReceipt("pickup-player-confirmed-final-accept");
            _pickupFinalAcceptSent = true;
            Trade.Accept();
            ReportTransferProgress("PICKUP HANDSHAKE", state.OrderId,
                "Central final Accept sent after player Confirm; waiting for AO Finished");
        }

        private bool PickupOfferExact()
        {
            if (_pickupItems.Count == 0 || _pickupOfferedIds.Count != _pickupItems.Count ||
                Trade.TargetWindowCache?.Items == null || Trade.TargetWindowCache.Items.Count != 0 ||
                Trade.PlayerWindowCache?.Items == null) return false;
            var available = new List<Item>(Trade.PlayerWindowCache.Items);
            if (available.Count != _pickupItems.Count) return false;
            foreach (WithdrawalState row in _pickupItems)
            {
                Item bound;
                bool hasBinding = _centralWithdrawalItems.TryGetValue(row.Id, out bound);
                Item match = available.FirstOrDefault(item => MatchesWithdrawalItem(item, row) &&
                    (!hasBinding || ReferenceEquals(item, bound)));
                if (match == null) return false;
                available.Remove(match);
            }
            return available.Count == 0;
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
            ActiveLedgerStore.RecordWithdrawalArrival(_settingsDir, state, Client.CharacterName,
                SnapshotTradeItems(new[] { received }).Single(), received.Slot.Instance);
            WithdrawalStore.Update(_settingsDir, rows =>
            {
                WithdrawalState fresh = rows.First(row => row.Id == state.Id);
                fresh.CentralItemIdentity = IsUsableIdentity(received.UniqueIdentity.ToString())
                    ? received.UniqueIdentity.ToString() : null;
                fresh.Status = "central-ready";
                foreach (WithdrawalState row in rows.Where(row => row.OrderId == fresh.OrderId &&
                    WithdrawalStore.HasStatus(row, "central-ready")))
                {
                    WithdrawalStore.RenewPickupWindow(row);
                    WithdrawalStore.Touch(row);
                }
                return true;
            });
            ReportTransferProgress("WITHDRAWAL READY", state.Id,
                "collector=" + state.RequestedBy + "; destination=" + Client.CharacterName + "; pickup window three minutes",
                new[] { state.Item });
            TellDirectPlayer(state.RequestedBy,
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
                ((IsUsableIdentity(row.CentralItemIdentity) && row.CentralItemIdentity == item.UniqueIdentity.ToString()) ||
                 (IsUsableIdentity(row.ExtractedItemIdentity) && row.ExtractedItemIdentity == item.UniqueIdentity.ToString()) ||
                 (IsUsableIdentity(row.SourceItemIdentity) && row.SourceItemIdentity == item.UniqueIdentity.ToString()) ||
                 (!IsUsableIdentity(row.CentralItemIdentity) &&
                  (WithdrawalStore.HasStatus(row, "failed") ||
                   WithdrawalStore.HasStatus(row, "central-received") ||
                   WithdrawalStore.HasStatus(row, "central-ready") ||
                   WithdrawalStore.HasStatus(row, "pickup-trading") ||
                   WithdrawalStore.HasStatus(row, "returning")) &&
                  MatchesWithdrawalItem(item, row))));
        }

        private void BindVerifiedWithdrawalArrival(WithdrawalState state)
        {
            if (_receipt == null || _receipt.Kind != "withdrawal-transfer" ||
                _receipt.Direction != 1 || _receipt.BatchId != state.Id ||
                _receipt.LiveBeforeItems == null)
                throw new InvalidOperationException("Withdrawal arrival lacks live receipt evidence.");

            List<Item> inventory = Inventory.Items.Where(item => item != null &&
                item.Slot.Type == IdentityType.Inventory).ToList();
            // The full count delta has already been verified. Also require all prior
            // live occurrences to remain present and exactly one new occurrence.
            List<Item> added = inventory.Where(item => !_receipt.LiveBeforeItems.Any(
                before => ReferenceEquals(before, item))).ToList();
            if (added.Count != 1 || !MatchesWithdrawalItem(added[0], state) ||
                _receipt.LiveBeforeItems.Any(before => !inventory.Any(item => ReferenceEquals(before, item))))
                throw new InvalidOperationException("Cannot bind the single verified withdrawal arrival to a live occurrence.");

            var activeIds = new HashSet<string>(WithdrawalStore.LoadAll(_settingsDir)
                .Where(WithdrawalStore.IsActive).Select(row => row.Id));
            foreach (string id in _centralWithdrawalItems.Keys.Where(id => !activeIds.Contains(id)).ToList())
                _centralWithdrawalItems.Remove(id);
            if (_centralWithdrawalItems.Any(pair => pair.Key != state.Id && ReferenceEquals(pair.Value, added[0])))
                throw new InvalidOperationException("Withdrawal arrival already belongs to another request.");
            _centralWithdrawalItems[state.Id] = added[0];
            _receipt.WithdrawalArrivalSlot = added[0].Slot.Instance;
            ReportTransferProgress("WITHDRAWAL COPY BOUND", state.Id,
                "requester=" + state.RequestedBy + "; character=" + Client.CharacterName +
                "; inventorySlot=" + added[0].Slot + "; verified new occurrence", new[] { state.Item });
        }

        private Item FindWithdrawalInventoryItem(WithdrawalState state)
        {
            List<Item> inventory = (Inventory.Items ?? new List<Item>()).Where(item =>
                item != null && item.Slot.Type == IdentityType.Inventory &&
                item.UniqueIdentity.Type != IdentityType.Container).ToList();

            Item bound;
            if (_isCentral && state != null && _centralWithdrawalItems.TryGetValue(state.Id, out bound))
            {
                // Do not substitute another identical copy if this occurrence disappears.
                return inventory.Any(item => ReferenceEquals(item, bound)) && MatchesWithdrawalItem(bound, state)
                    ? bound : null;
            }

            if (!_isCentral && state?.LiveInventoryAnchorSlot.HasValue == true)
            {
                List<Item> anchored = inventory.Where(item =>
                    item.Slot.Instance == state.LiveInventoryAnchorSlot.Value &&
                    ((IsUsableIdentity(state.LiveInventoryAnchorIdentity) && string.Equals(
                         item.UniqueIdentity.ToString(),
                         state.LiveInventoryAnchorIdentity,
                         StringComparison.Ordinal)) ||
                     string.Equals(
                         item.Name ?? string.Empty,
                         state.Item?.Name ?? string.Empty,
                         StringComparison.OrdinalIgnoreCase) && item.Ql == state.Item.Ql))
                    .ToList();
                if (anchored.Count == 1)
                    return anchored[0];
            }

            if (_isCentral && IsUsableIdentity(state?.CentralItemIdentity))
            {
                List<Item> exactCentral = inventory.Where(item =>
                    item.UniqueIdentity.ToString() == state.CentralItemIdentity).ToList();
                return exactCentral.Count == 1 ? exactCentral[0] : null;
            }

            if (IsUsableIdentity(state?.ExtractedItemIdentity))
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
                (!_isCentral || !_centralWithdrawalItems.Any(pair => pair.Key != state.Id &&
                    others.Any(row => row.Id == pair.Key) && ReferenceEquals(pair.Value, item))) &&
                !others.Any(row => IsUsableIdentity(row.CentralItemIdentity) &&
                    row.CentralItemIdentity == item.UniqueIdentity.ToString())).ToList();
            if (matches.Count == 1)
                return matches[0];
            if (_isCentral) return null; // Never reuse a worker-side extraction slot on Central.

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
                    !others.Any(row => IsUsableIdentity(row.CentralItemIdentity) &&
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
            VerifyCancelledReceipt();
            TryDeclineTrade();
            ResetWithdrawalLocal();
            Logger.Error("[CityBankers] WITHDRAWAL FAILED " + state.Id + ": " + error);
            try { TellPlayer(state.RequestedBy, "Withdrawal paused for physical recovery: " + error); }
            catch (Exception ex) { Logger.Warning("[CityBankers] Withdrawal failure notice unavailable: " + ex.Message); }
        }

        private void SetWithdrawalDeadline()
        {
            _withdrawalPhaseAge.Restart();
        }

        private bool CheckClosedWithdrawalTrade()
        {
            if (!_withdrawalTradeOpened || Trade.IsTrading || !WithdrawalReceipt(_receipt))
            { _withdrawalClosedAge = null; return false; }
            if (_afterReceipt != null) return true;
            if (_withdrawalClosedAge == null) _withdrawalClosedAge = Stopwatch.StartNew();
            if (_withdrawalClosedAge.ElapsedMilliseconds < 1000) return true;
            // Closure alone says neither delivered nor returned. A complete
            // unchanged receipt is safe to retry; any changed inventory takes
            // the full affected-character census path.
            VerifyCancelledReceipt();
            return true;
        }

        private void ResetWithdrawalTrade()
        {
            _withdrawalPreparation = null;
            _withdrawalTradeOpened = false;
            _withdrawalItemOffered = false;
            _withdrawalAccepted = false;
            _withdrawalTradePartner = Identity.None;
            _withdrawalPickupTrade = false;
            _pickupDeclineSent = false;
            _pickupPlayerAccepted = false;
            _pickupConfirmSent = false;
            _pickupPlayerConfirmed = false;
            _pickupFinalAcceptSent = false;
            _pickupConfirmAge.Reset();
            _withdrawalClosedAge = null;
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
