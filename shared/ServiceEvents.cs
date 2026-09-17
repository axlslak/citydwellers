using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    internal sealed class SyslogSettings
    {
        public bool Enabled = false;
        public string Host = "";
        public int Port = 514;
        public string Transport = "tcp";
    }

    public sealed class ServiceEvent
    {
        public string Id;
        public DateTime TimeUtc;
        public string Character;
        public int CharacterId;
        public string Role;
        public string Event;
        public string Severity;
        public string Message;
        public JObject Data;
        public string Via;
    }

    // Per-client AppDomain. Reports contain original identity/time; relaying never
    // turns a banker's event into an event attributed to Central or Manager.
    public static class ServiceEvents
    {
        private static Router _router;
        private static string _character, _role;
        private static Func<int> _identity;
        public static void Start(string settings, string character, Func<int> identity,
            Action<string> warning, Action<ServiceEvent> managerSink = null)
        {
            var root = JObject.Parse(File.ReadAllText(Path.Combine(settings, "citydwellers.json")));
            var config = root.GetValue("Syslog", StringComparison.OrdinalIgnoreCase)?.ToObject<SyslogSettings>();
            if (config?.Enabled != true) return;
            var roles = (root["Bankers"]?["Roles"] as JObject)?.Properties().ToDictionary(
                p => (string)p.Value["Character"], p => p.Name, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string central = roles.FirstOrDefault(p => p.Value == "central").Key;
            if (managerSink == null && string.IsNullOrWhiteSpace(central)) throw new InvalidDataException("Event reporting requires configured Central.");
            string role;
            if (managerSink != null) role = "manager";
            else if (!roles.TryGetValue(character, out role)) throw new InvalidDataException("Unknown banker event source.");
            _character = character; _role = role; _identity = identity;
            _router = new Router(role, central, roles, warning, managerSink);
        }

        public static void Report(string name, string severity, string message, object data = null)
        {
            var router = _router;
            if (router == null) return;
            int id = 0;
            try { id = _identity(); } catch { /* Not yet logged in: explicitly unknown. */ }
            try { router.Add(new ServiceEvent { Id = Guid.NewGuid().ToString("N"), TimeUtc = DateTime.UtcNow,
                Character = _character, CharacterId = id, Role = _role, Event = name,
                Severity = severity, Message = message, Data = data == null ? null : JObject.FromObject(data) }); }
            catch { /* Reporting must never change custody or game behavior. */ }
        }

        public static void Stop() { var router = _router; _router = null; router?.Dispose(); }

        private sealed class Router : IDisposable
        {
            private readonly string _role, _central;
            private readonly Dictionary<string, string> _roles;
            private readonly Action<string> _warning;
            private readonly Action<ServiceEvent> _sink;
            private readonly BlockingCollection<ServiceEvent> _queue = new BlockingCollection<ServiceEvent>(256);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly HashSet<string> _seen = new HashSet<string>();
            private readonly Queue<string> _seenOrder = new Queue<string>();
            private readonly Task _worker, _server;
            private long _dropped;
            private static string Pipe(string owner) => "CityDwellers.Events." + Process.GetCurrentProcess().Id + "." + owner;

            public Router(string role, string central, Dictionary<string, string> roles, Action<string> warning, Action<ServiceEvent> sink)
            {
                _role = role; _central = central; _roles = roles; _warning = warning; _sink = sink;
                _worker = Task.Run(() => Send());
                if (role == "central" || role == "manager") _server = Task.Run(() => Listen());
            }

            public bool Add(ServiceEvent report)
            {
                try
                {
                    if (JsonConvert.SerializeObject(report).Length <= 60000 && _queue.TryAdd(report)) return true;
                }
                catch (InvalidOperationException) { }
                Interlocked.Increment(ref _dropped);
                return false;
            }

            private bool Valid(ServiceEvent report)
            {
                Guid id;
                string role;
                return report != null && Guid.TryParseExact(report.Id, "N", out id) &&
                    report.TimeUtc != default(DateTime) && !string.IsNullOrWhiteSpace(report.Event) && report.Event.Length <= 32 &&
                    (report.Severity == "info" || report.Severity == "warning" || report.Severity == "error") &&
                    _roles.TryGetValue(report.Character ?? "", out role) && role == report.Role &&
                    (_role == "central" ? report.Via == null : report.Via == _central);
            }

            private async Task Listen()
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(Pipe(_role), PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                        using (_stop.Token.Register(() => pipe.Dispose()))
                        {
                            await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                            await LocalIpc.RespondAsync(pipe, line =>
                            {
                                if (line == null || line.Length > 65536) return Task.FromResult("invalid");
                                var report = JsonConvert.DeserializeObject<ServiceEvent>(line);
                                if (!Valid(report)) return Task.FromResult("invalid");
                                if (_role == "central") report.Via = _central;
                                return Task.FromResult(Add(report) ? "accepted" : "busy");
                            }, 3000, _stop.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception) when (!_stop.IsCancellationRequested)
                    { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); }
                    catch (Exception) when (_stop.IsCancellationRequested) { break; }
                }
            }

            private async Task Send()
            {
                ServiceEvent pending = null;
                bool failed = false;
                var interval = Stopwatch.StartNew();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        if (interval.ElapsedMilliseconds >= 30000)
                        {
                            long dropped = Interlocked.Exchange(ref _dropped, 0);
                            if (dropped > 0) _warning("Event reporting queue full or report too large: " + dropped + " reports not queued; local diagnostics retained.");
                            interval.Restart();
                        }
                        if (pending == null && !_queue.TryTake(out pending, 250, _stop.Token))
                        {
                            if (_queue.IsCompleted) break;
                            continue;
                        }
                        try
                        {
                            if (_role == "manager")
                            {
                                if (!_seen.Contains(pending.Id))
                                {
                                    _sink(pending);
                                    _seen.Add(pending.Id); _seenOrder.Enqueue(pending.Id);
                                    if (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
                                }
                            }
                            else
                            {
                                if (_role == "central") pending.Via = _central;
                                string reply = await LocalIpc.RequestLineAsync(Pipe(_role == "central" ? "manager" : "central"),
                                    JsonConvert.SerializeObject(pending), 1000, 3000).ConfigureAwait(false);
                                if (reply != "accepted") throw new IOException("Event relay not ready.");
                            }
                            pending = null;
                            if (failed) _warning("Event reporting resumed.");
                            failed = false;
                        }
                        catch (Exception) when (!_stop.IsCancellationRequested)
                        {
                            if (!failed) _warning("Event reporting waiting for " + (_role == "manager" ? "event log" : _role == "central" ? "Manager" : "Central") + "; local diagnostics continue.");
                            failed = true;
                            await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }

            public void Dispose()
            {
                _queue.CompleteAdding();
                if (!_worker.Wait(1000)) _stop.Cancel();
                _stop.Cancel();
            }
        }
    }
}
