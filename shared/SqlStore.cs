using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MySqlConnector;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    /// <summary>Mandatory database boundary. All mutable state shares an atomic, cross-AppDomain writer lock.</summary>
    public static class SqlStore
    {
        internal const int ChunkSize = 256 * 1024;
        internal const int AppendChunkSize = 16 * 1024;
        internal const int ProjectionLimit = 16 * 1024 * 1024;
        private const string RuntimeFlag = "CITYDWELLERS_MYSQL_RUNTIME_ACTIVE";
        private static readonly object InitializationSync = new object();
        private static string _connectionString, _runtimeRoot, _dataRoot, _leaseName, _writerName;
        private static bool _initialized, _migration;
        private static MySqlConnection _lease;
        private static Timer _heartbeat;
        private static int _heartbeatBusy;
        private static Exception _leaseFailure;
        [ThreadStatic] private static DbContext _context;

        internal sealed class DbContext
        {
            internal MySqlConnection Connection;
            internal MySqlTransaction Transaction;
            internal bool WriterHeld, RollbackOnly;
            internal readonly List<Action> AfterCommit = new List<Action>();
        }

        public sealed class MigrationFile
        {
            public string Path { get; set; }
            public string OriginalPath { get; set; }
            public long Length { get; set; }
            public DateTime ModifiedUtc { get; set; }
            public string Sha256 { get; set; }
            public bool Archived { get; set; }
            public bool SourceDeleted { get; set; }
        }

        public static string GetDataDirectory(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot)) throw new ArgumentException("Runtime root is required.", nameof(runtimeRoot));
            return System.IO.Path.Combine(System.IO.Path.GetFullPath(runtimeRoot), "data");
        }

        public static void Initialize(string runtimeRoot, bool migration = false)
        {
            lock (InitializationSync)
            {
                string root = System.IO.Path.GetFullPath(runtimeRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                if (_initialized)
                {
                    if (!string.Equals(_runtimeRoot, root, StringComparison.OrdinalIgnoreCase) || migration != _migration)
                        throw new InvalidOperationException("A database boundary cannot change runtime root or mode after initialization.");
                    return;
                }
                try
                {
                    string bootstrap = System.IO.Path.Combine(root, "citydwellers.json");
                    var settings = JObject.Parse(System.IO.File.ReadAllText(bootstrap));
                    var sql = settings.GetValue("MySql", StringComparison.OrdinalIgnoreCase) as JObject;
                    if (sql == null) throw new InvalidOperationException("citydwellers.json requires a MySql section (Host, User, Password, Database, Port, SslMode).");
                    string host = Required(sql, "Host"), user = Required(sql, "User"), database = Required(sql, "Database");
                    JToken password = sql.GetValue("Password", StringComparison.OrdinalIgnoreCase);
                    if (password == null || password.Type == JTokenType.Null) throw new InvalidOperationException("MySql.Password must be specified.");
                    uint port = (uint?)sql.GetValue("Port", StringComparison.OrdinalIgnoreCase) ?? 3306;
                    MySqlSslMode sslMode;
                    string ssl = (string)sql.GetValue("SslMode", StringComparison.OrdinalIgnoreCase) ?? "Required";
                    if (!Enum.TryParse(ssl, true, out sslMode) || !Enum.IsDefined(typeof(MySqlSslMode), sslMode))
                        throw new InvalidOperationException("MySql.SslMode is invalid.");
                    if (port == 0 || port > 65535) throw new InvalidOperationException("MySql.Port must be between 1 and 65535.");
                    var builder = new MySqlConnectionStringBuilder
                    {
                        Server = host, Port = port, UserID = user, Password = (string)password,
                        Database = database, SslMode = sslMode, Pooling = true,
                        ConnectionTimeout = 15, DefaultCommandTimeout = 60,
                        MinimumPoolSize = 0, MaximumPoolSize = 32, ConnectionReset = true,
                        AllowUserVariables = false, AllowLoadLocalInfile = false,
                        DateTimeKind = MySqlDateTimeKind.Utc
                    };
                    _runtimeRoot = root; _dataRoot = GetDataDirectory(root); _connectionString = builder.ConnectionString;
                    _migration = migration;
                    string identity = HashText(database.ToLowerInvariant()).Substring(0, 40);
                    _leaseName = "citydwellers.host." + identity;
                    _writerName = "citydwellers.write." + identity;
                    using (var connection = NewConnection())
                    {
                        string version;
                        using (var command = new MySqlCommand("SELECT VERSION()", connection)) version = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                        Version parsed;
                        string numeric = new string(version.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                        if (version.IndexOf("MariaDB", StringComparison.OrdinalIgnoreCase) >= 0 || !Version.TryParse(numeric, out parsed) || parsed.Major < 8)
                            throw new InvalidOperationException("City Dwellers requires MySQL 8.0 or later; MariaDB is not supported by this schema.");
                        using (var packet = new MySqlCommand("SELECT @@max_allowed_packet", connection))
                            if (Convert.ToInt64(packet.ExecuteScalar()) < 32L * 1024 * 1024)
                                throw new InvalidOperationException("MySQL max_allowed_packet must be at least 33554432 (32 MiB) for bounded state JSON projections.");
                        if (migration)
                        {
                            TakeLease();
                            SqlSchema.Create(connection);
                            string existing = Meta(connection, null, "schema_version");
                            if (existing != null && existing != SqlSchema.Version.ToString(CultureInfo.InvariantCulture))
                                throw new InvalidOperationException("The database schema version is incompatible with this executable.");
                            SetMeta(connection, null, "schema_version", SqlSchema.Version.ToString(CultureInfo.InvariantCulture));
                        }
                        else CheckRuntimeReady(connection, null);
                    }
                    _initialized = true;
                    if (migration) StartHeartbeat();
                }
                catch (Exception ex)
                {
                    if (_lease != null) { _lease.Dispose(); _lease = null; }
                    if (IsRuntimeActive) FailClosed("MySQL initialization failed.", ex);
                    throw;
                }
            }
        }

        private static string Required(JObject settings, string key)
        {
            string value = (string)settings.GetValue(key, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("MySql." + key + " is required.");
            return value;
        }

        private static bool IsRuntimeActive { get { return Environment.GetEnvironmentVariable(RuntimeFlag) == "1"; } }

        private static void EnsureInitialized()
        {
            if (_initialized)
            {
                if (_leaseFailure != null) throw new DatabaseUnavailableException("The migration process lost its MySQL lease; rerun DataMigration.");
                return;
            }
            string root = Environment.GetEnvironmentVariable("CITYDWELLERS_RUNTIME_ROOT");
            Initialize(string.IsNullOrWhiteSpace(root) ? AppDomain.CurrentDomain.BaseDirectory : root);
        }

        private static void CheckRuntimeReady(MySqlConnection connection, MySqlTransaction transaction)
        {
            if (System.IO.Directory.Exists(_dataRoot) || System.IO.File.Exists(_dataRoot))
                throw new InvalidOperationException("The physical data folder still exists. Run DataMigration to verify the archive and remove the source before starting bots.");
            if (Meta(connection, transaction, "schema_version") != SqlSchema.Version.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("MySQL schema is absent or incompatible. Run DataMigration first.");
            string completed = Meta(connection, transaction, "completed_run_id");
            if (string.IsNullOrEmpty(completed)) throw new InvalidOperationException("MySQL migration has not completed. Run DataMigration first.");
            if (Meta(connection, transaction, "cleanup_completed_run_id") != completed)
                throw new InvalidOperationException("MySQL migration source cleanup is not complete. Rerun DataMigration before starting bots.");
            using (var command = Command(connection, transaction, "SELECT COUNT(*) FROM cd_migration_runs WHERE run_id=@id AND completed_utc IS NOT NULL AND inventory_staged=1", "@id", completed))
                if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidDataException("MySQL migration seal is inconsistent.");
        }

        public static void StartRuntime()
        {
            EnsureInitialized();
            lock (InitializationSync)
            {
                if (_migration) throw new InvalidOperationException("The migration utility cannot start bot runtime.");
                if (_lease != null) return;
                TakeLease();
                try { CheckRuntimeReady(_lease, null); }
                catch { _lease.Dispose(); _lease = null; throw; }
                Environment.SetEnvironmentVariable(RuntimeFlag, "1", EnvironmentVariableTarget.Process);
                StartHeartbeat();
            }
        }

        private static void TakeLease()
        {
            var builder = new MySqlConnectionStringBuilder(_connectionString) { Pooling = false };
            var connection = new MySqlConnection(builder.ConnectionString);
            try
            {
                connection.Open();
                using (var command = new MySqlCommand("SELECT GET_LOCK(@name,0)", connection))
                {
                    command.Parameters.AddWithValue("@name", _leaseName);
                    object result = command.ExecuteScalar();
                    if (result == null || result == DBNull.Value || Convert.ToInt32(result) != 1)
                        throw new InvalidOperationException("This MySQL database is already in use by a bot host or migration utility. Stop that process first.");
                }
                _lease = connection;
            }
            catch { connection.Dispose(); throw; }
        }

        private static void StartHeartbeat()
        {
            _heartbeat = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _heartbeatBusy, 1) != 0) return;
                try
                {
                    using (var command = new MySqlCommand("SELECT IS_USED_LOCK(@name)=CONNECTION_ID()", _lease))
                    {
                        command.CommandTimeout = 10;
                        command.Parameters.AddWithValue("@name", _leaseName);
                        object result = command.ExecuteScalar();
                        if (result == null || result == DBNull.Value || Convert.ToInt32(result) != 1)
                            throw new InvalidOperationException("The exclusive MySQL process lease was lost.");
                    }
                }
                catch (Exception ex)
                {
                    if (_migration) Interlocked.CompareExchange(ref _leaseFailure, ex, null);
                    else FailClosed("MySQL heartbeat or exclusive process lease failed.", ex);
                }
                finally { Volatile.Write(ref _heartbeatBusy, 0); }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        /// <summary>Never writes a crash dump or calls Console's SQL-backed tee.</summary>
        public static void FailClosed(string message, Exception exception = null)
        {
            try
            {
                using (var writer = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false), 1024, true))
                {
                    writer.WriteLine(DateTime.UtcNow.ToString("O") + " FATAL: " + message);
                    // Connection strings, passwords, SQL parameters, and data content are deliberately absent.
                    if (exception != null)
                    {
                        writer.WriteLine("Failure type: " + exception.GetType().FullName);
                        // These messages originate only from our fixed internal
                        // guards, never from connector SQL/parameters or secrets.
                        if (exception is DatabaseUnavailableException)
                            writer.WriteLine("Failure reason: " + exception.Message);
                        if (!string.IsNullOrEmpty(exception.StackTrace))
                            writer.WriteLine("Failure stack: " + exception.StackTrace);
                        var sql = exception as MySqlException;
                        if (sql != null) writer.WriteLine("MySQL error number: " + sql.Number + "; SQLSTATE: " + sql.SqlState);
                        var socket = exception.InnerException as System.Net.Sockets.SocketException;
                        if (socket != null) writer.WriteLine("Socket error: " + socket.SocketErrorCode);
                    }
                    writer.Flush();
                }
            }
            catch { }
            Environment.Exit(2);
            throw new InvalidOperationException(message, exception);
        }

        internal static void HandleDatabaseFailure(Exception exception)
        {
            bool connectorFailure = exception is InvalidOperationException &&
                (string.Equals(exception.Source, "MySqlConnector", StringComparison.Ordinal) ||
                 (exception.StackTrace ?? "").IndexOf("MySqlConnector.", StringComparison.Ordinal) >= 0);
            if (IsRuntimeActive && (exception is MySqlException || exception is DatabaseUnavailableException || exception is TimeoutException || connectorFailure))
                FailClosed("MySQL persistence failed; stopping all bots to prevent unrecorded state changes.", exception);
        }

        internal sealed class DatabaseUnavailableException : IOException
        {
            internal DatabaseUnavailableException(string message) : base(message) { }
        }

        public static void RequireAvailable()
        {
            Execute(false, context =>
            {
                using (var command = Command(context, "SELECT 1")) command.ExecuteScalar();
                return 0;
            });
        }

        internal static bool IsIndependentLog(string key)
        {
            return key == "citydwellers.log" || key.StartsWith("service-events/source-", StringComparison.Ordinal) && key.EndsWith(".jsonl", StringComparison.Ordinal);
        }

        internal static void RequireMutableDocument(string key)
        {
            if (IsRuntimeActive && IsIndependentLog(key))
                throw new InvalidOperationException("Runtime evidence is append-only: mysql:" + key);
        }

        /// <summary>Append-only evidence is outside gameplay transactions, including cross-AppDomain console calls.</summary>
        public static void AppendRuntimeLog(string path, string text)
        {
            string key = Key(path);
            if (!IsIndependentLog(key)) throw new ArgumentException("Independent log append requires citydwellers.log or service-events/source-*.jsonl.", nameof(path));
            try
            {
                using (var connection = NewConnection())
                using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted))
                {
                    var context = new DbContext { Connection = connection, Transaction = transaction };
                    SqlFile.Append(context, key, text ?? "", new UTF8Encoding(false));
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                if (IsRuntimeActive) FailClosed("MySQL runtime log append failed for mysql:" + key + ".", ex);
                throw;
            }
        }

        /// <summary>Utility diagnostics only. Runtime persistence should use SqlFile/WithLock.</summary>
        public static MySqlConnection OpenConnection()
        {
            EnsureInitialized();
            try { return NewConnection(); }
            catch (Exception ex) { HandleDatabaseFailure(ex); throw; }
        }

        private static MySqlConnection NewConnection()
        {
            var connection = new MySqlConnection(_connectionString);
            try { connection.Open(); return connection; }
            catch { connection.Dispose(); throw; }
        }

        public static void WithLock(string name, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            WithLock(name, () => { action(); return 0; });
        }

        public static T WithLock<T>(string name, Func<T> action)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A diagnostic scope name is required.", nameof(name));
            if (action == null) throw new ArgumentNullException(nameof(action));
            return Execute(true, context => action());
        }

        // Only for a single SELECT: its statement snapshot is already atomic.
        // Reuse an enclosing transaction so callers still see their own writes.
        internal static T ReadStatement<T>(Func<DbContext, T> action, bool independent = false)
        {
            EnsureInitialized();
            if (_context != null && !independent) return Execute(false, action);
            try
            {
                using (var connection = NewConnection())
                    return action(new DbContext { Connection = connection });
            }
            catch (Exception ex) { HandleDatabaseFailure(ex); throw; }
        }

        internal static T Execute<T>(bool write, Func<DbContext, T> action)
        {
            EnsureInitialized();
            if (_context != null)
            {
                try
                {
                    if (write && !_context.WriterHeld) AcquireWriter(_context);
                    return action(_context);
                }
                catch (Exception ex) { _context.RollbackOnly = true; HandleDatabaseFailure(ex); throw; }
            }
            DbContext context = null;
            try
            {
                context = new DbContext { Connection = NewConnection() };
                // One global writer avoids AB/BA lock ordering across stock, ledger, history, and IPC.
                if (write) AcquireWriter(context);
                // The writer lease serializes mutations. READ COMMITTED avoids gap locks that could
                // block an independent console append while this transaction synchronously logs.
                context.Transaction = context.Connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
                _context = context;
                T result = action(context);
                if (_leaseFailure != null) throw new DatabaseUnavailableException("The migration process lost its MySQL lease before commit.");
                if (context.RollbackOnly) throw new DatabaseUnavailableException("The persistence transaction was marked rollback-only by a failed nested operation.");
                context.Transaction.Commit();
                foreach (Action committed in context.AfterCommit) committed();
                return result;
            }
            catch (Exception ex)
            {
                if (context != null && context.Transaction != null)
                    try { context.Transaction.Rollback(); } catch (Exception rollbackError) { HandleDatabaseFailure(rollbackError); }
                HandleDatabaseFailure(ex);
                throw;
            }
            finally
            {
                _context = null;
                if (context != null)
                {
                    if (context.Transaction != null) context.Transaction.Dispose();
                    if (context.WriterHeld)
                        try
                        {
                            using (var release = new MySqlCommand("SELECT RELEASE_LOCK(@name)", context.Connection))
                            { release.Parameters.AddWithValue("@name", _writerName); release.ExecuteScalar(); }
                        }
                        catch (Exception ex) { HandleDatabaseFailure(ex); }
                    context.Connection.Dispose();
                }
            }
        }

        private static void AcquireWriter(DbContext context)
        {
            using (var command = Command(context, "SELECT GET_LOCK(@name,30)", "@name", _writerName))
            {
                object result = command.ExecuteScalar();
                if (result == null || result == DBNull.Value || Convert.ToInt32(result) != 1)
                    throw new DatabaseUnavailableException("Timed out acquiring the MySQL persistence writer lock.");
            }
            context.WriterHeld = true;
        }

        internal static MySqlCommand Command(DbContext context, string sql, params object[] pairs)
        {
            return Command(context.Connection, context.Transaction, sql, pairs);
        }

        internal static MySqlCommand Command(MySqlConnection connection, MySqlTransaction transaction, string sql, params object[] pairs)
        {
            var command = new MySqlCommand(sql, connection, transaction);
            for (int i = 0; i < pairs.Length; i += 2)
                command.Parameters.AddWithValue((string)pairs[i], pairs[i + 1] ?? DBNull.Value);
            return command;
        }

        internal static string Meta(MySqlConnection connection, MySqlTransaction transaction, string key)
        {
            using (var command = Command(connection, transaction, "SELECT meta_value FROM cd_meta WHERE meta_key=@key", "@key", key))
            {
                object result = command.ExecuteScalar();
                return result == null || result == DBNull.Value ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
            }
        }

        internal static void SetMeta(MySqlConnection connection, MySqlTransaction transaction, string key, string value)
        {
            using (var command = Command(connection, transaction,
                "INSERT INTO cd_meta(meta_key,meta_value,updated_utc) VALUES(@key,@value,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE meta_value=VALUES(meta_value),updated_utc=VALUES(updated_utc)",
                "@key", key, "@value", value)) command.ExecuteNonQuery();
        }

        internal static string Key(string path)
        {
            string key;
            if (!TryKey(path, out key)) throw new UnauthorizedAccessException("Mutable bot data must use the logical MySQL data namespace.");
            return key;
        }

        internal static bool TryKey(string path, out string key)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A data path is required.", nameof(path));
            string replaced = path.Replace('\\', '/');
            if (replaced.Split('/').Any(p => p == "." || p == "..")) throw new UnauthorizedAccessException("Data paths cannot contain traversal segments.");
            string relative;
            if (System.IO.Path.IsPathRooted(path))
            {
                string full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                if (string.Equals(full, _dataRoot, StringComparison.OrdinalIgnoreCase)) relative = "";
                else if (full.StartsWith(_dataRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) relative = full.Substring(_dataRoot.Length + 1);
                else
                {
                    if (replaced.Split('/').Any(p => p.Equals("data", StringComparison.OrdinalIgnoreCase)))
                        throw new UnauthorizedAccessException("A physical data path outside the bound runtime cannot be read or written.");
                    key = null; return false;
                }
            }
            else relative = path;
            key = relative.Replace('\\', '/').Trim('/').ToLowerInvariant();
            if (key.Length > 640 || key.Any(c => char.IsControl(c) || c == ':') || key.Split('/').Any(p => p.Length == 0) && key.Length != 0)
                throw new ArgumentException("The data key is invalid or exceeds 640 characters.", nameof(path));
            return true;
        }

        internal static string LogicalPath(string key) { EnsureInitialized(); return System.IO.Path.Combine(_dataRoot, key.Replace('/', System.IO.Path.DirectorySeparatorChar)); }
        public static string DescribePath(string path) { return "mysql:" + Key(path); }
        internal static string OriginalRelativePath(string path)
        {
            Key(path);
            return (System.IO.Path.IsPathRooted(path) ? System.IO.Path.GetFullPath(path).Substring(_dataRoot.Length).TrimStart('\\', '/') : path).Replace('\\', '/');
        }

        internal static string HashText(string value)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        internal static string Hex(byte[] hash) { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }

        public static string ComputeDocumentSha256(string path)
        {
            using (Stream stream = SqlFile.OpenRead(path))
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream));
        }

        private static void RequireMigration() { EnsureInitialized(); if (!_migration) throw new InvalidOperationException("This operation is only available to the offline migration utility."); }

        public static string BeginMigration()
        {
            RequireMigration();
            return Execute(true, context =>
            {
                string completed = Meta(context.Connection, context.Transaction, "completed_run_id");
                if (!string.IsNullOrEmpty(completed)) return completed;
                string active = Meta(context.Connection, context.Transaction, "active_run_id");
                if (!string.IsNullOrEmpty(active)) return active;
                string id = Guid.NewGuid().ToString("N");
                using (var command = Command(context, "INSERT INTO cd_migration_runs(run_id,started_utc) VALUES(@id,UTC_TIMESTAMP(6))", "@id", id)) command.ExecuteNonQuery();
                SetMeta(context.Connection, context.Transaction, "active_run_id", id);
                return id;
            });
        }

        public static bool GetMigrationCompleted() { return !string.IsNullOrEmpty(GetCompletedMigrationRunId()); }
        public static string GetCompletedMigrationRunId()
        {
            return Execute(false, context => Meta(context.Connection, context.Transaction, "completed_run_id"));
        }

        private static void RequireUnsealed(DbContext context, string runId)
        {
            if (!string.IsNullOrEmpty(Meta(context.Connection, context.Transaction, "completed_run_id")))
                throw new InvalidOperationException("The migration is already sealed; live state and original archives cannot be imported again.");
            using (var command = Command(context, "SELECT COUNT(*) FROM cd_migration_runs WHERE run_id=@id AND completed_utc IS NULL", "@id", runId))
                if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidOperationException("The migration run does not exist or is sealed.");
        }

        public static void BindMigrationSource(string runId, string canonicalFullSourceRoot)
        {
            RequireMigration();
            string root = System.IO.Path.GetFullPath(canonicalFullSourceRoot).TrimEnd('\\', '/');
            Execute(true, context =>
            {
                using (var select = Command(context, "SELECT source_root FROM cd_migration_runs WHERE run_id=@id", "@id", runId))
                {
                    object existing = select.ExecuteScalar();
                    if (existing == null) throw new InvalidOperationException("Migration run is missing.");
                    if (existing != DBNull.Value)
                    {
                        if (!string.Equals((string)existing, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The migration is bound to a different physical source root.");
                        return 0;
                    }
                }
                RequireUnsealed(context, runId);
                using (var command = Command(context, "UPDATE cd_migration_runs SET source_root=@root WHERE run_id=@id", "@id", runId, "@root", root)) command.ExecuteNonQuery();
                return 0;
            });
        }

        public static string GetMigrationSourceRoot(string runId)
        {
            return Execute(false, context => { using (var command = Command(context, "SELECT source_root FROM cd_migration_runs WHERE run_id=@id", "@id", runId)) { object value = command.ExecuteScalar(); return value == null || value == DBNull.Value ? null : (string)value; } });
        }

        public static bool HasMigrationInventory(string runId)
        {
            return Execute(false, context => { using (var command = Command(context, "SELECT inventory_staged FROM cd_migration_runs WHERE run_id=@id", "@id", runId)) return Convert.ToBoolean(command.ExecuteScalar()); });
        }

        public static void StageMigrationInventory(string runId, IEnumerable<MigrationFile> files)
        {
            RequireMigration();
            if (files == null) throw new ArgumentNullException(nameof(files));
            var inventory = files.ToList();
            Execute(true, context =>
            {
                RequireUnsealed(context, runId);
                if (HasMigrationInventory(runId)) throw new InvalidOperationException("The immutable migration inventory is already staged.");
                if (string.IsNullOrEmpty(GetMigrationSourceRoot(runId))) throw new InvalidOperationException("Bind the physical source root before staging inventory.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in inventory)
                {
                    string key = Key(file.Path ?? file.OriginalPath);
                    if (key.Length == 0 || !seen.Add(key) || file.Length < 0 || file.Sha256 == null || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)))
                        throw new InvalidDataException("Migration inventory contains invalid metadata or colliding case-insensitive paths.");
                    string original = file.OriginalPath ?? OriginalRelativePath(file.Path);
                    if (Key(original) != key) throw new InvalidDataException("Original migration path does not match its canonical key.");
                    using (var command = Command(context, "INSERT INTO cd_migration_files(run_id,path,original_path,byte_length,modified_utc,sha256) VALUES(@run,@path,@original,@length,@modified,@hash)",
                        "@run", runId, "@path", key, "@original", original, "@length", file.Length, "@modified", file.ModifiedUtc.ToUniversalTime(), "@hash", file.Sha256.ToLowerInvariant())) command.ExecuteNonQuery();
                }
                using (var command = Command(context, "UPDATE cd_migration_runs SET inventory_staged=1,last_error=NULL WHERE run_id=@id", "@id", runId)) command.ExecuteNonQuery();
                return 0;
            });
        }

        public static List<MigrationFile> GetMigrationInventory(string runId) { return GetMigrationFiles(runId); }
        public static List<MigrationFile> GetMigrationFiles(string runId)
        {
            return Execute(false, context =>
            {
                var files = new List<MigrationFile>();
                using (var command = Command(context, "SELECT path,original_path,byte_length,modified_utc,sha256,archived,source_deleted FROM cd_migration_files WHERE run_id=@id ORDER BY path", "@id", runId))
                using (var reader = command.ExecuteReader()) while (reader.Read()) files.Add(new MigrationFile {
                    Path = reader.GetString(0), OriginalPath = reader.GetString(1), Length = reader.GetInt64(2), ModifiedUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                    Sha256 = reader.GetString(4), Archived = reader.GetBoolean(5), SourceDeleted = reader.GetBoolean(6) });
                return files;
            });
        }

        public static MigrationFile ImportMigrationFile(string runId, string path, Stream source, DateTime modifiedUtc)
        {
            RequireMigration();
            if (source == null || !source.CanRead) throw new ArgumentException("A readable migration source stream is required.", nameof(source));
            string key = Key(path);
            return Execute(true, context =>
            {
                RequireUnsealed(context, runId);
                long archiveId; MigrationFile expected;
                using (var command = Command(context, "SELECT migration_file_id,original_path,byte_length,modified_utc,sha256,archived FROM cd_migration_files WHERE run_id=@run AND path=@path", "@run", runId, "@path", key))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidDataException("The source file is not in the immutable staged migration inventory: " + key);
                    archiveId = reader.GetInt64(0);
                    expected = new MigrationFile { Path = key, OriginalPath = reader.GetString(1), Length = reader.GetInt64(2), ModifiedUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc), Sha256 = reader.GetString(4), Archived = reader.GetBoolean(5) };
                }
                if (expected.Archived)
                {
                    using (var hash = SHA256.Create())
                    {
                        long count = 0; var buffer = new byte[ChunkSize]; int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) != 0) { hash.TransformBlock(buffer, 0, read, null, 0); count += read; }
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        if (count != expected.Length || Hex(hash.Hash) != expected.Sha256) throw new InvalidDataException("Source content changed since the immutable inventory was staged: " + key);
                    }
                    return expected;
                }
                long document = SqlFile.CreateEmpty(context, key, expected.ModifiedUtc, true);
                var bytes = new byte[ChunkSize]; long length = 0, chunk = 0;
                using (var hash = SHA256.Create())
                using (var projection = new SqlFile.ProjectionWriter(context, document, key))
                {
                    int read;
                    while ((read = source.Read(bytes, 0, bytes.Length)) != 0)
                    {
                        hash.TransformBlock(bytes, 0, read, null, 0);
                        byte[] part = read == bytes.Length ? bytes : bytes.Take(read).ToArray();
                        SqlFile.InsertChunk(context, document, chunk, length, part);
                        using (var command = Command(context, "INSERT INTO cd_migration_chunks(migration_file_id,chunk_no,byte_offset,content) VALUES(@id,@chunk,@offset,@bytes)",
                            "@id", archiveId, "@chunk", chunk, "@offset", length, "@bytes", part)) command.ExecuteNonQuery();
                        projection.Feed(part, 0, read);
                        length += read; chunk++;
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    if (length != expected.Length || Hex(hash.Hash) != expected.Sha256) throw new InvalidDataException("Source content changed since the immutable inventory was staged: " + key);
                    projection.Complete(length, chunk, expected.ModifiedUtc);
                }
                using (var command = Command(context, "UPDATE cd_migration_files SET archived=1 WHERE migration_file_id=@id", "@id", archiveId)) command.ExecuteNonQuery();
                expected.Archived = true;
                return expected;
            });
        }

        public static string ComputeMigrationFileSha256(string runId, string path)
        {
            RequireMigration(); string key = Key(path);
            return Execute(false, context =>
            {
                long id;
                using (var command = Command(context, "SELECT migration_file_id FROM cd_migration_files WHERE run_id=@run AND path=@path AND archived=1", "@run", runId, "@path", key))
                {
                    object value = command.ExecuteScalar(); if (value == null) throw new FileNotFoundException("The source archive is not complete: " + key); id = Convert.ToInt64(value);
                }
                using (var hash = SHA256.Create())
                using (var command = Command(context, "SELECT content FROM cd_migration_chunks WHERE migration_file_id=@id ORDER BY chunk_no", "@id", id))
                using (var reader = command.ExecuteReader(System.Data.CommandBehavior.SequentialAccess))
                {
                    var buffer = new byte[ChunkSize];
                    while (reader.Read())
                    {
                        long offset = 0; long count;
                        while ((count = reader.GetBytes(0, offset, buffer, 0, buffer.Length)) > 0) { hash.TransformBlock(buffer, 0, (int)count, null, 0); offset += count; }
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0); return Hex(hash.Hash);
                }
            });
        }

        public static void CompleteMigration(string runId)
        {
            RequireMigration();
            Execute(true, context =>
            {
                string complete = Meta(context.Connection, context.Transaction, "completed_run_id");
                if (complete == runId) return 0;
                RequireUnsealed(context, runId);
                if (!HasMigrationInventory(runId)) throw new InvalidOperationException("The source inventory was never staged.");
                using (var command = Command(context, "SELECT COUNT(*) FROM cd_migration_files WHERE run_id=@run AND archived=0", "@run", runId))
                    if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidOperationException("Migration cannot seal while source files remain unarchived.");
                using (var command = Command(context, "UPDATE cd_migration_runs SET completed_utc=UTC_TIMESTAMP(6),last_error=NULL WHERE run_id=@run", "@run", runId)) command.ExecuteNonQuery();
                SetMeta(context.Connection, context.Transaction, "completed_run_id", runId);
                return 0;
            });
        }

        public static void MarkMigrationSourceDeleted(string runId, string path)
        {
            RequireMigration(); string key = Key(path);
            Execute(true, context =>
            {
                if (Meta(context.Connection, context.Transaction, "completed_run_id") != runId) throw new InvalidOperationException("Source deletion must follow a sealed migration.");
                using (var command = Command(context, "UPDATE cd_migration_files SET source_deleted=1 WHERE run_id=@run AND path=@path AND archived=1", "@run", runId, "@path", key))
                    if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Cannot mark an absent or unarchived source as deleted.");
                return 0;
            });
        }

        public static void MarkMigrationCleanupComplete(string runId)
        {
            RequireMigration();
            Execute(true, context =>
            {
                if (Meta(context.Connection, context.Transaction, "completed_run_id") != runId) throw new InvalidOperationException("Source cleanup must follow a sealed migration.");
                using (var command = Command(context, "SELECT COUNT(*) FROM cd_migration_files WHERE run_id=@run AND (archived=0 OR source_deleted=0)", "@run", runId))
                    if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidOperationException("Migration source cleanup still has undeleted files.");
                string source = GetMigrationSourceRoot(runId);
                if (string.IsNullOrEmpty(source) || System.IO.Directory.Exists(source) || System.IO.File.Exists(source)) throw new InvalidOperationException("The physical migration source directory still exists or is unknown.");
                SetMeta(context.Connection, context.Transaction, "cleanup_completed_run_id", runId);
                using (var command = Command(context, "UPDATE cd_migration_runs SET last_error=NULL WHERE run_id=@run", "@run", runId)) command.ExecuteNonQuery();
                return 0;
            });
        }

        public static void AbortMigration(string runId, string error)
        {
            RequireMigration();
            Execute(true, context => { using (var command = Command(context, "UPDATE cd_migration_runs SET last_error=@error WHERE run_id=@run", "@run", runId, "@error", error == null ? null : error.Substring(0, Math.Min(error.Length, 16000)))) command.ExecuteNonQuery(); return 0; });
        }

        internal static DbContext CurrentContext { get { return _context; } }
    }
}
