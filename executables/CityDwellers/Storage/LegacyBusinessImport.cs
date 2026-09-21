using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CityBankers;
using CityBankers.Shared;
using CityDwellers.Shared;
using MySqlConnector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Host
{
    // Startup-only reader for the retired schema. Raw bytes are decoded once;
    // only business entities enter the new tables. There is no archive copy.
    internal sealed class LegacyBusinessImport
    {
        private readonly MySqlConnection _connection;
        private readonly string _root;
        private readonly Dictionary<string, long> _documents = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<TellQueueJob> Tells = new List<TellQueueJob>();
        internal readonly List<ManagerChannelJob> Channels = new List<ManagerChannelJob>();
        internal string RaidCoordinator, OrgOutputBudget;
        internal LegacyBusinessImport(MySqlConnection connection, string root)
        {
            _connection = connection; _root = root;
            if (Exists("cd_documents"))
                using (var command = new MySqlCommand("SELECT path,document_id FROM cd_documents ORDER BY path", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) _documents.Add(reader.GetString(0), reader.GetInt64(1));
        }
        internal bool Exists(string table)
        {
            using (var command = new MySqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table", _connection))
            { command.Parameters.AddWithValue("@table", table); return Convert.ToInt32(command.ExecuteScalar()) != 0; }
        }
        private string Text(string name)
        {
            long id;
            if (!_documents.TryGetValue(name, out id)) return null;
            using (var content = new MemoryStream())
            {
                using (var command = new MySqlCommand("SELECT content FROM cd_document_chunks WHERE document_id=@id ORDER BY chunk_no", _connection))
                {
                    command.Parameters.AddWithValue("@id", id);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        { byte[] bytes = (byte[])reader.GetValue(0); content.Write(bytes, 0, bytes.Length); }
                }
                return new UTF8Encoding(false, true).GetString(content.ToArray()).TrimStart('\uFEFF');
            }
        }
        private T Read<T>(string name) where T : class
        {
            string text = Text(name);
            return text == null ? null : JsonConvert.DeserializeObject<T>(text);
        }
        private IEnumerable<JObject> Lines(string name)
        {
            using (var reader = new StringReader(Text(name) ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (!string.IsNullOrWhiteSpace(line)) yield return JObject.Parse(line);
            }
        }
        private JObject Relational(string table, string stateName, string[] fields)
        {
            var result = new JObject { ["Items"] = new JArray(), ["UpdatedUtc"] = DateTime.MinValue };
            using (var command = new MySqlCommand("SELECT present,format,baseline_run_id,updated_ticks,updated_kind FROM cd_banker_state WHERE state_name=@name", _connection))
            {
                command.Parameters.AddWithValue("@name", stateName);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidDataException("Existing " + stateName + " state metadata is missing.");
                    if (!reader.GetBoolean(0)) return null;
                    if (!reader.IsDBNull(1)) result["Format"] = reader.GetString(1);
                    if (stateName == "stock" && !reader.IsDBNull(2)) result["BaselineRunId"] = reader.GetString(2);
                    result["UpdatedUtc"] = new DateTime(reader.GetInt64(3), (DateTimeKind)reader.GetInt32(4));
                }
            }
            string[] properties = fields.Select(f => f.Split(':')[0]).ToArray();
            string[] columns = fields.Select(f => f.Split(':')[1]).ToArray();
            using (var command = new MySqlCommand("SELECT " + string.Join(",", columns) + " FROM " + table + " ORDER BY ordinal", _connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var item = new JObject();
                    for (int i = 0; i < columns.Length; i++)
                        item[properties[i]] = reader.IsDBNull(i) ? (properties[i].EndsWith("Utc") ? new JValue(DateTime.MinValue) : JValue.CreateNull()) : JToken.FromObject(reader.GetValue(i));
                    ((JArray)result["Items"]).Add(item);
                }
            return result;
        }
        internal AccountingState ReadBusiness()
        {
            var state = new AccountingState
            {
                Ledger = Exists("cd_ledger_items") ? Relational("cd_ledger_items", "ledger", new[] {
                    "Id:ledger_id", "AoId:aoid", "HighId:high_id", "Ql:ql", "TransactionId:transaction_id", "From:donor",
                    "ReceivedUtc:received_utc", "Family:family", "Character:character_name", "Location:location", "Bag:bag_slot", "Slot:item_slot"
                })?.ToObject<ActiveLedgerState>() : Read<ActiveLedgerState>("ledger.json"),
                Stock = Exists("cd_stock_items") ? Relational("cd_stock_items", "stock", new[] {
                    "TransactionId:transaction_id", "Role:storage_role", "PhysicalRole:physical_role", "RouteMatchesPhysicalRole:route_matches",
                    "Character:character_name", "BagSource:bag_source", "BagOuterSlot:bag_slot", "InnerSlot:item_slot", "UniqueIdentity:unique_identity",
                    "AoId:aoid", "HighId:high_id", "Ql:ql", "Name:item_name", "ObservedUtc:observed_utc"
                })?.ToObject<CurrentStockState>() : Read<CurrentStockState>("current-stock.json"),
                Storage = Read<StorageState>("storage-state.json"),
                Dispatch = Read<DispatchQueueState>("dispatch-queue.json") ?? new DispatchQueueState(),
                LostItems = Read<LostItemsState>("lost.json") ?? new LostItemsState(),
                ItemIndex = Read<SymbiantIndexState>("symbiant-index.json"),
                Cloak = Read<CloakState>("citymanager-cloak-state.json")
            };
            var withdrawals = Read<JObject>("withdrawal.json");
            state.Withdrawals = withdrawals == null ? new WithdrawalQueueState() : withdrawals["Withdrawals"] != null
                ? withdrawals.ToObject<WithdrawalQueueState>() : new WithdrawalQueueState { Withdrawals = new List<WithdrawalState> { withdrawals.ToObject<WithdrawalState>() } };
            foreach (var order in state.Withdrawals.Withdrawals)
                if (string.IsNullOrEmpty(order.OrderId)) order.OrderId = order.Id;
            if (state.Ledger == null)
            {
                if (state.Stock?.Items?.Count > 0) throw new InvalidDataException("Stock exists without its ledger; refusing to invent item ownership during import.");
                state.Ledger = new ActiveLedgerState();
            }
            if (state.Stock == null) state.Stock = new CurrentStockState();
            foreach (string name in _documents.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (name.StartsWith("ledger/citybankers-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    foreach (var row in Lines(name))
                    { var record = row.ToObject<LedgerRecord>(); record.Id = Guid.NewGuid().ToString("N"); state.Transactions.Add(record); }
                else if (name.StartsWith("history/history-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    foreach (var row in Lines(name))
                    { var record = row.ToObject<ActiveHistoryRecord>(); record.Id = Guid.NewGuid().ToString("N"); state.ItemHistory.Add(record); }
                else if (name == "citymanager-cloak-events.jsonl")
                    foreach (var row in Lines(name))
                    { var record = row.ToObject<CloakEvent>(); record.Id = Guid.NewGuid().ToString("N"); state.CloakEvents.Add(record); }
                else if (name.StartsWith("history/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    ImportDifferences(state, name, Read<JObject>(name));
                else if (name.StartsWith("custody-transactions/", StringComparison.OrdinalIgnoreCase) && !name.Contains("/dispatch-cancel-"))
                    ImportReceipt(state, name);
                else if (name.StartsWith("banker-extractions/", StringComparison.OrdinalIgnoreCase))
                    ImportExtraction(state, name);
                else if (name.StartsWith("banker-returns/", StringComparison.OrdinalIgnoreCase))
                    ImportReturn(state, name);
                else if (name == "bag-recovery/central-reserve.json")
                {
                    var value = Read<JObject>(name);
                    state.Reserve.Bags = value?["Bags"]?.ToObject<List<ReserveBag>>() ?? new List<ReserveBag>();
                    foreach (var target in (value?["Targets"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                        state.Reserve.SetTarget(target.Name, (int)target.Value);
                }
                else if (name.StartsWith("bag-recovery/", StringComparison.OrdinalIgnoreCase) && name.EndsWith("/reserve-move.json"))
                {
                    var operation = Read<ReserveOperation>(name);
                    if (operation != null && operation.Phase != "completed")
                    { operation.Character = name.Split('/')[1]; state.ReserveOperations.Add(operation); }
                }
                else if (name.StartsWith("bag-recovery/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json"))
                {
                    var value = Read<JObject>(name);
                    if ((string)value?["Format"] == "citybankers-shared-bag-recovery-v1")
                    {
                        var record = value.ToObject<BagHistoryRecord>();
                        record.PendingActionId = (string)value["Pending"]?["Id"];
                        record.PendingActionKind = (string)value["Pending"]?["Kind"];
                        var previous = state.BagHistory.SingleOrDefault(row => row.Run == record.Run);
                        if (previous == null || previous.UpdatedUtc <= record.UpdatedUtc)
                        { state.BagHistory.Remove(previous); state.BagHistory.Add(record); }
                    }
                }
                else if ((name.StartsWith("tell-queue/pending/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tell-queue/assigned/", StringComparison.OrdinalIgnoreCase)) && name.EndsWith(".json"))
                    Tells.Add(Read<TellQueueJob>(name));
                else if (name.StartsWith("tell-queue/manager-channel/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json"))
                    Channels.Add(Read<ManagerChannelJob>(name));
            }
            state.Receipts.RemoveAll(row => row.Phase == "applied");
            state.Extractions.RemoveAll(row => row.Phase == "completed");
            state.Returns.RemoveAll(row => row.CompletedUtc.HasValue);
            RaidCoordinator = Text("citymanager-raid-state.json");
            OrgOutputBudget = Text("citymanager-org-size.json");
            var names = (state.ItemIndex?.Items ?? new List<SymbiantIndexItem>()).GroupBy(row => row.AoId).ToDictionary(group => group.Key, group => group.First().Name);
            foreach (var item in state.Ledger.Items.Concat(state.ItemHistory.SelectMany(row => new[] { row.Item, row.CurrentItem })).Where(item => item != null))
            { string name; if (names.TryGetValue(item.AoId, out name) && string.IsNullOrEmpty(item.Name)) item.Name = name; }
            foreach (var record in state.ItemHistory)
                if (string.IsNullOrEmpty(record.ItemName)) record.ItemName = record.Item?.Name ?? record.CurrentItem?.Name;
            ExportSettings();
            return state;
        }
        private static void ImportDifferences(AccountingState state, string name, JObject value)
        {
            foreach (var difference in (value?["Differences"] ?? value?["Plan"]?["Differences"]) as JArray ?? new JArray())
                state.ItemHistory.Add(new ActiveHistoryRecord
                {
                    Id = Guid.NewGuid().ToString("N"), Reason = "census-" + (string)difference["Kind"],
                    LeftUtc = (DateTime?)value["RecordedUtc"] ?? DateTime.MinValue, Source = (string)value["Generation"] ?? name,
                    Item = difference["Previous"]?.ToObject<ActiveLedgerItem>(), CurrentItem = difference["Current"]?.ToObject<ActiveLedgerItem>(),
                    ItemName = (string)difference["Physical"]?["Item"]?["Name"], EventTimeKnown = false
                });
        }
        private void ImportReceipt(AccountingState state, string name)
        {
            if (!name.EndsWith(".json")) return;
            var value = Read<JObject>(name);
            if (value?["Kind"] == null || value["Phase"] == null) return;
            var receipt = value.ToObject<ReceiptEvidence>();
            receipt.Id = name.Split('/')[1];
            // Paths were enumerated in sequence order; retain just the current head.
            state.Receipts.RemoveAll(row => row.Id == receipt.Id);
            state.Receipts.Add(receipt);
        }
        private void ImportExtraction(AccountingState state, string name)
        {
            if (!name.EndsWith(".json")) return;
            var proof = Read<ExtractionProof>(name);
            if (proof == null || string.IsNullOrEmpty(proof.Id)) return;
            proof.Phase = Path.GetFileNameWithoutExtension(name);
            var prior = state.Extractions.SingleOrDefault(row => row.Id == proof.Id);
            if (prior?.Phase == "completed") return;
            state.Extractions.RemoveAll(row => row.Id == proof.Id); state.Extractions.Add(proof);
        }
        private void ImportReturn(AccountingState state, string name)
        {
            if (!name.EndsWith(".json")) return;
            var value = Read<JObject>(name);
            var offer = (value?["Offer"] ?? value)?.ToObject<ReturnOffer>();
            if (offer == null || string.IsNullOrEmpty(offer.Id)) return;
            offer.Phase = Path.GetFileNameWithoutExtension(name);
            var prior = state.Returns.SingleOrDefault(row => row.Id == offer.Id);
            if (prior?.CompletedUtc != null) return;
            state.Returns.RemoveAll(row => row.Id == offer.Id); state.Returns.Add(offer);
        }
        private void ExportSettings()
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "adminlist.json", "banlist.json", "alts.json", "memberlist.json", "citymanager-membership-state.json",
                "citybankers-phatz-policy.json", "citybankers-bank-terminal.json", "items-pairs.json", "items.json",
                "buffers/BanList.json", "buffers/UserRanks.json"
            };
            foreach (string name in _documents.Keys.Where(name => allowed.Contains(name) ||
                (name.StartsWith("buffers/", StringComparison.OrdinalIgnoreCase) && name.Split('/').Length == 3 && !name.Split('/').Any(part => part == "." || part == "..") &&
                 (name.EndsWith("/BanList.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith("/UserRanks.json", StringComparison.OrdinalIgnoreCase)))))
            {
                string target = Path.Combine(_root, name == "items.json" ? "data" : "config", name.Replace('/', Path.DirectorySeparatorChar));
                if (name.StartsWith("buffers/", StringComparison.OrdinalIgnoreCase))
                    target = Path.Combine(Path.GetDirectoryName(target), name.EndsWith("/banlist.json", StringComparison.OrdinalIgnoreCase) ? "BanList.json" : "UserRanks.json");
                if (File.Exists(target)) continue; // Preserve operator edits on reruns.
                DiskFiles.WriteAllText(target, Text(name));
            }
        }
    }
}
