using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CityBankers;
using CityBankers.Shared;
using CityDwellers.Shared;
using MySqlConnector;

namespace CityDwellers.Host
{
    // A fixed mapping of business entities to ordinary relational columns. No
    // paths, serialized documents, checksums or runtime-created entity types.
    internal sealed class BusinessTables
    {
        internal sealed class Table
        {
            internal string Name;
            internal Type Type;
            internal FieldInfo Field;
            internal bool Many;
            internal Func<object, string> Identity;
            internal Func<object, bool> Retain;
            internal FieldInfo[] Columns;
            internal string[] ColumnNames => Scalar(Type) ? new[] { "value" } : Columns.Select(Column).ToArray();
            internal readonly List<Table> Children = new List<Table>();
            internal Table(string name, Type parent, string field, Func<object, string> identity)
            {
                Name = name;
                Field = parent.GetField(field);
                Many = typeof(IList).IsAssignableFrom(Field.FieldType);
                Type = Many ? Field.FieldType.GetGenericArguments()[0] : Field.FieldType;
                Identity = identity;
                Columns = Type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f => Scalar(f.FieldType) && f.Name != "Format" && f.Name != "PickupHostGeneration" && f.Name != "PickupDeadlineStamp" && f.Name != "ObservationAfter" && f.Name != "ReadRetries").ToArray();
            }
            internal Table Child(string name, string field, Func<object, string> identity)
            {
                var table = new Table(name, Type, field, identity);
                Children.Add(table);
                return table;
            }
        }
        internal sealed class Row
        {
            internal string Id, Parent;
            internal int Position;
            internal object[] Values;
        }
        private readonly List<Table> _roots = new List<Table>();
        private Dictionary<string, Dictionary<string, Row>> _committedRows;
        internal BusinessTables()
        {
            var reserve = Root("cd_reserve", "Reserve");
            reserve.Child("cd_reserve_bags", "Bags", o => ((ReserveBag)o).Identity);
            reserve.Child("cd_reserve_targets", "Targets", o => ((ReserveTarget)o).Character.ToLowerInvariant());
            Root("cd_pending_reserve_moves", "ReserveOperations", o => ((ReserveOperation)o).Character.ToLowerInvariant());
            var bagsHistory = Root("cd_bag_history", "BagHistory", o => ((BagHistoryRecord)o).Run);
            bagsHistory.Child("cd_bag_transfers", "Transfers", o => ((BagHistoryTransfer)o).Id)
                .Child("cd_bag_transfer_items", "Item", null);
            bagsHistory.Child("cd_bag_disposals", "Disposals", o => ((BagHistoryTransfer)o).Id)
                .Child("cd_bag_disposal_items", "Item", null);
            Root("cd_cloak", "Cloak");
            Root("cd_cloak_events", "CloakEvents", o => ((CloakEvent)o).Id);
            var alts = Root("cd_alts", "Alts");
            var altGroups = alts.Child("cd_alt_groups", "Groups", o =>
                Part(((AltGroupState)o).Main));
            altGroups.Child("cd_alt_observed", "ObservedCharacters", o =>
                ((string)o).ToLowerInvariant());
            altGroups.Child("cd_alt_added", "AddedCharacters", o =>
                ((string)o).ToLowerInvariant());
            altGroups.Child("cd_alt_removed", "RemovedCharacters", o =>
                ((string)o).ToLowerInvariant());
            var ledger = Root("cd_ledger", "Ledger");
            ledger.Child("cd_ledger_entries", "Items", o => ((ActiveLedgerItem)o).Id);
            var stock = Root("cd_stock", "Stock");
            stock.Child("cd_stock_entries", "Items", o =>
            {
                var item = (StockItemState)o;
                return Part(item.Character) + Part(item.BagSource) + item.BagOuterSlot + ":" + item.InnerSlot;
            });
            var storage = Root("cd_storage", "Storage");
            var workers = storage.Child("cd_storage_workers", "Workers", o => Part(((StorageWorkerState)o).Character));
            var bags = workers.Child("cd_storage_bags", "Bags", o =>
            {
                var bag = (StorageBagState)o;
                return Part(bag.Source) + bag.OuterSlotInstance;
            });
            bags.Child("cd_storage_contents", "Items", o => ((StoredItemState)o).InnerSlot.ToString(CultureInfo.InvariantCulture));
            Root("cd_dispatch", "Dispatch").Child("cd_dispatch_batches", "Batches", o => ((DispatchBatchState)o).BatchId)
                .Child("cd_dispatch_items", "Items", null);
            var withdrawals = Root("cd_withdrawals", "Withdrawals")
                .Child("cd_withdrawal_orders", "Withdrawals", o => ((WithdrawalState)o).Id);
            withdrawals.Child("cd_withdrawal_items", "Item", null);
            withdrawals.Child("cd_withdrawal_recipients", "AllowedCharacters", null);
            withdrawals.Child("cd_withdrawal_source_slots", "PreExtractionInventorySlots", null);
            Root("cd_lost_found", "LostItems").Child("cd_lost_claims", "Entries", o => ((LostItemRecord)o).IncidentId)
                .Child("cd_lost_claim_items", "PreviousLedgerEntry", null);
            Root("cd_transactions", "Transactions", o => ((LedgerRecord)o).Id)
                .Child("cd_transaction_items", "Items", null);
            var history = Root("cd_item_history", "ItemHistory", o => ((ActiveHistoryRecord)o).Id);
            history.Child("cd_item_history_items", "Item", null);
            history.Child("cd_item_history_current", "CurrentItem", null);
            var receipts = Root("cd_pending_custody", "Receipts", o => ((ReceiptEvidence)o).Id);
            receipts.Retain = o => ((ReceiptEvidence)o).Phase != "applied";
            foreach (string name in new[] { "PreparedItems", "Before", "Expected", "Observed" })
                receipts.Child("cd_custody_" + name.ToLowerInvariant(), name, null);
            receipts.Child("cd_custody_ledger_entries", "LedgerIds", null);
            receipts.Child("cd_custody_before_slots", "BeforeSlots", null);
            receipts.Child("cd_custody_observed_slots", "ObservedSlots", null);
            var extractions = Root("cd_pending_extractions", "Extractions", o => ((ExtractionProof)o).Id);
            extractions.Retain = o => ((ExtractionProof)o).Phase != "completed";
            extractions.Child("cd_extraction_item", "Item", null);
            foreach (string name in new[] { "BeforeSource", "AfterSource", "BeforeInventory", "AfterInventory" })
                extractions.Child("cd_extraction_" + name.ToLowerInvariant(), name, null)
                    .Child("cd_extraction_" + name.ToLowerInvariant() + "_items", "Item", null);
            var returns = Root("cd_pending_returns", "Returns", o => ((ReturnOffer)o).Id);
            returns.Retain = o => !((ReturnOffer)o).CompletedUtc.HasValue;
            returns.Child("cd_return_items", "Item", null);

        }
        private Table Root(string name, string field, Func<object, string> identity = null)
        {
            var table = new Table(name, typeof(AccountingState), field, identity);
            _roots.Add(table);
            return table;
        }
        private static string Part(string value)
        {
            string text = (value ?? "").ToLowerInvariant();
            return text.Length + ":" + text;
        }
        private static bool Scalar(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(string) || type == typeof(int) || type == typeof(long) ||
                type == typeof(bool) || type == typeof(DateTime);
        }
        private static string Column(FieldInfo field) => Regex.Replace(field.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
        private static string SqlType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return "TEXT";
            if (type == typeof(bool)) return "BOOLEAN";
            if (type == typeof(DateTime)) return "DATETIME(6)";
            return type == typeof(long) ? "BIGINT" : "INT";
        }
        private static object Value(object value)
        {
            if (value is DateTime)
            {
                var date = (DateTime)value;
                if (date.Year < 1000) return null;
                // DATETIME is the authority, including operator edits.
                if (date.Kind == DateTimeKind.Local) date = date.ToUniversalTime();
                return new DateTime(date.Ticks - date.Ticks % 10, DateTimeKind.Utc);
            }
            return value;
        }
        private IEnumerable<Table> Tables() => _roots.SelectMany(Descendants);
        private static IEnumerable<Table> Descendants(Table table)
        {
            yield return table;
            foreach (var child in table.Children.SelectMany(Descendants)) yield return child;
        }
        internal void Create(MySqlConnection connection)
        {
            foreach (var table in Tables())
            {
                var columns = Scalar(table.Type) ? new[] { "`value` " + SqlType(table.Type) + " NULL" } :
                    table.Columns.Select(f => "`" + Column(f) + "` " + SqlType(f.FieldType) + " NULL");
                string sql = "CREATE TABLE IF NOT EXISTS " + table.Name +
                    " (record_id VARCHAR(640) NOT NULL PRIMARY KEY, parent_id VARCHAR(640) NULL,position INT NOT NULL" +
                    (table.ColumnNames.Length == 0 ? "" : "," + string.Join(",", columns)) + ",KEY ix_parent(parent_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin";
                using (var command = new MySqlCommand(sql, connection)) command.ExecuteNonQuery();
            }
        }

