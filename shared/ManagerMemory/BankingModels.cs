using System;
using System.Collections.Generic;
using System.Linq;

namespace CityBankers.Shared
{
    [Serializable]
    public class StorageState
    {
        public string Format = "citybankers-storage-state-v1";
        public string BaselineRunId;
        public DateTime UpdatedUtc;
        public List<StorageWorkerState> Workers = new List<StorageWorkerState>();
        internal StorageState Copy()
        {
            var copy = (StorageState)MemberwiseClone();
            copy.Workers = Workers?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class StorageWorkerState
    {
        public string Role;
        public string Character;
        public DateTime ObservedUtc;
        public List<StorageBagState> Bags = new List<StorageBagState>();
        internal StorageWorkerState Copy()
        {
            var copy = (StorageWorkerState)MemberwiseClone();
            copy.Bags = Bags?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class StorageBagState
    {
        public string Source;
        public string OuterSlotType;
        public int OuterSlotInstance;
        public string LastUniqueIdentity;
        public int LastHandle;
        public int Capacity = 21;
        public List<StoredItemState> Items = new List<StoredItemState>();
        internal StorageBagState Copy()
        {
            var copy = (StorageBagState)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class StoredItemState
    {
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        public int InnerSlot;
        public DateTime ObservedUtc;
        public string TransactionId;
        internal StoredItemState Copy()
        {
            var copy = (StoredItemState)MemberwiseClone();

            return copy;
        }
    }

    [Serializable]
    public class CurrentStockState
    {
        public string Format = "citybankers-current-stock-v1";
        public string BaselineRunId;
        public DateTime UpdatedUtc;
        public List<StockItemState> Items = new List<StockItemState>();
        internal CurrentStockState Copy()
        {
            var copy = (CurrentStockState)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class StockItemState
    {
        public string TransactionId;
        public string Role;
        public string PhysicalRole;
        public bool RouteMatchesPhysicalRole;
        public string Character;
        public string BagSource;
        public int BagOuterSlot;
        public int InnerSlot;
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        public DateTime ObservedUtc;
        internal StockItemState Copy()
        {
            var copy = (StockItemState)MemberwiseClone();

            return copy;
        }
    }

    [Serializable]
    public class DispatchQueueState
    {
        public string Format = "citybankers-dispatch-queue-v1";
        public DateTime UpdatedUtc;
        public List<DispatchBatchState> Batches = new List<DispatchBatchState>();
        internal DispatchQueueState Copy()
        {
            var copy = (DispatchQueueState)MemberwiseClone();
            copy.Batches = Batches?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class DispatchBatchState
    {
        public string BatchId;
        public string AttemptId;
        public string LastCancelledAttempt;
        public string TransactionId;
        public string Role;
        public string Character;
        public string Status;
        public DateTime CreatedUtc;
        public DateTime UpdatedUtc;
        public int AttemptCount;
        // Set only by a failed source check before any dispatch command/trade is issued.
        public bool TransferNeverStarted;
        // Central has since rebuilt routing from a full physical census.
        // Old cancellation receipts must not enqueue a second copy of that plan.
        public bool RequiresPairedCensus;
        public string LastError;
        public List<TransferItemState> Items = new List<TransferItemState>();
        internal DispatchBatchState Copy()
        {
            var copy = (DispatchBatchState)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class TransferItemState
    {
        public int Quantity = 1;
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        internal TransferItemState Copy()
        {
            var copy = (TransferItemState)MemberwiseClone();

            return copy;
        }
    }

    [Serializable]
    public class DispatchCommand
    {
        public string Format = "citybankers-dispatch-command-v1";
        public string BatchId;
        public string AttemptId;
        public string TransactionId;
        public string Role;
        public string SourceCharacter;
        public string DestinationCharacter;
        public DateTime CreatedUtc;
        public List<TransferItemState> Items = new List<TransferItemState>();
        internal DispatchCommand Copy()
        {
            var copy = (DispatchCommand)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class StorageBatchResult
    {
        public string Format = "citybankers-storage-result-v1";
        public string BatchId;
        public string TransactionId;
        public string Role;
        public string Character;
        public DateTime CompletedUtc;
        public bool Success;
        public int ExpectedCount;
        public int StoredCount;
        public string Error;
        internal StorageBatchResult Copy()
        {
            var copy = (StorageBatchResult)MemberwiseClone();

            return copy;
        }
    }

    [Serializable]
    public class LedgerRecord
    {
        public string Id;
        public string Format = "citybankers-ledger-v1";
        public DateTime Utc;
        public string Event;
        public string TransactionId;
        public string BatchId;
        public string Actor;
        public string Role;
        public string Character;
        public string Source;
        public string Destination;
        public string Message;
        public List<LedgerItem> Items;
        internal LedgerRecord Copy()
        {
            var copy = (LedgerRecord)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public class LedgerItem
    {
        public int Quantity = 1;
        public string UniqueIdentity;
        public int AoId;
        public int HighId;
        public int Ql;
        public string Name;
        public string Role;
        public string BagSource;
        public int? BagOuterSlot;
        public int? InnerSlot;
        internal LedgerItem Copy()
        {
            var copy = (LedgerItem)MemberwiseClone();

            return copy;
        }
    }

}

namespace CityBankers.Shared
{
    [Serializable]
    public sealed class WithdrawalState
    {
        public string Format = "citybankers-withdrawal-v2";
        public string Id;
        public string OrderId;
        public long Revision;
        public int RecoveryAttempts;
        public int LiveInventoryRecoveryAttempts;
        public int LiveInventoryAnchorAttempts;
        public string Status;
        public DateTime CreatedUtc;
        public DateTime UpdatedUtc;
        public DateTime? PickupExpiresUtc;
        public string PickupHostGeneration;
        public long PickupDeadlineStamp;
        public DateTime? DeliveredUtc;
        public string RequestedBy;
        public string RecipientMain;
        public List<string> AllowedCharacters = new List<string>();
        public string ActiveLedgerId;
        public string DonationTransactionId;
        public string SourceRole;
        public string SourceCharacter;
        public string SourceBag;
        public int SourceBagOuterSlot;
        public int SourceInnerSlot;
        public string SourceItemIdentity;
        public List<int> PreExtractionInventorySlots = new List<int>();
        public string ExtractedItemIdentity;
        public int? LiveInventoryAnchorSlot;
        public string LiveInventoryAnchorIdentity;
        public string CentralItemIdentity;
        public string TransferAttemptId;
        public string ReturnBatchId;
        public string ReconciledByCensus;
        public string RecoveryCensusId;
        public string Error;
        public TransferItemState Item;
        internal WithdrawalState Copy()
        {
            var copy = (WithdrawalState)MemberwiseClone();
            copy.AllowedCharacters = AllowedCharacters?.ToList();
            copy.PreExtractionInventorySlots = PreExtractionInventorySlots?.ToList();
            copy.Item = Item?.Copy();
            return copy;
        }
    }

    [Serializable]
    public sealed class WithdrawalQueueState
    {
        public string Format = "citybankers-withdrawal-queue-v2";
        public List<WithdrawalState> Withdrawals = new List<WithdrawalState>();
        internal WithdrawalQueueState Copy()
        {
            var copy = (WithdrawalQueueState)MemberwiseClone();
            copy.Withdrawals = Withdrawals?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

}

namespace CityBankers
{
    [Serializable]
    public sealed class ActiveLedgerState
    {
        public string Format = "citybankers-active-ledger-v1";
        public DateTime UpdatedUtc;
        public List<ActiveLedgerItem> Items = new List<ActiveLedgerItem>();
        internal ActiveLedgerState Copy()
        {
            var copy = (ActiveLedgerState)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public sealed class ActiveLedgerItem
    {
        public string Id;
        public string Name;
        public int AoId;
        public int? HighId;
        public int? Ql;
        public string TransactionId;
        public string From;
        public DateTime ReceivedUtc;
        public string Family;
        public string Character;
        public string Location;
        public int? Bag;
        public int? Slot;
        internal ActiveLedgerItem Copy()
        {
            var copy = (ActiveLedgerItem)MemberwiseClone();

            return copy;
        }
    }

}

namespace CityBankers.Shared
{
    [Serializable]
    public sealed class LostItemRecord
    {
        public string IncidentId;
        public string LedgerId;
        public string ItemName;
        public int Quantity = 1;
        public DateTime DiscoveredUtc;
        public DateTime? RemovalRecordedUtc;
        public string ExcludedReason;
        public string Reason;
        public string Evidence;
        // Preserve the complete original claim, including donor, receipt time and location.
        public CityBankers.ActiveLedgerItem PreviousLedgerEntry;
        internal LostItemRecord Copy()
        {
            var copy = (LostItemRecord)MemberwiseClone();
            copy.PreviousLedgerEntry = PreviousLedgerEntry?.Copy();
            return copy;
        }
    }

    [Serializable]
    public sealed class LostItemsState
    {
        public string Format = "citybankers-lost-items-v1";
        public List<LostItemRecord> Entries = new List<LostItemRecord>();
        internal LostItemsState Copy()
        {
            var copy = (LostItemsState)MemberwiseClone();
            copy.Entries = Entries?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

}

namespace CityBankers
{
    [Serializable]
    public sealed class SymbiantIndexState
    {
        public string Format = "citybankers-symbiant-index-v1";
        public DateTime UpdatedUtc;
        public List<SymbiantIndexItem> Items = new List<SymbiantIndexItem>();
        internal SymbiantIndexState Copy()
        {
            var copy = (SymbiantIndexState)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public sealed class SymbiantIndexItem
    {
        public int AoId;
        public string Family;
        public int? HighId;
        public int? Ql;
        public string Name;
        public string Slot;
        internal SymbiantIndexItem Copy()
        {
            var copy = (SymbiantIndexItem)MemberwiseClone();

            return copy;
        }
    }

    [Serializable]
    public sealed class ActiveHistoryRecord
    {
        public string Id;
        public string Format = "citybankers-history-v1";
        public DateTime LeftUtc;
        public string Reason;
        public string Recipient;
        public ActiveLedgerItem Item;
        public ActiveLedgerItem CurrentItem;
        public string Source, ItemName;
        public bool EventTimeKnown = true;
        internal ActiveHistoryRecord Copy()
        {
            var copy = (ActiveHistoryRecord)MemberwiseClone();
            copy.Item = Item?.Copy();
            copy.CurrentItem = CurrentItem?.Copy();
            return copy;
        }
    }
}

namespace CityBankers.Shared
{
    [Serializable]
    public sealed class RecoveryReservation
        {
            public string OperationId;
            public string LedgerId;
            public string CensusCharacter;
            public bool CensusCentral;
            public string WithdrawalCensusId;
        internal RecoveryReservation Copy() => (RecoveryReservation)MemberwiseClone();
        }
}
