using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

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

        // Keep complete diagnostics on disk. Only the operator console is condensed.
        private readonly ThreadLocal<StringBuilder> _line =
            new ThreadLocal<StringBuilder>(() => new StringBuilder());
        private readonly bool _verbose = string.Equals(
            Environment.GetEnvironmentVariable("CITYDWELLERS_VERBOSE_CONSOLE"), "1", StringComparison.Ordinal);
        public override void Write(char value) => Write(value.ToString());

        public override void Write(string value)
        {
            if (value == null) return;
            lock (_sync)
            {
                _second.Write(value);
                foreach (char character in value)
                {
                    if (character == '\n')
                    {
                        RenderLine(_line.Value.ToString().TrimEnd('\r'));
                        _line.Value.Clear();
                    }
                    else _line.Value.Append(character);
                    if (_line.Value.Length >= 16384)
                    {
                        RenderLine(_line.Value.ToString());
                        _line.Value.Clear();
                    }
                }
            }
        }

        public override void WriteLine(string value) => Write((value ?? string.Empty) + NewLine);
        public override void WriteLine() => Write(NewLine);

        private void RenderLine(string line)
        {
            bool error = line.Contains(" ERR]") || line.Contains(" FTL]");
            bool warning = line.Contains(" WRN]");
            bool connectionStatus = line.Contains("Gameserver state transition from ") ||
                line.Contains("Failed to connect to ") || line.Contains("Failed to login:");
            if (!_verbose && !error && !warning &&
                (line.Contains("[AOSharp.Clientless] MoveToBank Slot ") ||
                 (line.Contains(" DBG]") && !connectionStatus) || line.Contains(" VRB]") ||
                 line.Contains("BAG AUDIT opens inventory bags in place."))) return;
            if (!_verbose)
                line = line.Replace("[CityBankers] [CityBankers]", "[CityBankers]");
            if (Console.IsOutputRedirected) { _first.WriteLine(line); return; }
            ConsoleColor previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = error ? ConsoleColor.Red : warning ? ConsoleColor.Yellow :
                    line.Contains("COMPLETE") || line.Contains("BANKER READY") ? ConsoleColor.Green : ConsoleColor.Gray;
                _first.WriteLine(line);
            }
            finally { Console.ForegroundColor = previous; }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                if (_line.Value.Length > 0)
                {
                    _first.Write(_line.Value.ToString());
                    _line.Value.Clear();
                }
                _first.Flush();
                _second.Flush();
            }
        }
    }
}
