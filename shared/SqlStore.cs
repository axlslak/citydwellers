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
        private static bool _initialized;
        private static MySqlConnection _lease;
        private static Timer _heartbeat;
        private static int _heartbeatBusy;
        [ThreadStatic] private static DbContext _context;

        internal sealed class DbContext
        {
            internal MySqlConnection Connection;
            internal MySqlTransaction Transaction;
            internal bool WriterHeld, RollbackOnly;
            internal readonly List<Action> AfterCommit = new List<Action>();
        }

        public static string GetDataDirectory(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot)) throw new ArgumentException("Runtime root is required.", nameof(runtimeRoot));
            return System.IO.Path.Combine(System.IO.Path.GetFullPath(runtimeRoot), "data");
        }

        public static void Initialize(string runtimeRoot)
        {
            lock (InitializationSync)
            {
                string root = System.IO.Path.GetFullPath(runtimeRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                if (_initialized)
                {
                    if (!string.Equals(_runtimeRoot, root, StringComparison.OrdinalIgnoreCase))
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
                    string identity = HashText(database.ToLowerInvariant()).Substring(0, 40);
                    _leaseName = "citydwellers.host." + identity;
                    _writerName = "citydwellers.write." + identity;
                    using (var connection = NewConnection())
                    {
                        string version;
                        using (var command = new MySqlCommand("SELECT VERSION()", connection)) version = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                        // Server branding/version strings are not a capability check.
                        // MariaDB supports the JSON validation used by our writes;
                        // the existing schema and relational upgrade check the tables.
                        try
                        {
                            using (var command = new MySqlCommand("SELECT JSON_VALID('{\"citydwellers\":true}')=1 AND JSON_VALID('invalid')=0", connection))
                                if (!Convert.ToBoolean(command.ExecuteScalar(), CultureInfo.InvariantCulture))
                                    throw new InvalidOperationException("JSON_VALID returned an unexpected result.");
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException("Database server " + version +
                                " cannot provide the JSON_VALID function required by City Dwellers.", ex);
                        }
                        using (var packet = new MySqlCommand("SELECT @@max_allowed_packet", connection))
                            if (Convert.ToInt64(packet.ExecuteScalar()) < 32L * 1024 * 1024)
                                throw new InvalidOperationException("MySQL max_allowed_packet must be at least 33554432 (32 MiB) for bounded state JSON projections.");
                        CheckRuntimeReady(connection, null);
                    }
                    _initialized = true;
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
                return;
            }
            string root = Environment.GetEnvironmentVariable("CITYDWELLERS_RUNTIME_ROOT");
            Initialize(string.IsNullOrWhiteSpace(root) ? AppDomain.CurrentDomain.BaseDirectory : root);
        }

        private static void CheckRuntimeReady(MySqlConnection connection, MySqlTransaction transaction)
        {
            string version = Meta(connection, transaction, "schema_version");
            if (version != SqlSchema.Version.ToString(CultureInfo.InvariantCulture) &&
                !((version == "1" || version == "2") && !IsRuntimeActive))
                throw new InvalidOperationException("MySQL schema is absent or incompatible with this build.");
            if (version == SqlSchema.Version.ToString(CultureInfo.InvariantCulture)) return;
            // Older installations must have finished their original import. The
            // current schema has no archive dependency and never recreates one.
            string completed = Meta(connection, transaction, "completed_run_id");
            if (string.IsNullOrEmpty(completed)) throw new InvalidOperationException("The existing database has no completed import; refusing to discard an incomplete source archive.");
            using (var command = Command(connection, transaction, "SELECT COUNT(*) FROM cd_migration_runs WHERE run_id=@id AND completed_utc IS NOT NULL AND inventory_staged=1", "@id", completed))
                if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidDataException("MySQL migration seal is inconsistent.");
        }

        public static void StartRuntime()
        {
            EnsureInitialized();
            lock (InitializationSync)
            {
                if (_lease != null) return;
                TakeLease();
                try { CheckRuntimeReady(_lease, null); BankerSqlStore.Upgrade(); RestoreDiskCatalog(); }
                catch { _lease.Dispose(); _lease = null; throw; }
                Environment.SetEnvironmentVariable(RuntimeFlag, "1", EnvironmentVariableTarget.Process);
                StartHeartbeat();
                StartOperationalCleanup();
            }
        }

        // Disk is intentional for the static catalogue and disposable diagnostics.
        // Business state continues to require MySQL; there is no database fallback.
        internal static bool DiskPath(string path, out string physical)
        {
            physical = null;
            string key;
            if (!TryKey(path, out key)) return false;
            if (!DiskKey(key)) return false;
            physical = LogicalPath(key == "citydwellers.log" ? "citydweller.log" : key);
            return true;
        }

        private static bool DiskKey(string key)
        {
            return key == "items.json" || key.StartsWith("items.json.", StringComparison.Ordinal) ||
                key == "logs" || key.StartsWith("logs/", StringComparison.Ordinal) ||
                key == "diagnostic-dumps" || key.StartsWith("diagnostic-dumps/", StringComparison.Ordinal) ||
                key == "incident-dumps" || key.StartsWith("incident-dumps/", StringComparison.Ordinal) ||
                key == "navigationtraces" || key.StartsWith("navigationtraces/", StringComparison.Ordinal) ||
                key.IndexOf('/') < 0 && (key.EndsWith(".log", StringComparison.Ordinal) || key.Contains(".log."));
        }

        private static void RestoreDiskCatalog()
        {
            // Only the catalogue and its reusable index. Never overwrite an operator's file.
            foreach (string key in new[] { "items.json", "items.json.index-v1.bin" })
            {
                string destination = LogicalPath(key);
                if (System.IO.File.Exists(destination)) continue;
                bool exists = ReadStatement(context => { using (var command = Command(context,
                    "SELECT EXISTS(SELECT 1 FROM cd_documents WHERE path=@path)", "@path", key))
                    return Convert.ToBoolean(command.ExecuteScalar()); });
                if (!exists) continue;
                System.IO.Directory.CreateDirectory(_dataRoot);
                string temporary = destination + ".restoring";
                DateTime modified = DateTime.UtcNow;
                using (var connection = NewConnection())
                using (var command = Command(connection, null,
                    "SELECT c.content,d.modified_utc FROM cd_documents d JOIN cd_document_chunks c ON c.document_id=d.document_id WHERE d.path=@path ORDER BY c.chunk_no", "@path", key))
                using (var reader = command.ExecuteReader())
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // One streaming SELECT, not one network round trip per chunk.
                    while (reader.Read())
                    {
                        byte[] bytes = (byte[])reader.GetValue(0);
                        output.Write(bytes, 0, bytes.Length);
                        modified = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                    }
                    output.Flush(true);
                }
                System.IO.File.SetLastWriteTimeUtc(temporary, modified);
                System.IO.File.Move(temporary, destination);
                using (var connection = NewConnection()) SetMeta(connection, null, "disk_restored_" + key, "1");
            }
        }

        private static bool DiskCatalogRestored(string key)
        {
            if (!System.IO.File.Exists(LogicalPath(key))) return false;
            using (var connection = NewConnection()) return Meta(connection, null, "disk_restored_" + key) == "1";
        }

        private static Timer _cleanupTimer;
        private static int _cleanupRunning;
        private static void StartOperationalCleanup()
        {
            // Do not make AO startup wait for gigabytes of obsolete snapshots to be removed.
            _cleanupTimer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _cleanupRunning, 1) != 0) return;
                try { CleanupOperationalStorage(); }
                catch (Exception ex) { Console.Error.WriteLine("Operational cleanup incomplete: " + ex.GetType().Name + "; will retry."); }
                finally { Volatile.Write(ref _cleanupRunning, 0); }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
        }

        private static void CleanupOperationalStorage()
        {
            string generation = System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            // An explicit allowlist: no history, custody, lost/found, settings, or pending messages.
            string disposable = "(path LIKE 'startup-census/%' AND path NOT LIKE @current)" +
                " OR path LIKE 'diagnostic-dumps/%' OR path LIKE 'incident-dumps/%'" +
                " OR path LIKE 'navigationtraces/%' OR path LIKE 'logs/%'" +
                " OR path LIKE 'transaction-traces/%' OR path LIKE 'service-events/%'" +
                " OR path='citydwellers-events.jsonl'" +
                " OR (path NOT LIKE '%/%' AND (path LIKE '%.log' OR path LIKE '%.log.%'))" +
                " OR (path LIKE 'tell-queue/acknowledgements/%' AND modified_utc < @cutoff)";
            if (DiskCatalogRestored("items.json")) disposable += " OR path='items.json'";
            if (DiskCatalogRestored("items.json.index-v1.bin")) disposable += " OR path='items.json.index-v1.bin'";
            // The relational upgrade has already completed before this timer starts.
            // These are obsolete source copies, never the current item rows.
            disposable += " OR path='ledger.json' OR path='current-stock.json'";
            object[] parameters = { "@current", "startup-census/" + generation + "/%", "@cutoff", DateTime.UtcNow.AddDays(-1) };
            // Dedicated connection, short transactions, no global gameplay writer lock.
            using (var connection = NewConnection())
            {
                for (int batch = 0; batch < 5000; batch++)
                {
                    int removed;
                    using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted))
                    {
                        using (var command = Command(connection, transaction, "DELETE FROM cd_documents WHERE " + disposable + " LIMIT 20", parameters))
                            removed = command.ExecuteNonQuery();
                        transaction.Commit();
                    }
                    if (removed == 0) break;
                }
                // The completed import already produced the live records. Drop
                // its duplicate byte store outright; no copy, replay or rehash.
                // Child first for the legacy foreign keys. These tables have no
                // runtime readers in schema 3. IF EXISTS makes retries harmless.
                foreach (string table in new[] { "cd_migration_chunks", "cd_migration_files", "cd_migration_runs" })
                    using (var command = Command(connection, null, "DROP TABLE IF EXISTS " + table))
                        command.ExecuteNonQuery();
                using (var command = Command(connection, null,
                    "DELETE FROM cd_meta WHERE meta_key IN ('completed_run_id','cleanup_completed_run_id','source_preserved_run_id','active_run_id')"))
                    command.ExecuteNonQuery();
                using (var command = Command(connection, null,
                    "DELETE FROM cd_directories WHERE path LIKE 'startup-census/%' AND path NOT LIKE @current AND path<>@root",
                    "@current", "startup-census/" + generation + "/%", "@root", "startup-census/" + generation)) command.ExecuteNonQuery();
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
                        throw new InvalidOperationException("This MySQL database is already in use by a bot host. Stop that process first.");
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
                    FailClosed("MySQL heartbeat or exclusive process lease failed.", ex);
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

        internal static void AppendDiskText(string path, string text, Encoding encoding)
        {
            // Linked plugins have separate statics; use a process-wide disk-only lock.
            using (var mutex = new Mutex(false, "Local\\CityDwellers.DiskLogs." + System.Diagnostics.Process.GetCurrentProcess().Id))
            {
                try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.AppendAllText(path, text ?? "", encoding);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        /// <summary>Append-only evidence is outside gameplay transactions, including cross-AppDomain console calls.</summary>
        public static void AppendRuntimeLog(string path, string text)
        {
            string disk;
            if (DiskPath(path, out disk))
            {
                AppendDiskText(disk, text, new UTF8Encoding(false));
                return;
            }
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
        public static string DescribePath(string path) { string disk; return DiskPath(path, out disk) ? disk : "mysql:" + Key(path); }
        internal static string HashText(string value)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        internal static string Hex(byte[] hash) { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }

        internal static DbContext CurrentContext { get { return _context; } }
    }
}
