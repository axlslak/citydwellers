using System;
using System.Collections.Generic;
using System.Linq;

namespace CityBankers.Shared
{
    [Serializable]
    public class BankerHealthHeartbeat
    {
        public int ProcessId;
        public DateTime ObservedUtc;
        public string Character;
        public bool InPlay;
        public bool BankOpen;
        public bool BankNeedsId;
        public int BankTerminalInstance;
        public int InventoryFreeSlots;
        public List<InventoryItemSnapshot> InventoryItems;
        internal BankerHealthHeartbeat Copy()
        {
            var value = (BankerHealthHeartbeat)MemberwiseClone();
            value.InventoryItems = InventoryItems?.Select(item => item?.Copy()).ToList();
            return value;
        }
    }

    [Serializable]
    public class InventoryItemSnapshot
    {
        public int Slot;
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        public bool IsContainer;
        public bool? IsStackable;
        public int? Quantity;
        internal InventoryItemSnapshot Copy() => (InventoryItemSnapshot)MemberwiseClone();
    }

}
