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
        private bool _cruMergeAttempted;

        private List<Item> CruInventory() => (Inventory.Items ?? new List<Item>()).Where(i =>
            StackableItems.IsStack(i) && i.Slot.Type == IdentityType.Inventory && StackableItems.Quantity(i) != 0).ToList();

        private bool HandleCruProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "cru") return false;
            if (!_isCentral || !StartupCensusGate.IsOpen || !Client.InPlay || Trade.IsTrading ||
                _stackOperation != null || _receipt != null || _donationActive || _activeBatch != null ||
                _extraction != null || _returnOffer != null)
            { proposal.Reply.TrySetResult("Central is busy. Please try #cru again shortly."); return true; }
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
                StackableItems.Split(stack, 1);
                return true;
            }
            // Unverified background merges must not repeatedly acquire the shared
            // operation guard and interrupt otherwise unrelated banking work.
            // Explicit requests above still use singles or perform a requested split.
            if (!_automaticCruStackingEnabled || _cruMergeAttempted || available.Count < 2) return false;
            Item target = available.OrderByDescending(StackableItems.Quantity).First();
            Item source = available.FirstOrDefault(i => !ReferenceEquals(i, target) && i.Id == target.Id &&
                i.HighId == target.HighId && i.Ql == target.Ql);
            if (source == null) return false;
            _stackOperation = new StackOperation { Source = source, Target = target,
                SourceCount = StackableItems.Quantity(source), TargetCount = StackableItems.Quantity(target),
                Total = CruInventory().Sum(StackableItems.Quantity), Before = CruInventory() };
            Logger.Information("[CityBankers] STACK merge CRU via action53 source=" + source.Slot + "; target=" + target.Slot +
                "; quantities=" + _stackOperation.SourceCount + "+" + _stackOperation.TargetCount);
            // One diagnostic attempt per process: no background retry loop while
            // the action-53 response/cache semantics are being established.
            _cruMergeAttempted = true;
            Logger.Information("[CityBankers] STACK diagnostic merge attempt; no further automatic merges until restart.");
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
                : totalMatches && !current.Contains(op.Source) && current.Contains(op.Target) &&
                    StackableItems.Quantity(op.Target) == op.SourceCount + op.TargetCount;
            if (done && op.Age.ElapsedMilliseconds >= 500)
            {
                Logger.Information("[CityBankers] STACK " + (op.Target == null ? "split" : "merge") + " verified; CRU units=" + op.Total);
                if (split != null) ReadyCru(op.RequestId, split);
                _stackOperation = null;
                return true;
            }
            if (op.Age.Elapsed.TotalSeconds < 10) return true;
            _lastStackFailure = string.Join(";", current.Where(i => !_centralWithdrawalItems.Values.Contains(i))
                .Select(i => i.Slot + "/" + StackableItems.Quantity(i)));
            _stackFailureAge.Restart();
            Logger.Warning("[CityBankers] STACK operation not verified; no quantity inferred. Inventory=" + _lastStackFailure);
            if (op.RequestId != null)
            {
                WithdrawalStore.Update(_settingsDir, rows =>
                {
                    var row = rows.Single(r => r.Id == op.RequestId);
                    row.Status = "expired"; row.Error = "CRU split was not verified."; WithdrawalStore.Touch(row); return true;
                });
                var expiredRequest = WithdrawalStore.LoadAll(_settingsDir).Single(r => r.Id == op.RequestId);
                TellPlayer(expiredRequest.RequestedBy, "Central could not finish splitting your CRU. Please try #cru again shortly.");
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
