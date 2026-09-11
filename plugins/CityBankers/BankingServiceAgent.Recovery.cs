using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using CityBankers.Shared;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private readonly Stopwatch _looseRecoveryPoll = Stopwatch.StartNew();
        private readonly Stopwatch _looseRecoveryStable = Stopwatch.StartNew();
        private string _looseRecoverySignature;
        private readonly Dictionary<string, Stopwatch> _localRecoveryAttempts = new Dictionary<string, Stopwatch>();

        private void TickLocalStorageRecovery()
        {
            if (_isCentral || !Inventory.Bank.IsOpen || Trade.IsTrading || _storageJob != null ||
                _workerCommand != null || _reservedDispatch != null || _receipt != null || _withdrawal != null ||
                _looseRecoveryPoll.ElapsedMilliseconds < 1000) return;
            _looseRecoveryPoll.Restart();
            var inventory = Inventory.Items.Where(item => item != null && item.Slot.Type == IdentityType.Inventory)
                .OrderBy(item => item.Slot.Instance).ToList();
            string signature = string.Join(";", inventory.Select(item =>
                item.Slot.Instance + "/" + item.Id + "/" + item.HighId + "/" + item.Ql));
            if (signature != _looseRecoverySignature)
            {
                _looseRecoverySignature = signature;
                _looseRecoveryStable.Restart();
                return;
            }
            if (_looseRecoveryStable.ElapsedMilliseconds < 500) return;
            var ledger = ActiveLedgerStore.LoadLedger(_settingsDir);
            if (ledger == null) return;
            var withdrawals = WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList();
            foreach (var item in inventory)
            {
                string destination;
                if (!SymbiantCatalog.TryGetDestinationRole(_settingsDir, item.Id, out destination) &&
                    !SymbiantCatalog.TryGetDestinationRole(_settingsDir, item.HighId, out destination)) continue;
                if (!string.Equals(destination, _role, StringComparison.OrdinalIgnoreCase)) continue;
                var candidates = ledger.Items.Where(entry => entry.AoId == item.Id &&
                    (!entry.HighId.HasValue || entry.HighId == item.HighId) &&
                    (!entry.Ql.HasValue || entry.Ql == item.Ql) &&
                    string.Equals(entry.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    entry.Location == "inventory" && !entry.Bag.HasValue).ToList();
                var entries = candidates.Where(entry => entry.Slot.HasValue &&
                    (entry.Slot.Value & 65535) == (item.Slot.Instance & 65535)).ToList();
                if (entries.Count == 0 && inventory.Count(copy => copy.Id == item.Id &&
                    copy.HighId == item.HighId && copy.Ql == item.Ql) == 1)
                    entries = candidates.Where(entry => !entry.Slot.HasValue).ToList();
                if (entries.Count != 1) continue; // census/accounting owns importing unknown occurrences
                var entryToStore = entries[0];
                Stopwatch attempt;
                if (_localRecoveryAttempts.TryGetValue(entryToStore.Id, out attempt) && attempt.ElapsedMilliseconds < 30000) continue;
                if (string.IsNullOrWhiteSpace(entryToStore.TransactionId) || withdrawals.Any(w =>
                    w.ActiveLedgerId == entryToStore.Id || (w.Item != null && w.Item.AoId == item.Id &&
                    w.Item.HighId == item.HighId && w.Item.Ql == item.Ql))) continue;
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                if (storage == null || RuntimeStateStore.FindNextFreeBag(storage, _role, Client.CharacterName) == null) return;
                _localRecoveryAttempts[entryToStore.Id] = Stopwatch.StartNew();
                _storageJob = new StorageJob
                {
                    LocalRecovery = true, RecoverySlot = item.Slot.Instance,
                    Command = new DispatchCommand
                    {
                        BatchId = "local-" + entryToStore.Id, TransactionId = entryToStore.TransactionId,
                        Role = _role, SourceCharacter = Client.CharacterName, DestinationCharacter = Client.CharacterName,
                        CreatedUtc = DateTime.UtcNow, Items = SnapshotTradeItems(new[] { item })
                    },
                    Phase = StoragePhase.FindBag, PhaseStartedUtc = DateTime.UtcNow,
                    DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.ItemMoveTimeoutMs)
                };
                RuntimeStateStore.AppendActivity(_settingsDir, Client.CharacterName, _role,
                    "LOCAL RECOVERY storing physically observed " + item.Name + " QL" + item.Ql +
                    "; ledger=" + entryToStore.Id + ".");
                return;
            }
        }

        private Item FindStorageInventoryItem(TransferItemState expected)
        {
            if (!_storageJob.LocalRecovery) return FindInventoryItem(expected);
            // Identical templates may belong to different ledger occurrences.
            // Local recovery moves the observed slot, never the first matching copy.
            return Inventory.Items.FirstOrDefault(item => item != null && item.Slot.Type == IdentityType.Inventory &&
                item.Slot.Instance == _storageJob.RecoverySlot && item.Id == expected.AoId &&
                item.HighId == expected.HighId && item.Ql == expected.Ql);
        }
    }
}
