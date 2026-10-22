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
        public string Key => Low + "/" + High + "/" + Ql + "/" + Identity;
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

    [Serializable]
    public sealed class BagRecoveryAddress
    {
        public int Type, Slot, Bag;
        public string Key => Type + "/" + Slot + "/" + Bag;
        internal BagRecoveryAddress Copy() => (BagRecoveryAddress)MemberwiseClone();
    }

    [Serializable]
    public sealed class BagRecoveryTarget
    {
        public int Bag;
        public List<BagHistoryItem> Items = new List<BagHistoryItem>();
        internal BagRecoveryTarget Copy() => new BagRecoveryTarget
        { Bag = Bag, Items = Items.Select(i => i.Copy()).ToList() };
    }

    [Serializable]
    public sealed class BagRecoveryReadFailure
    {
        public int Bag, Count;
        internal BagRecoveryReadFailure Copy() => (BagRecoveryReadFailure)MemberwiseClone();
    }

    [Serializable]
    public sealed class BagRecoveryIntent
    {
        public string Id, Kind, Epoch;
        public BagRecoveryAddress Source;
        public int TargetBag;
        public List<BagRecoveryAddress> Before = new List<BagRecoveryAddress>();
        public List<BagHistoryItem> SourceBefore = new List<BagHistoryItem>();
        public List<BagHistoryItem> TargetBefore = new List<BagHistoryItem>();
        public BagHistoryTransfer Transfer;
        internal BagRecoveryIntent Copy()
        {
            var copy = (BagRecoveryIntent)MemberwiseClone();
            copy.Source = Source?.Copy();
            copy.Before = Before.Select(a => a.Copy()).ToList();
            copy.SourceBefore = SourceBefore.Select(i => i.Copy()).ToList();
            copy.TargetBefore = TargetBefore.Select(i => i.Copy()).ToList();
            copy.Transfer = Transfer?.Copy();
            return copy;
        }
    }

    // One interrupted, affected-banker repair. Intent and conservation evidence
    // live in Manager RAM and ordinary relational rows, before any AO mutation.
    [Serializable]
    public sealed class BagRecoveryState
    {
        public string Format = "citybankers-shared-bag-recovery-v2";
        public string Run, Character, Phase, Epoch, FirstReadRefreshEpoch, LastUnappliedAddress;
        public int Bag, UnappliedAttempts;
        public DateTime UpdatedUtc, RetryAfterUtc;
        public bool EmptyShellsDisappeared, BaselineRecorded;
        public List<BagRecoveryAddress> Original = new List<BagRecoveryAddress>();
        public List<int> Destinations = new List<int>();
        public List<int> FullTargets = new List<int>();
        public List<BagHistoryTransfer> Transfers = new List<BagHistoryTransfer>();
        public List<BagHistoryItem> Baseline = new List<BagHistoryItem>();
        public List<BagRecoveryTarget> TargetBaselines = new List<BagRecoveryTarget>();
        public List<BagRecoveryReadFailure> ReadFailures = new List<BagRecoveryReadFailure>();
        public BagRecoveryIntent Pending;
        internal BagRecoveryState Copy()
        {
            var copy = (BagRecoveryState)MemberwiseClone();
            copy.Original = Original.Select(a => a.Copy()).ToList();
            copy.Destinations = new List<int>(Destinations);
            copy.FullTargets = new List<int>(FullTargets);
            copy.Transfers = Transfers.Select(t => t.Copy()).ToList();
            copy.Baseline = Baseline.Select(i => i.Copy()).ToList();
            copy.TargetBaselines = TargetBaselines.Select(t => t.Copy()).ToList();
            copy.ReadFailures = ReadFailures.Select(f => f.Copy()).ToList();
            copy.Pending = Pending?.Copy();
            return copy;
        }
    }
}