        private Dictionary<string, Dictionary<string, Row>> Flatten(AccountingState state)
        {
            var result = Tables().ToDictionary(t => t.Name, t => new Dictionary<string, Row>(StringComparer.Ordinal));
            foreach (var root in _roots) Flatten(root, state, null, result);
            return result;
        }
        private static void Flatten(Table table, object parent, string parentId, Dictionary<string, Dictionary<string, Row>> output)
        {
            object value = table.Field.GetValue(parent);
            if (value == null) return;
            var entries = table.Many ? ((IEnumerable)value).Cast<object>() : new[] { value };
            int ordinal = 0;
            foreach (var entry in entries)
            {
                if (entry == null) throw new InvalidOperationException("Null entry in " + table.Name);
                if (table.Retain != null && !table.Retain(entry)) continue;
                string local = table.Identity == null ? ordinal.ToString(CultureInfo.InvariantCulture) : table.Identity(entry);
                if (string.IsNullOrEmpty(local)) throw new InvalidOperationException("Missing identity in " + table.Name);
                string id = (parentId == null || parentId == "0") ? local : parentId + "/" + local;
                if (id.Length > 640) throw new InvalidOperationException("Business identity exceeds 640 characters in " + table.Name);
                output[table.Name].Add(id, new Row { Id = id, Parent = parentId, Position = table.Identity == null ? ordinal : 0,
                    Values = Scalar(table.Type) ? new[] { Value(entry) } : table.Columns.Select(f => Value(f.GetValue(entry))).ToArray() });
                foreach (var child in table.Children) Flatten(child, entry, id, output);
                ordinal++;
            }
        }
        internal AccountingState Load(MySqlConnection connection)
        {
            var state = new AccountingState();
            var loaded = Tables().ToDictionary(table => table.Name, table => new Dictionary<string, Row>(StringComparer.Ordinal));
            foreach (var root in _roots) Load(root, connection, new Dictionary<string, object> { [""] = state }, loaded);
            _committedRows = loaded;
            return state;
        }
        private static void Load(Table table, MySqlConnection connection, Dictionary<string, object> parents, Dictionary<string, Dictionary<string, Row>> loaded)
        {
            var entries = new Dictionary<string, object>(StringComparer.Ordinal);
            using (var command = new MySqlCommand("SELECT record_id,parent_id,position" +
                (table.ColumnNames.Length == 0 ? "" : "," + string.Join(",", table.ColumnNames.Select(c => "`" + c + "`"))) + " FROM " + table.Name + " ORDER BY parent_id,position,record_id", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    string id = reader.GetString(0), parentId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    object parent;
                    if (!parents.TryGetValue(parentId, out parent)) throw new InvalidOperationException("Missing parent for " + table.Name + " row " + id);
                    var entry = Scalar(table.Type) ? Convert.ChangeType(reader.GetValue(3), table.Type, CultureInfo.InvariantCulture) : Activator.CreateInstance(table.Type);
                    for (int i = 0; i < table.Columns.Length; i++)
                    {
                        if (reader.IsDBNull(i + 3)) continue;
                        var field = table.Columns[i];
                        var type = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
                        object value = reader.GetValue(i + 3);
                        if (type == typeof(DateTime)) value = DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);
                        else value = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
                        field.SetValue(entry, value);
                    }
                    if (table.Many)
                    {
                        var list = (IList)table.Field.GetValue(parent);
                        if (list == null) { list = (IList)Activator.CreateInstance(table.Field.FieldType); table.Field.SetValue(parent, list); }
                        list.Add(entry);
                    }
                    else table.Field.SetValue(parent, entry);
                    entries.Add(id, entry);
                    loaded[table.Name].Add(id, new Row { Id = id, Parent = parentId == "" ? null : parentId,
                        Position = reader.GetInt32(2), Values = Scalar(table.Type) ? new[] { Value(entry) } :
                            table.Columns.Select(field => Value(field.GetValue(entry))).ToArray() });
                }
            foreach (var child in table.Children) Load(child, connection, entries, loaded);
        }
        internal void Commit(MySqlConnection connection, AccountingState previous, AccountingState next, Action<MySqlTransaction> beforeCommit = null)
        {
            var before = _committedRows ?? Flatten(previous);
            var after = Flatten(next);
            var changed = Tables().SelectMany(table => after[table.Name].Values
                .Where(row => !before[table.Name].ContainsKey(row.Id) || !Same(before[table.Name][row.Id], row))
                .Select(row => Tuple.Create(table, row))).ToArray();
            var removed = Tables().Reverse().SelectMany(table => before[table.Name].Keys.Except(after[table.Name].Keys)
                .Select(id => Tuple.Create(table, id))).ToArray();
            if (changed.Length == 0 && removed.Length == 0 && beforeCommit == null) return;
            // Reconnect only when a real write is needed, before starting a
            // transaction. Never replay an indeterminate commit.
            if (connection.State != System.Data.ConnectionState.Open || !connection.Ping())
            { connection.Close(); connection.Open(); }
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var group in removed.GroupBy(entry => entry.Item1))
                    for (int offset = 0; offset < group.Count(); offset += 100)
                    {
                        var ids = group.Skip(offset).Take(100).Select(entry => entry.Item2).ToArray();
                        using (var command = new MySqlCommand("DELETE FROM " + group.Key.Name + " WHERE record_id IN (" +
                            string.Join(",", Enumerable.Range(0, ids.Length).Select(i => "@id" + i)) + ")", connection, transaction))
                        {
                            for (int i = 0; i < ids.Length; i++) command.Parameters.AddWithValue("@id" + i, ids[i]);
                            command.ExecuteNonQuery();
                        }
                    }
                foreach (var group in changed.GroupBy(entry => entry.Item1))
                {
                    var table = group.Key;
                    var columns = new[] { "record_id", "parent_id", "position" }.Concat(table.ColumnNames).ToArray();
                    var rows = group.Select(entry => entry.Item2).ToArray();
                    for (int offset = 0; offset < rows.Length; offset += 100)
                        using (var command = new MySqlCommand("", connection, transaction))
                        {
                            var groups = new List<string>();
                            int parameter = 0;
                            foreach (var row in rows.Skip(offset).Take(100))
                            {
                                var values = new object[] { row.Id, row.Parent, row.Position }.Concat(row.Values).ToArray();
                                var names = new List<string>();
                                foreach (object value in values)
                                {
                                    string name = "@v" + parameter++; names.Add(name);
                                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                                }
                                groups.Add("(" + string.Join(",", names) + ")");
                            }
                            command.CommandText = "INSERT INTO " + table.Name + " (" + string.Join(",", columns.Select(c => "`" + c + "`")) +
                                ") VALUES " + string.Join(",", groups) + " ON DUPLICATE KEY UPDATE " +
                                string.Join(",", columns.Skip(1).Select(c => "`" + c + "`=VALUES(`" + c + "`)"));
                            command.ExecuteNonQuery();
                        }
                }
                beforeCommit?.Invoke(transaction);
                transaction.Commit();
                _committedRows = after;
            }
        }
        private static bool Same(Row a, Row b) => a.Parent == b.Parent && a.Position == b.Position && a.Values.SequenceEqual(b.Values);
    }
}
