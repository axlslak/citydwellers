using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BuffHistoryEntry
    {
        public string Id, Recipient, Caster, NanoName;
        public int RecipientId, NanoId, BuffProfession, CasterProfession;
        public bool Paid;
        public DateTime CompletedUtc;
        internal BuffHistoryEntry Copy() => (BuffHistoryEntry)MemberwiseClone();
    }

    [Serializable]
    public sealed class BuffHistoryRank
    {
        public string Name;
        public int NanoId;
        public long Count;
        internal BuffHistoryRank Copy() => (BuffHistoryRank)MemberwiseClone();
    }

    [Serializable]
    public sealed class BuffHistorySnapshot
    {
        public string Recipient;
        public long Total, Paid;
        public DateTime? FirstUtc;
        public int Page, Pages;
        public BuffHistoryEntry[] Recent;
        public BuffHistoryRank[] Buffs, Recipients;
    }

    public interface IBuffHistoryPersistence
    {
        void AppendBuffHistory(BuffHistoryEntry entry);
    }

    public sealed partial class ManagerMemory
    {
        private sealed class BuffHistoryBucket
        {
            internal readonly List<BuffHistoryEntry> Entries = new List<BuffHistoryEntry>();
            internal readonly Dictionary<int, BuffHistoryRank> Buffs = new Dictionary<int, BuffHistoryRank>();
            internal long Paid;
        }

        private readonly HashSet<string> _buffHistoryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly BuffHistoryBucket _buffHistory = new BuffHistoryBucket();
        private readonly Dictionary<string, BuffHistoryBucket> _buffHistoryPeople =
            new Dictionary<string, BuffHistoryBucket>(StringComparer.OrdinalIgnoreCase);
        private IBuffHistoryPersistence _buffHistoryPersistence;
        private static readonly IComparer<BuffHistoryEntry> BuffHistoryOrder = Comparer<BuffHistoryEntry>.Create((a, b) =>
        {
            int time = a.CompletedUtc.CompareTo(b.CompletedUtc);
            return time != 0 ? time : string.CompareOrdinal(a.Id, b.Id);
        });

        public static void InitializeBuffHistory(BuffHistoryEntry[] entries, IBuffHistoryPersistence persistence)
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only ManagerHost owns buff history persistence.");
            lock (Current._accountingSync)
            {
                if (Current._buffHistoryPersistence != null) throw new InvalidOperationException("Buff history is already initialized.");
                Current._buffHistoryPersistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
                foreach (var entry in entries.OrderBy(e => e.CompletedUtc).ThenBy(e => e.Id, StringComparer.Ordinal))
                    Current.RememberBuffHistory(entry.Copy());
            }
        }

        // The existing positive cast-completion path calls this once. SQL belongs
        // to Manager; queries and aggregation use RAM, separate from bank ledgers.
        public void RecordBuffDelivered(BuffHistoryEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id) || entry.NanoId <= 0 ||
                entry.RecipientId == 0 || string.IsNullOrWhiteSpace(entry.Recipient) ||
                string.IsNullOrWhiteSpace(entry.Caster) || entry.CompletedUtc == default(DateTime))
                throw new ArgumentException("A completed buff and its actual recipient are required.");
            lock (_accountingSync)
            {
                if (_buffHistoryPersistence == null) throw new InvalidOperationException("Buff history is not initialized.");
                if (_buffHistoryIds.Contains(entry.Id)) return;
                var copy = entry.Copy();
                _buffHistoryPersistence.AppendBuffHistory(copy);
                RememberBuffHistory(copy);
                RecordBuffGiven(copy.Paid, copy.BuffProfession, copy.CasterProfession);
            }
        }

        private void RememberBuffHistory(BuffHistoryEntry entry)
        {
            if (!_buffHistoryIds.Add(entry.Id)) return;
            AddBuffHistory(_buffHistory, entry);
            BuffHistoryBucket person;
            if (!_buffHistoryPeople.TryGetValue(entry.Recipient, out person))
                _buffHistoryPeople.Add(entry.Recipient, person = new BuffHistoryBucket());
            AddBuffHistory(person, entry);
        }

        private static void AddBuffHistory(BuffHistoryBucket bucket, BuffHistoryEntry entry)
        {
            // Usually append; retain UTC ordering even after a clock correction.
            int position = bucket.Entries.Count;
            if (position > 0 && BuffHistoryOrder.Compare(bucket.Entries[position - 1], entry) > 0)
                position = ~bucket.Entries.BinarySearch(entry, BuffHistoryOrder);
            bucket.Entries.Insert(position, entry);
            if (entry.Paid) bucket.Paid++;
            BuffHistoryRank buff;
            if (!bucket.Buffs.TryGetValue(entry.NanoId, out buff))
                bucket.Buffs.Add(entry.NanoId, buff = new BuffHistoryRank { NanoId = entry.NanoId, Name = entry.NanoName });
            buff.Count++;
        }

        public BuffHistorySnapshot ReadBuffHistory(string recipient, int page)
        {
            lock (_accountingSync)
            {
                BuffHistoryBucket bucket;
                if (string.IsNullOrEmpty(recipient)) bucket = _buffHistory;
                else if (!_buffHistoryPeople.TryGetValue(recipient, out bucket)) bucket = new BuffHistoryBucket();
                const int pageSize = 20;
                int pages = Math.Max(1, (int)((bucket.Entries.Count + (long)pageSize - 1) / pageSize));
                page = Math.Max(1, Math.Min(pages, page));
                int last = bucket.Entries.Count - 1 - (page - 1) * pageSize;
                var recent = new List<BuffHistoryEntry>();
                for (int i = last; i >= 0 && recent.Count < pageSize; i--) recent.Add(bucket.Entries[i].Copy());
                return new BuffHistorySnapshot {
                    Recipient = string.IsNullOrEmpty(recipient) ? null : bucket.Entries.LastOrDefault()?.Recipient ?? recipient,
                    Total = bucket.Entries.Count, Paid = bucket.Paid,
                    FirstUtc = bucket.Entries.Count == 0 ? (DateTime?)null : bucket.Entries[0].CompletedUtc,
                    Page = page, Pages = pages, Recent = recent.ToArray(),
                    Buffs = bucket.Buffs.Values.OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.NanoId).Take(20).Select(r => r.Copy()).ToArray(),
                    Recipients = string.IsNullOrEmpty(recipient) ? _buffHistoryPeople.Select(p => new BuffHistoryRank {
                        Name = p.Value.Entries.Last().Recipient, Count = p.Value.Entries.Count })
                        .OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(20).ToArray()
                        : new BuffHistoryRank[0]
                };
            }
        }
    }
}
