using System;
using System.Collections.Generic;
using System.Linq;
using AOSharp.Clientless;
using CityBankers.Shared;
using AOSharp.Common.GameData;

namespace CityBankers
{
    internal static class StorageBagPolicy
    {
        public const int ColonistBackpackId = BankerPersonalItems.ColonistBackpackId;
        public const int SmallBackpackId = 99228;
        public static bool IsColonist(Item item) => item != null &&
            (item.Id == ColonistBackpackId || item.HighId == ColonistBackpackId);
        public static bool IsNormalInventory(Item item) => item != null &&
            item.Slot.Type == IdentityType.Inventory &&
            item.Slot.Instance >= Inventory.INVENTORY_START &&
            item.Slot.Instance < Inventory.INVENTORY_END;
        public static bool IsStorageBag(Item item) => item != null &&
            item.UniqueIdentity.Type == IdentityType.Container &&
            (item.Id == SmallBackpackId || item.HighId == SmallBackpackId) && !IsColonist(item) &&
            (IsNormalInventory(item) || item.Slot.Type == IdentityType.BankByRef);
        public static bool IsStorageContainer(Container container) => container != null &&
            ((Inventory.Items != null && Inventory.Items.Any(i => IsStorageBag(i) && i.UniqueIdentity == container.Identity)) ||
             (Inventory.Bank.Items != null && Inventory.Bank.Items.Any(i => IsStorageBag(i) && i.UniqueIdentity == container.Identity)));
        public static bool IsSmallBackpack(Item item) => IsStorageBag(item) &&
            (item.Id == SmallBackpackId || item.HighId == SmallBackpackId);

        // One outer record of one physical bag. "bank" or "inventory" names the
        // listing the record came from, not a claim about where the bag is.
        public sealed class BagRecord
        {
            public string Location;
            public Item Bag;
            public Identity Identity { get { return Bag.UniqueIdentity; } }
            public int OuterSlot { get { return Bag.Slot.Instance & 65535; } }
            public override string ToString() { return Location + "/" + OuterSlot; }
        }

        // Every outer storage record the client currently lists, in the client's
        // own order. That order carries information: a record the client adds for
        // a move is appended, so within one identity the later record is the newer
        // one. Callers that want a walk order sort afterwards.
        public static List<BagRecord> AllBagRecords()
        {
            var records = new List<BagRecord>();
            if (Inventory.Bank.Items != null)
                records.AddRange(Inventory.Bank.Items.Where(IsStorageBag)
                    .Select(i => new BagRecord { Location = "bank", Bag = i }));
            if (Inventory.Items != null)
                records.AddRange(Inventory.Items.Where(i => IsNormalInventory(i) && IsStorageBag(i))
                    .Select(i => new BagRecord { Location = "inventory", Bag = i }));
            return records;
        }

        // A container identity belongs to exactly one physical bag, so a second
        // record for the same identity is a stale client entry, not another bag.
        // The bot's own bag moves create them: when the client misses the removal
        // at the source slot, the record it adds at the destination is an extra.
        // Both records resolve to the same container, so working from either one
        // reaches the same physical bag; enumerate once and keep the order stable.
        public static List<BagRecord> DistinctBags()
        {
            return AllBagRecords()
                .GroupBy(r => r.Identity)
                // Keep the newest record of each bag. The stale one is the entry
                // whose removal was missed, so it is the older of the two, and the
                // record the client appended for the move is where the bag now is.
                .Select(g => g.Last())
                .OrderBy(r => r.Location == "bank" ? 0 : 1)
                .ThenBy(r => r.OuterSlot)
                .ToList();
        }

        public static string DescribeDuplicates()
        {
            var records = AllBagRecords();
            var groups = records.GroupBy(r => r.Identity).Where(g => g.Count() != 1).ToList();
            if (groups.Count == 0) return null;
            return string.Join("; ", groups.Select(g =>
                g.Key + " listed at " + string.Join(", ", g.Select(r => r.ToString())) +
                ", keeping " + g.Last())) +
                ". A container identity belongs to exactly one bag, so the extra entries are stale " +
                "client records. Storage bag entries=" + records.Count +
                "; distinct identities=" + records.Select(r => r.Identity).Distinct().Count() + ".";
        }

        public static bool TryValidatePhysicalLayout(out string error)
        {
            error = null;
            var outer = (Inventory.Bank.Items ?? new List<Item>()).Where(i => i != null)
                .Select(i => new { Location = "bank", Item = i })
                .Concat((Inventory.Items ?? new List<Item>()).Where(IsNormalInventory)
                    .Select(i => new { Location = "inventory", Item = i })).ToList();
            // Two different items in one slot is a contradiction the client cannot
            // resolve. Two records of one uniquely identified item are not: that is
            // the same physical thing listed twice. Items with no unique identity
            // cannot be told apart, so a repeated slot among those still stops here.
            var repeatedSlot = outer.GroupBy(x => x.Location + "/" + (x.Item.Slot.Instance & 65535))
                .FirstOrDefault(g => g.Count() != 1 &&
                    (g.Select(x => x.Item.UniqueIdentity).Distinct().Count() != 1 ||
                     g.First().Item.UniqueIdentity.Instance == 0));
            if (repeatedSlot != null)
            {
                error = "More than one outer item occupies " + repeatedSlot.Key + ".";
                return false;
            }
            // A bag with no container identity cannot be opened, moved to a known
            // place, or matched to stock, so it still stops the audit.
            var unusable = outer.Where(x => IsStorageBag(x.Item) && x.Item.UniqueIdentity.Instance == 0)
                .ToList();
            if (unusable.Count != 0)
            {
                var sample = unusable[0].Item;
                error = "Storage bag" +
                    (string.IsNullOrEmpty(sample.Name) ? "" : " '" + sample.Name + "'") +
                    " QL" + sample.Ql + " at " +
                    string.Join(", ", unusable.Select(x => x.Location + "/" + (x.Item.Slot.Instance & 65535))) +
                    " has no container identity.";
                return false;
            }
            return true;
        }
    }
}
