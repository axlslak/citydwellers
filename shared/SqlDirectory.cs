using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CityDwellers.Shared
{
    /// <summary>Logical MySQL namespaces. These operations never create a physical data directory.</summary>
    public static class SqlDirectory
    {
        private static readonly ConcurrentDictionary<string, byte> Confirmed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        public static void CreateDirectory(string path)
        {
            string key = SqlStore.Key(path);
            if (Confirmed.ContainsKey(key)) return;
            SqlStore.Execute(true, context => { Ensure(context, key); return 0; });
        }

        internal static void EnsureParents(SqlStore.DbContext context, string documentKey)
        {
            int slash = documentKey.LastIndexOf('/'); Ensure(context, slash < 0 ? "" : documentKey.Substring(0, slash));
        }

        private static void Ensure(SqlStore.DbContext context, string key)
        {
            if (Confirmed.ContainsKey(key)) return;
            var parents = new List<string> { "" };
            if (key.Length != 0)
            {
                string current = "";
                foreach (string segment in key.Split('/')) { current = current.Length == 0 ? segment : current + "/" + segment; parents.Add(current); }
            }
            foreach (string parent in parents)
            {
                if (Confirmed.ContainsKey(parent)) continue;
                using (var command = SqlStore.Command(context, "INSERT IGNORE INTO cd_directories(path,created_utc) VALUES(@path,UTC_TIMESTAMP(6))", "@path", parent)) command.ExecuteNonQuery();
                string committed = parent;
                context.AfterCommit.Add(() => Confirmed.TryAdd(committed, 0));
            }
        }

        public static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string key; if (!SqlStore.TryKey(path, out key)) return Directory.Exists(path);
            if (key.Length == 0 || Confirmed.ContainsKey(key)) return true;
            return SqlStore.Execute(false, context =>
            {
                using (var command = SqlStore.Command(context,
                    "SELECT EXISTS(SELECT 1 FROM cd_directories WHERE path=@path) OR EXISTS(SELECT 1 FROM cd_documents WHERE path LIKE @prefix ESCAPE '=')",
                    "@path", key, "@prefix", EscapeLike(key + "/") + "%")) return Convert.ToBoolean(command.ExecuteScalar());
            });
        }

        public static string[] GetFiles(string path) { return GetFiles(path, "*", SearchOption.TopDirectoryOnly); }
        public static string[] GetFiles(string path, string searchPattern) { return GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly); }
        public static string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        {
            string key; if (!SqlStore.TryKey(path, out key)) return Directory.GetFiles(path, searchPattern, searchOption);
            Validate(searchPattern, searchOption);
            string prefix = key.Length == 0 ? "" : key + "/";
            Regex pattern = Pattern(searchPattern);
            return SqlStore.Execute(false, context =>
            {
                var result = new List<string>();
                using (var command = SqlStore.Command(context, "SELECT path FROM cd_documents WHERE path LIKE @prefix ESCAPE '=' ORDER BY path", "@prefix", EscapeLike(prefix) + "%"))
                using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    string full = reader.GetString(0), relative = full.Substring(prefix.Length);
                    if (searchOption == SearchOption.TopDirectoryOnly && relative.IndexOf('/') >= 0) continue;
                    string name = relative.Substring(relative.LastIndexOf('/') + 1);
                    if (pattern.IsMatch(name)) result.Add(SqlStore.LogicalPath(full));
                }
                return result.ToArray();
            });
        }
        public static IEnumerable<string> EnumerateFiles(string path) { return GetFiles(path); }
        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern) { return GetFiles(path, searchPattern); }
        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) { return GetFiles(path, searchPattern, searchOption); }

        public static string[] GetDirectories(string path) { return GetDirectories(path, "*", SearchOption.TopDirectoryOnly); }
        public static string[] GetDirectories(string path, string searchPattern) { return GetDirectories(path, searchPattern, SearchOption.TopDirectoryOnly); }
        public static string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
        {
            string key; if (!SqlStore.TryKey(path, out key)) return Directory.GetDirectories(path, searchPattern, searchOption);
            Validate(searchPattern, searchOption);
            string prefix = key.Length == 0 ? "" : key + "/";
            Regex pattern = Pattern(searchPattern);
            return SqlStore.Execute(false, context =>
            {
                var directories = new HashSet<string>(StringComparer.Ordinal);
                using (var command = SqlStore.Command(context, "SELECT path,1 AS directory FROM cd_directories WHERE path LIKE @prefix ESCAPE '=' UNION ALL SELECT path,0 AS directory FROM cd_documents WHERE path LIKE @prefix ESCAPE '='", "@prefix", EscapeLike(prefix) + "%"))
                using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    string full = reader.GetString(0);
                    string relative = full.Substring(prefix.Length);
                    if (relative.Length == 0) continue;
                    string directory = reader.GetInt32(1) == 1 ? relative : relative.IndexOf('/') < 0 ? "" : relative.Substring(0, relative.LastIndexOf('/'));
                    while (directory.Length > 0)
                    {
                        int slash = directory.IndexOf('/');
                        if (searchOption == SearchOption.TopDirectoryOnly) { directories.Add(prefix + (slash < 0 ? directory : directory.Substring(0, slash))); break; }
                        directories.Add(prefix + directory);
                        int last = directory.LastIndexOf('/'); if (last < 0) break; directory = directory.Substring(0, last);
                    }
                }
                return directories.Where(p => pattern.IsMatch(p.Substring(p.LastIndexOf('/') + 1))).OrderBy(p => p, StringComparer.Ordinal).Select(SqlStore.LogicalPath).ToArray();
            });
        }
        public static IEnumerable<string> EnumerateDirectories(string path) { return GetDirectories(path); }
        public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern) { return GetDirectories(path, searchPattern); }
        public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) { return GetDirectories(path, searchPattern, searchOption); }

        private static void Validate(string pattern, SearchOption option)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (pattern.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) throw new ArgumentException("Search patterns must match a name, not a path.", nameof(pattern));
            if (option != SearchOption.TopDirectoryOnly && option != SearchOption.AllDirectories) throw new ArgumentOutOfRangeException(nameof(option));
        }
        private static Regex Pattern(string pattern)
        {
            if (pattern == "*.*") pattern = "*";
            return new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        private static string EscapeLike(string value) { return value.Replace("=", "==").Replace("%", "=%").Replace("_", "=_"); }
    }
}
