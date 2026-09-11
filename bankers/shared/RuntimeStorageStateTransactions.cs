using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CityBankers.Shared
{
    /// <summary>
    /// Live physical-storage reconciliation helpers that share the exact same cross-process
    /// mutex as RuntimeStateStore.RecordPlacement. Every mutation is therefore one atomic
    /// load -> validate -> modify -> save transaction relative to normal worker placement.
    /// </summary>
    public static class RuntimeStorageStateTransactions
    {
        private const string RuntimeStateMutexName = "CityBankers.RuntimeState.v1";
        private const string IdentityNoneText = "(None:0000)";

        public static void ReplaceCensusedWorker(string settingsDir, StorageWorkerState replacement)
        {
            WithRuntimeStateMutex(delegate
            {
                var state = RuntimeStateStore.LoadStorageState(settingsDir);
                if (state?.Workers == null) throw new InvalidOperationException("Storage state unavailable during local census.");
                state.Workers.RemoveAll(w => string.Equals(w.Character, replacement.Character, StringComparison.OrdinalIgnoreCase));
                state.Workers.Add(replacement);
                state.UpdatedUtc = DateTime.UtcNow;
                RuntimeStateStore.WriteJsonAtomic(RuntimeStateStore.GetStorageStatePath(settingsDir), state);
                RuntimeStateStore.WriteJsonAtomic(RuntimeStateStore.GetCurrentStockPath(settingsDir), RuntimeStateStore.BuildCurrentStock(settingsDir, state));
            });
        }

        public static void CommitVerifiedExtraction(string settingsDir, string character, string source,
            int outerSlot, string bagIdentity, int finalOuterSlot, int removedSlot,
            IEnumerable<LiveBagItemSnapshot> before, IEnumerable<LiveBagItemSnapshot> after)
        {
            WithRuntimeStateMutex(delegate
            {
                var state = RuntimeStateStore.LoadStorageState(settingsDir);
                var worker = state?.Workers?.SingleOrDefault(w => string.Equals(w.Character, character, StringComparison.OrdinalIgnoreCase));
                var bag = worker?.Bags?.SingleOrDefault(b => b.Source == source &&
                    (IsUsableIdentity(bagIdentity) ? b.LastUniqueIdentity == bagIdentity : (b.OuterSlotInstance & 65535) == outerSlot));
                if (bag?.Items == null) throw new InvalidOperationException("Verified extraction source bag is unavailable.");
                Func<LiveBagItemSnapshot, string> key = i => (i.InnerSlot & 65535) + "/" + i.AoId + "/" + i.HighId + "/" + i.Ql;
                var actual = bag.Items.Select(i => (i.InnerSlot & 65535) + "/" + i.AoId + "/" + i.HighId + "/" + i.Ql).OrderBy(k => k).ToList();
                var expectedBefore = before.Select(key).OrderBy(k => k).ToList();
                var expectedAfter = after.Select(key).OrderBy(k => k).ToList();
                if (actual.SequenceEqual(expectedBefore))
                    bag.Items = bag.Items.Where(i => (i.InnerSlot & 65535) != removedSlot).ToList();
                else if (!actual.SequenceEqual(expectedAfter))
                    throw new InvalidOperationException("Source storage changed while extraction was awaiting accounting.");
                if (!bag.Items.Select(i => (i.InnerSlot & 65535) + "/" + i.AoId + "/" + i.HighId + "/" + i.Ql)
                    .OrderBy(k => k).SequenceEqual(expectedAfter))
                    throw new InvalidOperationException("Extraction would remove more than its verified occurrence.");
                if (worker.Bags.Any(other => other != bag && other.Source == source && (other.OuterSlotInstance & 65535) == finalOuterSlot))
                    throw new InvalidOperationException("Returned extraction bag conflicts with another persisted bag slot.");
                bag.OuterSlotInstance = finalOuterSlot;
                state.UpdatedUtc = DateTime.UtcNow;
                RuntimeStateStore.WriteJsonAtomic(RuntimeStateStore.GetStorageStatePath(settingsDir), state);
                RuntimeStateStore.WriteJsonAtomic(RuntimeStateStore.GetCurrentStockPath(settingsDir), RuntimeStateStore.BuildCurrentStock(settingsDir, state));
            });
        }

        public sealed class LiveBagItemSnapshot
        {
            public string UniqueIdentity;
            public int AoId;
            public int HighId;
            public int Ql;
            public string Name;
            public int InnerSlot;
        }

        public static bool TryReconcileBagContents(
            string settingsDir,
            string role,
            string character,
            string source,
            string bagUniqueIdentity,
            int persistedOuterSlot,
            int bagHandle,
            IEnumerable<LiveBagItemSnapshot> liveItems,
            string transactionId,
            out int importedExtras,
            out List<LedgerItem> importedLedgerItems,
            out string error)
        {
            importedExtras = 0;
            importedLedgerItems = new List<LedgerItem>();
            error = null;

            int reconciledImportedExtras = 0;
            var reconciledImportedLedgerItems = new List<LedgerItem>();

            try
            {
                WithRuntimeStateMutex(delegate
                {
                    StorageState state = RuntimeStateStore.LoadStorageState(settingsDir);
                    if (state == null)
                        throw new InvalidOperationException("Persistent storage state is missing.");

                    StorageWorkerState worker = (state.Workers ?? new List<StorageWorkerState>())
                        .FirstOrDefault(candidate =>
                            candidate != null &&
                            string.Equals(candidate.Role, role, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(candidate.Character, character, StringComparison.OrdinalIgnoreCase));
                    if (worker == null)
                        throw new InvalidOperationException(
                            "Persistent storage worker entry is missing for " + character + "/" + role + ".");

                    List<StorageBagState> bagMatches = (worker.Bags ?? new List<StorageBagState>())
                        .Where(bag => bag != null &&
                            string.Equals(bag.Source, source, StringComparison.OrdinalIgnoreCase) &&
                            (IsUsableIdentity(bagUniqueIdentity)
                                ? string.Equals(
                                    bag.LastUniqueIdentity,
                                    bagUniqueIdentity,
                                    StringComparison.Ordinal)
                                : bag.OuterSlotInstance == persistedOuterSlot))
                        .ToList();
                    if (bagMatches.Count != 1)
                        throw new InvalidOperationException(
                            "Persistent target bag is not uniquely resolvable; matches=" +
                            bagMatches.Count + ".");

                    StorageBagState bagState = bagMatches[0];
                    List<LiveBagItemSnapshot> remaining = (liveItems ??
                        Enumerable.Empty<LiveBagItemSnapshot>())
                        .Where(item => item != null)
                        .OrderBy(item => item.InnerSlot)
                        .ToList();

                    if (remaining.Count > bagState.Capacity)
                        throw new InvalidOperationException(
                            "Live bag contains " + remaining.Count +
                            " item(s), exceeding persisted capacity " + bagState.Capacity + ".");

                    var merged = new List<StoredItemState>();
                    foreach (StoredItemState persisted in
                        bagState.Items ?? new List<StoredItemState>())
                    {
                        if (persisted == null)
                            continue;

                        int matchIndex = FindLiveMatch(remaining, persisted);
                        if (matchIndex < 0)
                            throw new InvalidOperationException(
                                "Persisted item is missing from the live bag; refusing to erase " +
                                "physical/accounting history: " +
                                (persisted.Name ?? "<unnamed>") + " AOID=" +
                                persisted.AoId + " QL" + persisted.Ql + ".");

                        LiveBagItemSnapshot actual = remaining[matchIndex];
                        remaining.RemoveAt(matchIndex);

                        persisted.UniqueIdentity = IsUsableIdentity(actual.UniqueIdentity)
                            ? actual.UniqueIdentity
                            : null;
                        persisted.AoId = actual.AoId;
                        persisted.HighId = actual.HighId;
                        persisted.Ql = actual.Ql;
                        persisted.Name = actual.Name ?? persisted.Name ?? string.Empty;
                        persisted.InnerSlot = actual.InnerSlot;
                        persisted.ObservedUtc = DateTime.UtcNow;
                        merged.Add(persisted);
                    }

                    foreach (LiveBagItemSnapshot extra in remaining)
                    {
                        string routedRole;
                        bool managed = SymbiantCatalog.TryGetDestinationRole(settingsDir, extra.AoId, out routedRole);
                        if (!managed && extra.HighId != extra.AoId)
                            managed = SymbiantCatalog.TryGetDestinationRole(settingsDir, extra.HighId, out routedRole);

                        if (!managed || !string.Equals(
                                routedRole,
                                role,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "Live bag contains an unaccounted item that is unmanaged or " +
                                "routed elsewhere; refusing automatic import: " +
                                (extra.Name ?? "<unnamed>") + " AOID=" + extra.AoId +
                                " QL" + extra.Ql + " routed=" +
                                (routedRole ?? "unmanaged") + ".");
                        }

                        var imported = new StoredItemState
                        {
                            UniqueIdentity = IsUsableIdentity(extra.UniqueIdentity)
                                ? extra.UniqueIdentity
                                : null,
                            AoId = extra.AoId,
                            HighId = extra.HighId,
                            Ql = extra.Ql,
                            Name = extra.Name ?? string.Empty,
                            InnerSlot = extra.InnerSlot,
                            ObservedUtc = DateTime.UtcNow,
                            TransactionId = transactionId
                        };
                        merged.Add(imported);
                        reconciledImportedExtras++;
                        reconciledImportedLedgerItems.Add(new LedgerItem
                        {
                            UniqueIdentity = imported.UniqueIdentity,
                            AoId = imported.AoId,
                            HighId = imported.HighId,
                            Ql = imported.Ql,
                            Name = imported.Name,
                            Role = role,
                            BagSource = bagState.Source,
                            BagOuterSlot = bagState.OuterSlotInstance,
                            InnerSlot = imported.InnerSlot
                        });
                    }

                    bagState.Items = merged.OrderBy(item => item.InnerSlot).ToList();
                    if (IsUsableIdentity(bagUniqueIdentity))
                        bagState.LastUniqueIdentity = bagUniqueIdentity;
                    bagState.LastHandle = bagHandle;
                    worker.ObservedUtc = DateTime.UtcNow;
                    state.UpdatedUtc = DateTime.UtcNow;

                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetStorageStatePath(settingsDir),
                        state);
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetCurrentStockPath(settingsDir),
                        RuntimeStateStore.BuildCurrentStock(settingsDir, state));
                });

                importedExtras = reconciledImportedExtras;
                importedLedgerItems = reconciledImportedLedgerItems;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                importedExtras = 0;
                importedLedgerItems = new List<LedgerItem>();
                return false;
            }
        }

        public static bool TryUpdateBankBagOuterSlot(
            string settingsDir,
            string role,
            string character,
            string bagUniqueIdentity,
            int liveOuterSlot,
            out string error)
        {
            error = null;
            try
            {
                WithRuntimeStateMutex(delegate
                {
                    StorageState state = RuntimeStateStore.LoadStorageState(settingsDir);
                    if (state == null)
                        throw new InvalidOperationException("Persistent storage state is missing.");

                    StorageWorkerState worker = (state.Workers ?? new List<StorageWorkerState>())
                        .FirstOrDefault(candidate =>
                            candidate != null &&
                            string.Equals(candidate.Role, role, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(candidate.Character, character, StringComparison.OrdinalIgnoreCase));
                    if (worker == null)
                        throw new InvalidOperationException("Persistent storage worker entry is missing.");

                    List<StorageBagState> matches = (worker.Bags ?? new List<StorageBagState>())
                        .Where(bag => bag != null &&
                            string.Equals(bag.Source, "bank", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(
                                bag.LastUniqueIdentity,
                                bagUniqueIdentity,
                                StringComparison.Ordinal))
                        .ToList();
                    if (matches.Count != 1)
                        throw new InvalidOperationException(
                            "Returned bank bag identity is not unique in persisted state; matches=" +
                            matches.Count + ".");

                    matches[0].OuterSlotInstance = liveOuterSlot;
                    worker.ObservedUtc = DateTime.UtcNow;
                    state.UpdatedUtc = DateTime.UtcNow;
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetStorageStatePath(settingsDir),
                        state);
                    RuntimeStateStore.WriteJsonAtomic(
                        RuntimeStateStore.GetCurrentStockPath(settingsDir),
                        RuntimeStateStore.BuildCurrentStock(settingsDir, state));
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static int FindLiveMatch(
            List<LiveBagItemSnapshot> live,
            StoredItemState persisted)
        {
            if (live == null || persisted == null)
                return -1;

            if (IsUsableIdentity(persisted.UniqueIdentity))
            {
                int identityIndex = live.FindIndex(item =>
                    item != null && string.Equals(
                        item.UniqueIdentity,
                        persisted.UniqueIdentity,
                        StringComparison.Ordinal));
                if (identityIndex >= 0)
                    return identityIndex;
            }

            return live.FindIndex(item =>
                item != null &&
                item.AoId == persisted.AoId &&
                item.HighId == persisted.HighId &&
                item.Ql == persisted.Ql);
        }

        private static bool IsUsableIdentity(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, IdentityNoneText, StringComparison.Ordinal);
        }

        private static void WithRuntimeStateMutex(Action action)
        {
            using (var mutex = new Mutex(false, RuntimeStateMutexName))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                        throw new InvalidOperationException(
                            "Timed out waiting for CityBankers runtime-state transaction lock.");

                    action();
                }
                finally
                {
                    if (acquired)
                        mutex.ReleaseMutex();
                }
            }
        }
    }
}
