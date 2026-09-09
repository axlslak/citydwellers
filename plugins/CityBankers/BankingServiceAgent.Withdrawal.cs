using System;
using System.Collections.Generic;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;

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
        private readonly HashSet<string> _withdrawalRecoveryAttemptedIds =
            new HashSet<string>(StringComparer.Ordinal);

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

        private bool TickWithdrawalCentral()
        {
            WithdrawalState state = WithdrawalStore.Load(_settingsDir);
            _withdrawal = state;
            if (!WithdrawalStore.IsActive(state))
            {
                ResetWithdrawalLocal();
                return false;
            }

            if (string.Equals(state.Status, "requested", StringComparison.OrdinalIgnoreCase))
            {
                if (_donationActive || _donationCleanup != null || _activeBatch != null ||
                    Trade.IsTrading || HasUnresolvedDispatchWork())
                    return false;
                state.Status = "extracting";
                WithdrawalStore.Save(_settingsDir, state);
            }

            if (string.Equals(state.Status, "return-queued", StringComparison.OrdinalIgnoreCase))
            {
                if (WithdrawalReturnIsStored(state))
                {
                    state.Status = "expired";
                    WithdrawalStore.Save(_settingsDir, state);
                    TellPlayer(state.RequestedBy,
                        "Your pickup expired. The reserved item was safely returned to storage.");
                }
                return false;
            }

            if (string.Equals(state.Status, "delivery-confirmed", StringComparison.OrdinalIgnoreCase))
            {
                CompleteWithdrawalAccounting(state);
                return true;
            }

            if ((string.Equals(state.Status, "extracting", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(state.Status, "central-received", StringComparison.OrdinalIgnoreCase)) &&
                FindWithdrawalInventoryItem(state) != null && !Trade.IsTrading)
            {
                OpenPickupWindow(state);
                return true;
            }

            if (string.Equals(state.Status, "central-ready", StringComparison.OrdinalIgnoreCase) &&
                state.PickupExpiresUtc.HasValue && DateTime.UtcNow >= state.PickupExpiresUtc.Value)
            {
                QueueWithdrawalReturn(state);
                return false;
            }

            if (string.Equals(state.Status, "pickup-trading", StringComparison.OrdinalIgnoreCase))
            {
                if (!Trade.IsTrading && !_withdrawalTradeOpened)
                {
                    if (FindWithdrawalInventoryItem(state) == null)
                    {
                        FailWithdrawal(state,
                            "Pickup state survived a restart but the reserved item is not uniquely visible on Central; refusing to guess whether AO delivered it.");
                        return true;
                    }
                    state.Status = "central-ready";
                    WithdrawalStore.Save(_settingsDir, state);
                }
                else
                    TickWithdrawalPickupTrade(state);
            }

            if (string.Equals(state.Status, "failed", StringComparison.OrdinalIgnoreCase))
                return true;

            return true;
        }

        private bool TickWithdrawalWorker()
        {
            WithdrawalState state = WithdrawalStore.Load(_settingsDir);
            _withdrawal = state;
            if (!WithdrawalStore.IsActive(state) ||
                !string.Equals(state.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state.Status, "return-queued", StringComparison.OrdinalIgnoreCase))
            {
                ResetWithdrawalLocal();
                return false;
            }

            if (string.Equals(state.Status, "requested", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(state.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(state.Id) ||
                    _withdrawalRecoveryAttemptedIds.Contains(state.Id) ||
                    Trade.IsTrading)
                {
                    return true;
                }

                _withdrawalRecoveryAttemptedIds.Add(state.Id);
                string priorError = state.Error;
                state.Status = "extracting";
                state.Error =
                    "One-shot worker recovery is verifying physical AO state. Prior failure: " +
                    priorError;
                WithdrawalStore.Save(_settingsDir, state);
                ResetWithdrawalLocal();
                _withdrawal = state;
                Logger.Warning(
                    "[CityBankers] WITHDRAWAL RECOVERY RETRY " + state.Id +
                    ": worker will re-locate the exact reserved item once; prior failure: " +
                    priorError);
            }
            if (!string.Equals(state.Status, "extracting", StringComparison.OrdinalIgnoreCase))
                return string.Equals(state.Status, "failed", StringComparison.OrdinalIgnoreCase);
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
            WithdrawalState state = WithdrawalStore.Load(_settingsDir);
            if (!WithdrawalStore.IsActive(state))
                return false;
            _withdrawal = state;

            if (!_isCentral &&
                string.Equals(state.Status, "extracting", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(state.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(targetName, _centralCharacter, StringComparison.OrdinalIgnoreCase))
            {
                _withdrawalTradeOpened = true;
                _withdrawalTradePartner = target;
                return true;
            }

            if (_isCentral &&
                string.Equals(state.Status, "extracting", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(targetName, state.SourceCharacter, StringComparison.OrdinalIgnoreCase))
            {
                _withdrawalTradeOpened = true;
                _withdrawalTradePartner = target;
                return true;
            }

            if (_isCentral &&
                string.Equals(state.Status, "central-ready", StringComparison.OrdinalIgnoreCase))
            {
                if (!WithdrawalStore.IsAllowedCollector(state, targetName))
                {
                    TellPlayer(targetName, "That reserved pickup belongs to " + state.RecipientMain + ".");
                    Trade.Decline();
                    return true;
                }
                state.Status = "pickup-trading";
                WithdrawalStore.Save(_settingsDir, state);
                _withdrawalPickupTrade = true;
                _withdrawalTradeOpened = true;
                _withdrawalTradePartner = target;
                _withdrawalItemOffered = false;
                _withdrawalAccepted = false;
                return true;
            }
            return false;
        }

        private bool TryHandleWithdrawalTradeStatus(Identity target, TradeStatus status)
        {
            WithdrawalState state = WithdrawalStore.Load(_settingsDir);
            if (!WithdrawalStore.IsActive(state) || !_withdrawalTradeOpened ||
                target != _withdrawalTradePartner)
                return false;

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
                Trade.Confirm();
                return true;
            }
            if (status == TradeStatus.Finished)
            {
                if (_isCentral)
                {
                    if (_withdrawalPickupTrade)
                    {
                        state.DeliveredUtc = DateTime.UtcNow;
                        state.Status = "delivery-confirmed";
                        WithdrawalStore.Save(_settingsDir, state);
                        CompleteWithdrawalAccounting(state);
                    }
                    else
                    {
                        OpenPickupWindow(state);
                    }
                }
                ResetWithdrawalTrade();
                return true;
            }
            if (status == TradeStatus.Declined)
            {
                if (_isCentral && _withdrawalPickupTrade)
                {
                    state.Status = "central-ready";
                    WithdrawalStore.Save(_settingsDir, state);
                }
                else if (!_isCentral)
                {
                    _withdrawalWorkerPhase = WithdrawalWorkerPhase.OpenTrade;
                    SetWithdrawalDeadline();
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
            item.MoveToContainer(DynelManager.LocalPlayer.Identity);
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.WaitItemInventory;
            SetWithdrawalDeadline();
        }

        private void WithdrawalWaitItemInventory()
        {
            Item extracted = FindWithdrawalInventoryItem(_withdrawal);
            if (extracted == null) return;
            Logger.Information(
                "[CityBankers] WITHDRAWAL ITEM OBSERVED " + (_withdrawal?.Id ?? "?") +
                ": exact reserved item is now in " + Client.CharacterName +
                " normal inventory; continuing extraction.");
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
            _withdrawalDeadlineUtc = DateTime.UtcNow.AddSeconds(WithdrawalPhaseTimeoutSeconds);
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
            if (state.PickupExpiresUtc.HasValue && DateTime.UtcNow >= state.PickupExpiresUtc.Value)
            {
                TryDeclineTrade();
                state.Status = "central-ready";
                WithdrawalStore.Save(_settingsDir, state);
                ResetWithdrawalTrade();
                return;
            }
            if (!_withdrawalTradeOpened || !Trade.IsTrading) return;
            if (!_withdrawalItemOffered)
            {
                Item item = FindWithdrawalInventoryItem(state);
                if (item == null)
                {
                    FailWithdrawal(state, "Reserved item disappeared from Central inventory.");
                    return;
                }
                Trade.AddItem(item.Slot);
                _withdrawalItemOffered = true;
                return;
            }
            if (!_withdrawalAccepted)
            {
                List<Item> offered = Trade.PlayerWindowCache?.Items ?? new List<Item>();
                if (offered.Count == 1 && MatchesWithdrawalItem(offered[0], state))
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
            state.Status = "central-ready";
            state.PickupExpiresUtc = DateTime.UtcNow.AddSeconds(WithdrawalStore.PickupSeconds);
            WithdrawalStore.Save(_settingsDir, state);
            TellPlayer(state.RequestedBy,
                (state.Item?.Name ?? "Your item") + " is ready on Kbcentral. " +
                "Open trade within three minutes; no ledger item is removed until AO finishes the trade.");
        }

        private void CompleteWithdrawalAccounting(WithdrawalState state)
        {
            if (!ActiveLedgerStore.ArchiveActiveItemById(
                    _settingsDir, state.ActiveLedgerId,
                    state.DeliveredUtc ?? DateTime.UtcNow,
                    "withdrawn", state.RecipientMain))
            {
                FailWithdrawal(state, "AO delivered the item, but active-ledger archival failed.");
                return;
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
            string batchId = "withdraw-return-" + Guid.NewGuid().ToString("N");
            queue.Batches.Add(new DispatchBatchState
            {
                BatchId = batchId,
                TransactionId = state.DonationTransactionId,
                Role = state.SourceRole,
                Character = state.SourceCharacter,
                Status = "queued",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Items = new List<TransferItemState> { state.Item }
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
            return (RuntimeStateStore.LoadCurrentStock(_settingsDir).Items ?? new List<StockItemState>())
                .Any(item => item != null && item.AoId == state.Item.AoId &&
                    string.Equals(item.TransactionId, state.DonationTransactionId, StringComparison.Ordinal) &&
                    string.Equals(item.Character, state.SourceCharacter, StringComparison.OrdinalIgnoreCase));
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

        private static Item FindWithdrawalInventoryItem(WithdrawalState state)
        {
            List<Item> inventory = (Inventory.Items ?? new List<Item>()).Where(item =>
                item != null && item.Slot.Type == IdentityType.Inventory &&
                item.UniqueIdentity.Type != IdentityType.Container).ToList();

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

            List<Item> matches = inventory.Where(item => MatchesWithdrawalItem(item, state)).ToList();
            return matches.Count == 1 ? matches[0] : null;
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
        }

        private void ResetWithdrawalLocal()
        {
            _withdrawal = null;
            _withdrawalWorkerPhase = WithdrawalWorkerPhase.None;
            _withdrawalBag = null;
            _withdrawalBagIdentity = null;
            _withdrawalBankBag = false;
            ResetWithdrawalTrade();
        }
    }
}
