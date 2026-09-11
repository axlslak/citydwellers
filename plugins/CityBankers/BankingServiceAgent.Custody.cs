using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private ReceiptEvidence _receipt;
        private string _receiptDirectory;
        private Action _afterReceipt;
        private Stopwatch _custodyVerificationWait;
        private int _receiptSequence;

        private sealed class ReceiptEvidence
        {
            public string Kind;
            public string TransactionId;
            public string BatchId;
            public string Character;
            public string Phase;
            public List<TransferItemState> Before;
            public List<TransferItemState> Expected;
            public List<TransferItemState> Observed;
            public object BeforeSlots;
            public object ObservedSlots;
            public int Direction;
        }

        private List<TransferItemState> PhysicalInventory()
        {
            if (Inventory.Items == null) throw new InvalidOperationException("Inventory unavailable.");
            return SnapshotTradeItems(Inventory.Items.Where(i => i != null &&
                i.Slot.Type == IdentityType.Inventory).ToList());
        }

        private static string CustodyKey(TransferItemState item)
        {
            return item.AoId + "/" + item.HighId + "/" + item.Ql;
        }

        private object PhysicalSlots()
        {
            return Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory)
                .Select(i => new { Slot = i.Slot.ToString(), Identity = i.UniqueIdentity.ToString(),
                    AoId = i.Id, HighId = i.HighId, Ql = i.Ql, Name = i.Name }).ToList();
        }

        private void RecordDonationOffer()
        {
            if (_receipt == null) throw new InvalidOperationException("Donation has no durable preparation.");
            _receipt.Expected = new List<TransferItemState>(_donationSnapshot);
            PersistReceipt("offer-validated");
        }

        private void PrepareReceipt(string kind, string transaction, string batch,
            List<TransferItemState> expected, int direction)
        {
            if (_receipt != null)
                throw new InvalidOperationException("Unresolved custody evidence prevents another trade.");
            _receiptDirectory = Path.Combine(RuntimeStateStore.GetDataDirectory(_settingsDir),
                "custody-transactions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_receiptDirectory);
            _receiptSequence = 0;
            _receipt = new ReceiptEvidence { Kind = kind, TransactionId = transaction,
                BatchId = batch, Character = Client.CharacterName, Before = PhysicalInventory(), BeforeSlots = PhysicalSlots(),
                Expected = expected == null ? null : new List<TransferItemState>(expected), Direction = direction };
            PersistReceipt("prepared");
        }

        private void PersistReceipt(string phase)
        {
            _receipt.Phase = phase;
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_receipt, Formatting.Indented));
            string path = Path.Combine(_receiptDirectory,
                (_receiptSequence++).ToString("D6") + "-" + phase + ".json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private void AwaitPhysicalReceipt(List<TransferItemState> expected, Action apply)
        {
            if (_afterReceipt != null) return;
            if (_receipt == null) { StartupCensusGate.Block("Finished without prepared custody evidence."); return; }
            _receipt.Expected = new List<TransferItemState>(expected ?? new List<TransferItemState>());
            PersistReceipt("finished-awaiting-inventory");
            _custodyVerificationWait = Stopwatch.StartNew();
            _afterReceipt = apply;
        }

        private bool TickPhysicalReceipt()
        {
            if (_afterReceipt == null) return false;
            _receipt.Observed = PhysicalInventory();
            _receipt.ObservedSlots = PhysicalSlots();
            var expectedCounts = _receipt.Before.GroupBy(CustodyKey)
                .ToDictionary(group => group.Key, group => group.Count());
            foreach (var group in _receipt.Expected.GroupBy(CustodyKey))
            {
                int count;
                expectedCounts.TryGetValue(group.Key, out count);
                expectedCounts[group.Key] = count + _receipt.Direction * group.Count();
            }
            var actualCounts = _receipt.Observed.GroupBy(CustodyKey)
                .ToDictionary(group => group.Key, group => group.Count());
            bool exact = (_receipt.Direction == 0 || _receipt.Expected.Count > 0) &&
                expectedCounts.Where(pair => pair.Value != 0).OrderBy(pair => pair.Key)
                    .SequenceEqual(actualCounts.OrderBy(pair => pair.Key));
            if (!exact)
            {
                if (_custodyVerificationWait.Elapsed.TotalSeconds >= 10)
                {
                    PersistReceipt("inventory-mismatch");
                    StartupCensusGate.Block("Physical inventory delta did not match " +
                        _receipt.Kind + " transaction " + _receipt.TransactionId +
                        ". Evidence retained; no receipt inferred from the trade window.");
                }
                return true;
            }
            PersistReceipt("inventory-verified");
            Action apply = _afterReceipt;
            // An exception after an AO action must not automatically replay it next tick.
            _afterReceipt = null;
            try
            {
                apply();
                PersistReceipt("applied");
                _receipt = null;
            }
            catch (Exception ex)
            {
                StartupCensusGate.Block("Verified custody could not be committed: " + ex);
            }
            return true;
        }

        private void VerifyCancelledReceipt()
        {
            if (_receipt == null || _afterReceipt != null) return;
            _receipt.Direction = 0;
            _receipt.Expected = new List<TransferItemState>();
            PersistReceipt("cancelled-awaiting-inventory");
            _custodyVerificationWait = Stopwatch.StartNew();
            _afterReceipt = () => { };
        }
    }
}
