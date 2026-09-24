using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using CityDwellers.Shared;

namespace CityManager
{
    // Holds the most recent transfer traces so one transaction can be dumped on
    // demand. Live telemetry only: bounded, in memory, never written to disk or to
    // accounting state, and lost on restart by design. Session 192 forbids a
    // physical runtime data folder, and trace rows must never join a custody
    // commit, so durable archiving is syslog's job (Syslog.Enabled in
    // citydwellers.json). This exists so the owner can read a trace with no
    // external receiver configured.
    internal sealed class TraceCollector
    {
        private const int MaxSpans = 64;

        private readonly object _sync = new object();
        private readonly Queue<ServiceEvent> _spans = new Queue<ServiceEvent>();

        internal void Observe(ServiceEvent report)
        {
            if (report == null || !string.Equals(report.Event, "bank.trace", StringComparison.Ordinal))
                return;
            lock (_sync)
            {
                _spans.Enqueue(report);
                while (_spans.Count > MaxSpans) _spans.Dequeue();
            }
        }

        // One line per retained span, newest last, so the owner can pick one.
        internal string Index()
        {
            List<ServiceEvent> spans;
            lock (_sync) spans = _spans.ToList();
            if (spans.Count == 0)
                return "TRACE index: empty. No transfer has completed or failed since Manager started.";
            return "TRACE index (" + spans.Count + " retained, newest last):\n" + string.Join("\n",
                spans.Select((span, index) =>
                {
                    return (index + 1).ToString(CultureInfo.InvariantCulture) + ") " +
                        Field(span, "Kind") + " txn=" + Short(Field(span, "TransactionId")) +
                        " batch=" + Short(Field(span, "BatchId")) +
                        " " + Field(span, "Source") + "->" + Field(span, "Destination") +
                        " items=" + Field(span, "ManifestCount") +
                        " total=" + Field(span, "TotalMs") + "ms" +
                        " " + Field(span, "Outcome");
                }));
        }

        // The full timeline for one span, as indented JSON. This is the artifact the
        // measurement phase reads; it is deliberately not sent to AO chat.
        internal string Dump(string selector)
        {
            List<ServiceEvent> spans;
            lock (_sync) spans = _spans.ToList();
            if (spans.Count == 0) return "TRACE: nothing retained.";

            int ordinal;
            var matches = int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal)
                ? (ordinal >= 1 && ordinal <= spans.Count
                    ? new List<ServiceEvent> { spans[ordinal - 1] }
                    : new List<ServiceEvent>())
                : spans.Where(span =>
                    Contains(Field(span, "TransactionId"), selector) ||
                    Contains(Field(span, "BatchId"), selector) ||
                    Contains(Field(span, "AttemptId"), selector)).ToList();

            if (matches.Count == 0)
                return "TRACE: no retained span matches '" + selector + "'. Use 'trace' for the index.";
            // Both sides of one transfer are useful together, so a transaction or
            // batch selector intentionally returns every matching span.
            return string.Join("\n\n", matches.Select(span =>
                JsonConvert.SerializeObject(span.Data, Formatting.Indented)));
        }

        private static string Field(ServiceEvent span, string name)
        {
            var value = span?.Data?[name];
            return value == null || value.Type == Newtonsoft.Json.Linq.JTokenType.Null
                ? "-" : value.ToString();
        }

        private static bool Contains(string value, string selector) =>
            !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(selector) &&
            value.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Short(string value) =>
            string.IsNullOrEmpty(value) || value.Length <= 12 ? (value ?? "-") : value.Substring(0, 12);
    }
}
