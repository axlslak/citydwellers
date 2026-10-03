using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using CityBankers.Shared;
using CityBankers;
using Newtonsoft.Json;
using CityDwellers.Shared;
using MySqlConnector;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Host
{
    // This instance lives only in the host's Manager service. Neither this source
    // nor the connector is included in client plugins.
    internal sealed class ManagerDatabase : IAccountingPersistence, IDisposable
    {
        private readonly MySqlConnection _connection;
        private readonly BusinessTables _tables = new BusinessTables();
        private readonly Action<Exception> _failed;
        private bool _disposed;
        private readonly string _root;

        internal ManagerDatabase(string runtimeRoot, Action<Exception> failed)
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only the Manager host owns SQL.");
            _root = runtimeRoot;
            _failed = failed ?? throw new ArgumentNullException(nameof(failed));
            var settings = JObject.Parse(File.ReadAllText(Path.Combine(runtimeRoot, "citydwellers.json")));
            var sql = settings.GetValue("MySql", StringComparison.OrdinalIgnoreCase) as JObject;
            if (sql == null) throw new InvalidOperationException("MySql settings are missing.");
            MySqlSslMode ssl;
            if (!Enum.TryParse((string)sql.GetValue("SslMode", StringComparison.OrdinalIgnoreCase) ?? "Required", true, out ssl) ||
                !Enum.IsDefined(typeof(MySqlSslMode), ssl)) throw new InvalidOperationException("Invalid MySql.SslMode.");
            var password = sql.GetValue("Password", StringComparison.OrdinalIgnoreCase);
            if (password == null || password.Type == JTokenType.Null) throw new InvalidOperationException("MySql.Password is required.");
            var builder = new MySqlConnectionStringBuilder
            {
                Server = Required(sql, "Host"), UserID = Required(sql, "User"), Database = Required(sql, "Database"),
                Password = (string)password, Port = (uint?)sql.GetValue("Port", StringComparison.OrdinalIgnoreCase) ?? 3306,
                SslMode = ssl, Pooling = false, ConnectionTimeout = 15, DefaultCommandTimeout = 60,
                AllowUserVariables = false, AllowLoadLocalInfile = false, DateTimeKind = MySqlDateTimeKind.Utc
            };
            if (builder.Port == 0 || builder.Port > 65535) throw new InvalidOperationException("Invalid MySql.Port.");
            _connection = new MySqlConnection(builder.ConnectionString);
            try { _connection.Open(); }
            catch { _connection.Dispose(); throw; }
        }
        private static string Required(JObject settings, string name)
        {
            string value = (string)settings.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("MySql." + name + " is required.");
            return value;
        }
        internal void Initialize()
        {
            using (var command = new MySqlCommand("CREATE TABLE IF NOT EXISTS cd_storage_version (version INT NOT NULL PRIMARY KEY) ENGINE=InnoDB", _connection)) command.ExecuteNonQuery();
            int version;
            using (var command = new MySqlCommand("SELECT COALESCE(MAX(version),0) FROM cd_storage_version", _connection)) version = Convert.ToInt32(command.ExecuteScalar());
            AccountingState state;
            if (version == 0)
            {
                using (var command = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name <> 'cd_storage_version'", _connection))
                    if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                        throw new InvalidOperationException("Database has tables but no current schema version. Legacy import is no longer supported. Use an empty database or restore a current-schema backup.");
                RuntimeLog.Write("Initializing empty business database (schema 6).");
                _tables.Create(_connection);
                state = new AccountingState
                {
                    Ledger = new ActiveLedgerState(), Stock = new CurrentStockState(),
                    Dispatch = new DispatchQueueState(), Withdrawals = new WithdrawalQueueState(),
                    LostItems = new LostItemsState()
                };
                _tables.Commit(_connection, new AccountingState { Reserve = null, Alts = null }, state, transaction =>
                {
                    using (var command = new MySqlCommand("INSERT INTO cd_storage_version(version) VALUES(6)", _connection, transaction)) command.ExecuteNonQuery();
                });
            }
            else
            {
                if (version == 5)
                {
                    RuntimeLog.Write("Upgrading business schema 5 to 6: adding learned buff tables.");
                    // MySQL DDL commits independently. IF NOT EXISTS makes interruption
                    // between the two creates safe to resume before advancing the version.
                    _tables.Create(_connection, "cd_learned_buffs", "cd_learned_buff_nanos");
                    using (var command = new MySqlCommand("INSERT INTO cd_storage_version(version) VALUES(6)", _connection)) command.ExecuteNonQuery();
                    version = 6;
                }
                if (version != 6) throw new InvalidOperationException("Unsupported business schema version " + version + ". Legacy migration is no longer supported; current schema is 6.");
                RuntimeLog.Write("Loading relational business rows into Manager RAM.");
                state = _tables.Load(_connection);
            }
            RuntimeLog.Write("Business rows loaded; loading settings.");
            state.BankTerminal = Configuration<BankTerminalState>("citybankers-bank-terminal.json") ?? new BankTerminalState();
            state.PhatzPolicy = Configuration<PhatzPolicyState>("citybankers-phatz-policy.json") ?? new PhatzPolicyState();
            state.PhatzPolicy.Items = state.PhatzPolicy.Items ?? new List<PhatzPolicyItem>();
            state.PhatzPolicy.DisabledAoIds = state.PhatzPolicy.DisabledAoIds ?? new List<int>();
            state.PhatzPolicy.KnownPairs = state.PhatzPolicy.KnownPairs ?? new List<ItemTemplatePair>();
            state.ItemPairs = Configuration<List<ItemTemplatePair>>("items-pairs.json") ?? new List<ItemTemplatePair>();
            if (state.ItemIndex == null)
            {
                var items = (state.Ledger?.Items ?? new List<ActiveLedgerItem>())
                    .Concat(state.ItemHistory.SelectMany(row => new[] { row.Item, row.CurrentItem })).Where(item => item != null);
                state.ItemIndex = new SymbiantIndexState { Items = items.GroupBy(item => item.AoId).Select(group =>
                {
                    var item = group.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Name)) ?? group.First();
                    return new SymbiantIndexItem { AoId = item.AoId, HighId = item.HighId, Ql = item.Ql, Family = item.Family, Name = item.Name };
                }).ToList() };
            }
            state.Alts = state.Alts ?? new AltState();
            state.Alts.Groups = state.Alts.Groups ?? new List<AltGroupState>();
            ManagerMemory.InitializeAccounting(state, this);
        }

        private T Configuration<T>(string name) where T : class
        {
            string path = Path.Combine(_root, "config", name);
            return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) : null;
        }
        public void Commit(AccountingState previous, AccountingState next)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ManagerDatabase));
            // Manager serializes business commits. No heartbeat, SQL reader loop,
            // advisory lock, second connection or automatic transaction replay.
            try
            {
                _tables.Commit(_connection, previous, next);
                if (!ReferenceEquals(previous.BankTerminal, next.BankTerminal))
                    DiskFiles.WriteAllText(Path.Combine(_root, "config", "citybankers-bank-terminal.json"), JsonConvert.SerializeObject(next.BankTerminal, Formatting.Indented));
                if (!ReferenceEquals(previous.PhatzPolicy, next.PhatzPolicy))
                    DiskFiles.WriteAllText(Path.Combine(_root, "config", "citybankers-phatz-policy.json"), JsonConvert.SerializeObject(next.PhatzPolicy, Formatting.Indented));
                if (!ReferenceEquals(previous.ItemPairs, next.ItemPairs))
                    DiskFiles.WriteAllText(Path.Combine(_root, "config", "items-pairs.json"), JsonConvert.SerializeObject(next.ItemPairs, Formatting.Indented));
            }
            catch (Exception error)
            {
                _failed(error);
                throw;
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}
