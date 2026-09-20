using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CityDwellers.Shared;
using MySqlConnector;

namespace CityDwellers.DataMigration
{
    internal static class Program
    {
        private static int _cancelRequested;

        private static int Main(string[] args)
        {
            string runId = null;
            bool sealedRun = false;
            try
            {
                Options options = Options.Parse(args);
                if (options.Help)
                {
                    PrintUsage();
                    return 0;
                }

                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Interlocked.Exchange(ref _cancelRequested, 1);
                    Console.Error.WriteLine("Cancellation requested. Finishing the current atomic file operation.");
                };

                string source = Path.Combine(options.RuntimeRoot, "data");
                Console.WriteLine("City Dwellers offline MySQL migration");
                Console.WriteLine("Runtime: " + options.RuntimeRoot);
                Console.WriteLine("Source:  " + source);
                Console.WriteLine("All bots must be stopped. Acquiring the exclusive SQL lease...");
                SqlStore.Initialize(options.RuntimeRoot, migration: true);

                if (options.Command == "schema")
                {
                    PrintSchema();
                    return 0;
                }

                sealedRun = SqlStore.GetMigrationCompleted();
                if (options.Command == "verify" && !sealedRun)
                    throw new InvalidOperationException("Migration has not been sealed. Run DataMigration.exe to finish importing first.");

                runId = SqlStore.BeginMigration();
                if (options.Command != "verify")
                {
                    string previousSource = SqlStore.GetMigrationSourceRoot(runId);
                    StringComparison sourceComparison = Environment.OSVersion.Platform == PlatformID.Win32NT
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (previousSource != null && !string.Equals(previousSource,
                        Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), sourceComparison))
                        throw new IOException("This migration is bound to another original source: " + previousSource);
                    SqlStore.BindMigrationSource(runId, Path.GetFullPath(source));
                }
                Console.WriteLine("Migration: " + runId + (sealedRun ? " (already sealed)" : " (not sealed)"));

                if (sealedRun)
                {
                    List<SqlStore.MigrationFile> archived = SqlStore.GetMigrationFiles(runId);
                    VerifyArchives(runId, archived, verifyLiveDocuments: false);
                    if (options.Command == "verify")
                    {
                        Console.WriteLine("VERIFIED: immutable migration archive. Live SQL documents were not replayed or compared to old data.");
                        return 0;
                    }
                    Console.WriteLine("Sealed migration: verifying/finishing source cleanup only; live SQL documents will not be overwritten.");
                    CleanupSource(runId, source, archived);
                    PrintSuccess(archived);
                    return 0;
                }

                if (!SqlStore.HasMigrationInventory(runId))
                {
                    List<string> directories;
                    List<SqlStore.MigrationFile> snapshot = CaptureInventory(source, options.Empty, out directories);
                    CheckCancellation();
                    foreach (string directory in directories)
                        SqlDirectory.CreateDirectory(directory);
                    SqlStore.StageMigrationInventory(runId, snapshot);
                    Console.WriteLine("Staged immutable source inventory: " + snapshot.Count + " files, " +
                        snapshot.Sum(file => file.Length).ToString(CultureInfo.InvariantCulture) + " bytes.");
                }

                List<SqlStore.MigrationFile> manifest = SqlStore.GetMigrationFiles(runId);
                ValidateSourceInventory(source, manifest, allowMissing: false, compareContents: false);

                int imported = 0;
                foreach (SqlStore.MigrationFile file in manifest.OrderBy(file => file.Path, StringComparer.Ordinal))
                {
                    CheckCancellation();
                    string physical = SourcePath(source, file.OriginalPath);
                    if (file.Archived)
                    {
                        Console.WriteLine("RESUME " + (++imported) + "/" + manifest.Count + " " + file.OriginalPath);
                        continue;
                    }

                    using (FileStream stream = VerifiedSource.OpenRead(physical, source))
                    {
                        if (stream.Length != file.Length)
                            throw new IOException("Source length changed since inventory: " + physical);
                        SqlStore.MigrationFile result = SqlStore.ImportMigrationFile(runId, physical, stream, file.ModifiedUtc);
                        if (result.Length != file.Length || !SameHash(result.Sha256, file.Sha256))
                            throw new IOException("Imported bytes differ from the staged source inventory: " + physical);
                    }
                    Console.WriteLine("IMPORTED " + (++imported) + "/" + manifest.Count + " " + file.OriginalPath +
                        " bytes=" + file.Length + " sha256=" + file.Sha256);
                }

