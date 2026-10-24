using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using CityDwellers.Shared;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        // Fund a working balance once, rather than trade each purchased bag.
        private const int BagFundingBalance = 1000000;
        private sealed class BagFundingWork
        {
            public ReceiptEvidence Receipt;
            public Identity Peer = Identity.None;
            public Task<string> Request;
            public bool Restored, Opened, Offered, Accepted, PeerAccepted, Confirmed;
            public readonly Stopwatch Age = Stopwatch.StartNew();
            public DateTime RetryAfter;
        }
        private BagFundingWork _bagFunding;
        private bool _bagFundingChecked, _bagDealerRequested;
        private Task<string> _bagDealerRequest;
        private DateTime _bagDealerRequestAfter, _bagDealerSummonAfter;
        private readonly Dictionary<string, ReceiptEvidence> _bagFundingRequests =
            new Dictionary<string, ReceiptEvidence>(StringComparer.OrdinalIgnoreCase);
        private bool BagFundingPending => _bagFunding != null || _bagFundingRequests.Count != 0 || _bagDealerRequested;
        private bool BagFundingReservesTrade => _bagFunding != null &&
            (_bagFunding.Receipt.Phase == "preparing" || _bagFunding.Receipt.Phase == "prepared" ||
             _bagFunding.Receipt.Phase == "opening" ||
             (_bagFunding.Opened && Trade.IsTrading && Trade.CurrentTarget == _bagFunding.Peer));
        private static string BagFundingId(string character) => "bag-funding:" + character.ToLowerInvariant();
        private static bool IsBagFundingReceipt(ReceiptEvidence receipt) =>
            receipt?.Kind == "bag-funding-send" || receipt?.Kind == "bag-funding-receive";
        private static bool BagFundingTerminal(ReceiptEvidence receipt) =>
            receipt.Phase == "applied" || receipt.Phase == "cancelled" || receipt.Phase == "closed-unknown";

        private bool YieldUnopenedBagFunding()
        {
            var work = _bagFunding;
            if (!BagFundingReservesTrade) return true;
            if (work.Receipt.Phase == "preparing" || work.Receipt.Phase == "prepared")
                return CancelUnopenedBagFunding(work.Receipt);
            return false; // The native Open has already been sent.
        }

        private void InvalidateBagFunding()
        {
            _bagFunding = null; _bagFundingChecked = false;
            _bagDealerRequest = null; _bagDealerRequested = false; _bagFundingRequests.Clear();
        }

        private bool SaveBagFunding(ReceiptEvidence receipt)
        {
            return ManagerAccounting.Transaction("Retain banker credit transfer", () =>
            {
                var current = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, receipt.Id);
                if (current?.TransactionId == receipt.TransactionId && BagFundingTerminal(current)) return false;
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, receipt);
                return true;
            });
        }

        private void RequestBagFunding()
        {
            if (_isCentral)
            { BagShopUnavailable("Central has insufficient credits to buy its own backpacks."); return; }
            if (_bagFunding != null) return;
            int cash;
            if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash) || cash >= BagFundingBalance) return;
            var receipt = new ReceiptEvidence { Id = BagFundingId(Client.CharacterName), Kind = "bag-funding-receive",
                Character = Client.CharacterName, PeerCharacter = _centralCharacter, Direction = 1,
                TransactionId = Guid.NewGuid().ToString("N"), Phase = "requested", CreditBefore = cash,
                CreditAmount = BagFundingBalance - cash };
            SaveBagFunding(receipt);
            _bagFunding = new BagFundingWork { Receipt = receipt };
            BankerActivityGovernor.Wake();
        }

        private void RequestBagDealer()
        {
            if (_isCentral) { _bagDealerRequested = true; return; }
            if (DateTime.UtcNow >= _bagDealerRequestAfter && (_bagDealerRequest == null || _bagDealerRequest.IsCompleted))
            {
                _bagDealerRequest = SendBankerMemory(_centralCharacter, new DispatchProposal { Kind = "bag-dealer" });
                _bagDealerRequestAfter = DateTime.UtcNow.AddSeconds(2);
            }
        }

        private void TickBagDealer()
        {
            if (!_isCentral || !_bagDealerRequested || Trade.IsTrading || BagSupplyServiceWaiting()) return;
            var ready = BankerReadiness.GetReadyCharacters(_settingsDir);
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            bool needed = _config.Roles.Values.Any(account => !SameCharacter(account.Character, _centralCharacter) &&
                ready.Contains(account.Character) && storage.Workers.Any(worker => SameCharacter(worker.Character, account.Character) &&
                    worker.Bags.Select(bag => bag.LastUniqueIdentity).Distinct().Count() < StorageBankBagTarget));
            if (!needed) { _bagDealerRequested = false; return; }
            if (FindBagDealer() != null) { _bagDealerRequested = false; return; }
            if (DynelManager.LocalPlayer.IsCasting || DateTime.UtcNow < _bagDealerSummonAfter) return;
            if (!(DynelManager.LocalPlayer.SpellList ?? new int[0]).Contains(SummonBagDealerNano)) return;
            DynelManager.LocalPlayer.Cast(SummonBagDealerNano);
            _bagDealerSummonAfter = DateTime.UtcNow.AddSeconds(10);
            Logger.Information("[CityBankers] BAG SHOP summoning dealer for nearby bankers; service continues.");
        }

        private bool HandleBagFundingProposal(DispatchProposal proposal)
        {
            if (proposal.Kind == "bag-dealer")
            {
                if (_isCentral) _bagDealerRequested = true;
                proposal.Reply.TrySetResult(_isCentral ? "queued" : "not-central"); return true;
            }
            if (proposal.Kind == "bag-funding-request")
            {
                var request = proposal.Funding;
                if (_isCentral && request?.Kind == "bag-funding-receive" && request.Phase == "requested" &&
                    SameCharacter(request.PeerCharacter, Client.CharacterName) && request.CreditBefore.HasValue &&
                    request.CreditBefore >= 0 && request.CreditBefore < BagFundingBalance &&
                    _config.Roles.Values.Any(a => SameCharacter(a.Character, request.Character) && !SameCharacter(a.Character, Client.CharacterName)))
                {
                    _bagFundingRequests[request.Character] = request;
                    proposal.Reply.TrySetResult("queued");
                }
                else proposal.Reply.TrySetResult("invalid");
                return true;
            }
            if (proposal.Kind != "bag-funding-prepare") return false;
            var offer = proposal.Funding;
            var work = _bagFunding;
            if (_isCentral || work == null || offer?.Kind != "bag-funding-send" ||
                offer.TransactionId != work.Receipt.TransactionId || !SameCharacter(offer.Character, _centralCharacter) ||
                !SameCharacter(offer.PeerCharacter, Client.CharacterName) || !offer.CreditAmount.HasValue || offer.CreditAmount <= 0)
            { proposal.Reply.TrySetResult("invalid"); return true; }
            var retained = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, work.Receipt.Id);
            var senderReceipt = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, BagFundingId(_centralCharacter));
            if (retained?.TransactionId != offer.TransactionId || BagFundingTerminal(retained) ||
                senderReceipt?.TransactionId != offer.TransactionId || senderReceipt.Phase != "preparing")
            { proposal.Reply.TrySetResult("invalid"); return true; }
            YieldUnpaidBagShop();
            if (!CanStartLocalCensus() || BagSupplyServiceWaiting())
            { proposal.Reply.TrySetResult("pending"); return true; }
            int cash;
            if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash) || (long)cash + offer.CreditAmount.Value > BagFundingBalance)
            { proposal.Reply.TrySetResult("pending"); return true; }
            if (work.Receipt.Phase == "requested")
            {
                if (!BagShopSupplyPending())
                {
                    work.Receipt.Phase = "cancelled";
                    SaveBagFunding(work.Receipt); _bagFunding = null;
                    return false;
                }
                work.Receipt.CreditAmount = offer.CreditAmount;
                work.Receipt.CreditBefore = cash;
                work.Receipt.Phase = "prepared";
                SaveBagFunding(work.Receipt);
                work.Age.Restart();
            }
            proposal.Reply.TrySetResult(work.Receipt.Phase == "prepared" ? "ready:" + offer.TransactionId : "pending");
            return true;
        }

        private bool StartBagFunding()
        {
            if (!_isCentral || _bagFunding != null || !CanStartLocalCensus() || BagSupplyServiceWaiting()) return false;
            int cash;
            if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash) || cash <= 0) return false;
            foreach (var request in _bagFundingRequests.Values.ToList())
            {
                var current = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, request.Id);
                if (current?.TransactionId != request.TransactionId || current.Phase != "requested")
                { _bagFundingRequests.Remove(request.Character); continue; }
                var peer = DynelManager.Players.FirstOrDefault(p => p != null && SameCharacter(p.Name, request.Character));
                if (peer == null) continue;
                int amount = Math.Min(cash, BagFundingBalance - current.CreditBefore.Value);
                var receipt = new ReceiptEvidence { Id = BagFundingId(Client.CharacterName), Kind = "bag-funding-send",
                    Character = Client.CharacterName, PeerCharacter = request.Character, Direction = -1,
                    TransactionId = request.TransactionId, Phase = "preparing", CreditAmount = amount, CreditBefore = cash };
                SaveBagFunding(receipt);
                _bagFunding = new BagFundingWork { Receipt = receipt, Peer = peer.Identity };
                _bagFundingRequests.Remove(request.Character);
                return true;
            }
            return false;
        }

        private bool BagFundingTradeOpened(Identity target)
        {
            var work = _bagFunding;
            if (work == null || work.Receipt.Phase == "requested") return false;
            var peer = DynelManager.Players.FirstOrDefault(p => p != null && p.Identity == target && SameCharacter(p.Name, work.Receipt.PeerCharacter));
            if (peer == null) return false;
            if (work.Restored || (work.Receipt.Phase != "opening" && work.Receipt.Phase != "prepared")) return false;
            if (_receipt != null || _afterReceipt != null || _activeBatch != null || _workerCommand != null ||
                _reservedDispatch != null || _storageJob != null || _returnOffer != null || _withdrawal != null ||
                _bankLayout != null || _reserveOperation != null || _stackOperation != null || _extraction != null) return false;
            work.Peer = target; work.Opened = true; work.Receipt.Phase = "trading";
            work.Age.Restart(); SaveBagFunding(work.Receipt);
            return true;
        }

        private bool BagFundingTradeStatus(Identity target, TradeStatus status)
        {
            var work = _bagFunding;
            if (work == null || !work.Opened || target != work.Peer) return false;
            if (status == TradeStatus.Accept || status == TradeStatus.Confirm) work.PeerAccepted = true;
            if (status == TradeStatus.Finished || status == TradeStatus.Declined)
            {
                work.Receipt.Phase = status == TradeStatus.Finished ? "finished" : "declined";
                SaveBagFunding(work.Receipt); work.Age.Restart();
            }
            BankerActivityGovernor.Wake();
            return true;
        }

        private bool TickBagFunding()
        {
            if (!_bagFundingChecked)
            {
                _bagFundingChecked = true;
                var saved = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, BagFundingId(Client.CharacterName));
                if (IsBagFundingReceipt(saved) && !BagFundingTerminal(saved))
                    _bagFunding = new BagFundingWork { Receipt = saved, Restored = saved.Phase != "requested" };
            }
            var work = _bagFunding;
            if (work == null) return false;
            var current = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, work.Receipt.Id);
            if (current?.TransactionId != work.Receipt.TransactionId || BagFundingTerminal(current))
            { _bagFunding = null; return false; }
            work.Receipt = current;
            if (work.Receipt.Phase == "requested")
            {
                if (DateTime.UtcNow >= work.RetryAfter && (work.Request == null || work.Request.IsCompleted))
                {
                    work.Request = SendBankerMemory(_centralCharacter,
                        new DispatchProposal { Kind = "bag-funding-request", Funding = work.Receipt });
                    work.RetryAfter = DateTime.UtcNow.AddSeconds(2);
                }
                return false;
            }
            if (work.Restored || work.Receipt.Phase == "finished" || work.Receipt.Phase == "declined" ||
                work.Receipt.Phase == "observed" || work.Receipt.Phase == "recovered" || work.Receipt.Phase == "refresh-required")
            {
                if (Trade.IsTrading) return false;
                if (work.Receipt.Phase == "refresh-required" && !work.Restored)
                {
                    StartupCensusGate.Block("Refresh native balances for unresolved banker funding " + work.Receipt.TransactionId + ".");
                    return false;
                }
                int cash;
                if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash)) return true;
                long expected = (long)work.Receipt.CreditBefore.Value + work.Receipt.Direction * work.Receipt.CreditAmount.Value;
                if (work.Receipt.Phase == "observed" || work.Receipt.Phase == "recovered")
                    ResolveBagFunding(work.Receipt);
                else if (work.Restored || cash == expected || (work.Receipt.Phase == "declined" && cash == work.Receipt.CreditBefore))
                {
                    work.Receipt.CreditAfter = cash;
                    work.Receipt.Phase = work.Restored ? "recovered" : "observed";
                    SaveBagFunding(work.Receipt); work.Restored = false;
                    ResolveBagFunding(work.Receipt);
                }
                else if (work.Age.Elapsed.TotalSeconds > ServicePolicy.TradeTimeoutSeconds)
                    StartupCensusGate.Block("Refresh native credit balance for interrupted banker funding " + work.Receipt.TransactionId + ".");
                return false; // Item service continues; only further cash work waits.
            }
            if (_isCentral && work.Receipt.Phase == "preparing")
            {
                if (BagSupplyServiceWaiting()) { CancelUnopenedBagFunding(work.Receipt); return false; }
                if (work.Age.Elapsed.TotalSeconds > ServicePolicy.TradeTimeoutSeconds)
                { CancelUnopenedBagFunding(work.Receipt); return false; }
                if (work.Request == null)
                {
                    work.Request = SendBankerMemory(work.Receipt.PeerCharacter,
                        new DispatchProposal { Kind = "bag-funding-prepare", Funding = work.Receipt });
                    return true;
                }
                if (!work.Request.IsCompleted) return true;
                bool ready = work.Request.Status == TaskStatus.RanToCompletion && work.Request.Result == "ready:" + work.Receipt.TransactionId;
                work.Request = null;
                if (!ready || !CanStartLocalCensus() || BagSupplyServiceWaiting()) return true;
                work.Receipt.Phase = "opening";
                if (!SaveBagFunding(work.Receipt)) { _bagFunding = null; return false; }
                work.Age.Restart();
                Trade.Open(work.Peer);
                return true;
            }
            if (work.Age.Elapsed.TotalSeconds > ServicePolicy.TradeTimeoutSeconds)
            {
                if (work.Receipt.Phase == "prepared" && CancelUnopenedBagFunding(work.Receipt)) return false;
                if (Trade.IsTrading && Trade.CurrentTarget == work.Peer) Trade.Decline();
                StartupCensusGate.Block("Resolve interrupted banker credit transfer " + work.Receipt.TransactionId + " from fresh native balances.");
                return true;
            }
            if (!work.Opened || Trade.CurrentTarget != work.Peer) return work.Receipt.Phase == "prepared" || work.Receipt.Phase == "opening";
            var local = Trade.PlayerWindowCache;
            var remote = Trade.TargetWindowCache;
            if (local.Items.Count != 0 || remote.Items.Count != 0)
            { Trade.Decline(); return true; }
            int amountExpected = work.Receipt.CreditAmount.Value;
            if (_isCentral && !work.Offered)
            {
                work.Offered = true;
                Client.Send(new TradeMessage { Unknown1 = 2, Action = TradeAction.UpdateCredits, Param2 = amountExpected });
                return true;
            }
            int offered = work.Receipt.Direction < 0 ? local.Credits : remote.Credits;
            int other = work.Receipt.Direction < 0 ? remote.Credits : local.Credits;
            if (other != 0 || (offered != 0 && offered != amountExpected)) { Trade.Decline(); return true; }
            if (offered != amountExpected) return true;
            if (!work.Accepted) { work.Accepted = true; Trade.Accept(); }
            if (work.PeerAccepted && !work.Confirmed) { work.Confirmed = true; Trade.Confirm(); }
            return true;
        }

        private bool CancelUnopenedBagFunding(ReceiptEvidence receipt)
        {
            bool cancelled = ManagerAccounting.Transaction("Cancel unopened credit transfer", () =>
            {
                var sender = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId,
                    BagFundingId(receipt.Direction < 0 ? receipt.Character : receipt.PeerCharacter));
                if (sender?.TransactionId != receipt.TransactionId || sender.Phase != "preparing") return false;
                foreach (var name in new[] { receipt.Character, receipt.PeerCharacter })
                {
                    var row = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, BagFundingId(name));
                    if (row?.TransactionId != receipt.TransactionId || (row.Phase != "preparing" && row.Phase != "prepared" && row.Phase != "requested")) continue;
                    row.Phase = "cancelled"; ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, row);
                }
                return true;
            });
            if (cancelled) _bagFunding = null;
            return cancelled;
        }

        private void ResolveBagFunding(ReceiptEvidence receipt)
        {
            ManagerAccounting.Transaction("Resolve banker credit transfer", () =>
            {
                var peer = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, BagFundingId(receipt.PeerCharacter));
                if (peer?.TransactionId != receipt.TransactionId)
                {
                    if (receipt.Phase == "recovered" && receipt.CreditAfter == receipt.CreditBefore)
                    { receipt.Phase = "cancelled"; ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, receipt); }
                    return;
                }
                // No window can have opened before the receiving banker left
                // requested. A fresh unchanged sender balance ends that attempt.
                if (receipt.Phase == "recovered" && receipt.CreditAfter == receipt.CreditBefore &&
                    (peer.Phase == "requested" || peer.Phase == "cancelled"))
                {
                    receipt.Phase = peer.Phase = "cancelled";
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, receipt);
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, peer);
                    return;
                }
                if (!peer.CreditAfter.HasValue || !receipt.CreditAfter.HasValue) return;
                bool balanced = receipt.CreditAmount == peer.CreditAmount && receipt.Direction == -peer.Direction &&
                    receipt.CreditAfter.Value == (long)receipt.CreditBefore.Value + receipt.Direction * receipt.CreditAmount.Value &&
                    peer.CreditAfter.Value == (long)peer.CreditBefore.Value + peer.Direction * peer.CreditAmount.Value;
                bool unchanged = receipt.CreditAfter == receipt.CreditBefore && peer.CreditAfter == peer.CreditBefore;
                bool freshPair = receipt.Phase == "recovered" && peer.Phase == "recovered";
                if (!balanced && !unchanged && !freshPair &&
                    (receipt.Phase == "recovered" || peer.Phase == "recovered")) return;
                string phase = balanced ? "applied" : unchanged ? "cancelled" : freshPair ? "closed-unknown" : "refresh-required";
                if (phase == "closed-unknown")
                {
                    // Retain the ambiguous attempt permanently. Future funding
                    // uses the fresh balance and a new capped top-up, never a
                    // replay of this amount or a claim that this transfer worked.
                    foreach (var row in new[] { receipt, peer })
                        ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, new ReceiptEvidence {
                            Id = row.Id + ":" + row.TransactionId, Kind = row.Kind, Character = row.Character,
                            PeerCharacter = row.PeerCharacter, TransactionId = row.TransactionId,
                            Direction = row.Direction, CreditAmount = row.CreditAmount, CreditBefore = row.CreditBefore,
                            CreditAfter = row.CreditAfter, Phase = "attribution-unknown" });
                }
                receipt.Phase = peer.Phase = phase;
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, receipt);
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, peer);
                if (phase == "refresh-required") return;
                var sender = receipt.Direction < 0 ? receipt : peer;
                var receiver = receipt.Direction > 0 ? receipt : peer;
                ManagerMemory.Current.RecordTransaction(ManagerAccounting.TransactionId, new LedgerRecord {
                    Id = "bag-funding-" + receipt.TransactionId, Utc = DateTime.UtcNow, Event = "banker_bag_funding",
                    Actor = sender.Character, Character = receiver.Character, Role = "bag-funding",
                    Message = phase + "; credits=" + receipt.CreditAmount + "; sender=" + sender.CreditBefore + "->" + sender.CreditAfter +
                        "; receiver=" + receiver.CreditBefore + "->" + receiver.CreditAfter });
                Logger.Information("[CityBankers] BAG FUNDING " + phase + "; " + sender.Character + " -> " + receiver.Character +
                    "; credits=" + receipt.CreditAmount + ".");
            });
        }
    }
}
