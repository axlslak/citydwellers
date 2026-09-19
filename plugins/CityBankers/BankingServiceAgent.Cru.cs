using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class StackOperation
        {
            public string RequestId;
            public Item Source;
            public Item Target;
            public int Total;
            public int SourceCount;
            public int TargetCount;
            public List<Item> Before;
            public readonly Stopwatch Age = Stopwatch.StartNew();
        }
        private StackOperation _stackOperation;
        private string _lastStackFailure;
        private readonly Stopwatch _stackFailureAge = Stopwatch.StartNew();
        private bool _cruRecovered;
        private bool _automaticCruStackingEnabled;
        // Only verified completion clears this latch. Actor readmission must not
        // retry an uncertain split or merge against potentially stale quantities.
        private static bool _cruPreparationUnverified;
        private static readonly Stopwatch _cruMergeIdleAge = Stopwatch.StartNew();

        private List<Item> CruInventory() => (Inventory.Items ?? new List<Item>()).Where(i =>
            StackableItems.IsStack(i) && i.Slot.Type == IdentityType.Inventory && StackableItems.Quantity(i) != 0).ToList();

        private bool HandleCruProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "cru") return false;
            if (!_isCentral || !StartupCensusGate.IsOpen || !Client.InPlay || Trade.IsTrading ||
                _reserveOperation != null || _stackOperation != null || _receipt != null || _donationActive || _activeBatch != null ||
                _extraction != null || _returnOffer != null)
            { proposal.Reply.TrySetResult("Central is busy. Please try #cru again shortly."); return true; }
            if (_cruPreparationUnverified)
            { proposal.Reply.TrySetResult("CRU preparation is paused after an unverified stack operation. Please contact the bot owner."); return true; }
            StackableItems.Flush();
            var inventory = CruInventory();
            if (inventory.Any(i => StackableItems.Quantity(i) < 1))
            { proposal.Reply.TrySetResult("Central is waiting for the CRU stack quantity. Please try again shortly."); return true; }
            var request = proposal.CruRequest;
            if (request == null || string.IsNullOrWhiteSpace(request.RequestedBy) ||
                string.IsNullOrWhiteSpace(request.RecipientMain) || request.AllowedCharacters == null ||
                request.AllowedCharacters.Count == 0)
            { proposal.Reply.TrySetResult("CRU request is incomplete."); return true; }
            // An IPC reply may be lost after admission. The request ID makes retry idempotent.
            if (WithdrawalStore.LoadAll(_settingsDir).Any(r => r.Id == request.Id))
            { proposal.Reply.TrySetResult("CRU is already in your pickup order. Wait for the ready tell."); return true; }
            var template = inventory.FirstOrDefault();
            if (template == null)
            { proposal.Reply.TrySetResult("No CRU is available right now."); return true; }
            request.Status = "requested";
            request.SourceRole = "central";
            request.SourceCharacter = Client.CharacterName;
            request.SourceBag = "inventory";
            request.ActiveLedgerId = "cru-" + request.Id;
            request.DonationTransactionId = null;
            request.Item = new TransferItemState { AoId = template.Id, HighId = template.HighId,
                Ql = template.Ql, Name = template.Name, Quantity = 1 };
            string error;
            bool added = WithdrawalStore.TryAdd(_settingsDir, request, out error,
                inventory.Sum(StackableItems.Quantity));
            proposal.Reply.TrySetResult(added ? "One CRU added to your pickup order. Wait for the ready tell, then trade with " +
                Client.CharacterName + " within three minutes." : error);
            return true;
        }

        private bool TickCru()
        {
            try { return TickCruCore(); }
            catch (Exception ex)
            {
                Logger.Warning("[CityBankers] CRU operation interrupted: " + ex.Message);
                // Keep an issued operation until its normal observation timeout.
                // Never replay a split just because a subsequent write failed.
                return _stackOperation != null;
            }
        }

        private void ReleaseDisputedCru(string reason)
        {
            if (_receipt == null) return;
            WithdrawalStore.Update(_settingsDir, rows =>
            {
                foreach (var row in rows.Where(r => CruPolicy.IsCru(r) && WithdrawalStore.IsActive(r) &&
                    !WithdrawalStore.HasConfirmedDelivery(r) && (r.Id == _receipt.BatchId || r.OrderId == _receipt.BatchId)))
                {
                    row.Status = "reconciled"; row.Error = reason; WithdrawalStore.Touch(row);
                    _centralWithdrawalItems.Remove(row.Id);
                    Logger.Warning("[CityBankers] CRU pickup unconfirmed; request released without recording delivery: " + row.Id);
                }
                return true;
            });
        }

        private bool TickCruCore()
        {
            if (!_isCentral) return false;
            StackableItems.Flush();
            if (!_cruRecovered)
            {
                // A new actor cannot reuse live Item references. Release unconfirmed
                // CRU requests; the inventory remains the supply, with no loss inference.
                WithdrawalStore.Update(_settingsDir, rows =>
                {
                    foreach (var row in rows.Where(r => CruPolicy.IsCru(r) && WithdrawalStore.IsActive(r) &&
                        !WithdrawalStore.HasConfirmedDelivery(r) && r.Status != "requested"))
                    { row.Status = "reconciled"; row.Error = "CRU pickup interrupted; please request again."; WithdrawalStore.Touch(row); }
                    return true;
                });
                _automaticCruStackingEnabled = (bool?)SettingsPaths.ReadBankersSettings(
                    _settingsDir)["EnableAutomaticCruStacking"] ?? false;
                if (!_automaticCruStackingEnabled)
                    Logger.Information("[CityBankers] Automatic CRU merging paused; donations and requested pickups remain enabled.");
                _cruRecovered = true;
            }
            if (_stackOperation != null) return TickStackOperation();
            if (Trade.IsTrading || _receipt != null || _donationActive || _donationCleanup != null ||
                _activeBatch != null || _extraction != null || _returnOffer != null || _withdrawalTradeOpened ||
                _dispatchPreparation != null || _reservedDispatch != null ||
                WithdrawalStore.LoadAll(_settingsDir).Any(WithdrawalStore.OwnsCentralTrade)) return false;
            var rowsNow = WithdrawalStore.LoadAll(_settingsDir);
            var active = rowsNow.Where(r => CruPolicy.IsCru(r) && WithdrawalStore.IsActive(r)).ToList();
            foreach (var row in active.Where(r => !WithdrawalStore.HasConfirmedDelivery(r) &&
                (r.Status == "returning" || r.Status == "failed" ||
                 (r.Status == "central-ready" && !WithdrawalStore.PickupWindowOpen(r)))))
            {
                _centralWithdrawalItems.Remove(row.Id);
                row.Status = "expired";
                WithdrawalStore.Save(_settingsDir, row);
                TellPlayer(row.RequestedBy, "Your CRU pickup expired. It stays on Central; use #cru when you need it.");
            }
            var liveIds = new HashSet<string>(WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).Select(r => r.Id));
            foreach (string id in _centralWithdrawalItems.Keys.Where(id => !liveIds.Contains(id)).ToList())
                _centralWithdrawalItems.Remove(id);
            var available = CruInventory().Where(i => !_centralWithdrawalItems.Values.Any(v => ReferenceEquals(v, i))).ToList();
            if (available.Any(i => StackableItems.Quantity(i) < 1)) return false;
            string signature = string.Join(";", available.Select(i => i.Slot + "/" + StackableItems.Quantity(i)));
            if (_lastStackFailure == signature && _stackFailureAge.Elapsed.TotalSeconds < 60) return false;
            var next = active.FirstOrDefault(r => r.Status == "requested");
            if (_cruPreparationUnverified) return false;
            if (next != null)
            {
                Item single = available.FirstOrDefault(i => StackableItems.Quantity(i) == 1);
                if (single != null) { ReadyCru(next.Id, single); return true; }
                Item stack = available.FirstOrDefault(i => StackableItems.Quantity(i) > 1);
                if (stack == null)
                {
                    next.Status = "expired"; WithdrawalStore.Save(_settingsDir, next);
                    TellPlayer(next.RequestedBy, "No CRU is available right now."); return false;
                }
                if (Inventory.NumFreeSlots == 0) return false;
                _stackOperation = new StackOperation { RequestId = next.Id, Source = stack,
                    SourceCount = StackableItems.Quantity(stack), Total = CruInventory().Sum(StackableItems.Quantity),
                    Before = CruInventory() };
                Logger.Information("[CityBankers] STACK split one CRU request=" + next.Id + "; source=" + stack.Slot +
                    "; quantity=" + _stackOperation.SourceCount);
                _cruPreparationUnverified = true;
                StackableItems.Split(stack, 1);
                return true;
            }
            // Yield between successful merges so normal banking can start. Explicit
            // pickups above have priority and their reserved units remain separate.
            if (!_automaticCruStackingEnabled || _cruMergeIdleAge.Elapsed.TotalSeconds < 2 || available.Count < 2) return false;
            Func<Item, Item, bool> canMerge = (a, b) => !ReferenceEquals(a, b) &&
                a.Id == b.Id && a.HighId == b.HighId && a.Ql == b.Ql &&
                (long)StackableItems.Quantity(a) + StackableItems.Quantity(b) <= ushort.MaxValue;
            Item target = available.OrderByDescending(StackableItems.Quantity)
                .FirstOrDefault(candidate => available.Any(other => canMerge(candidate, other)));
            if (target == null) return false;
            Item source = available.FirstOrDefault(i => canMerge(i, target));
            if (source == null) return false;
            _stackOperation = new StackOperation { Source = source, Target = target,
                SourceCount = StackableItems.Quantity(source), TargetCount = StackableItems.Quantity(target),
                Total = CruInventory().Sum(StackableItems.Quantity), Before = CruInventory() };
            Logger.Information("[CityBankers] STACK merge CRU via action53 survivor=" + source.Slot + "; consumed=" + target.Slot +
                "; quantities=" + _stackOperation.SourceCount + "+" + _stackOperation.TargetCount);
            // Sending alone never authorizes another merge. A timeout, exception
            // or actor teardown leaves preparation paused until a runtime restart.
            _cruPreparationUnverified = true;
            StackableItems.Merge(source, target);
            return true;
        }

        private bool TickStackOperation()
        {
            var op = _stackOperation;
            var current = CruInventory();
            bool known = current.All(i => StackableItems.Quantity(i) > 0);
            bool totalMatches = known && current.Sum(StackableItems.Quantity) == op.Total;
            Item split = op.Target == null ? current.FirstOrDefault(i => !op.Before.Contains(i) && StackableItems.Quantity(i) == 1) : null;
            bool done = op.Target == null
                ? totalMatches && split != null && current.Contains(op.Source) && StackableItems.Quantity(op.Source) == op.SourceCount - 1
                : totalMatches && current.Contains(op.Source) && !current.Contains(op.Target) &&
                    StackableItems.Quantity(op.Source) == op.SourceCount + op.TargetCount;
            if (done && op.Age.ElapsedMilliseconds >= 500)
            {
                Logger.Information("[CityBankers] STACK " + (op.Target == null ? "split" : "merge") + " verified; CRU units=" + op.Total);
                if (split != null) ReadyCru(op.RequestId, split);
                if (op.Target != null)
                {
                    StackableItems.CancelMerge(op.Source, op.Target);
                    _cruMergeIdleAge.Restart();
                }
                _cruPreparationUnverified = false;
                _stackOperation = null;
                return true;
            }
            if (op.Age.Elapsed.TotalSeconds < 10) return true;
            _lastStackFailure = string.Join(";", current.Where(i => !_centralWithdrawalItems.Values.Contains(i))
                .Select(i => i.Slot + "/" + StackableItems.Quantity(i)));
            _stackFailureAge.Restart();
            if (op.Target != null) StackableItems.CancelMerge(op.Source, op.Target);
            Logger.Warning("[CityBankers] STACK operation not verified; no quantity inferred. Inventory=" + _lastStackFailure);
            Logger.Warning("[CityBankers] CRU merging and new CRU preparation paused until restart; other banking remains available.");
            if (op.RequestId != null)
            {
                WithdrawalStore.Update(_settingsDir, rows =>
                {
                    var row = rows.Single(r => r.Id == op.RequestId);
                    row.Status = "expired"; row.Error = "CRU split was not verified."; WithdrawalStore.Touch(row); return true;
                });
                var expiredRequest = WithdrawalStore.LoadAll(_settingsDir).Single(r => r.Id == op.RequestId);
                TellPlayer(expiredRequest.RequestedBy, "Central could not verify the CRU split. CRU preparation is paused; please contact the bot owner.");
            }
            _stackOperation = null;
            return true;
        }

        private void ReadyCru(string requestId, Item item)
        {
            if (StackableItems.Quantity(item) != 1) throw new InvalidOperationException("CRU pickup requires exactly one unit.");
            _centralWithdrawalItems[requestId] = item;
            string requester = null;
            WithdrawalStore.Update(_settingsDir, rows =>
            {
                var row = rows.Single(r => r.Id == requestId);
                if (row.Status != "requested") return false;
                row.Status = "central-ready";
                row.CentralItemIdentity = item.UniqueIdentity.ToString();
                row.SourceInnerSlot = item.Slot.Instance;
                row.Item.UniqueIdentity = item.UniqueIdentity.ToString();
                requester = row.RequestedBy;
                foreach (var ready in rows.Where(r => r.OrderId == row.OrderId && r.Status == "central-ready"))
                { WithdrawalStore.RenewPickupWindow(ready); WithdrawalStore.Touch(ready); }
                return true;
            });
            if (requester != null) TellDirectPlayer(requester,
                "One CRU is ready on " + Client.CharacterName + ". Open trade within three minutes to collect your ready items.");
        }
    }
}
