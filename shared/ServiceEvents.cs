using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    // Per-client AppDomain. Manager memory retains immutable reports with their
    // original identity/time; only Manager delivers to its diagnostic sink.
    public static class ServiceEvents
    {
        private static Router _router;
        private static string _character, _role;
        private static Func<int> _identity;
        public static void Start(string settings, string character, Func<int> identity,
            Action<string> warning, Action<ServiceEvent> managerSink = null)
        {
            var root = JObject.Parse(File.ReadAllText(Path.Combine(settings, "citydwellers.json")));
            // Events are bounded in-memory telemetry for optional external forwarding.
            var roles = (root["Bankers"]?["Roles"] as JObject)?.Properties().ToDictionary(
                p => (string)p.Value["Character"], p => p.Name, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string role;
            if (managerSink != null) role = "manager";
            else if (!roles.TryGetValue(character, out role)) throw new InvalidDataException("Unknown banker event source.");
            _character = character; _role = role; _identity = identity;
            _router = new Router(roles, warning, managerSink);
        }

        public static void Report(string name, string severity, string message, object data = null)
        {
            var router = _router;
            if (router == null) return;
            int id = 0;
            try { id = _identity(); } catch { /* Not yet logged in: explicitly unknown. */ }
            try
            {
                var report = new ServiceEvent { Id = Guid.NewGuid().ToString("N"), TimeUtc = DateTime.UtcNow,
                    Character = _character, CharacterId = id, Role = _role, Event = name,
                    Severity = severity, Message = message, Data = data == null ? null : JObject.FromObject(data) };
                router.Add(report);
            }
            catch { /* Diagnostic logging/relay errors do not retry game effects. */ }
        }

        public static void Stop() { var router = _router; _router = null; router?.Dispose(); }

        private sealed class Router : IDisposable
        {
            private readonly string _character;
            private readonly Dictionary<string, string> _roles;
            private readonly Action<string> _warning;
            private readonly Action<ServiceEvent> _sink;
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly HashSet<string> _seen = new HashSet<string>();
            private readonly Queue<string> _seenOrder = new Queue<string>();
            private readonly Task _worker;
            private readonly string _consumer;
            private readonly object _dropSync = new object();
            private readonly Stopwatch _dropNoticeAge = Stopwatch.StartNew();
            private long _dropped;
            private bool _dropNoticeSent;

            public Router(Dictionary<string, string> roles, Action<string> warning, Action<ServiceEvent> sink)
            {
                _character = ServiceEvents._character;
                _roles = roles; _warning = warning; _sink = sink;
                if (sink != null)
                {
                    _consumer = ManagerMemory.Current.BeginServiceEventConsumer();
                    _worker = Task.Run(() => Send());
                }
            }

            public bool Add(ServiceEvent report)
            {
                if (_stop.IsCancellationRequested) return false;
                // No game operations or remote sink callbacks execute on this thread.
                if (ManagerMemory.Current.EnqueueServiceEvent(JsonConvert.SerializeObject(report))) return true;
                lock (_dropSync)
                {
                    _dropped++;
                    if (!_dropNoticeSent || _dropNoticeAge.ElapsedMilliseconds >= 30000)
                    {
                        _warning("Event reporting queue full or report too large: " + _dropped + " reports not queued.");
                        _dropped = 0;
                        _dropNoticeSent = true;
                        _dropNoticeAge.Restart();
                    }
                }
                return false;
            }

            private bool Valid(ServiceEvent report)
            {
                // Manager's own reports retain their previous local-sink behavior.
                if (report != null && report.Role == "manager")
                    return string.Equals(report.Character, _character, StringComparison.OrdinalIgnoreCase);
                Guid id;
                string role;
                return report != null && Guid.TryParseExact(report.Id, "N", out id) &&
                    report.TimeUtc != default(DateTime) && !string.IsNullOrWhiteSpace(report.Event) && report.Event.Length <= 32 &&
                    (report.Severity == "info" || report.Severity == "warning" || report.Severity == "error") &&
                    _roles.TryGetValue(report.Character ?? "", out role) && role == report.Role;
            }

            private async Task Send()
            {
                bool failed = false;
                try
                {
                    while (!_stop.IsCancellationRequested && ManagerMemory.Current.IsServiceEventConsumer(_consumer))
                    {
                        string payload = ManagerMemory.Current.ReadServiceEvent(_consumer, 250);
                        if (payload == null) continue;
                        ServiceEvent report;
                        try { report = JsonConvert.DeserializeObject<ServiceEvent>(payload); }
                        catch (JsonException)
                        {
                            ManagerMemory.Current.CompleteServiceEvent(_consumer, payload);
                            _warning("Invalid service event discarded.");
                            continue;
                        }
                        if (!Valid(report))
                        {
                            ManagerMemory.Current.CompleteServiceEvent(_consumer, payload);
                            _warning("Invalid service event source or fields discarded.");
                            continue;
                        }
                        if (_stop.IsCancellationRequested || !ManagerMemory.Current.IsServiceEventConsumer(_consumer)) break;
                        try
                        {
                            if (!_seen.Contains(report.Id))
                            {
                                _sink(report);
                                _seen.Add(report.Id); _seenOrder.Enqueue(report.Id);
                                if (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
                            }
                            ManagerMemory.Current.CompleteServiceEvent(_consumer, payload);
                            if (failed) _warning("Event reporting resumed.");
                            failed = false;
                        }
                        catch (Exception) when (!_stop.IsCancellationRequested)
                        {
                            if (!failed) _warning("Event reporting waiting for event log.");
                            failed = true;
                            // Existing sink-failure retry only; arrival wakes the inbox immediately.
                            await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }

            public void Dispose()
            {
                _stop.Cancel();
                if (_consumer != null) ManagerMemory.Current.EndServiceEventConsumer(_consumer);
                if (_worker != null) try { _worker.Wait(1000); } catch (AggregateException) { }
                // Unacknowledged events stay in host memory across Manager restart.
            }
        }
    }
}
