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
            public Item SelectedOffer;
            public readonly HashSet<string> PreviousContainers = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<Identity, Dictionary<Stat, int>> NativeBags = new Dictionary<Identity, Dictionary<Stat, int>>();
            public ReceiptEvidence Receipt;
            public readonly Stopwatch Age = Stopwatch.StartNew();
            public readonly List<string> NativePackets = new List<string>();
            public int NativePacketsDropped;
            public string NativePacketCaptureError;
            public readonly BagShopApproach Approach = new BagShopApproach();
        }
        private BagShopWork _bagShop, _pendingBagPurchase;
        private bool _bagShopChecked;
        private DateTime _bagShopRetryAfter;
        private string BagShopReceiptId => "reserve-shop:" + Client.CharacterName.ToLowerInvariant();

        private void InvalidateBagShop()
        {
            ReleaseBagShopOffer(_bagShop);
            ReleaseBagShopOffer(_pendingBagPurchase);
            StopBagShopApproach(_bagShop);
            InvalidateBagFunding();
            _bagShop = null; _pendingBagPurchase = null; _bagShopChecked = false;
        }

        private void ResumeBagShopWork()
        {
            if (_bagShopChecked || !Inventory.Bank.IsOpen) return;
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

        private bool StartBagShop()
        {
            if (_isCentral || _bagShop != null || _pendingBagPurchase != null || _bagFunding != null || DateTime.UtcNow < _bagShopRetryAfter || !CanStartLocalCensus() ||
                Inventory.NumFreeSlots < 2) return false;
            if (!BagShopSupplyPending() || BagSupplyServiceWaiting()) return false;
            var dealer = FindBagDealer();
            if (dealer == null && !(DynelManager.LocalPlayer.SpellList ?? new int[0]).Contains(SummonBagDealerNano))
            { RequestBagDealer(); return false; }
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
            if (_isCentral || DateTime.UtcNow < _bagShopRetryAfter || !Inventory.Bank.IsOpen || ReserveBankFreeSlots < 1) return false;
            var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            var own = storage?.Workers?.SingleOrDefault(w => SameCharacter(w.Character, Client.CharacterName));
            if (own == null) return false;
            // Finish previously assigned physical deliveries before adding more
            // equipment locally. Another banker's deliveries do not hold us up.
            if (RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.Any(b => IsReserveBatch(b.Items) &&
                SameCharacter(b.Character, Client.CharacterName))) return false;
            if (ReadReserve().Bags.Any(b => !b.Delivered && !b.Quarantined && SameCharacter(b.Destination, Client.CharacterName))) return false;
            var identities = new HashSet<string>(own.Bags.Select(b => b.LastUniqueIdentity));
            identities.UnionWith(StorageBagPolicy.AllBagRecords().Select(b => b.Identity.ToString()));
            return identities.Count < StorageBankBagTarget;
        }

        private bool BagSupplyServiceWaiting()
        {
            return _publicTradePartner != Identity.None || _withdrawalReceiveReserved ||
                WithdrawalStore.LoadAll(_settingsDir).Any(w =>
                    (WithdrawalStore.HasStatus(w, "requested") || WithdrawalStore.HasStatus(w, "extracting") ||
                     WithdrawalStore.HasStatus(w, "central-received")) &&
                    (_isCentral || SameCharacter(w.SourceCharacter, Client.CharacterName)));
        }

        private bool YieldUnpaidBagShop()
        {
            if (_bagShop == null) return true;
            if (_bagShop.Receipt != null) return false;
            CloseBagShop(_bagShop);
            _bagShop = null;
            return true;
        }

        private Dynel FindBagDealer() => DynelManager.Npcs
            .Where(d => string.Equals(d.Name, BagDealerName, StringComparison.OrdinalIgnoreCase)).Cast<Dynel>()
            .Concat(DynelManager.VendingMachines
                .Where(d => string.Equals(d.Name, BagDealerName, StringComparison.OrdinalIgnoreCase)).Cast<Dynel>())
            .Where(d => d.Transform != null && d.DistanceFrom(DynelManager.LocalPlayer) <= 5)
            .OrderBy(d => d.DistanceFrom(DynelManager.LocalPlayer)).FirstOrDefault();

        private void OpenBagShop(Dynel dealer)
        {
            if (dealer.DistanceFrom(DynelManager.LocalPlayer) > BagShopUseDistance)
            { BeginBagShopApproach(dealer); return; }
            _bagShop.Dealer = dealer.Identity; _bagShop.Phase = "opening"; _bagShop.Age.Restart();
            dealer.Use();
            Logger.Information("[CityBankers] BAG SHOP opening " + dealer.Identity +
                "; distance=" + dealer.DistanceFrom(DynelManager.LocalPlayer).ToString("F2") + "m.");
        }

        private void ObserveBagShop(object sender, Message message)
        {
            // Capture the server's catalogue before native processing, then
            // consume it on Tick. Never interrupt the SDK's packet handler.
            try
            {
                var work = _bagShop ?? _pendingBagPurchase;
                if (work == null) return;
                ObserveBagShopMovement(work, message?.Body as CharDCMoveMessage);
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
            // Clientless does not clear its offer caches when another window
            // opens. A shop selection must never enter a player's trade.
            if (work != null && target != work.Shop) ReleaseBagShopOffer(work);
            if (work != null && target != work.Shop && work.Receipt == null &&
                (work.Phase != "opening" || (target != work.Dealer && target.Type != IdentityType.VendingMachine)))
            { StopBagShopApproach(work); _bagShop = null; return false; }
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
            if (work.Receipt == null && BagSupplyServiceWaiting())
            { YieldUnpaidBagShop(); return false; }
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
            if (work.Phase == "approaching" || work.Phase == "stopping")
            { TickBagShopApproach(work); return true; }
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
                { StopBagShop("The shop session changed or contains an item offered by the buyer; purchase cancelled."); return true; }
                // Shops acknowledge the catalogue selection with AddItem. The
                // Clientless player-trade cache is not populated by that reply.
                if (!work.SelectionAcknowledged) return true;
                var offers = Trade.TargetWindowCache.Items;
                if (offers.Count != 0 && (offers.Count != 1 || offers[0].UniqueIdentity != Identity.None ||
                    offers[0].Id != work.Low || offers[0].HighId != work.High || offers[0].Ql != work.Ql))
                { StopBagShop("The shop offer differs from the acknowledged catalogue selection; purchase cancelled."); return true; }
                var selection = new TransferItemState { AoId = work.Low, HighId = work.High,
                    Ql = work.Ql, Quantity = 1, Name = "Small Backpack" };
                int cash;
                long price = Math.Max(Math.Abs((long)Trade.PlayerWindowCache.Credits), Math.Abs((long)Trade.TargetWindowCache.Credits));
                if (!DynelManager.LocalPlayer.TryGetStat(Stat.Cash, out cash)) return true;
                if (cash <= 0 || price > cash)
                {
                    YieldUnpaidBagShop();
                    RequestBagFunding();
                    return true;
                }
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
                // The server acknowledged the catalogue item, but Clientless
                // handles that AddItem as a player inventory offer and loses it.
                // Supply the missing target entry so its native Complete handler
                // adds the purchase to inventory. Receipt binding still requires
                // the server's completion and native bag identity.
                work.SelectedOffer = offers.Count == 1 ? offers[0] : new Item(
                    new Identity(IdentityType.KnuBotTradeWindow, 0), Identity.None, work.Low, work.High, work.Ql);
                if (offers.Count == 0) offers.Add(work.SelectedOffer);
                work.NativePurchaseStarted = true;
                work.Phase = "buying"; work.Age.Restart();
                // Let the server complete the ordinary shop transaction.
                Client.Send(new TradeMessage { Unknown1 = 2, Action = TradeAction.Accept, Param1 = 0, Param2 = 0 });
                Logger.Information("[CityBankers] BAG SHOP purchasing one small backpack; receipt=" + work.Receipt.TransactionId + ".");
            }
            return true;
        }

        private void ObservePurchasedBag(BagShopWork work, object body)
        {
            CaptureBagPurchasePacket(work, body);
            var expected = work.Receipt.Expected.Single();
            var completed = body as TradeMessage;
            var offered = Trade.TargetWindowCache?.Items;
            if (ReferenceEquals(_bagShop, work) && work.NativePurchaseStarted && work.Opened && work.SelectionAcknowledged &&
                work.Shop != Identity.None && Trade.CurrentTarget == work.Shop && _publicTradePartner == Identity.None &&
                !_withdrawalTradeOpened && (Trade.PlayerWindowCache?.Items?.Count ?? -1) == 0 &&
                offered != null && offered.Count == 1 && offered[0].Id == expected.AoId &&
                offered[0].HighId == expected.HighId && offered[0].Ql == expected.Ql &&
                completed != null && completed.Identity == DynelManager.LocalPlayer.Identity && completed.Action == TradeAction.Complete &&
                completed.Param1 == (int)work.Shop.Type && completed.Param2 == work.Shop.Instance)
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

        private void CaptureBagPurchasePacket(BagShopWork work, object body)
        {
            // This trace observes the original receipt only. It never changes
            // inventory, acknowledgements or durable business evidence.
            try
            {
                if (!work.NativePurchaseStarted || body == null) return;
                string kind = body.GetType().Name;
                if (kind != "ContainerAddItem" && kind != "TemplateActionMessage" && kind != "AddTemplateMessage" &&
                    kind != "TradeMessage" && kind != "KnuBotTradeMessage" && kind != "KnuBotFinishTradeMessage" &&
                    kind != "KnuBotStartTradeMessage" && kind != "KnuBotRejectedItemsMessage" &&
                    kind != "InventoryUpdateMessage" && kind != "BankMessage" && kind != "ChestFullUpdateMessage") return;
                var chest = body as ChestFullUpdateMessage;
                if (chest != null && (chest.Owner != DynelManager.LocalPlayer.Identity ||
                    work.PreviousContainers.Contains(chest.Identity.ToString()))) return;
                if (work.NativePackets.Count >= 32) { work.NativePacketsDropped++; return; }
                work.NativePackets.Add("+" + work.Age.ElapsedMilliseconds + "ms " + Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    Kind = kind, Body = body, CurrentTarget = Trade.CurrentTarget,
                    PlayerOfferCount = Trade.PlayerWindowCache?.Items?.Count,
                    TargetOfferCount = Trade.TargetWindowCache?.Items?.Count
                }));
            }
            catch (Exception error) { work.NativePacketCaptureError = error.GetType().Name; }
        }

        private void BindPurchasedBag(BagShopWork work)
        {
            if (!work.NativePurchaseStarted || !ReferenceEquals(_bagShop, work) || !work.SelectionAcknowledged ||
                work.NativeBags.Count != 1 || _publicTradePartner != Identity.None) return;
            var expected = work.Receipt.Expected.Single();
            var before = work.Receipt.BeforeSlots;
            var beforeSlots = new HashSet<string>(before.Select(slot => slot.Slot), StringComparer.Ordinal);
            var native = work.NativeBags.Single();
            if (work.PreviousContainers.Contains(native.Key.ToString()) ||
                (IsUsableIdentity(expected.UniqueIdentity) && expected.UniqueIdentity != native.Key.ToString())) return;
            // The SDK also receives shop bags through CFU -> overflow spawn.
            // That route already has the native identity and sends neither an
            // AddTemplate receipt nor a shop Complete. Do not replace that item.
            var identified = Inventory.Items.Where(item => StorageBagPolicy.IsNormalInventory(item) &&
                item.UniqueIdentity == native.Key && item.Id == expected.AoId && item.HighId == expected.HighId &&
                item.Ql == expected.Ql && !beforeSlots.Contains(item.Slot.ToString())).ToList();
            if (identified.Count == 1 && Inventory.Items.Concat(Inventory.Bank.Items).Count(item => item.UniqueIdentity == native.Key) == 1 &&
                before.Select(BagShopSlotKey).OrderBy(key => key).SequenceEqual(
                    PhysicalSlots().Where(slot => slot.Slot != identified[0].Slot.ToString()).Select(BagShopSlotKey).OrderBy(key => key)))
            {
                expected.UniqueIdentity = native.Key.ToString();
                ManagerAccounting.Transaction("Bind purchased bag native inventory identity", () =>
                    ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt));
                Logger.Information("[CityBankers] BAG SHOP native inventory bag bound " + native.Key + "; slot=" + identified[0].Slot + ".");
                return;
            }
            if (!(work.TemplateReceipts == 1 || (work.TemplateReceipts == 0 && work.NativePurchaseCompleted))) return;
            var candidates = Inventory.Items.Where(item => StorageBagPolicy.IsNormalInventory(item) &&
                item.UniqueIdentity == Identity.None && item.Id == expected.AoId && item.HighId == expected.HighId &&
                item.Ql == expected.Ql && !beforeSlots.Contains(item.Slot.ToString())).ToList();
            if (candidates.Count != 1) return;
            var stub = candidates[0];
            if (!before.Select(BagShopSlotKey).OrderBy(key => key).SequenceEqual(
                PhysicalSlots().Where(slot => slot.Slot != stub.Slot.ToString()).Select(BagShopSlotKey).OrderBy(key => key))) return;
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
                "; original shop complete=" + work.NativePurchaseCompleted +
                "; native identities=" + string.Join(",", work.NativeBags.Keys) +
                "; new inventory=" + string.Join(",", PhysicalSlots().Where(slot => !work.Receipt.BeforeSlots.Any(prior => prior.Slot == slot.Slot))
                    .Select(slot => slot.Slot + "/" + slot.Identity + "/" + slot.AoId + "/" + slot.HighId + "/" + slot.Ql)) +
                "; refreshing native inventory before further transfers.");
            try
            {
                Logger.Warning("[CityBankers] BAG SHOP receipt inventory " + work.Receipt.TransactionId + ": " +
                    Newtonsoft.Json.JsonConvert.SerializeObject(new { Before = work.Receipt.BeforeSlots, Current = PhysicalSlots() }));
                foreach (string packet in work.NativePackets)
                    Logger.Warning("[CityBankers] BAG SHOP receipt packet " + work.Receipt.TransactionId + ": " + packet);
                if (work.NativePacketsDropped != 0 || work.NativePacketCaptureError != null)
                    Logger.Warning("[CityBankers] BAG SHOP receipt trace " + work.Receipt.TransactionId +
                        "; dropped=" + work.NativePacketsDropped + "; capture error=" + (work.NativePacketCaptureError ?? "none") + ".");
            }
            catch (Exception error) { Logger.Warning("[CityBankers] BAG SHOP receipt trace unavailable: " + error.GetType().Name); }
            // CFU can arrive without an SDK inventory slot. Its next-free-slot
            // guesses must not be used to offer another player's requested item.
            StartupCensusGate.Block("Resolve pending backpack purchase " + work.Receipt.TransactionId + " from fresh native custody.");
        }

        private void CloseBagShop(BagShopWork work)
        {
            StopBagShopApproach(work);
            ReleaseBagShopOffer(work);
            if (work.Opened && Trade.CurrentTarget == work.Shop) Trade.Decline();
        }

        private static void ReleaseBagShopOffer(BagShopWork work)
        {
            if (work?.SelectedOffer == null) return;
            var offers = Trade.TargetWindowCache?.Items;
            if (offers != null) offers.RemoveAll(item => ReferenceEquals(item, work.SelectedOffer));
            work.SelectedOffer = null;
        }

        private void SettleRecoveredBagPurchase()
        {
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
                    RuntimeStorageStateTransactions.UpdateReserveBag(_settingsDir, Client.CharacterName, bag.UniqueIdentity.ToString(),
                        new StorageBagState { Source = location, OuterSlotType = bag.Slot.Type.ToString(),
                            OuterSlotInstance = bag.Slot.Instance & 65535, LastUniqueIdentity = bag.UniqueIdentity.ToString(),
                            LastHandle = 0, Capacity = BankerRoles.SmallBackpackCapacity, Items = new List<StoredItemState>() });
                    enrolled = true;
                }
                work.Receipt.Observed = SnapshotTradeItems(new[] { bag }); work.Receipt.ObservedSlots = after;
                work.Receipt.Phase = "applied";
                ManagerMemory.Current.ChangeReceipt(ManagerAccounting.TransactionId, work.Receipt);
            });
            // Legacy reserve assignments keep their original delivery path.
            if (!enrolled && reserve.Bags.Any(b => b.Identity == bag.UniqueIdentity.ToString()))
                _reserveEmptyArrivals.Add(bag.UniqueIdentity.ToString());
            FinishBagPurchase(work);
            Logger.Information("[CityBankers] BAG SHOP receipt applied " + work.Receipt.TransactionId +
                "; bag=" + bag.UniqueIdentity + "; local storage registered=" + enrolled + ".");
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
