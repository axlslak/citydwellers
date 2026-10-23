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
        private sealed class BagShopWork
        {
            public string Phase;
            public Identity Dealer = Identity.None, Shop = Identity.None;
            public int Slot = -1, Low, High, Ql;
            public bool Opened, Declined;
            public ReceiptEvidence Receipt;
            public readonly Stopwatch Age = Stopwatch.StartNew();
        }
        private BagShopWork _bagShop;
        private bool _bagShopChecked;
        private DateTime _bagShopRetryAfter;
        private string BagShopReceiptId => "reserve-shop:" + Client.CharacterName.ToLowerInvariant();

        private void InvalidateBagShop()
        { _bagShop = null; _bagShopChecked = false; }

        private void ResumeBagShopWork()
        {
            if (!_isCentral || _bagShopChecked || !Inventory.Bank.IsOpen) return;
            _bagShopChecked = true;
            var receipt = ManagerMemory.Current.ReadReceipt(ManagerAccounting.TransactionId, BagShopReceiptId);
            if (receipt == null || receipt.Phase == "applied") return;
            if (receipt.Kind != "reserve-shop" || !string.Equals(receipt.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) ||
                receipt.Expected?.Count != 1 || !IsReserveBag(receipt.Expected[0]) || receipt.BeforeSlots == null)
                throw new InvalidOperationException("Interrupted reserve purchase has invalid receipt evidence.");
            _bagShop = new BagShopWork { Phase = "buying", Receipt = receipt };
            BankerActivityGovernor.Wake();
            Logger.Information("[CityBankers] BAG SHOP restoring purchase receipt " + receipt.TransactionId + "; checking inventory before any new purchase.");
        }

        private bool StartBagShop(BagReserve reserve, StorageState storage, HashSet<string> ready)
        {
            if (!_isCentral || _bagShop != null || DateTime.UtcNow < _bagShopRetryAfter || !CanStartLocalCensus() ||
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
                var work = _bagShop;
                if (work == null || work.Phase != "opening" || _publicTradePartner != Identity.None ||
                    !(message?.Body is ShopUpdateMessage shop)) return;
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
            var work = _bagShop;
            if (work == null) return false;
            if (work.Phase == "buying" && CompleteBagShopReceipt(work)) return true;
            if (work.Receipt != null && work.Declined && !Trade.IsTrading &&
                work.Receipt.BeforeSlots.Select(BagShopSlotKey).OrderBy(s => s).SequenceEqual(PhysicalSlots().Select(BagShopSlotKey).OrderBy(s => s)))
            {
                work.Receipt.Phase = "applied";
                ManagerAccounting.Transaction("Cancel declined replacement bag purchase", () =>
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt));
                _bagShop = null;
                BagShopUnavailable("The dealer declined the purchase; inventory is unchanged.");
                return true;
            }
            if (work.Age.Elapsed.TotalSeconds > ServicePolicy.TradeTimeoutSeconds)
            {
                if (work.Receipt != null)
                    throw new InvalidOperationException("Replacement bag purchase remains unverified: " + work.Receipt.TransactionId +
                        ". Receipt retained; another purchase will not be sent.");
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
                var offered = Trade.TargetWindowCache.Items;
                if (offered.Count == 0) return true;
                if (offered.Count != 1 || offered[0].Id != work.Low || offered[0].HighId != work.High || offered[0].Ql != work.Ql ||
                    StackableItems.Quantity(offered[0]) != 1)
                { StopBagShop("The dealer's offer does not contain exactly the requested small backpack."); return true; }
                int cash;
                long price = Math.Max(Math.Abs((long)Trade.PlayerWindowCache.Credits), Math.Abs((long)Trade.TargetWindowCache.Credits));
                if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash) || cash <= 0 || price > cash)
                { StopBagShop("Central cannot pay the dealer's quoted price."); return true; }
                work.Receipt = new ReceiptEvidence { Id = BagShopReceiptId, Kind = "reserve-shop", Character = Client.CharacterName,
                    TransactionId = "reserve-shop-" + Guid.NewGuid().ToString("N"), Phase = "buying",
                    Before = PhysicalInventory(), BeforeSlots = PhysicalSlots(), Expected = SnapshotTradeItems(offered),
                    PreparedItems = SnapshotTradeItems(offered) };
                ManagerAccounting.Transaction("Prepare replacement bag purchase", () =>
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt));
                work.Phase = "buying"; work.Age.Restart();
                // The ordinary shop transaction uses the same completion target
                // as the Grace reference. Nothing is offered from our inventory.
                Client.Send(new TradeMessage { Unknown1 = 2, Action = TradeAction.Accept, Param1 = 0, Param2 = 0 });
                Logger.Information("[CityBankers] BAG SHOP purchasing one small backpack; receipt=" + work.Receipt.TransactionId + ".");
            }
            return true;
        }

        private bool CompleteBagShopReceipt(BagShopWork work)
        {
            if (work.Receipt == null || (Trade.IsTrading && _publicTradePartner == Identity.None)) return false;
            string error;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out error)) throw new InvalidOperationException(error);
            var before = work.Receipt.BeforeSlots;
            var expected = work.Receipt.Expected.Single();
            var previousIds = new HashSet<string>(before.Select(i => i.Identity));
            var arrivals = Inventory.Items.Where(i => StorageBagPolicy.IsNormalInventory(i) && StorageBagPolicy.IsSmallBackpack(i) &&
                i.Id == expected.AoId && i.HighId == expected.HighId && i.Ql == expected.Ql &&
                (expected.UniqueIdentity == null || !expected.UniqueIdentity.StartsWith("(Container:", StringComparison.Ordinal) ||
                    expected.UniqueIdentity == i.UniqueIdentity.ToString()) &&
                !previousIds.Contains(i.UniqueIdentity.ToString())).ToList();
            if (arrivals.Count != 1) return false;
            var bag = arrivals[0];
            var after = PhysicalSlots();
            if (!before.Select(BagShopSlotKey).OrderBy(s => s).SequenceEqual(after.Where(i => i.Identity != bag.UniqueIdentity.ToString()).Select(BagShopSlotKey).OrderBy(s => s)))
                return false;
            ManagerAccounting.Transaction("Enroll purchased replacement bag", () =>
            {
                var reserve = ReadReserve();
                var existing = reserve.Bags.SingleOrDefault(b => b.Identity == bag.UniqueIdentity.ToString());
                if (existing != null && existing.Transaction != work.Receipt.TransactionId)
                    throw new InvalidOperationException("Purchased bag identity already belongs to another reserve receipt: " + bag.UniqueIdentity);
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                if (storage?.Workers?.Any(w => w.Bags.Any(b => b.LastUniqueIdentity == bag.UniqueIdentity.ToString())) == true)
                    throw new InvalidOperationException("Purchased bag identity is already registered as storage: " + bag.UniqueIdentity);
                if (existing == null)
                    reserve.Bags.Add(new ReserveBag { Identity = bag.UniqueIdentity.ToString(), Transaction = work.Receipt.TransactionId });
                SaveReserve(reserve);
                work.Receipt.Observed = SnapshotTradeItems(new[] { bag }); work.Receipt.ObservedSlots = after;
                work.Receipt.Phase = "applied";
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt);
            });
            _reserveEmptyArrivals.Add(bag.UniqueIdentity.ToString());
            _bagShop = null;
            Logger.Information("[CityBankers] BAG SHOP received " + bag.UniqueIdentity + "; replacement reserve replenished.");
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
            if (_bagShop != null && _bagShop.Opened && Trade.CurrentTarget == _bagShop.Shop) Trade.Decline();
            _bagShop = null;
            BagShopUnavailable(reason);
        }
    }
}