                manifest = SqlStore.GetMigrationFiles(runId);
                VerifyArchives(runId, manifest, verifyLiveDocuments: true);
                ValidateSourceInventory(source, manifest, allowMissing: false, compareContents: true);
                CheckCancellation();
                SqlStore.CompleteMigration(runId);
                sealedRun = true;
                Console.WriteLine("SEALED: complete source inventory and SQL readback verified. Beginning verified source cleanup.");
                CleanupSource(runId, source, manifest);
                PrintSuccess(manifest);
                return 0;
            }
            catch (Exception ex)
            {
                string error = ex.GetType().Name + ": " + ex.Message;
                Console.Error.WriteLine("FAILED: " + error);
                if (runId != null)
                {
                    try { SqlStore.AbortMigration(runId, error); }
                    catch (Exception recordError)
                    {
                        Console.Error.WriteLine("SQL could not record this failure: " + recordError.GetType().Name + ": " + recordError.Message);
                    }
                }
                Console.Error.WriteLine(sealedRun
                    ? "SQL migration remains sealed. Fix the reported cleanup/verification issue and rerun; archived data is never replayed into live records."
                    : "Migration is not complete; bot startup stays blocked. No source files were deleted before sealing. Fix the reported issue and rerun the same command.");
                return ex is OperationCanceledException ? 130 : 1;
            }
        }

        private static List<SqlStore.MigrationFile> CaptureInventory(string source, bool allowEmpty, out List<string> directories)
        {
            var result = new List<SqlStore.MigrationFile>();
            List<string> files = EnumerateSource(source, out directories);
            if (files.Count == 0 && !allowEmpty)
                throw new IOException("The source contains no files. Check --root; use --empty only for an intentionally empty/new installation.");

            foreach (string path in files)
            {
                CheckCancellation();
                using (FileStream stream = VerifiedSource.OpenRead(path, source))
                {
                    string relative = RelativePath(source, path);
                    var file = new SqlStore.MigrationFile
                    {
                        Path = relative.ToLowerInvariant(),
                        OriginalPath = relative,
                        Length = stream.Length,
                        ModifiedUtc = File.GetLastWriteTimeUtc(path),
                        Sha256 = VerifiedSource.Sha256(stream),
                        Archived = false,
                        SourceDeleted = false
                    };
                    result.Add(file);
                    Console.WriteLine("INVENTORY " + relative + " bytes=" + file.Length + " sha256=" + file.Sha256);
                }
            }
            return result;
        }

        private static List<string> EnumerateSource(string source, out List<string> directories)
        {
            directories = new List<string>();
            var files = new List<string>();
            if (!Directory.Exists(source))
            {
                if (File.Exists(source)) throw new IOException("The data source is a file, not a directory: " + source);
                // File.GetAttributes distinguishes absent from access denied;
                // Directory.Exists alone would hide a permissions failure.
                try { File.GetAttributes(source); }
                catch (FileNotFoundException) { return files; }
                catch (DirectoryNotFoundException) { return files; }
                throw new IOException("Cannot enumerate data source: " + source);
            }

            VerifiedSource.RejectReparsePath(source, source);
            var pending = new Stack<string>();
            pending.Push(source);
            var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count != 0)
            {
                CheckCancellation();
                string directory = pending.Pop();
                VerifiedSource.RejectReparsePath(directory, source);
                directories.Add(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (Path.DirectorySeparatorChar != '\\' && Path.GetFileName(entry).IndexOf('\\') >= 0)
                        throw new IOException("Source filename contains a literal backslash incompatible with SQL logical paths: " + entry);
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Source contains a symbolic link/junction; nothing may be silently skipped: " + entry);
                    if (!canonicalPaths.Add(RelativePath(source, entry)))
                        throw new IOException("Source names collide after Windows/SQL path normalization: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else files.Add(entry);
                }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static void ValidateSourceInventory(string source, IList<SqlStore.MigrationFile> manifest,
            bool allowMissing, bool compareContents)
        {
            List<string> directories;
            List<string> current = EnumerateSource(source, out directories);
            var expected = manifest.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string physical in current)
            {
                CheckCancellation();
                string relative = RelativePath(source, physical);
                SqlStore.MigrationFile file;
                if (!expected.TryGetValue(relative, out file))
                    throw new IOException("New/uninventoried source file retained: " + physical + ". The original inventory cannot be silently expanded.");
                if (file.SourceDeleted)
                    throw new IOException("A new file appeared at a source path already cleaned; retained: " + physical);
                found.Add(file.Path);
                if (compareContents)
                {
                    using (FileStream stream = VerifiedSource.OpenRead(physical, source))
                    {
                        if (stream.Length != file.Length || !SameHash(VerifiedSource.Sha256(stream), file.Sha256))
                            throw new IOException("Source contents changed since inventory; retained: " + physical);
                    }
                }
            }
            if (!allowMissing)
            {
                SqlStore.MigrationFile missing = manifest.FirstOrDefault(file => !found.Contains(file.Path));
                if (missing != null)
                    throw new IOException("Inventoried source disappeared before migration was sealed: " + missing.OriginalPath);
                // Preserve empty directory metadata across an interrupted first
                // inventory and ensure final pre-seal directories are included.
                foreach (string directory in directories)
                    SqlDirectory.CreateDirectory(directory);
            }
        }

        private static void VerifyArchives(string runId, IList<SqlStore.MigrationFile> files, bool verifyLiveDocuments)
        {
            int verified = 0;
            foreach (SqlStore.MigrationFile file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                CheckCancellation();
                if (!file.Archived)
                    throw new InvalidOperationException("Source is inventoried but not archived: " + file.OriginalPath);
                if (!SameHash(SqlStore.ComputeMigrationFileSha256(runId, file.Path), file.Sha256))
                    throw new IOException("SQL immutable archive SHA-256 mismatch: " + file.OriginalPath);
                if (verifyLiveDocuments && !SameHash(SqlStore.ComputeDocumentSha256(file.Path), file.Sha256))
                    throw new IOException("SQL live document SHA-256 mismatch before sealing: " + file.OriginalPath);
                Console.WriteLine("VERIFIED " + (++verified) + "/" + files.Count + " " + file.OriginalPath +
                    " bytes=" + file.Length + " sha256=" + file.Sha256);
            }
        }

        private static void CleanupSource(string runId, string source, IList<SqlStore.MigrationFile> files)
        {
            // Reject unexpected or modified survivors before deleting any more
            // files in this invocation. A sealed run never imports survivors.
            ValidateSourceInventory(source, files, allowMissing: true, compareContents: true);
            foreach (SqlStore.MigrationFile file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                CheckCancellation();
                string physical = SourcePath(source, file.OriginalPath);
                if (!File.Exists(physical))
                {
                    if (Directory.Exists(physical)) throw new IOException("Source file path became a directory: " + physical);
                    // Confirm absence without suppressing access-denied errors.
                    try { File.GetAttributes(physical); }
                    catch (FileNotFoundException) { SqlStore.MarkMigrationSourceDeleted(runId, file.Path); continue; }
                    catch (DirectoryNotFoundException) { SqlStore.MarkMigrationSourceDeleted(runId, file.Path); continue; }
                    throw new IOException("Cannot inspect source cleanup path: " + physical);
                }
                if (file.SourceDeleted)
                    throw new IOException("A new file appeared after this path was cleaned; retained: " + physical);
                if (!SameHash(SqlStore.ComputeMigrationFileSha256(runId, file.Path), file.Sha256))
                    throw new IOException("SQL archive changed before source deletion; retained: " + physical);
                VerifiedSource.DeleteVerified(physical, source, file.Length, file.Sha256);
                SqlStore.MarkMigrationSourceDeleted(runId, file.Path);
                Console.WriteLine("CLEANED " + file.OriginalPath);
            }

            List<string> directories;
            List<string> remaining = EnumerateSource(source, out directories);
            if (remaining.Count != 0)
                throw new IOException("Source contains new files after cleanup; retained: " + remaining[0]);
            foreach (string directory in directories.OrderByDescending(directory => directory.Length))
            {
                CheckCancellation();
                VerifiedSource.RejectReparsePath(directory, source);
                Directory.Delete(directory, recursive: false);
            }
            if (Directory.Exists(source) || File.Exists(source))
                throw new IOException("The data path still exists; runtime startup remains blocked: " + source);
            SqlStore.MarkMigrationCleanupComplete(runId);
        }

        private static string SourcePath(string source, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Split('/', '\\').Any(part => part == ".." || part == "." || part.Length == 0))
                throw new IOException("Unsafe path in SQL migration manifest: " + relative);
            string result = Path.GetFullPath(Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Migration path escapes original source: " + relative);
            return result;
        }

        private static string RelativePath(string source, string fullPath)
        {
            string prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Source path escaped data directory: " + fullPath);
            return full.Substring(prefix.Length).Replace('\\', '/');
        }

        private static bool SameHash(string first, string second)
        {
            return !string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second) &&
                string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static void CheckCancellation()
        {
            if (Volatile.Read(ref _cancelRequested) != 0)
                throw new OperationCanceledException("Migration cancelled at a safe operation boundary; rerun to resume.");
        }

        private static void PrintSuccess(IList<SqlStore.MigrationFile> files)
        {
            Console.WriteLine("COMPLETE: " + files.Count + " files, " + files.Sum(file => file.Length) +
                " original bytes verified in MySQL; the data folder is gone. City Dwellers can now start.");
            Console.WriteLine("Run DataMigration.exe --schema for every live table definition and index; --verify checks the immutable archive.");
        }

        private static void PrintSchema()
        {
            using (MySqlConnection connection = SqlStore.OpenConnection())
            {
                var tables = new List<string>();
                using (MySqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SHOW FULL TABLES WHERE Table_type = 'BASE TABLE'";
                    using (MySqlDataReader reader = command.ExecuteReader())
                        while (reader.Read()) tables.Add(reader.GetString(0));
                }
                foreach (string table in tables.OrderBy(table => table, StringComparer.Ordinal))
                {
                    string identifier = "`" + table.Replace("`", "``") + "`";
                    Console.WriteLine();
                    Console.WriteLine("TABLE " + table);
                    using (MySqlCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SHOW CREATE TABLE " + identifier;
                        using (MySqlDataReader reader = command.ExecuteReader())
                            while (reader.Read()) Console.WriteLine(reader.GetString(1) + ";");
                    }
                    using (MySqlCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SHOW INDEX FROM " + identifier;
                        using (MySqlDataReader reader = command.ExecuteReader())
                        {
                            Console.WriteLine(string.Join("\t", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
                            while (reader.Read())
                                Console.WriteLine(string.Join("\t", Enumerable.Range(0, reader.FieldCount)
                                    .Select(index => reader.IsDBNull(index) ? "NULL" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
                        }
                    }
                }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("DataMigration.exe [migrate|verify|schema] [--root <runtime-directory>] [--empty]");
            Console.WriteLine("  migrate (default)  Import all data recursively, verify SQL bytes, seal, remove verified source.");
            Console.WriteLine("  verify / --verify  Check a sealed immutable archive without importing or deleting source.");
            Console.WriteLine("  schema / --schema  Show every SQL table definition, column and index while bots are offline.");
            Console.WriteLine("  --root             Folder containing citydwellers.json and the old data directory; defaults to executable folder.");
            Console.WriteLine("  --empty            Explicitly permit the first migration inventory to contain no files.");
            Console.WriteLine("Configuration is read from the MySql section of citydwellers.json. Never pass credentials on the command line.");
        }

        private sealed class Options
        {
            internal string Command = "migrate";
            internal string RuntimeRoot = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            internal bool Empty;
            internal bool Help;

            internal static Options Parse(string[] args)
            {
                var result = new Options();
                bool commandSeen = false;
                for (int index = 0; index < args.Length; index++)
                {
                    string argument = args[index].ToLowerInvariant();
                    if (argument == "--help" || argument == "-h" || argument == "/?") result.Help = true;
                    else if (argument == "--root")
                    {
                        if (++index >= args.Length) throw new ArgumentException("--root needs a runtime directory.");
                        result.RuntimeRoot = Path.GetFullPath(args[index]);
                    }
                    else if (argument == "--empty") result.Empty = true;
                    else if (argument == "migrate" || argument == "verify" || argument == "schema" || argument == "--verify" || argument == "--schema")
                    {
                        if (commandSeen) throw new ArgumentException("Specify one command: migrate, verify or schema.");
                        result.Command = argument.TrimStart('-');
                        commandSeen = true;
                    }
                    else throw new ArgumentException("Unknown argument: " + args[index] + ". Use --help.");
                }
                if (result.Empty && result.Command != "migrate")
                    throw new ArgumentException("--empty only applies to migrate.");
                return result;
            }
        }
    }
}
