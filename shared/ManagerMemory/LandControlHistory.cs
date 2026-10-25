using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class LandControlEvent
    {
        public string Id, Kind, Message, RawMessage, Source, Observer;
        public string AttackerSide, AttackerOrg, Attacker, DefenderSide, DefenderOrg, Playfield, Tower;
        public int? X, Y, HealthPercent, GroupType, ChannelId, Category, Instance;
        public DateTime ObservedUtc;
        public long Sequence;
        internal LandControlEvent Copy() => (LandControlEvent)MemberwiseClone();
    }

    [Serializable]
    public sealed class LandControlSnapshot
    {
        public long Total;
        public int Page, Pages;
        public DateTime? FirstUtc;
        public LandControlEvent[] Recent;
    }

    public interface ILandControlPersistence
    {
        void AppendLandControlEvent(LandControlEvent entry);
        void AppendLandControlReport(LandControlReport report);
    }

    public sealed partial class ManagerMemory
    {
        private readonly List<LandControlEvent> _landControlEvents = new List<LandControlEvent>();
        private readonly HashSet<string> _landControlIds = new HashSet<string>(StringComparer.Ordinal);
        private ILandControlPersistence _landControlPersistence;
        private static readonly IComparer<LandControlEvent> LandControlOrder = Comparer<LandControlEvent>.Create((a, b) =>
        {
            int time = a.ObservedUtc.CompareTo(b.ObservedUtc);
            int sequence = a.Sequence.CompareTo(b.Sequence);
            return time != 0 ? time : sequence != 0 ? sequence : string.CompareOrdinal(a.Id, b.Id);
        });

        public static void InitializeLandControl(LandControlEvent[] entries, LandControlReport[] reports, ILandControlPersistence persistence)
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only ManagerHost owns land control persistence.");
            lock (Current._accountingSync)
            {
                if (Current._landControlPersistence != null) throw new InvalidOperationException("Land control is already initialized.");
                Current._landControlPersistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
                foreach (var entry in entries.OrderBy(e => e.ObservedUtc).ThenBy(e => e.Sequence).ThenBy(e => e.Id, StringComparer.Ordinal))
                    Current.RememberLandControl(entry.Copy());
                Current._landControlReports.AddRange(reports.Select(r => r.Copy()));
            }
        }

        public bool RecordLandControlEvent(LandControlEvent entry)
        {
            Guid id;
            if (entry == null || !Guid.TryParseExact(entry.Id, "N", out id) ||
                entry.ObservedUtc == default(DateTime) || string.IsNullOrWhiteSpace(entry.Kind) ||
                string.IsNullOrWhiteSpace(entry.Message) || string.IsNullOrWhiteSpace(entry.Source))
                throw new ArgumentException("An observed native land control event is required.");
            lock (_accountingSync)
            {
                if (_landControlPersistence == null) throw new InvalidOperationException("Land control is not initialized.");
                if (_landControlIds.Contains(entry.Id)) return false;
                var copy = entry.Copy();
                copy.ObservedUtc = LandControlMicroseconds(copy.ObservedUtc);
                _landControlPersistence.AppendLandControlEvent(copy);
                RememberLandControl(copy);
                return true;
            }
        }

        private void RememberLandControl(LandControlEvent entry)
        {
            if (!_landControlIds.Add(entry.Id)) return;
            int index = _landControlEvents.Count;
            if (index > 0 && LandControlOrder.Compare(_landControlEvents[index - 1], entry) > 0)
                index = ~_landControlEvents.BinarySearch(entry, LandControlOrder);
            _landControlEvents.Insert(index, entry);
            if (_landControlProjection != null && entry.ObservedUtc > _landControlProjection.LastUtc)
                _landControlProjection.Observe(entry);
            else _landControlProjection = null;
        }

        public LandControlSnapshot ReadLandControl(string kind, string organization, int page)
        {
            lock (_accountingSync)
            {
                Func<LandControlEvent, bool> matches = e =>
                    (string.IsNullOrEmpty(kind) || e.Kind == kind) &&
                    (string.IsNullOrEmpty(organization) ||
                        string.Equals(e.AttackerOrg, organization, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.DefenderOrg, organization, StringComparison.OrdinalIgnoreCase));
                bool unfiltered = string.IsNullOrEmpty(kind) && string.IsNullOrEmpty(organization);
                int count = unfiltered ? _landControlEvents.Count : 0;
                DateTime? first = count == 0 ? (DateTime?)null : _landControlEvents[0].ObservedUtc;
                if (!unfiltered)
                    foreach (var row in _landControlEvents)
                        if (matches(row)) { if (count == 0) first = row.ObservedUtc; count++; }
                const int size = 20;
                int pages = Math.Max(1, (int)(((long)count + size - 1) / size));
                page = Math.Max(1, Math.Min(pages, page));
                int skip = (page - 1) * size;
                var recent = new List<LandControlEvent>(size);
                for (int i = unfiltered ? count - 1 - skip : _landControlEvents.Count - 1; i >= 0 && recent.Count < size; i--)
                    if (unfiltered || matches(_landControlEvents[i]))
                    {
                        if (!unfiltered && skip-- > 0) continue;
                        recent.Add(_landControlEvents[i].Copy());
                    }
                return new LandControlSnapshot {
                    Total = count, Page = page, Pages = pages, FirstUtc = first,
                    Recent = recent.ToArray()
                };
            }
        }
    }
}
