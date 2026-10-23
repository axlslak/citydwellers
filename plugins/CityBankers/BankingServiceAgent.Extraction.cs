using File = CityDwellers.Shared.DiskFiles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{



    public partial class BankingServiceAgent
    {
        private enum ExtractionPhase { Locate, BagInventory, BagOpen, ItemInventory, BagReturn, Commit }
        private ExtractionProof _extraction;
        private WorkingBagReturn _extractionBagReturn;
        private ExtractionPhase _extractionPhase;
        private Task<string> _extractionCommit;
        private readonly Stopwatch _extractionAge = Stopwatch.StartNew();
        private readonly Stopwatch _extractionScan = Stopwatch.StartNew();
        private readonly Stopwatch _extractionStable = Stopwatch.StartNew();
        private readonly Stopwatch _extractionMoveWait = Stopwatch.StartNew();
        private int _extractionMoveAttempts;
        private string _extractionSignature;
        private string _extractionCommitError;
        private readonly Stopwatch _extractionCommitRetry = Stopwatch.StartNew();

        private static List<ExtractionSlot> ExtractionSnapshot(IEnumerable<Item> items)
        {
            if (items == null) throw new InvalidOperationException("Extraction inventory is unavailable.");
            return items.Where(i => i != null).OrderBy(i => i.Slot.Instance).Select(i =>
                new ExtractionSlot { Slot = i.Slot.Instance & 65535, Item = SnapshotTradeItems(new[] { i })[0] }).ToList();
        }

        private List<ExtractionSlot> ExtractionInventory() => ExtractionSnapshot(Inventory.Items.Where(i =>
            i != null && i.Slot.Type == IdentityType.Inventory));
        private static string ExtractionKey(ExtractionSlot slot) => slot.Slot + "/" + CustodyKey(slot.Item);
        private static bool ValidExtraction(ExtractionProof proof)
        {
            if (proof?.Item == null || proof.BeforeSource == null || proof.AfterSource == null ||
                proof.BeforeInventory == null || proof.AfterInventory == null) return false;
            foreach (var snapshot in new[] { proof.BeforeSource, proof.AfterSource, proof.BeforeInventory, proof.AfterInventory })
                if (snapshot.Any(s => s?.Item == null) || snapshot.GroupBy(s => s.Slot).Any(g => g.Count() != 1)) return false;
            var removed = proof.BeforeSource.Where(s => s.Slot == proof.SourceSlot && CustodyKey(s.Item) == CustodyKey(proof.Item)).ToList();
            if (removed.Count != 1 || !proof.BeforeSource.Where(s => s != removed[0]).Select(ExtractionKey).OrderBy(k => k)
                .SequenceEqual(proof.AfterSource.Select(ExtractionKey).OrderBy(k => k))) return false;
            if (!proof.BeforeInventory.Select(s => CustodyKey(s.Item)).Concat(new[] { CustodyKey(proof.Item) }).OrderBy(k => k)
                .SequenceEqual(proof.AfterInventory.Select(s => CustodyKey(s.Item)).OrderBy(k => k))) return false;
            return !proof.BeforeInventory.Any(s => s.Slot == proof.InventorySlot) &&
                proof.AfterInventory.Any(s => s.Slot == proof.InventorySlot && CustodyKey(s.Item) == CustodyKey(proof.Item));
        }

        private void SaveExtraction(string phase) => SaveExtraction(_extraction, phase);
        private static void SaveExtraction(ExtractionProof proof, string phase)
        {
            proof.Phase = phase;
            CityDwellers.Shared.ManagerAccounting.Transaction("Extraction", () =>
                CityDwellers.Shared.ManagerMemory.Current.ChangeExtraction(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, proof));
        }
        private void ExtractionStep(ExtractionPhase phase)
        { _extractionPhase = phase; _extractionAge.Restart(); _extractionSignature = null; _extractionStable.Restart(); }

        private void StartRecoveryExtraction(bool localStorage = false)
        {
            if (_localCensus != null || _extraction != null || Trade.IsTrading || !Inventory.Bank.IsOpen || Inventory.NumFreeSlots < 3 ||
                _receipt != null || (!localStorage && _returnOffer != null) || _storageJob != null || _workerCommand != null ||
                _reservedDispatch != null || _withdrawal != null || _activeBatch != null || _donationActive ||
                _donationCleanup != null || _dispatchPreparation != null || (!localStorage && _extractionScan.ElapsedMilliseconds < 1500)) return;
            _extractionScan.Restart();
            if (CensusReservedHere()) return;
            var ledger = CityDwellers.Shared.BankerState.ReadLedgerForCharacter<ActiveLedgerState>(Client.CharacterName);
            if (ledger == null) return;
            var withdrawals = WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList();
            var candidates = ledger.Items.Where(e => e.Slot.HasValue && e.HighId.HasValue &&
                e.Ql.HasValue && (e.Bag.HasValue || e.Location == "bank")).ToList();
            if (localStorage)
            {
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                if (RuntimeStateStore.FindNextFreeBag(storage, _role, Client.CharacterName) == null) return;
                candidates = candidates.Where(e => e.Location == "inventory" && e.Bag.HasValue &&
                    storage.Workers.Any(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        w.Bags.Any(b => b.Source == "inventory" && (b.OuterSlotInstance & 65535) == (e.Bag.Value & 65535) &&
                            StorageBagPolicy.AllRecordsFor(b.LastUniqueIdentity, "inventory").Count == 1))).ToList();
            }
            if (candidates.Count == 0) return;
            var rules = SymbiantCatalog.GetRulesFor(_settingsDir,
                candidates.SelectMany(e => new[] { e.AoId, e.HighId.Value }));
            foreach (var entry in candidates)
            {
                SymbiantCatalog.AcceptanceRule rule;
                string destination = rules.TryGetValue(entry.AoId, out rule) ||
                    rules.TryGetValue(entry.HighId.Value, out rule) ? rule.Role : "central";
                if (!localStorage && ((_isCentral && destination == "central") ||
                    (!_isCentral && entry.Bag.HasValue && string.Equals(destination, _role, StringComparison.OrdinalIgnoreCase)))) continue;
                if (withdrawals.Any(w => w.ActiveLedgerId == entry.Id || (w.Item != null && w.Item.AoId == entry.AoId &&
                    w.Item.HighId == entry.HighId && w.Item.Ql == entry.Ql))) continue;
                string operation = Guid.NewGuid().ToString("N");
                bool reserved = CityDwellers.Shared.ManagerAccounting.Transaction("Reserve exact inventory extraction", () =>
                {
                    if (!WithdrawalStore.TryReserveRecovery(_settingsDir, operation, entry.Id)) return false;
                    _extraction = new ExtractionProof { Id = operation, LocalStorage = localStorage, LedgerId = entry.Id,
                        TransactionId = entry.TransactionId, Character = Client.CharacterName, Role = _role,
                        Source = entry.Location, Bag = entry.Bag, SourceSlot = entry.Slot.Value & 65535,
                        RecordedUtc = DateTime.UtcNow,
                        Item = new TransferItemState { AoId = entry.AoId, HighId = entry.HighId.Value, Ql = entry.Ql.Value } };
                    SaveExtraction("intent");
                    return true;
                });
                if (!reserved) continue;
                ExtractionStep(ExtractionPhase.Locate);
                if (localStorage) Logger.Information("[CityBankers] BANK SETUP emptying inventory bag " + entry.Bag +
                    "; item=" + entry.AoId + "; ledger=" + entry.Id + "; bank storage before surplus trades.");
                return;
            }
        }

        private bool TickRecoveryExtraction()
        {
            if (_extraction == null) return false;
            if (_extractionPhase != ExtractionPhase.Commit && _extractionAge.ElapsedMilliseconds > 20000)
            {
                SaveExtraction("incomplete");
                if (!StartLocalCensus("Extraction needs local reconciliation: " + _extraction.Id + " phase=" + _extractionPhase))
                    StartupCensusGate.Block("Extraction needs local reconciliation: " + _extraction.Id + " phase=" + _extractionPhase);
                return true;
            }
            if (_extractionPhase == ExtractionPhase.Commit)
            {
                if (!Inventory.Items.Any(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                    (i.Slot.Instance & 65535) == _extraction.InventorySlot && i.Id == _extraction.Item.AoId &&
                    i.HighId == _extraction.Item.HighId && i.Ql == _extraction.Item.Ql)) return true;
                if (_isCentral || _extraction.LocalStorage)
                {
                    _extractionCommitRetry.Restart();
                    try
                    {
                        CityDwellers.Shared.ManagerAccounting.Transaction("Commit verified local extraction", () => ApplyExtraction(_extraction));
                        FinishExtraction();
                    }
                    catch (Exception ex)
                    {
                        if (_extractionCommitError != ex.Message)
                            Logger.Warning("[CityBankers] Verified extraction accounting retry: " + ex.Message);
                        _extractionCommitError = ex.Message;
                    }
                }
                else if (_extractionCommit == null)
                    _extractionCommit = SendExtraction(_extraction);
                else if (_extractionCommit.IsCompleted)
                {
                    if (_extractionCommit.Result == "complete:" + _extraction.Id) FinishExtraction();
                    else _extractionCommit = null;
                }
                return true;
            }
            if (_extractionPhase == ExtractionPhase.Locate)
            {
                if (!_extraction.Bag.HasValue)
                {
                    PrepareExtractionItem(Inventory.Bank.Items);
                    return true;
                }
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                var bagState = storage?.Workers?.SingleOrDefault(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    ?.Bags?.SingleOrDefault(b => b.Source == _extraction.Source && (b.OuterSlotInstance & 65535) == _extraction.Bag);
                if (bagState == null) throw new InvalidOperationException("Extraction bag is absent from physical storage state.");
                // SingleOrDefault threw on a bag the client lists twice, and the tick
                // catch-all turns any unknown exception into a roster-wide recovery.
                // Two records of one container identity are one bag.
                var bag = StorageBagPolicy.PreferredRecord(bagState.LastUniqueIdentity,
                    _extraction.Source == "bank" ? "bank" : "inventory");
                if (bag == null) return true;
                _extraction.BagIdentity = bag.UniqueIdentity.ToString();
                SaveExtraction("bag-prepared");
                if (_extraction.Source == "bank") { BagOriginTrace.MoveToInventory(bag); ExtractionStep(ExtractionPhase.BagInventory); }
                else
                {
                    var open = FindContainerByIdentity(_extraction.BagIdentity);
                    if (!_extraction.LocalStorage || open == null || !open.IsOpen) bag.Use();
                    ExtractionStep(ExtractionPhase.BagOpen);
                }
                return true;
            }
            if (_extractionPhase == ExtractionPhase.BagInventory)
            {
                var bag = FindInventoryBagByIdentity(_extraction.BagIdentity);
                if (bag != null) { bag.Use(); ExtractionStep(ExtractionPhase.BagOpen); }
                return true;
            }
            if (_extractionPhase == ExtractionPhase.BagOpen)
            {
                var container = FindContainerByIdentity(_extraction.BagIdentity);
                int handle, count;
                bool fresh = !_extraction.LocalStorage || SharedBagRecovery.TryObserveContainer(
                    container?.Identity ?? Identity.None, 0, out handle, out count);
                if (fresh && container != null && container.IsOpen && container.Items != null) PrepareExtractionItem(container.Items);
                return true;
            }
            if (_extractionPhase == ExtractionPhase.ItemInventory)
            {
                IEnumerable<Item> source;
                if (_extraction.Bag.HasValue)
                {
                    var container = FindContainerByIdentity(_extraction.BagIdentity);
                    if (container == null || !container.IsOpen || container.Items == null)
                    {
                        if (_extraction.LocalStorage && _extractionMoveWait.ElapsedMilliseconds >= 1200)
                        {
                            var bag = FindInventoryBagByIdentity(_extraction.BagIdentity);
                            if (bag != null) { bag.Use(); _extractionMoveWait.Restart(); }
                        }
                        return true;
                    }
                    int handle, count;
                    if (_extraction.LocalStorage && !SharedBagRecovery.TryObserveContainer(container.Identity, 0, out handle, out count))
                        return true;
                    source = container.Items;
                }
                else source = Inventory.Bank.Items;
                _extraction.AfterSource = ExtractionSnapshot(source);
                _extraction.AfterInventory = ExtractionInventory();
                var arrived = _extraction.AfterInventory.Where(s => !_extraction.BeforeInventory.Any(b => b.Slot == s.Slot) &&
                    CustodyKey(s.Item) == CustodyKey(_extraction.Item)).ToList();
                if (arrived.Count != 1)
                {
                    if (arrived.Count == 0 && _extractionMoveAttempts < 3 && _extractionMoveWait.ElapsedMilliseconds >= 1200 &&
                        _extraction.BeforeSource.Select(ExtractionKey).OrderBy(k => k).SequenceEqual(_extraction.AfterSource.Select(ExtractionKey).OrderBy(k => k)) &&
                        _extraction.BeforeInventory.Select(ExtractionKey).OrderBy(k => k).SequenceEqual(_extraction.AfterInventory.Select(ExtractionKey).OrderBy(k => k)))
                    {
                        var stillThere = source.SingleOrDefault(i => i != null && (i.Slot.Instance & 65535) == _extraction.SourceSlot &&
                            i.Id == _extraction.Item.AoId && i.HighId == _extraction.Item.HighId && i.Ql == _extraction.Item.Ql);
                        if (stillThere != null)
                        {
                            _extractionMoveAttempts++;
                            SaveExtraction("move-retry-" + _extractionMoveAttempts);
                            if (_extraction.Bag.HasValue) BagOriginTrace.MoveToContainer(stillThere, DynelManager.LocalPlayer.Identity);
                            else BagOriginTrace.MoveToInventory(stillThere);
                            _extractionMoveWait.Restart();
                        }
                    }
                    return true;
                }
                _extraction.InventorySlot = arrived[0].Slot;
                if (!ValidExtraction(_extraction)) return true;
                if (_extraction.LocalStorage)
                {
                    var bag = FindInventoryBagByIdentity(_extraction.BagIdentity);
                    if (bag == null) return true;
                    _extraction.FinalBagSlot = bag.Slot.Instance & 65535;
                    _extraction.FinalBagSource = "inventory";
                    ExtractionStep(ExtractionPhase.Commit);
                }
                else if (_extraction.Bag.HasValue)
                {
                    var bag = FindInventoryBagByIdentity(_extraction.BagIdentity);
                    if (bag == null) return true;
                    _extractionBagReturn = BeginWorkingBagReturn(bag);
                    ExtractionStep(ExtractionPhase.BagReturn);
                }
                else ExtractionStep(ExtractionPhase.Commit);
                SaveExtraction("item-observed");
                return true;
            }
            if (_extractionPhase == ExtractionPhase.BagReturn)
            {
                Item bag;
                if (ObserveWorkingBagReturn(_extractionBagReturn, out bag))
                {
                    _extraction.FinalBagSlot = bag.Slot.Instance & 65535;
                    _extraction.FinalBagSource = "bank";
                    SaveExtraction("bag-returned");
                    ExtractionStep(ExtractionPhase.Commit);
                }
            }
            return true;
        }

        private void PrepareExtractionItem(IEnumerable<Item> source)
        {
            var items = source?.Where(i => i != null).ToList();
            if (items == null) return;
            var item = items.SingleOrDefault(i => (i.Slot.Instance & 65535) == _extraction.SourceSlot &&
                i.Id == _extraction.Item.AoId && i.HighId == _extraction.Item.HighId && i.Ql == _extraction.Item.Ql);
            if (item == null) return;
            _extraction.Item = SnapshotTradeItems(new[] { item })[0];
            _extraction.BeforeSource = ExtractionSnapshot(items);
            if (_extraction.Bag.HasValue)
            {
                var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                var bag = storage?.Workers?.SingleOrDefault(w => string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    ?.Bags?.SingleOrDefault(b => b.LastUniqueIdentity == _extraction.BagIdentity);
                if (bag?.Items == null || !bag.Items.Select(i => (i.InnerSlot & 65535) + "/" + i.AoId + "/" + i.HighId + "/" + i.Ql)
                    .OrderBy(k => k).SequenceEqual(_extraction.BeforeSource.Select(ExtractionKey).OrderBy(k => k))) return;
            }
            _extraction.BeforeInventory = ExtractionInventory();
            SaveExtraction("item-prepared");
            if (_extraction.Bag.HasValue) BagOriginTrace.MoveToContainer(item, DynelManager.LocalPlayer.Identity);
            else BagOriginTrace.MoveToInventory(item);
            _extractionMoveAttempts = 1;
            _extractionMoveWait.Restart();
            ExtractionStep(ExtractionPhase.ItemInventory);
        }

        private async Task<string> SendExtraction(ExtractionProof proof)
        {
            try
            {
                return await SendBankerMemory(_centralCharacter,
                    new DispatchProposal { Kind = "extraction-commit", Extraction = proof }).ConfigureAwait(false);
            }
            catch (Exception) { return "pending"; }
        }

        private bool HandleExtractionProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "extraction-commit") return false;
            if (!_isCentral || proposal.Extraction == null) { proposal.Reply.TrySetResult("pending"); return true; }
            if (WithdrawalStore.GetCensusCharacters(_settingsDir).Contains(proposal.Extraction.Character))
            { proposal.Reply.TrySetResult("pending"); return true; }
            ApplyExtraction(proposal.Extraction);
            proposal.Reply.TrySetResult("complete:" + proposal.Extraction.Id);
            return true;
        }

        private void ApplyExtraction(ExtractionProof proof)
        {
            Guid id;
            if (!Guid.TryParseExact(proof.Id, "N", out id) || !ValidExtraction(proof) ||
                !_config.Roles.Any(p => string.Equals(CityDwellers.Shared.BankerRoles.Canonical(p.Key), proof.Role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Value?.Character, proof.Character, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Incomplete extraction proof or unknown source.");
            var previous = CityDwellers.Shared.ManagerMemory.Current.ReadExtraction(
                CityDwellers.Shared.ManagerAccounting.TransactionId, proof.Id);
            if (previous?.Phase == "completed")
            {
                previous.Phase = proof.Phase;
                if (JsonConvert.SerializeObject(previous) != JsonConvert.SerializeObject(proof))
                    throw new InvalidOperationException("Extraction ID was reused with different evidence.");
                WithdrawalStore.ReleaseRecovery(_settingsDir, proof.Id);
                return;
            }
            if (!WithdrawalStore.OwnsRecovery(_settingsDir, proof.Id, proof.LedgerId))
                throw new InvalidOperationException("Extraction no longer owns its ledger reservation.");
            SaveExtraction(proof, "verified");
            ActiveLedgerStore.RecordExtraction(_settingsDir, proof);
            if (!proof.LocalStorage && string.Equals(proof.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase))
            {
                string role;
                if (SymbiantCatalog.TryGetDestinationRole(_settingsDir, proof.Item.AoId, out role) ||
                    SymbiantCatalog.TryGetDestinationRole(_settingsDir, proof.Item.HighId, out role))
                {
                    RoleConfig destination;
                    if (role != "central" && TryGetRole(role, out destination))
                    {
                        var queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                        string batch = "extraction-" + proof.Id;
                        if (!queue.Batches.Any(b => b.BatchId == batch))
                        {
                            queue.Batches.Add(new DispatchBatchState { BatchId = batch, TransactionId = proof.TransactionId,
                                Role = role, Character = destination.Character, Status = "queued", CreatedUtc = DateTime.UtcNow,
                                UpdatedUtc = DateTime.UtcNow, Items = new List<TransferItemState> { proof.Item } });
                            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                        }
                    }
                }
            }
            SaveExtraction(proof, "completed");
            WithdrawalStore.ReleaseRecovery(_settingsDir, proof.Id);
        }

        private void FinishExtraction()
        {
            _extraction = null;
            _extractionCommitError = null;
            _extractionCommit = null;
            _extractionScan.Restart();
        }

        private void ResumeLocalStorageExtraction()
        {
            if (_extraction != null || Trade.IsTrading || _receipt != null || !Inventory.Bank.IsOpen) return;
            var proof = CityDwellers.Shared.ManagerMemory.Current.ReadLocalStorageExtraction(
                CityDwellers.Shared.ManagerAccounting.TransactionId, Client.CharacterName);
            if (proof == null) return;
            if (!proof.Bag.HasValue || proof.Source != "inventory" ||
                !WithdrawalStore.OwnsRecovery(_settingsDir, proof.Id, proof.LedgerId))
                throw new InvalidOperationException("Local inventory emptying lost its exact source reservation.");
            _extraction = proof;
            _extractionMoveAttempts = 0;
            _extractionMoveWait.Restart();
            // Before a move was prepared there is no effect to recover. Otherwise
            // inspect both recorded sides before retrying or committing the move.
            ExtractionStep(proof.BeforeSource == null ? ExtractionPhase.Locate : ExtractionPhase.ItemInventory);
            Logger.Information("[CityBankers] BANK SETUP resuming inventory-bag item " + proof.LedgerId + ".");
        }
    }
}
