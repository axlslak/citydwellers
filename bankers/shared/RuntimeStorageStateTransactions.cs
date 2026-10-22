using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CityBankers.Shared
{
    /// <summary>
    /// Live physical-storage reconciliation helpers that share the exact same database transaction
    /// lock as RuntimeStateStore.RecordPlacement. Every mutation is therefore one atomic
    /// load -> validate -> modify -> save transaction relative to normal worker placement.
    /// </summary>
    public static class RuntimeStorageStateTransactions
    {
        private const string RuntimeStateMutexName = "CityBankers.RuntimeState.v1";
        private const string IdentityNoneText = "(None:0000)";

        // Only called after the addressed bag has left inventory and exactly
        // one bank occurrence has arrived. Move addresses, never item claims.
        public static void RecordBankReturn(string settingsDir, string character, string identity, int bankSlot)
        {
            WithRuntimeStateMutex(delegate
            {
                var state = RuntimeStateStore.LoadStorageState(settingsDir);
                var worker = state?.Workers?.SingleOrDefault(w => string.Equals(w.Character, character, StringComparison.OrdinalIgnoreCase));
                var bag = worker?.Bags?.SingleOrDefault(b => b.LastUniqueIdentity == identity);
                if (bag == null) throw new InvalidOperationException("Returned bag has no unique storage owner: " + identity);
                bankSlot &= 65535;
                if (worker.Bags.Any(b => b != bag && b.Source == "bank" && (b.OuterSlotInstance & 65535) == bankSlot))
                    throw new InvalidOperationException("Returned bag conflicts with another bank address.");
                string source = bag.Source;
                int previous = bag.OuterSlotInstance & 65535;
                if (source == "bank" && previous == bankSlot) return;
                var ledger = CityDwellers.Shared.ManagerMemory.Current.ReadLedger(CityDwellers.Shared.ManagerAccounting.TransactionId);
                foreach (var entry in ledger.Items.Where(e => string.Equals(e.Character, character, StringComparison.OrdinalIgnoreCase) &&
                    e.Location == source && e.Bag.HasValue && (e.Bag.Value & 65535) == previous))
                { entry.Location = "bank"; entry.Bag = bankSlot; }
                CityDwellers.Shared.ManagerMemory.Current.ChangeLedger(CityDwellers.Shared.ManagerAccounting.TransactionId, ledger);
                RelocateWithdrawalReferences(settingsDir, character, source, previous, bankSlot);
                bag.Source = "bank"; bag.OuterSlotType = "BankByRef"; bag.OuterSlotInstance = bankSlot;
                state.UpdatedUtc = DateTime.UtcNow;
                CityDwellers.Shared.ManagerMemory.Current.ChangeStorage(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                RuntimeStateStore.SaveCurrentStock(RuntimeStateStore.BuildCurrentStock(settingsDir, state));
            });
        }

        public static void RelocateWithdrawalReferences(string settingsDir, string character, string source, int oldSlot, int bankSlot)
        {
            WithRuntimeStateMutex(delegate
            {
                if (source == "bank" && (oldSlot & 65535) == (bankSlot & 65535)) return;
                // This file is linked into the host as well as plugins. Use the
                // shared typed state, not the plugin-only WithdrawalStore.
                var manager = CityDwellers.Shared.ManagerMemory.Current;
                string transaction = CityDwellers.Shared.ManagerAccounting.TransactionId;
                var queue = manager.ReadWithdrawals(transaction);
                if (queue?.Withdrawals == null) return;
                if (queue.Withdrawals.Any(w => w == null || string.IsNullOrWhiteSpace(w.Id)) ||
                    queue.Withdrawals.GroupBy(w => w.Id, StringComparer.Ordinal).Any(group => group.Count() != 1))
                    throw new InvalidOperationException("Cannot relocate references in an invalid withdrawal queue.");
                bool changed = false;
                foreach (var withdrawal in queue.Withdrawals.Where(w => !w.IsTerminal() &&
                    string.Equals(w.SourceCharacter, character, StringComparison.OrdinalIgnoreCase) && w.SourceBag == source &&
                    (w.SourceBagOuterSlot & 65535) == (oldSlot & 65535)))
                {
                    withdrawal.SourceBag = "bank"; withdrawal.SourceBagOuterSlot = bankSlot & 65535;
                    withdrawal.Revision++;
                    withdrawal.UpdatedUtc = DateTime.UtcNow;
                    changed = true;
                }
                if (changed) manager.ChangeWithdrawals(transaction, queue);
            });
        }

        // Outer empty containers are equipment for storage, never donor stock.
        public static void UpdateReserveBag(string settingsDir, string character, string identity, StorageBagState replacement)
        {
            WithRuntimeStateMutex(delegate
            {
                var state = RuntimeStateStore.LoadStorageState(settingsDir);
                var worker = state?.Workers?.SingleOrDefault(w => string.Equals(w.Character, character, StringComparison.OrdinalIgnoreCase));
                if (worker?.Bags == null) throw new InvalidOperationException("Reserve storage owner is unavailable.");
                var existing = worker.Bags.Where(b => b.LastUniqueIdentity == identity).ToList();
                if (existing.Count > 1 || existing.Any(b => b.Items == null || b.Items.Count != 0))
                    throw new InvalidOperationException("Reserve operation cannot replace occupied or ambiguous storage.");
                if (replacement != null && worker.Bags.Any(b => b.LastUniqueIdentity != identity && b.Source == replacement.Source &&
                    b.OuterSlotInstance == replacement.OuterSlotInstance))
                    throw new InvalidOperationException("Reserve destination conflicts with another bag.");
                worker.Bags.RemoveAll(b => b.LastUniqueIdentity == identity);
                if (replacement != null) worker.Bags.Add(replacement);
                state.UpdatedUtc = DateTime.UtcNow;
                CityDwellers.Shared.ManagerMemory.Current.ChangeStorage(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                RuntimeStateStore.SaveCurrentStock(RuntimeStateStore.BuildCurrentStock(settingsDir, state));
            });
        }

        public static void ReplaceCensusedWorker(string settingsDir, StorageWorkerState replacement)
        {
            WithRuntimeStateMutex(delegate
            {
                var state = RuntimeStateStore.LoadStorageState(settingsDir);
                if (state?.Workers == null) throw new InvalidOperationException("Storage state unavailable during local census.");
                state.Workers.RemoveAll(w => string.Equals(w.Character, replacement.Character, StringComparison.OrdinalIgnoreCase));
                state.Workers.Add(replacement);
                state.UpdatedUtc = DateTime.UtcNow;
                CityDwellers.Shared.ManagerMemory.Current.ChangeStorage(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                RuntimeStateStore.SaveCurrentStock(RuntimeStateStore.BuildCurrentStock(settingsDir, state));
            });
        }

        public static void CommitVerifiedExtraction(string settingsDir, string character, string source,
            int outerSlot, string bagIdentity, int finalOuterSlot, int removedSlot,
            IEnumerable<LiveBagItemSnapshot> before, IEnumerable<LiveBagItemSnapshot> after, string finalSource = null)
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
                finalSource = finalSource ?? source;
                if (worker.Bags.Any(other => other != bag && other.Source == finalSource && (other.OuterSlotInstance & 65535) == finalOuterSlot))
                    throw new InvalidOperationException("Returned extraction bag conflicts with another persisted bag slot.");
                bag.OuterSlotInstance = finalOuterSlot;
                bag.Source = finalSource;
                if (finalSource == "bank") bag.OuterSlotType = "BankByRef";
                state.UpdatedUtc = DateTime.UtcNow;
                CityDwellers.Shared.ManagerMemory.Current.ChangeStorage(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                RuntimeStateStore.SaveCurrentStock(RuntimeStateStore.BuildCurrentStock(settingsDir, state));
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

                    CityDwellers.Shared.ManagerMemory.Current.ChangeStorage(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                    RuntimeStateStore.SaveCurrentStock(RuntimeStateStore.BuildCurrentStock(settingsDir, state));
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
                    CityDwellers.Shared.ManagerMemory.Current.ChangeStorage(CityDwellers.Shared.ManagerAccounting.TransactionId, state);
                    RuntimeStateStore.SaveCurrentStock(RuntimeStateStore.BuildCurrentStock(settingsDir, state));
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
            CityDwellers.Shared.ManagerAccounting.Transaction(RuntimeStateMutexName, action);
        }
    }
}
