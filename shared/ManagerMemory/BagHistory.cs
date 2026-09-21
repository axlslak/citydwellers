using System;
using System.Collections.Generic;
using System.Linq;

namespace CityBankers
{
    [Serializable]
    public sealed class BagHistoryItem
    {
        public int Slot, Low, High, Ql, Quantity;
        public string Identity;
        internal BagHistoryItem Copy() => (BagHistoryItem)MemberwiseClone();
    }
    [Serializable]
    public sealed class BagHistoryTransfer
    {
        public string Id, AnchorId, OriginalLocation, AnchorTransaction;
        public DateTime AnchorReceivedUtc, VerifiedUtc;
        public int? OriginalBag, OriginalSlot;
        public int SourceBag, TargetBag, TargetSlot;
        public BagHistoryItem Item;
        internal BagHistoryTransfer Copy()
        { var copy = (BagHistoryTransfer)MemberwiseClone(); copy.Item = Item?.Copy(); return copy; }
    }
    [Serializable]
    public sealed class BagHistoryRecord
    {
        public string Run, Character, Phase, PendingActionId, PendingActionKind;
        public int Bag;
        public DateTime UpdatedUtc, ReconciledUtc;
        public bool EmptyShellsDisappeared;
        public List<BagHistoryTransfer> Transfers = new List<BagHistoryTransfer>();
        public List<BagHistoryTransfer> Disposals = new List<BagHistoryTransfer>();
        internal BagHistoryRecord Copy()
        {
            var copy = (BagHistoryRecord)MemberwiseClone();
            copy.Transfers = Transfers.Select(row => row.Copy()).ToList();
            copy.Disposals = Disposals.Select(row => row.Copy()).ToList();
            return copy;
        }
    }
}
