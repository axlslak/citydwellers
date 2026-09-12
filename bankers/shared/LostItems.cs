using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
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
        public JObject PreviousLedgerEntry;
    }

    public sealed class LostItemsState
    {
        public string Format = "citybankers-lost-items-v1";
        public List<LostItemRecord> Entries = new List<LostItemRecord>();
    }

    // Central is the sole writer; Manager only reads. The file is never trimmed.
    public static class LostItemsStore
    {
        private static readonly object Sync = new object();
        public static string GetPath(string settingsDir) =>
            Path.Combine(RuntimeStateStore.GetDataDirectory(settingsDir), "lost.json");

        public static LostItemsState Read(string settingsDir)
        {
            var state = RuntimeStateStore.ReadJsonStrict<LostItemsState>(GetPath(settingsDir)) ?? new LostItemsState();
            if (state.Format != "citybankers-lost-items-v1" || state.Entries == null ||
                state.Entries.Any(e => e == null || string.IsNullOrWhiteSpace(e.IncidentId) ||
                    string.IsNullOrWhiteSpace(e.LedgerId) || e.PreviousLedgerEntry == null) ||
                state.Entries.GroupBy(e => e.IncidentId, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new InvalidDataException("Invalid lost-item history; preserve it for investigation.");
            return state;
        }

        public static void RecordBeforeRemoval(string settingsDir, IEnumerable<LostItemRecord> records)
        {
            var incoming = records.ToList();
            if (incoming.Count == 0) return;
            lock (Sync)
            {
                var state = Read(settingsDir);
                var ids = new HashSet<string>(state.Entries.Select(e => e.IncidentId), StringComparer.Ordinal);
                bool changed = false;
                foreach (var record in incoming)
                    if (!state.Entries.Any(e => e.LedgerId == record.LedgerId && !e.RemovalRecordedUtc.HasValue && e.ExcludedReason == null) && ids.Add(record.IncidentId)) { state.Entries.Add(record); changed = true; }
                if (changed) RuntimeStateStore.WriteJsonAtomic(GetPath(settingsDir), state);
            }
        }

        public static void ExcludePendingRemovals(string settingsDir, ISet<string> excludedIds, string reason)
        {
            if (excludedIds.Count == 0 || !File.Exists(GetPath(settingsDir))) return;
            lock (Sync)
            {
                var state = Read(settingsDir);
                bool changed = false;
                foreach (var record in state.Entries.Where(e => !e.RemovalRecordedUtc.HasValue &&
                    e.ExcludedReason == null && excludedIds.Contains(e.LedgerId)))
                { record.ExcludedReason = reason; changed = true; }
                if (changed) RuntimeStateStore.WriteJsonAtomic(GetPath(settingsDir), state);
            }
        }

        public static void ConfirmRemovals(string settingsDir, ISet<string> remainingLedgerIds)
        {
            if (!File.Exists(GetPath(settingsDir))) return;
            lock (Sync)
            {
                var state = Read(settingsDir);
                bool changed = false;
                foreach (var record in state.Entries.Where(e => !e.RemovalRecordedUtc.HasValue && e.ExcludedReason == null &&
                    !remainingLedgerIds.Contains(e.LedgerId)))
                { record.RemovalRecordedUtc = DateTime.UtcNow; changed = true; }
                if (changed) RuntimeStateStore.WriteJsonAtomic(GetPath(settingsDir), state);
            }
        }
    }
}
