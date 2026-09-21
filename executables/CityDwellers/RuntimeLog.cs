using CityDwellers.Shared;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace CityDwellers.Host
{
    internal static class RuntimeLog
    {
        private static readonly object Sync = new object();
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();
        private static bool _timeTrusted;
        private static SynchronizedTeeWriter _writer;

        public static void Initialize(string dataDirectory)
        {
            lock (Sync)
            {
                if (_writer != null) return;
                _writer = new SynchronizedTeeWriter(Console.Out,
                    Path.Combine(dataDirectory, "citydweller.log"));
                Console.SetOut(_writer);
                Console.SetError(_writer);
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

    }

    internal sealed class SynchronizedTeeWriter : TextWriter
    {
        private readonly object _sync = new object();
        private readonly TextWriter _first;
        private readonly string _streamPath;

        public SynchronizedTeeWriter(TextWriter first, string streamPath)
        {
            _first = first;
            _streamPath = streamPath;
        }

        public override Encoding Encoding => new UTF8Encoding(false);

        // Append complete diagnostics to the native log before filtering the console.
        private readonly ThreadLocal<StringBuilder> _line =
            new ThreadLocal<StringBuilder>(() => new StringBuilder(), true);
        private readonly bool _verbose = string.Equals(
            Environment.GetEnvironmentVariable("CITYDWELLERS_VERBOSE_CONSOLE"), "1", StringComparison.Ordinal);
        public override void Write(char value) => Write(value.ToString());

        public override void Write(string value)
        {
            if (value == null) return;
            lock (_sync)
            {
                foreach (char character in value)
                {
                    if (character == '\n')
                    {
                        PersistAndRender(_line.Value.ToString().TrimEnd('\r'));
                        _line.Value.Clear();
                    }
                    else _line.Value.Append(character);
                    if (_line.Value.Length >= 16384)
                    {
                        PersistAndRender(_line.Value.ToString());
                        _line.Value.Clear();
                    }
                }
            }
        }

        public override void WriteLine(string value) => Write((value ?? string.Empty) + NewLine);
        public override void WriteLine() => Write(NewLine);

        private void PersistAndRender(string line)
        {
            // Child client loggers call back into this host while holding their
            // own SQL transactions. This independent append never reacquires the
            // gameplay writer lock and remains committed if game state rolls back.
            DiskFiles.AppendAllText(_streamPath, line + "\n");
            RenderLine(line);
        }

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
                foreach (StringBuilder pending in _line.Values)
                {
                    if (pending.Length == 0) continue;
                    PersistAndRender(pending.ToString());
                    pending.Clear();
                }
                _first.Flush();
            }
        }
    }
}
