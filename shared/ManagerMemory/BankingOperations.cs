using System;
using System.Collections.Generic;
using System.Linq;
using CityBankers.Shared;

namespace CityBankers
{
    [Serializable]
    public sealed class ExtractionSlot
    {
        public int Slot;
        public TransferItemState Item;
        internal ExtractionSlot Copy()
        {
            var copy = (ExtractionSlot)MemberwiseClone();
            copy.Item = Item?.Copy();
            return copy;
        }
    }

    [Serializable]
    public sealed class ExtractionProof
    {
        public string Id;
            public string Phase;
        public string LedgerId;
        public string TransactionId;
        public string Character;
        public string Role;
        public string Source;
        public int? Bag;
        public string BagIdentity;
        public int SourceSlot;
        public int? FinalBagSlot;
        public int InventorySlot;
        public TransferItemState Item;
        public DateTime RecordedUtc;
        public List<ExtractionSlot> BeforeSource;
        public List<ExtractionSlot> AfterSource;
        public List<ExtractionSlot> BeforeInventory;
        public List<ExtractionSlot> AfterInventory;
        internal ExtractionProof Copy()
        {
            var copy = (ExtractionProof)MemberwiseClone();
            copy.Item = Item?.Copy();
            copy.BeforeSource = BeforeSource?.Select(item => item?.Copy()).ToList();
            copy.AfterSource = AfterSource?.Select(item => item?.Copy()).ToList();
            copy.BeforeInventory = BeforeInventory?.Select(item => item?.Copy()).ToList();
            copy.AfterInventory = AfterInventory?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }

    [Serializable]
    public sealed class ReturnOffer
        {
            public string Id;
            public string Phase;
            public string LedgerId;
            public string TransactionId;
            public string Source;
            public int SourceSlot;
            public TransferItemState Item;
            public DateTime StartedUtc;
            public DateTime? CompletedUtc;
            internal ReturnOffer Copy()
        {
            var copy = (ReturnOffer)MemberwiseClone();
            copy.Item = Item?.Copy();
            return copy;
        }
    }

    [Serializable]
    public sealed class ReceiptEvidence
        {
            public string Id;
            public string Kind;
            public string TransactionId;
            public string BatchId;
            public string AttemptId;
            public List<TransferItemState> PreparedItems;
            public string Character;
            public string Phase;
            public List<TransferItemState> Before;
            public List<TransferItemState> Expected;
            public List<TransferItemState> Observed;
            public int? WithdrawalArrivalSlot;
            public List<CustodySlot> BeforeSlots;
            public List<CustodySlot> ObservedSlots;
            public int Direction;
            public List<string> LedgerIds;
            internal ReceiptEvidence Copy()
        {
            var copy = (ReceiptEvidence)MemberwiseClone();
            copy.PreparedItems = PreparedItems?.Select(item => item?.Copy()).ToList();
            copy.Before = Before?.Select(item => item?.Copy()).ToList();
            copy.Expected = Expected?.Select(item => item?.Copy()).ToList();
            copy.Observed = Observed?.Select(item => item?.Copy()).ToList();
            copy.BeforeSlots = BeforeSlots?.Select(item => item?.Copy()).ToList();
            copy.ObservedSlots = ObservedSlots?.Select(item => item?.Copy()).ToList();
            copy.LedgerIds = LedgerIds?.ToList();
            return copy;
        }
    }

    [Serializable]
    public sealed class CustodySlot
    {
        public string Slot, Identity, Name;
        public int AoId, HighId, Ql, Quantity;
        internal CustodySlot Copy() => (CustodySlot)MemberwiseClone();
    }
}

namespace CityBankers
{
    [Serializable]
    public sealed class ReserveTarget
    {
        public string Character;
        public int Count;
        internal ReserveTarget Copy() => (ReserveTarget)MemberwiseClone();
    }
    [Serializable]
    public sealed class BagReserve
    {
        public List<ReserveBag> Bags = new List<ReserveBag>();
        public List<ReserveTarget> Targets = new List<ReserveTarget>();
        public bool TryGetTarget(string character, out int count)
        {
            var target = Targets.SingleOrDefault(row => string.Equals(row.Character, character, StringComparison.OrdinalIgnoreCase));
            count = target?.Count ?? 0; return target != null;
        }
        public void SetTarget(string character, int count)
        {
            var target = Targets.SingleOrDefault(row => string.Equals(row.Character, character, StringComparison.OrdinalIgnoreCase));
            if (target == null) Targets.Add(new ReserveTarget { Character = character, Count = count });
            else target.Count = count;
        }
        internal BagReserve Copy() => new BagReserve { Bags = Bags.Select(row => row.Copy()).ToList(), Targets = Targets.Select(row => row.Copy()).ToList() };
    }
    [Serializable]
    public sealed class ReserveBag
    {
        public string Identity, Transaction, Destination, Role, Batch, EmptyProofBatch;
        public bool Quarantined, Delivered;
        internal ReserveBag Copy() => (ReserveBag)MemberwiseClone();
    }
    [Serializable]
    public sealed class ReserveOperation
    {
        public string Character, Identity, Purpose, Phase;
        public int SourceSlot, ReadRetries;
        public DateTime StartedUtc;
        public long ObservationAfter;
        public bool SenderVerifiedEmpty;
        internal ReserveOperation Copy() => (ReserveOperation)MemberwiseClone();
    }
}

namespace CityBankers
{
    [Serializable]
    public sealed class CancellationPair
    {
        public string AttemptId, Outcome;
        public ReceiptEvidence Sender, Receiver;
        public DispatchBatchState OriginalBatch;
        internal CancellationPair Copy() => new CancellationPair { AttemptId = AttemptId, Outcome = Outcome,
            Sender = Sender?.Copy(), Receiver = Receiver?.Copy(), OriginalBatch = OriginalBatch?.Copy() };
    }
}
