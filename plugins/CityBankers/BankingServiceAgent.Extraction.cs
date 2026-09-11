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
    internal sealed class ExtractionSlot
    {
        public int Slot;
        public TransferItemState Item;
    }

    internal sealed class ExtractionProof
    {
        public string Id;
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
    }

    public partial class BankingServiceAgent
    {
        private enum ExtractionPhase { Locate, BagInventory, BagOpen, ItemInventory, BagReturn, Commit }
        private ExtractionProof _extraction;
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

        private string ExtractionDirectory(ExtractionProof proof) => Path.Combine(
            RuntimeStateStore.GetDataDirectory(_settingsDir), "banker-extractions", proof.Id);
        private void SaveExtraction(string phase) => RuntimeStateStore.WriteJsonAtomic(
            Path.Combine(ExtractionDirectory(_extraction), phase + ".json"), _extraction);
        private void ExtractionStep(ExtractionPhase phase)
        { _extractionPhase = phase; _extractionAge.Restart(); _extractionSignature = null; _extractionStable.Restart(); }

        private void StartRecoveryExtraction()
        {
            if (_localCensus != null || _extraction != null || Trade.IsTrading || !Inventory.Bank.IsOpen || Inventory.NumFreeSlots < 3 ||
                _receipt != null || _returnOffer != null || _storageJob != null || _workerCommand != null ||
                _reservedDispatch != null || _withdrawal != null || _activeBatch != null || _donationActive ||
                _donationCleanup != null || _dispatchPreparation != null || _extractionScan.ElapsedMilliseconds < 1500) return;
            _extractionScan.Restart();
            if (CensusReservedHere()) return;
            var ledger = ActiveLedgerStore.LoadLedger(_settingsDir);
            if (ledger == null) return;
            var withdrawals = WithdrawalStore.LoadAll(_settingsDir).Where(WithdrawalStore.IsActive).ToList();
            foreach (var entry in ledger.Items.Where(e => string.Equals(e.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                e.Slot.HasValue && e.HighId.HasValue && e.Ql.HasValue && (e.Bag.HasValue || e.Location == "bank")))
            {
                string destination;
                if (!SymbiantCatalog.TryGetDestinationRole(_settingsDir, entry.AoId, out destination) &&
                    !SymbiantCatalog.TryGetDestinationRole(_settingsDir, entry.HighId.Value, out destination)) destination = "central";
                if ((_isCentral && destination == "central") ||
                    (!_isCentral && entry.Bag.HasValue && string.Equals(destination, _role, StringComparison.OrdinalIgnoreCase))) continue;
                if (withdrawals.Any(w => w.ActiveLedgerId == entry.Id || (w.Item != null && w.Item.AoId == entry.AoId &&
                    w.Item.HighId == entry.HighId && w.Item.Ql == entry.Ql))) continue;
                string operation = Guid.NewGuid().ToString("N");
                if (!WithdrawalStore.TryReserveRecovery(_settingsDir, operation, entry.Id)) continue;
                _extraction = new ExtractionProof { Id = operation, LedgerId = entry.Id,
                    TransactionId = entry.TransactionId, Character = Client.CharacterName, Role = _role,
                    Source = entry.Location, Bag = entry.Bag, SourceSlot = entry.Slot.Value & 65535,
                    RecordedUtc = DateTime.UtcNow,
                    Item = new TransferItemState { AoId = entry.AoId, HighId = entry.HighId.Value, Ql = entry.Ql.Value } };
                SaveExtraction("intent");
                ExtractionStep(ExtractionPhase.Locate);
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
            if (_extractionAge.ElapsedMilliseconds < 500) return true;
            if (_extractionPhase == ExtractionPhase.Commit)
            {
                if (!Inventory.Items.Any(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                    (i.Slot.Instance & 65535) == _extraction.InventorySlot && i.Id == _extraction.Item.AoId &&
                    i.HighId == _extraction.Item.HighId && i.Ql == _extraction.Item.Ql)) return true;
                if (_isCentral)
                {
                    if (_extractionCommitRetry.ElapsedMilliseconds < 1000) return true;
                    _extractionCommitRetry.Restart();
                    try { ApplyExtraction(_extraction); FinishExtraction(); }
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
                var bags = _extraction.Source == "bank" ? Inventory.Bank.Items : Inventory.Items;
                var bag = bags.SingleOrDefault(i => i != null && i.UniqueIdentity.Type == IdentityType.Container &&
                    i.UniqueIdentity.ToString() == bagState.LastUniqueIdentity);
                if (bag == null) return true;
                _extraction.BagIdentity = bag.UniqueIdentity.ToString();
                SaveExtraction("bag-prepared");
                if (_extraction.Source == "bank") { bag.MoveToInventory(); ExtractionStep(ExtractionPhase.BagInventory); }
                else { bag.Use(); ExtractionStep(ExtractionPhase.BagOpen); }
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
                if (container != null && container.IsOpen && container.Items != null) PrepareExtractionItem(container.Items);
                return true;
            }
            if (_extractionPhase == ExtractionPhase.ItemInventory)
            {
                IEnumerable<Item> source;
                if (_extraction.Bag.HasValue)
                {
                    var container = FindContainerByIdentity(_extraction.BagIdentity);
                    if (container == null || !container.IsOpen || container.Items == null) return true;
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
                            if (_extraction.Bag.HasValue) stillThere.MoveToContainer(DynelManager.LocalPlayer.Identity);
                            else stillThere.MoveToInventory();
                            _extractionMoveWait.Restart();
                        }
                    }
                    return true;
                }
                _extraction.InventorySlot = arrived[0].Slot;
                if (!ValidExtraction(_extraction)) return true;
                string signature = string.Join(";", _extraction.AfterInventory.Select(ExtractionKey)) + "|" +
                    string.Join(";", _extraction.AfterSource.Select(ExtractionKey));
                if (_extractionSignature != signature) { _extractionSignature = signature; _extractionStable.Restart(); return true; }
                if (_extractionStable.ElapsedMilliseconds < 500) return true;
                SaveExtraction("item-observed");
                if (_extraction.Bag.HasValue && _extraction.Source == "bank")
                {
                    var bag = FindInventoryBagByIdentity(_extraction.BagIdentity);
                    if (bag == null) return true;
                    bag.MoveToBank();
                    ExtractionStep(ExtractionPhase.BagReturn);
                }
                else
                {
                    _extraction.FinalBagSlot = _extraction.Bag;
                    ExtractionStep(ExtractionPhase.Commit);
                }
                return true;
            }
            if (_extractionPhase == ExtractionPhase.BagReturn)
            {
                var bag = FindBankBagByIdentity(_extraction.BagIdentity);
                if (bag != null && FindInventoryBagByIdentity(_extraction.BagIdentity) == null)
                {
                    string signature = "bank:" + bag.Slot.Instance;
                    if (_extractionSignature != signature) { _extractionSignature = signature; _extractionStable.Restart(); return true; }
                    if (_extractionStable.ElapsedMilliseconds < 500) return true;
                    _extraction.FinalBagSlot = bag.Slot.Instance & 65535;
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
            if (_extraction.Bag.HasValue) item.MoveToContainer(DynelManager.LocalPlayer.Identity);
            else item.MoveToInventory();
            _extractionMoveAttempts = 1;
            _extractionMoveWait.Restart();
            ExtractionStep(ExtractionPhase.ItemInventory);
        }

        private async Task<string> SendExtraction(ExtractionProof proof)
        {
            try
            {
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(_centralCharacter),
                    JsonConvert.SerializeObject(new DispatchProposal { Kind = "extraction-commit", Extraction = proof }), 1000, 4000).ConfigureAwait(false);
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
                !_config.Roles.Any(p => string.Equals(p.Key, proof.Role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Value?.Character, proof.Character, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Incomplete extraction proof or unknown source.");
            string completed = Path.Combine(ExtractionDirectory(proof), "completed.json");
            if (File.Exists(completed))
            {
                var previous = JsonConvert.DeserializeObject<ExtractionProof>(File.ReadAllText(completed));
                if (JsonConvert.SerializeObject(previous) != JsonConvert.SerializeObject(proof))
                    throw new InvalidOperationException("Extraction ID was reused with different evidence.");
                WithdrawalStore.ReleaseRecovery(_settingsDir, proof.Id);
                return;
            }
            if (!WithdrawalStore.OwnsRecovery(_settingsDir, proof.Id, proof.LedgerId))
                throw new InvalidOperationException("Extraction no longer owns its ledger reservation.");
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(ExtractionDirectory(proof), "verified.json"), proof);
            ActiveLedgerStore.RecordExtraction(_settingsDir, proof);
            if (string.Equals(proof.Character, _centralCharacter, StringComparison.OrdinalIgnoreCase))
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
            RuntimeStateStore.WriteJsonAtomic(completed, proof);
            WithdrawalStore.ReleaseRecovery(_settingsDir, proof.Id);
        }

        private void FinishExtraction()
        {
            _extraction = null;
            _extractionCommitError = null;
            _extractionCommit = null;
            _extractionScan.Restart();
        }
    }
}
