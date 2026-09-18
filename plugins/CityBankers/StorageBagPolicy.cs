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

        public static bool TryValidatePhysicalLayout(out string error)
        {
            error = null;
            var outer = Inventory.Bank.Items.Where(i => i != null)
                .Select(i => new { Location = "bank", Item = i })
                .Concat(Inventory.Items.Where(IsNormalInventory)
                    .Select(i => new { Location = "inventory", Item = i })).ToList();
            var repeatedSlot = outer.GroupBy(x => x.Location + "/" + (x.Item.Slot.Instance & 65535))
                .FirstOrDefault(g => g.Count() != 1);
            if (repeatedSlot != null)
                error = "More than one outer item occupies " + repeatedSlot.Key + ".";
            var storageBags = outer.Where(x => IsStorageBag(x.Item)).ToList();
            var repeatedBag = storageBags
                .GroupBy(x => x.Item.UniqueIdentity).FirstOrDefault(g => g.Key.Instance == 0 || g.Count() != 1);
            if (repeatedBag != null)
            {
                var sample = repeatedBag.First().Item;
                string label = (string.IsNullOrEmpty(sample.Name) ? "" : " '" + sample.Name + "'") +
                    " QL" + sample.Ql;
                string where = string.Join(", ",
                    repeatedBag.Select(x => x.Location + "/" + (x.Item.Slot.Instance & 65535)));
                if (repeatedBag.Key.Instance == 0)
                    error = "Storage bag" + label + " at " + where + " has no container identity.";
                else
                    // A container identity belongs to exactly one physical bag, so a
                    // second entry is a stale client record rather than another bag.
                    // Report the counts that make that visible instead of leaving the
                    // operator with a bare identity to chase.
                    error = "Ambiguous bag " + repeatedBag.Key + label + " reported at " + where +
                        ". A container identity belongs to exactly one bag, so one of those entries is a " +
                        "stale client record, not a second bag. Storage bag entries=" + storageBags.Count +
                        "; distinct identities=" +
                        storageBags.Select(x => x.Item.UniqueIdentity).Distinct().Count() + ".";
            }
            return error == null;
        }
    }
}
