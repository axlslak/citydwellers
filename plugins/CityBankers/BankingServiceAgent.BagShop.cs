using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using CityDwellers.Shared;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private const int SummonBagDealerNano = 300439;
        private const string BagDealerName = "Buckethead Technodealer";
        private const string BagPurchaseWithBankBaseline = "buying-bank-baseline";
        private sealed class BagShopWork
        {
            public string Phase;
            public Identity Dealer = Identity.None, Shop = Identity.None;
            public int Slot = -1, Low, High, Ql;
            public bool Opened, Declined, SelectionAcknowledged, NativePurchaseStarted, NativePurchaseCompleted;
            public int TemplateReceipts;
            public readonly HashSet<string> PreviousContainers = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<Identity, Dictionary<Stat, int>> NativeBags = new Dictionary<Identity, Dictionary<Stat, int>>();
            public ReceiptEvidence Receipt;
            public readonly Stopwatch Age = Stopwatch.StartNew();
        }
        private BagShopWork _bagShop, _pendingBagPurchase;
        private bool _bagShopChecked;
        private DateTime _bagShopRetryAfter;
        private string BagShopReceiptId => "reserve-shop:" + Client.CharacterName.ToLowerInvariant();

        private void InvalidateBagShop()
        { _bagShop = null; _pendingBagPurchase = null; _bagShopChecked = false; }

        private void ResumeBagShopWork()
        {
            if (!_isCentral || _bagShopChecked || !Inventory.Bank.IsOpen) return;
            _bagShopChecked = true;
            var receipt = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, BagShopReceiptId);
            if (receipt == null || receipt.Phase == "applied") return;
            if (receipt.Kind != "reserve-shop" || !string.Equals(receipt.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) ||
                receipt.Expected?.Count != 1 || !IsReserveBag(receipt.Expected[0]) || receipt.BeforeSlots == null)
                throw new InvalidOperationException("Interrupted reserve purchase has invalid receipt evidence.");
            _pendingBagPurchase = RestoreBagPurchase(receipt);
            if (receipt.Phase == "buying" || !IsUsableIdentity(receipt.Expected[0].UniqueIdentity))
            {
                // Older receipts lack a bank baseline; an unbound interrupted
                // receipt also lacks its original native identity association.
                StartupCensusGate.Block("Resolve interrupted backpack purchase from fresh native custody.");
                return;
            }
            BankerActivityGovernor.Wake();
            Logger.Information("[CityBankers] BAG SHOP restoring purchase receipt " + receipt.TransactionId + "; settling the original purchase before buying again.");
        }

        private bool StartBagShop(BagReserve reserve, StorageState storage, HashSet<string> ready)
        {
            if (!_isCentral || _bagShop != null || _pendingBagPurchase != null || DateTime.UtcNow < _bagShopRetryAfter || !CanStartLocalCensus() ||
                Inventory.NumFreeSlots < 2) return false;
            if (RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Any(b => IsReserveBatch(b.Items))) return false;
            if (!BagShopSupplyNeeded(reserve, storage, ready)) return false;
            int cash;
            if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash) || cash <= 0)
            { BagShopUnavailable("Central has no available credits for a replacement backpack."); return false; }
            var dealer = FindBagDealer();
            if (dealer == null && !(DynelManager.LocalPlayer.SpellList ?? new int[0]).Contains(SummonBagDealerNano))
            { BagShopUnavailable("Central has not uploaded Summon Buckethead Technodealer (300439)."); return false; }
            if (dealer == null && DynelManager.LocalPlayer.IsCasting) return false;
            _bagShop = new BagShopWork { Phase = dealer == null ? "summoning" : "opening" };
            if (dealer == null)
            {
                DynelManager.LocalPlayer.Cast(SummonBagDealerNano);
                Logger.Information("[CityBankers] BAG SHOP summoning Buckethead Technodealer for replacement backpacks.");
            }
            else OpenBagShop(dealer);
            BankerActivityGovernor.Wake();
            return true;
        }

        private bool BagShopSupplyPending()
        {
            if (!_isCentral || DateTime.UtcNow < _bagShopRetryAfter) return false;
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            return storage?.Workers != null && BagShopSupplyNeeded(ReadReserve(), storage, BankerReadiness.GetReadyCharacters(_settingsDir));
        }

        private bool BagShopSupplyNeeded(BagReserve reserve, StorageState storage, HashSet<string> ready)
        {
            // Provision known, available accounts. An unrelated offline banker
            // neither becomes an invented empty bank nor pauses healthy demand.
            var configured = _config.Roles.Values.Select(a => a.Character).Where(c => ready.Contains(c) &&
                storage.Workers.Any(w => string.Equals(w.Character, c, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (configured.Length == 0) return false;
            int existing = storage.Workers.Where(w => configured.Contains(w.Character, StringComparer.OrdinalIgnoreCase))
                .Sum(w => w.Bags.Select(b => b.LastUniqueIdentity).Distinct().Count());
            var registered = new HashSet<string>(storage.Workers.SelectMany(w => w.Bags).Select(b => b.LastUniqueIdentity));
            int incoming = reserve.Bags.Count(b => !b.Delivered && !b.Quarantined && !registered.Contains(b.Identity));
            return existing + incoming < configured.Length * StorageBankBagTarget;
        }

        private Dynel FindBagDealer() => DynelManager.Npcs
            .Where(d => string.Equals(d.Name, BagDealerName, StringComparison.OrdinalIgnoreCase)).Cast<Dynel>()
            .Concat(DynelManager.VendingMachines
                .Where(d => string.Equals(d.Name, BagDealerName, StringComparison.OrdinalIgnoreCase)).Cast<Dynel>())
            .Where(d => d.Transform != null && d.DistanceFrom(DynelManager.LocalPlayer) <= 5)
            .OrderBy(d => d.DistanceFrom(DynelManager.LocalPlayer)).FirstOrDefault();

        private void OpenBagShop(Dynel dealer)
        {
            _bagShop.Dealer = dealer.Identity; _bagShop.Phase = "opening"; _bagShop.Age.Restart();
            dealer.Use();
            Logger.Information("[CityBankers] BAG SHOP opening " + dealer.Identity + ".");
        }

        private void ObserveBagShop(object sender, Message message)
        {
            // Capture the server's catalogue before native processing, then
            // consume it on Tick. Never interrupt the SDK's packet handler.
            try
            {
                var work = _bagShop ?? _pendingBagPurchase;
                if (work == null) return;
                if (work.Phase == "buying" && work.Receipt != null)
                {
                    ObservePurchasedBag(work, message?.Body);
                    return;
                }
                if (_publicTradePartner != Identity.None) return;
                var trade = message?.Body as TradeMessage;
                if (work.Phase == "offering" && trade != null && trade.Action == TradeAction.AddItem &&
                    trade.Identity == DynelManager.LocalPlayer.Identity &&
                    trade.Param1 == (int)work.Shop.Type && trade.Param2 == work.Shop.Instance &&
                    trade.Param3 == (int)IdentityType.TradeWindow && trade.Param4 == work.Slot)
                {
                    work.SelectionAcknowledged = true;
                    Logger.Information("[CityBankers] BAG SHOP selection acknowledged; session=" + work.Shop + "; slot=" + work.Slot + ".");
                    BankerActivityGovernor.Wake();
                    return;
                }
                if (work.Phase != "opening" || !(message?.Body is ShopUpdateMessage shop)) return;
                // The N3 header is not the dealer identity. This catalogue belongs
                // to the one shop opening initiated by this actor.
                var slots = shop.VendingMachineSlots;
                if (slots == null) return;
                for (int i = 0; i < slots.Length; i++)
                {
                    var item = slots[i];
                    if (item.ItemLowId != StorageBagPolicy.SmallBackpackId && item.ItemHighId != StorageBagPolicy.SmallBackpackId) continue;
                    if (work.Slot < 0)
                        Logger.Information("[CityBankers] BAG SHOP catalogue " + shop.Identity + "; backpack slot=" + i + ".");
                    work.Slot = i; work.Low = item.ItemLowId; work.High = item.ItemHighId; work.Ql = item.Quality;
                    break;
                }
                BankerActivityGovernor.Wake();
            }
            catch (Exception error) { Logger.Warning("[CityBankers] BAG SHOP catalogue observation failed: " + error.Message); }
        }

        private bool BagShopTradeOpened(Identity target)
        {
            var work = _bagShop;
            if (work == null || work.Phase != "opening" || _publicTradePartner != Identity.None ||
                (target != work.Dealer && target.Type != IdentityType.VendingMachine && target != Identity.None)) return false;
            // An NPC's world identity and its shop-session identity differ.
            // Bind the callback target for all subsequent shop requests.
            work.Shop = target;
            work.Opened = true;
            Logger.Information("[CityBankers] BAG SHOP session " + target + "; dealer=" + work.Dealer + ".");
            BankerActivityGovernor.Wake();
            return true;
        }

        private bool BagShopTradeStatus(Identity target, TradeStatus status)
        {
            if (_bagShop == null || (_bagShop.Shop != target && target != Identity.None)) return false;
            if (status == TradeStatus.Declined) _bagShop.Declined = true;
            BankerActivityGovernor.Wake();
            return true;
        }

        private bool TickBagShop()
        {
            var work = _bagShop ?? _pendingBagPurchase;
            if (work == null) return false;
            if (work.Phase == "buying" && CompleteBagShopReceipt(work)) return true;
            // An uncertain purchase owns its receipt, not Central's work loop.
            if (_bagShop == null) return false;
            if (work.Age.Elapsed.TotalSeconds > ServicePolicy.TradeTimeoutSeconds)
            {
                if (work.Receipt != null)
                {
                    DeferBagPurchase(work);
                    return true;
                }
                StopBagShop("No completed shop response for phase " + work.Phase + ".");
                return true;
            }
            if (work.Phase == "summoning")
            {
                var dealer = FindBagDealer();
                if (dealer != null) OpenBagShop(dealer);
                return true;
            }
            if (work.Declined && work.Receipt == null)
            { StopBagShop("The shop session was declined before a purchase."); return true; }
            if (work.Phase == "opening" && work.Opened && work.Slot >= 0)
            {
                work.Phase = "offering"; work.Age.Restart();
                Trade.AddItem(work.Slot);
                Logger.Information("[CityBankers] BAG SHOP requesting shop slot " + work.Slot + "; item=" +
                    work.Low + "/" + work.High + "; QL=" + work.Ql + ".");
                return true;
            }
            if (work.Phase == "offering")
            {
                if (Trade.CurrentTarget != work.Shop || Trade.PlayerWindowCache.Items.Count != 0)
                { StopBagShop("The shop session changed or contains an item offered by Central; purchase cancelled."); return true; }
                // Shops acknowledge the catalogue selection with AddItem. The
                // Clientless player-trade cache is not populated by that reply.
                if (!work.SelectionAcknowledged) return true;
                var selection = new TransferItemState { AoId = work.Low, HighId = work.High,
                    Ql = work.Ql, Quantity = 1, Name = "Small Backpack" };
                int cash;
                long price = Math.Max(Math.Abs((long)Trade.PlayerWindowCache.Credits), Math.Abs((long)Trade.TargetWindowCache.Credits));
                if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash) || cash <= 0 || price > cash)
                { StopBagShop("Central cannot pay the dealer's quoted price."); return true; }
                work.Receipt = new ReceiptEvidence { Id = BagShopReceiptId, Kind = "reserve-shop", Character = Client.CharacterName,
                    TransactionId = "reserve-shop-" + Guid.NewGuid().ToString("N"), Phase = BagPurchaseWithBankBaseline,
                    Before = PhysicalInventory().Concat(SnapshotTradeItems(Inventory.Bank.Items
                        .Where(item => item.UniqueIdentity.Type == IdentityType.Container))).ToList(), BeforeSlots = PhysicalSlots(),
                    Expected = new List<TransferItemState> { selection },
                    PreparedItems = new List<TransferItemState> { selection } };
                ManagerAccounting.Transaction("Prepare replacement bag purchase", () =>
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt));
                work.PreviousContainers.UnionWith(Inventory.Items.Concat(Inventory.Bank.Items)
                    .Where(i => i.UniqueIdentity.Type == IdentityType.Container).Select(i => i.UniqueIdentity.ToString()));
                work.NativePurchaseStarted = true;
                work.Phase = "buying"; work.Age.Restart();
                // The ordinary shop transaction uses the same completion target
                // as the Grace reference. Nothing is offered from our inventory.
                Client.Send(new TradeMessage { Unknown1 = 2, Action = TradeAction.Accept, Param1 = 0, Param2 = 0 });
                Logger.Information("[CityBankers] BAG SHOP purchasing one small backpack; receipt=" + work.Receipt.TransactionId + ".");
            }
            return true;
        }

        private void ObservePurchasedBag(BagShopWork work, object body)
        {
            var expected = work.Receipt.Expected.Single();
            var completed = body as TradeMessage;
            var offered = Trade.TargetWindowCache?.Items;
            if (ReferenceEquals(_bagShop, work) && work.NativePurchaseStarted && work.Opened && work.SelectionAcknowledged &&
                work.Shop != Identity.None && Trade.CurrentTarget == work.Shop && _publicTradePartner == Identity.None &&
                !_withdrawalTradeOpened && (Trade.PlayerWindowCache?.Items?.Count ?? -1) == 0 &&
                offered != null && offered.Count == 1 && offered[0].Id == expected.AoId &&
                offered[0].HighId == expected.HighId && offered[0].Ql == expected.Ql &&
                completed != null && completed.Identity == DynelManager.LocalPlayer.Identity && completed.Action == TradeAction.Complete)
                work.NativePurchaseCompleted = true;
            var template = body as AddTemplateMessage;
            if (template != null && template.Identity == DynelManager.LocalPlayer.Identity &&
                template.LowId == expected.AoId && template.HighId == expected.HighId && template.Quality == expected.Ql)
            {
                work.TemplateReceipts += template.Count == 1 ? 1 : 2;
                BankerActivityGovernor.Wake();
            }
            var chest = body as ChestFullUpdateMessage;
            if (chest == null || chest.Owner != DynelManager.LocalPlayer.Identity ||
                chest.Identity.Type != IdentityType.Container || chest.Stats == null ||
                work.PreviousContainers.Contains(chest.Identity.ToString())) return;
            if (chest.Stats.GroupBy(stat => stat.Value1).Any(group => group.Count() != 1)) return;
            var stats = chest.Stats.ToDictionary(stat => stat.Value1, stat => stat.Value2);
            int low, high, ql;
            if (!stats.TryGetValue(Stat.ACGItemTemplateID, out low) || low != expected.AoId ||
                !stats.TryGetValue(Stat.ACGItemTemplateID2, out high) || high != expected.HighId ||
                !stats.TryGetValue(Stat.ACGItemLevel, out ql) || ql != expected.Ql) return;
            work.NativeBags[chest.Identity] = stats;
            BankerActivityGovernor.Wake();
        }

        private void BindPurchasedBag(BagShopWork work)
        {
            if (!work.NativePurchaseStarted ||
                !(work.TemplateReceipts == 1 || (work.TemplateReceipts == 0 && work.NativePurchaseCompleted)) || work.NativeBags.Count != 1 ||
                (_publicTradePartner != Identity.None && _publicTradeStarted)) return;
            var expected = work.Receipt.Expected.Single();
            var before = work.Receipt.BeforeSlots;
            var beforeSlots = new HashSet<string>(before.Select(slot => slot.Slot), StringComparer.Ordinal);
            var candidates = Inventory.Items.Where(item => StorageBagPolicy.IsNormalInventory(item) &&
                item.UniqueIdentity == Identity.None && item.Id == expected.AoId && item.HighId == expected.HighId &&
                item.Ql == expected.Ql && !beforeSlots.Contains(item.Slot.ToString())).ToList();
            if (candidates.Count != 1) return;
            var stub = candidates[0];
            if (!before.Select(BagShopSlotKey).OrderBy(key => key).SequenceEqual(
                PhysicalSlots().Where(slot => slot.Slot != stub.Slot.ToString()).Select(BagShopSlotKey).OrderBy(key => key))) return;
            var native = work.NativeBags.Single();
            if (IsUsableIdentity(expected.UniqueIdentity) && expected.UniqueIdentity != native.Key.ToString()) return;
            if (Inventory.Items.Concat(Inventory.Bank.Items).Any(item => item.UniqueIdentity == native.Key)) return;
            var items = Inventory.Items as IList<Item>;
            int index = items == null ? -1 : items.IndexOf(stub);
            if (index < 0 || items.IsReadOnly) return;
            // Both facts came from AO for this one purchase. Preserve the native
            // identity before upgrading its existing SDK-created slot object.
            expected.UniqueIdentity = native.Key.ToString();
            ManagerAccounting.Transaction("Bind purchased bag native identity", () =>
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt));
            items[index] = new UniqueItem(stub.Slot, native.Key, expected.AoId, expected.HighId, expected.Ql, native.Value);
            Logger.Information("[CityBankers] BAG SHOP native bag bound " + native.Key + "; slot=" + stub.Slot + ".");
        }

        private void DeferBagPurchase(BagShopWork work)
        {
            CloseBagShop(work);
            _pendingBagPurchase = work;
            if (ReferenceEquals(_bagShop, work)) _bagShop = null;
            Logger.Warning("[CityBankers] BAG SHOP purchase receipt pending " + work.Receipt.TransactionId +
                "; template receipts=" + work.TemplateReceipts + "; native containers=" + work.NativeBags.Count +
                "; original shop complete=" + work.NativePurchaseCompleted + "; refreshing native inventory before further transfers.");
            // CFU can arrive without an SDK inventory slot. Its next-free-slot
            // guesses must not be used to offer another player's requested item.
            StartupCensusGate.Block("Resolve pending backpack purchase " + work.Receipt.TransactionId + " from fresh native custody.");
        }

        private void CloseBagShop(BagShopWork work)
        {
            if (work.Opened && Trade.CurrentTarget == work.Shop) Trade.Decline();
        }

        private void SettleRecoveredBagPurchase()
        {
            if (!_isCentral) return;
            var receipt = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, BagShopReceiptId);
            if (receipt == null || receipt.Kind != "reserve-shop" || receipt.Phase == "applied") return;
            CompleteBagShopReceipt(RestoreBagPurchase(receipt));
        }

        private static BagShopWork RestoreBagPurchase(ReceiptEvidence receipt)
        {
            var work = new BagShopWork { Phase = "buying", Receipt = receipt };
            work.PreviousContainers.UnionWith((receipt.Before ?? new List<TransferItemState>())
                .Where(IsReserveBag).Select(item => item.UniqueIdentity).Where(IsUsableIdentity));
            return work;
        }

        private void FinishBagPurchase(BagShopWork work)
        {
            CloseBagShop(work);
            if (_bagShop?.Receipt?.TransactionId == work.Receipt.TransactionId) _bagShop = null;
            if (_pendingBagPurchase?.Receipt?.TransactionId == work.Receipt.TransactionId) _pendingBagPurchase = null;
        }

        private bool CompleteBagShopReceipt(BagShopWork work)
        {
            if (work.Receipt == null) return false;
            var saved = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, work.Receipt.Id);
            if (saved?.TransactionId == work.Receipt.TransactionId && saved.Phase == "applied")
            { FinishBagPurchase(work); return true; }
            BindPurchasedBag(work);
            var expected = work.Receipt.Expected.Single();
            // An anonymous new bag may belong to a player donation or a bank
            // withdrawal. Only the original native purchase binding identifies it.
            if (!IsUsableIdentity(expected.UniqueIdentity)) return false;
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error)) throw new InvalidOperationException(error);
            var before = work.Receipt.BeforeSlots;
            var previousIds = new HashSet<string>(before.Select(i => i.Identity));
            var reserve = ReadReserve();
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            var arrivals = Inventory.Items.Concat(Inventory.Bank.Items).Where(i =>
                (StorageBagPolicy.IsNormalInventory(i) || i.Slot.Type == IdentityType.BankByRef) && StorageBagPolicy.IsSmallBackpack(i) &&
                i.Id == expected.AoId && i.HighId == expected.HighId && i.Ql == expected.Ql &&
                expected.UniqueIdentity == i.UniqueIdentity.ToString() &&
                !previousIds.Contains(i.UniqueIdentity.ToString()) && !work.PreviousContainers.Contains(i.UniqueIdentity.ToString())).ToList();
            if (arrivals.Count != 1) return false;
            var bag = arrivals[0];
            var after = PhysicalSlots();
            // Its identity was durably bound at the original native receipt.
            // Later unrelated inventory moves cannot undo that association.
            var registrations = storage.Workers.SelectMany(worker => worker.Bags.Where(b =>
                b.LastUniqueIdentity == bag.UniqueIdentity.ToString()).Select(b => new { Worker = worker, Bag = b })).ToList();
            string location = StorageBagPolicy.IsNormalInventory(bag) ? "inventory" : "bank";
            if (registrations.Any(row => !SameCharacter(row.Worker.Character, Client.CharacterName) || row.Bag.Source != location ||
                (row.Bag.OuterSlotInstance & 65535) != (bag.Slot.Instance & 65535))) return false;
            bool enrolled = false;
            ManagerAccounting.Transaction("Enroll purchased replacement bag", () =>
            {
                reserve = ReadReserve();
                var existing = reserve.Bags.SingleOrDefault(b => b.Identity == bag.UniqueIdentity.ToString());
                // Transaction changes when reserve stock is assigned to a supply
                // batch. Preserve that disposition and any existing storage row.
                if (existing == null && registrations.Count == 0)
                {
                    reserve.Bags.Add(new ReserveBag { Identity = bag.UniqueIdentity.ToString(), Transaction = work.Receipt.TransactionId });
                    SaveReserve(reserve);
                    enrolled = true;
                }
                work.Receipt.Observed = SnapshotTradeItems(new[] { bag }); work.Receipt.ObservedSlots = after;
                work.Receipt.Phase = "applied";
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt);
            });
            if (enrolled) _reserveEmptyArrivals.Add(bag.UniqueIdentity.ToString());
            FinishBagPurchase(work);
            Logger.Information("[CityBankers] BAG SHOP receipt applied " + work.Receipt.TransactionId +
                "; bag=" + bag.UniqueIdentity + "; reserve entry added=" + enrolled + ".");
            return true;
        }

        private static string BagShopSlotKey(CustodySlot item) => item.Slot + "/" + item.Identity + "/" +
            item.AoId + "/" + item.HighId + "/" + item.Ql + "/" + item.Quantity;

        private void BagShopUnavailable(string reason)
        {
            _bagShopRetryAfter = DateTime.UtcNow.AddMinutes(1);
            Logger.Warning("[CityBankers] BAG SHOP " + reason + " Normal banking remains available.");
        }
        private void StopBagShop(string reason)
        {
            if (_bagShop != null) CloseBagShop(_bagShop);
            _bagShop = null;
            BagShopUnavailable(reason);
        }
    }
}
