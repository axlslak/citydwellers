namespace CityDwellers.Shared
{
    // A document read receives one committed SQL version. Publishing replaces the
    // document atomically inside MySQL; no temporary file or disk fallback exists.
    public static class FileSnapshot
    {
        public static string ReadText(string path) => SqlFile.ReadAllText(path);
        public static void WriteText(string path, string text) => SqlFile.WriteAllText(path, text);
    }
}
