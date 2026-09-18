using System;
using System.IO;
using System.Threading;

namespace CityDwellers.Shared
{
    // Readers retain one immutable version while writers atomically publish
    // the next. Sharing delete permits replacement on Windows as well as Mono.
    public static class FileSnapshot
    {
        public static string ReadText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        public static void WriteText(string path, string text)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, text);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return;
                    }
                    catch (IOException) when (attempt < 2 && File.Exists(temp))
                    {
                        // Retry publication only, never a caller's send or AO
                        // action. Preserve the previous destination on failure.
                        Thread.Sleep(5);
                    }
                }
            }
            finally
            {
                try { File.Delete(temp); }
                catch { /* Do not mask a publication failure or delete another writer's file. */ }
            }
        }
    }
}
