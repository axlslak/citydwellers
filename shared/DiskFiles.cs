using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CityDwellers.Shared
{
    // Native disk I/O for administrator configuration, the static catalogue,
    // logs and explicitly requested diagnostics. Runtime coordination uses
    // ManagerMemory; durable banking uses the host's relational tables.
    public static class DiskFiles
    {
        public static bool Exists(string path) => File.Exists(path);
        public static string ReadAllText(string path) => File.ReadAllText(path);
        public static string ReadAllText(string path, Encoding encoding) => File.ReadAllText(path, encoding);
        public static string ReadAllTextOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
        public static IEnumerable<string> ReadLines(string path) => File.ReadLines(path);
        public static IEnumerable<string> ReadTailLines(string path, int count)
        {
            var lines = new Queue<string>();
            foreach (string line in File.ReadLines(path)) { lines.Enqueue(line); if (lines.Count > count) lines.Dequeue(); }
            return lines.ToArray();
        }
        public static long GetLength(string path) => new FileInfo(path).Length;
        public static DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
        public static Stream OpenRead(string path) => File.OpenRead(path);
        public static void Delete(string path) => File.Delete(path);
        public static void Copy(string source, string destination, bool overwrite = false) => File.Copy(source, destination, overwrite);
        public static void Move(string source, string destination) => File.Move(source, destination);
        private static void EnsureParent(string path) => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        public static void WriteAllText(string path, string text) => WriteAllText(path, text, new UTF8Encoding(false));
        public static void WriteAllText(string path, string text, Encoding encoding) => WriteAllBytes(path, encoding.GetBytes(text ?? ""));
        public static void WriteAllBytes(string path, byte[] bytes)
        {
            EnsureParent(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static bool TryCreateNew(string path, string text)
        {
            EnsureParent(path);
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { byte[] bytes = new UTF8Encoding(false).GetBytes(text ?? ""); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                return true;
            }
            catch (IOException) { if (File.Exists(path)) return false; throw; }
        }
        public static void AppendAllText(string path, string text) => AppendAllText(path, text, new UTF8Encoding(false));
        public static void AppendAllText(string path, string text, Encoding encoding)
        { EnsureParent(path); File.AppendAllText(path, text, encoding); }
        public static void AppendAllLines(string path, IEnumerable<string> lines)
        { EnsureParent(path); File.AppendAllLines(path, lines); }
    }
}
