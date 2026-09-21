using System;
using System.Collections.Generic;
using System.Linq;

namespace CityBankers.Shared
{
    [Serializable]
    public class BagAuditCommand
    {
        public string RunId;
        public string Role;
        public int BagOpenTimeoutMs = 15000;
        public int BagMoveTimeoutMs = 3000;
        internal BagAuditCommand Copy()
        {
            var value = (BagAuditCommand)MemberwiseClone();

            return value;
        }

    }

    [Serializable]
    public class BagAuditResult
    {
        public List<BagInnerItem> LooseInventoryItems;
        public List<BagInnerItem> LooseBankItems;
        public string RunId;
        public string Role;
        public string Character;
        public DateTime ObservedUtc;
        public int PlayfieldModelId;
        public bool BankOpened;
        public int BankOuterItemCount;
        public int BankBagCount;
        public int InventoryBagCount;
        public int TotalBagCount;
        public int OpenedCount;
        public int FailedCount;
        public int EmptyCount;
        public int NonEmptyCount;
        public int BankStagedCount;
        public int BankReturnedCount;
        public int BankReturnFailureCount;
        public string FatalError;
        public List<BagAuditEntry> Bags;
        internal BagAuditResult Copy()
        {
            var value = (BagAuditResult)MemberwiseClone();
            value.LooseInventoryItems = LooseInventoryItems?.Select(item => item?.Copy()).ToList();
            value.LooseBankItems = LooseBankItems?.Select(item => item?.Copy()).ToList();
            value.Bags = Bags?.Select(item => item?.Copy()).ToList();
            return value;
        }

    }

    [Serializable]
    public class BagAuditEntry
    {
        public int Ordinal;
        public string Source;
        public string OuterSlot;
        public string OuterSlotType;
        public int OuterSlotInstance;
        public string UniqueIdentity;
        public string UniqueIdentityType;
        public int UniqueIdentityInstance;
        public string Name;
        public int LowId;
        public int HighId;
        public int Ql;
        public bool MoveToInventoryAttempted;
        public bool MoveToInventoryCompleted;
        public string StagedInventorySlot;
        public string StagedInventorySlotType;
        public int StagedInventorySlotInstance;
        public int MoveToInventoryElapsedMs;
        public int PreOpenHandle;
        public bool Opened;
        public int Handle;
        public string ContainerIdentity;
        public int ItemCount;
        public int FreeSlots;
        public int OpenElapsedMs;
        public bool ReturnToBankAttempted;
        public bool ReturnedToBank;
        public string ReturnedOuterSlot;
        public string ReturnedOuterSlotType;
        public int ReturnedOuterSlotInstance;
        public bool ReturnedToOriginalOuterSlot;
        public int ReturnToBankElapsedMs;
        public string Error;
        public List<BagInnerItem> Items;
        internal BagAuditEntry Copy()
        {
            var value = (BagAuditEntry)MemberwiseClone();
            value.Items = Items?.Select(item => item?.Copy()).ToList();
            return value;
        }

    }

    [Serializable]
    public class BagInnerItem
    {
        public string Slot;
        public string SlotType;
        public int SlotInstance;
        public string UniqueIdentity;
        public string Name;
        public int LowId;
        public int HighId;
        public int Ql;
        internal BagInnerItem Copy()
        {
            var value = (BagInnerItem)MemberwiseClone();

            return value;
        }

    }
}
