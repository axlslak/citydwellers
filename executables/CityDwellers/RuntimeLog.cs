using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CityDwellers.Host
{
    internal static class RuntimeLog
    {
        private const long MaximumLogBytes = 10L * 1024L * 1024L;
        private static readonly object Sync = new object();
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();
        private static bool _timeTrusted;
        private static StreamWriter _fileWriter;

        public static void Initialize(string dataDirectory)
        {
            lock (Sync)
            {
                if (_fileWriter != null)
                    return;

                string path = Path.Combine(dataDirectory, "citydwellers.log");
                RotateIfNeeded(path);

                _fileWriter = new StreamWriter(
                    new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                var writer = new SynchronizedTeeWriter(Console.Out, _fileWriter);
                Console.SetOut(writer);
                Console.SetError(writer);
            }
        }

        public static void MarkTimeTrusted()
        {
            lock (Sync)
                _timeTrusted = true;
        }

        public static void Write(string message)
        {
            string prefix;
            lock (Sync)
            {
                prefix = _timeTrusted
                    ? DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")
                    : "untrusted-clock +" + Uptime.Elapsed.TotalSeconds.ToString("F3") + "s";
            }

            Console.WriteLine("[HOST " + prefix + "] " + message);
        }

        private static void RotateIfNeeded(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumLogBytes)
                return;

            string previous = path + ".previous";
            if (File.Exists(previous))
                File.Delete(previous);
            File.Move(path, previous);
        }
    }

    internal sealed class SynchronizedTeeWriter : TextWriter
    {
        private readonly object _sync = new object();
        private readonly TextWriter _first;
        private readonly TextWriter _second;

        public SynchronizedTeeWriter(TextWriter first, TextWriter second)
        {
            _first = first;
            _second = second;
        }

        public override Encoding Encoding => _second.Encoding;

        public override void Write(char value)
        {
            lock (_sync)
            {
                _first.Write(value);
                _second.Write(value);
            }
        }

        public override void Write(string value)
        {
            lock (_sync)
            {
                _first.Write(value);
                _second.Write(value);
            }
        }

        public override void WriteLine(string value)
        {
            lock (_sync)
            {
                _first.WriteLine(value);
                _second.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                _first.Flush();
                _second.Flush();
            }
        }
    }
}
