using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    // Domain persistence: one item per row, typed columns and domain indexes.
    // JSON objects here are DTO adapters for the shared assemblies, never stored payloads.
    public static class BankerSqlStore
    {
        private sealed class Field
        {
            internal string Property, Column, Type;
            internal Field(string property, string column, string type) { Property = property; Column = column; Type = type; }
        }
        private sealed class Entity
        {
            internal string Name, Table, Legacy;
            internal Field[] Fields;
            internal Entity(string name, string table, string legacy, params Field[] fields)
            { Name = name; Table = table; Legacy = legacy; Fields = fields; }
        }
        private static readonly Entity Ledger = new Entity("ledger", "cd_ledger_items", "ledger.json",
            new Field("Id", "ledger_id", "VARCHAR(191)"), new Field("AoId", "aoid", "INT"),
            new Field("HighId", "high_id", "INT"), new Field("Ql", "ql", "INT"),
            new Field("TransactionId", "transaction_id", "VARCHAR(191)"), new Field("From", "donor", "VARCHAR(191)"),
            new Field("ReceivedUtc", "received_utc", "DATE"), new Field("Family", "family", "VARCHAR(64)"),
            new Field("Character", "character_name", "VARCHAR(191)"), new Field("Location", "location", "VARCHAR(64)"),
            new Field("Bag", "bag_slot", "INT"), new Field("Slot", "item_slot", "INT"));
        private static readonly Entity Stock = new Entity("stock", "cd_stock_items", "current-stock.json",
            new Field("TransactionId", "transaction_id", "VARCHAR(191)"), new Field("Role", "storage_role", "VARCHAR(64)"),
            new Field("PhysicalRole", "physical_role", "VARCHAR(64)"), new Field("RouteMatchesPhysicalRole", "route_matches", "BOOL"),
            new Field("Character", "character_name", "VARCHAR(191)"), new Field("BagSource", "bag_source", "VARCHAR(64)"),
            new Field("BagOuterSlot", "bag_slot", "INT"), new Field("InnerSlot", "item_slot", "INT"),
            new Field("UniqueIdentity", "unique_identity", "VARCHAR(191)"), new Field("AoId", "aoid", "INT"),
            new Field("HighId", "high_id", "INT"), new Field("Ql", "ql", "INT"), new Field("Name", "item_name", "TEXT"),
            new Field("ObservedUtc", "observed_utc", "DATE"));

        public static T ReadLedger<T>() where T : class => Read(Ledger)?.ToObject<T>();
        public static T ReadStock<T>() where T : class => Read(Stock)?.ToObject<T>();
        public static T ReadLedgerForCharacter<T>(string character) where T : class
        {
            if (string.IsNullOrWhiteSpace(character)) throw new ArgumentException("Character is required.");
            return Read(Ledger, character)?.ToObject<T>();
        }
        public static void SaveLedger(object state) => Save(Ledger, JObject.FromObject(state));
        public static void SaveStock(object state) => Save(Stock, JObject.FromObject(state));
        public static long LedgerRevision => SqlStore.ReadStatement(c =>
        {
            using (var cmd = SqlStore.Command(c, "SELECT revision FROM cd_banker_state WHERE state_name='ledger'"))
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
        });
        public static JArray LedgerTemplatePairs() => SqlStore.ReadStatement(c =>
        {
            var pairs = new JArray();
            using (var cmd = SqlStore.Command(c, "SELECT DISTINCT aoid,high_id FROM cd_ledger_items WHERE high_id IS NOT NULL AND aoid<>high_id"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) pairs.Add(new JObject { ["AoId"] = r.GetInt32(0), ["HighId"] = r.GetInt32(1) });
            return pairs;
        });

        private static IEnumerable<string> Columns(Field f)
        {
            if (f.Type == "DATE") return new[] { f.Column, f.Column + "_ticks", f.Column + "_kind" };
            return new[] { f.Column };
        }
        public static IEnumerable<string> SchemaStatements()
        {
            yield return "CREATE TABLE IF NOT EXISTS cd_banker_state (state_name VARCHAR(32) PRIMARY KEY, present BOOL NOT NULL, format VARCHAR(96) NULL, baseline_run_id VARCHAR(191) NULL, updated_ticks BIGINT NOT NULL, updated_kind INT NOT NULL, revision BIGINT NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin";
            foreach (var e in new[] { Ledger, Stock })
            {
                var columns = new List<string> { "row_key VARCHAR(191) NOT NULL PRIMARY KEY", "ordinal INT NOT NULL", "row_hash CHAR(64) CHARACTER SET ascii NOT NULL" };
                foreach (var f in e.Fields)
                    columns.Add(f.Type == "DATE" ? f.Column + " DATETIME(6) NULL," + f.Column + "_ticks BIGINT NULL," + f.Column + "_kind INT NULL" : f.Column + " " + f.Type + " NULL");
                columns.Add("KEY ix_" + e.Name + "_aoid (aoid,high_id,ql)");
                columns.Add("KEY ix_" + e.Name + "_transaction (transaction_id)");
                columns.Add("character_key VARCHAR(191) GENERATED ALWAYS AS (LOWER(character_name)) STORED");
                columns.Add("KEY ix_" + e.Name + "_character (character_key,bag_slot,item_slot)");
                if (e == Ledger) { columns.Add("UNIQUE KEY ux_ledger_id (ledger_id)"); columns.Add("KEY ix_ledger_donor (donor,received_utc)"); }
                yield return "CREATE TABLE IF NOT EXISTS " + e.Table + " (" + string.Join(",", columns) + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin";
            }
        }

        // Host calls this after taking its exclusive runtime lease, before any AO client starts.
        public static void Upgrade()
        {
            using (var connection = SqlStore.OpenConnection())
                foreach (string sql in SchemaStatements()) using (var cmd = new MySqlConnector.MySqlCommand(sql, connection)) cmd.ExecuteNonQuery();
            SqlStore.WithLock("CityBankers.RelationalUpgrade.v1", () =>
            {
                bool upgraded = SqlStore.ReadStatement(c => SqlStore.Meta(c.Connection, c.Transaction, "banker_relational_version") == "1");
                foreach (var e in new[] { Ledger, Stock })
                {
                    bool imported = SqlStore.ReadStatement(c => { using (var cmd = SqlStore.Command(c, "SELECT COUNT(*) FROM cd_banker_state WHERE state_name=@name", "@name", e.Name)) return Convert.ToInt32(cmd.ExecuteScalar()) != 0; });
                    if (upgraded)
                    {
                        if (!imported) throw new InvalidDataException("Relational state metadata is missing; refusing to restore stale source records.");
                        continue;
                    }
                    if (imported) throw new InvalidDataException("Unexpected unsealed relational state; refusing to overwrite it.");
                    bool occupied = SqlStore.ReadStatement(c => { using (var cmd = SqlStore.Command(c, "SELECT EXISTS(SELECT 1 FROM " + e.Table + ")")) return Convert.ToBoolean(cmd.ExecuteScalar()); });
                    if (occupied) throw new InvalidDataException("Unsealed relational item rows already exist; refusing to overwrite them.");
                    string text = SqlFile.ReadAllTextOrNull(SqlStore.LogicalPath(e.Legacy));
                    JObject source = text == null ? null : JObject.Parse(text);
                    Save(e, source);
                    if (!JToken.DeepEquals(Canonical(e, source), Canonical(e, Read(e))))
                        throw new InvalidDataException("Relational " + e.Name + " import verification failed; transaction rolled back.");
                }
                SqlStore.Execute(true, c => { SqlStore.SetMeta(c.Connection, c.Transaction, "banker_relational_version", "1"); SqlStore.SetMeta(c.Connection, c.Transaction, "schema_version", SqlSchema.Version.ToString(CultureInfo.InvariantCulture)); return 0; });
            });
        }
        private static DateTime Date(JToken value) => value == null || value.Type == JTokenType.Null ? DateTime.MinValue :
            value.Type == JTokenType.Date ? value.Value<DateTime>() : DateTime.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        private static JToken Canonical(Entity e, JObject state)
        {
            if (state == null) return JValue.CreateNull();
            var allowed = new HashSet<string>(new[] { "Format", "UpdatedUtc", "Items" });
            if (e == Stock) allowed.Add("BaselineRunId");
            if (state.Properties().Any(p => !allowed.Contains(p.Name)) || !(state["Items"] is JArray)) throw new InvalidDataException("Unsupported " + e.Name + " state fields.");
            var result = new JObject { ["Format"] = state["Format"], ["UpdatedUtc"] = Date(state["UpdatedUtc"]) };
            if (e == Stock) result["BaselineRunId"] = state["BaselineRunId"];
            var items = new JArray();
            foreach (var token in (JArray)state["Items"])
            {
                var item = token as JObject;
                if (item == null || item.Properties().Any(p => !e.Fields.Any(f => f.Property == p.Name))) throw new InvalidDataException("Unsupported " + e.Name + " item fields.");
                var row = new JObject();
                foreach (var f in e.Fields) row[f.Property] = item[f.Property] == null || item[f.Property].Type == JTokenType.Null ? JValue.CreateNull() :
                    f.Type == "DATE" ? new JValue(Date(item[f.Property])) : item[f.Property].DeepClone();
                items.Add(row);
            }
            result["Items"] = items; return result;
        }
        private static JObject Read(Entity e, string character = null)
        {
            return SqlStore.ReadStatement(c =>
            {
                JObject state = null; var items = new JArray();
                string columns = string.Join(",", e.Fields.SelectMany(Columns).Select(n => "i." + n));
                using (var cmd = SqlStore.Command(c, "SELECT s.present,s.format,s.baseline_run_id,s.updated_ticks,s.updated_kind,i.row_key," + columns +
                    " FROM cd_banker_state s LEFT JOIN " + e.Table + " i ON s.present=1" +
                    (character == null ? "" : " AND i.character_key=LOWER(@character)") + " WHERE s.state_name=@name ORDER BY i.ordinal", "@name", e.Name, "@character", (object)character ?? DBNull.Value))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (!r.GetBoolean(0)) return null;
                        if (state == null)
                        {
                            state = new JObject { ["Format"] = r.IsDBNull(1) ? null : r.GetString(1), ["UpdatedUtc"] = new DateTime(r.GetInt64(3), (DateTimeKind)r.GetInt32(4)), ["Items"] = items };
                            if (e == Stock) state["BaselineRunId"] = r.IsDBNull(2) ? null : r.GetString(2);
                        }
                        if (r.IsDBNull(5)) continue;
                        var item = new JObject(); int index = 6;
                        foreach (var f in e.Fields)
                        {
                            if (f.Type == "DATE")
                            {
                                item[f.Property] = r.IsDBNull(index + 1) ? JValue.CreateNull() : new JValue(new DateTime(r.GetInt64(index + 1), (DateTimeKind)r.GetInt32(index + 2)));
                                index += 3;
                            }
                            else { item[f.Property] = r.IsDBNull(index) ? JValue.CreateNull() : f.Type == "BOOL" ? new JValue(r.GetBoolean(index)) : JToken.FromObject(r.GetValue(index)); index++; }
                        }
                        items.Add(item);
                    }
                }
                return state;
            });
        }
        private static string Key(Entity e, JObject row)
        {
            if (e == Ledger)
            {
                string id = (string)row["Id"];
                if (string.IsNullOrWhiteSpace(id) || id.Length > 191) throw new InvalidDataException("Invalid ledger item ID.");
                return id;
            }
            return SqlStore.HashText(new JArray(((string)row["Character"])?.ToLowerInvariant(), ((string)row["BagSource"])?.ToLowerInvariant(), row["BagOuterSlot"], row["InnerSlot"]).ToString(Formatting.None));
        }
        private static void Save(Entity e, JObject input)
        {
            var state = Canonical(e, input) as JObject;
            SqlStore.Execute(true, c =>
            {
                var previous = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var cmd = SqlStore.Command(c, "SELECT row_key,row_hash FROM " + e.Table))
                using (var r = cmd.ExecuteReader()) while (r.Read()) previous.Add(r.GetString(0), r.GetString(1));
                var keys = new HashSet<string>(StringComparer.Ordinal); var changed = new List<object[]>(); int ordinal = 0;
                foreach (JObject row in state?["Items"] as JArray ?? new JArray())
                {
                    string key = Key(e, row); if (!keys.Add(key)) throw new InvalidDataException("Duplicate " + e.Name + " item identity.");
                    string hash = SqlStore.HashText(ordinal + "/" + row.ToString(Formatting.None));
                    string old; bool same = previous.TryGetValue(key, out old) && old == hash; previous.Remove(key);
                    var values = new List<object> { key, ordinal++, hash };
                    if (same) continue;
                    foreach (var f in e.Fields)
                    {
                        JToken token = row[f.Property]; bool empty = token == null || token.Type == JTokenType.Null;
                        if (f.Type == "DATE")
                        {
                            DateTime date = Date(token);
                            values.Add(empty || date.Year < 1000 ? (object)DBNull.Value : new DateTime(date.Ticks - date.Ticks % 10, DateTimeKind.Unspecified));
                            values.Add(empty ? (object)DBNull.Value : date.Ticks); values.Add(empty ? (object)DBNull.Value : (int)date.Kind);
                        }
                        else values.Add(empty ? DBNull.Value : ((JValue)token).Value);
                    }
                    changed.Add(values.ToArray());
                }
                string[] columns = new[] { "row_key", "ordinal", "row_hash" }.Concat(e.Fields.SelectMany(Columns)).ToArray();
                for (int offset = 0; offset < changed.Count; offset += 100)
                {
                    var batch = changed.Skip(offset).Take(100).ToArray();
                    using (var cmd = SqlStore.Command(c, ""))
                    {
                        var groups = new List<string>(); int parameter = 0;
                        foreach (var row in batch)
                        {
                            var names = new List<string>();
                            foreach (object value in row) { string name = "@v" + parameter++; names.Add(name); cmd.Parameters.AddWithValue(name, value); }
                            groups.Add("(" + string.Join(",", names) + ")");
                        }
                        cmd.CommandText = "INSERT INTO " + e.Table + " (" + string.Join(",", columns) + ") VALUES " + string.Join(",", groups) +
                            " ON DUPLICATE KEY UPDATE " + string.Join(",", columns.Skip(1).Select(n => n + "=VALUES(" + n + ")"));
                        cmd.ExecuteNonQuery();
                    }
                }
                var removed = previous.Keys.ToArray();
                for (int offset = 0; offset < removed.Length; offset += 100)
                {
                    using (var cmd = SqlStore.Command(c, ""))
                    {
                        var names = new List<string>();
                        foreach (string key in removed.Skip(offset).Take(100)) { string name = "@d" + names.Count; names.Add(name); cmd.Parameters.AddWithValue(name, key); }
                        cmd.CommandText = "DELETE FROM " + e.Table + " WHERE row_key IN (" + string.Join(",", names) + ")"; cmd.ExecuteNonQuery();
                    }
                }
                DateTime updated = Date(state?["UpdatedUtc"]);
                using (var cmd = SqlStore.Command(c, "INSERT INTO cd_banker_state(state_name,present,format,baseline_run_id,updated_ticks,updated_kind,revision) VALUES(@name,@present,@format,@baseline,@ticks,@kind,1) ON DUPLICATE KEY UPDATE present=VALUES(present),format=VALUES(format),baseline_run_id=VALUES(baseline_run_id),updated_ticks=VALUES(updated_ticks),updated_kind=VALUES(updated_kind),revision=revision+1",
                    "@name", e.Name, "@present", state != null, "@format", (object)(string)state?["Format"] ?? DBNull.Value, "@baseline", (object)(string)state?["BaselineRunId"] ?? DBNull.Value,
                    "@ticks", updated.Ticks, "@kind", (int)updated.Kind)) cmd.ExecuteNonQuery();
                return 0;
            });
        }
    }
}
