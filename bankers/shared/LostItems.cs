using File = CityDwellers.Shared.DiskFiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
    // Central is the sole writer; Manager only reads. The SQL record is never trimmed.
    public static class LostItemsStore
    {
        private static readonly object Sync = new object();
        public static string GetPath(string settingsDir) =>
            Path.Combine(RuntimeStateStore.GetDataDirectory(settingsDir), "lost.json");

        public static LostItemsState Read(string settingsDir)
        {
            var state = CityDwellers.Shared.ManagerMemory.Current.ReadLostItems(CityDwellers.Shared.ManagerAccounting.TransactionId) ?? new LostItemsState();
            if (state.Format != "citybankers-lost-items-v1" || state.Entries == null ||
                state.Entries.Any(e => e == null || string.IsNullOrWhiteSpace(e.IncidentId) ||
                    string.IsNullOrWhiteSpace(e.LedgerId) || e.PreviousLedgerEntry == null) ||
                state.Entries.GroupBy(e => e.IncidentId, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new InvalidDataException("Invalid lost-item history; preserve it for investigation.");
            return state;
        }

        public static void RecordBeforeRemoval(string settingsDir, IEnumerable<LostItemRecord> records)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction("CityBankers.Ledger.v1", () =>
            {
                var incoming = records.Where(r => r.PreviousLedgerEntry?.AoId != CruPolicy.AoId).ToList();
                if (incoming.Count == 0) return;
                lock (Sync)
                {
                    var state = Read(settingsDir);
                    var ids = new HashSet<string>(state.Entries.Select(e => e.IncidentId), StringComparer.Ordinal);
                    bool changed = false;
                    foreach (var record in incoming)
                        if (!state.Entries.Any(e => e.LedgerId == record.LedgerId && !e.RemovalRecordedUtc.HasValue && e.ExcludedReason == null) && ids.Add(record.IncidentId)) { state.Entries.Add(record); changed = true; }
                    if (changed) CityDwellers.Shared.ManagerMemory.Current.ChangeLostItems(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                }
            });
        }

        public static void ExcludePendingRemovals(string settingsDir, ISet<string> excludedIds, string reason)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction("CityBankers.Ledger.v1", () =>
            {
                if (excludedIds.Count == 0) return;
                lock (Sync)
                {
                    var state = Read(settingsDir);
                    bool changed = false;
                    foreach (var record in state.Entries.Where(e => !e.RemovalRecordedUtc.HasValue &&
                        e.ExcludedReason == null && excludedIds.Contains(e.LedgerId)))
                    { record.ExcludedReason = reason; changed = true; }
                    if (changed) CityDwellers.Shared.ManagerMemory.Current.ChangeLostItems(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                }
            });
        }

        public static void ConfirmRemovals(string settingsDir, ISet<string> remainingLedgerIds)
        {
            CityDwellers.Shared.ManagerAccounting.Transaction("CityBankers.Ledger.v1", () =>
            {
                lock (Sync)
                {
                    var state = Read(settingsDir);
                    bool changed = false;
                    foreach (var record in state.Entries.Where(e => !e.RemovalRecordedUtc.HasValue && e.ExcludedReason == null &&
                        !remainingLedgerIds.Contains(e.LedgerId)))
                    { record.RemovalRecordedUtc = DateTime.UtcNow; changed = true; }
                    if (changed) CityDwellers.Shared.ManagerMemory.Current.ChangeLostItems(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                }
            });
        }
    }
}
