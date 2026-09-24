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

        private const int IndexLines = 12;

        // One short line per retained span, newest last, so the owner can pick one.
        // Capped: a full ring would not survive the proven organization/tell size.
        internal string Index()
        {
            List<ServiceEvent> spans;
            lock (_sync) spans = _spans.ToList();
            if (spans.Count == 0)
                return "TRACE index: empty. No transfer has completed or failed since Manager started.";
            int skipped = Math.Max(0, spans.Count - IndexLines);
            return "TRACE index (" + spans.Count + " retained" +
                (skipped > 0 ? ", showing last " + IndexLines : "") + ", newest last):\n" +
                string.Join("\n", spans.Skip(skipped).Select((span, index) =>
                    (skipped + index + 1).ToString(CultureInfo.InvariantCulture) + ") " +
                        Field(span, "Kind") + " txn=" + Short(Field(span, "TransactionId")) +
                        " batch=" + Short(Field(span, "BatchId")) +
                        " " + Field(span, "Source") + "->" + Field(span, "Destination") +
                        " items=" + Field(span, "ManifestCount") +
                        " total=" + Field(span, "TotalMs") + "ms" +
                        (Field(span, "Truncated") == "True" ? " TRUNCATED" : "") +
                        " " + Field(span, "Outcome"))) +
                "\nUse 'trace <n|batch-id>' to write one timeline to the runtime log.";
        }

        // Writes the full timeline for one transfer to the runtime log and returns a
        // short confirmation for chat.
        //
        // The JSON payload for a ten-item span runs to tens of kilobytes, far past
        // the proven organization/tell blob size, so it must never be replied into
        // AO chat: it would be silently cut and the measurement lost. The runtime log
        // is where it goes - the owner already captures that file, so the artifact
        // arrives complete and copyable with no new disk path and no chat truncation.
        internal string DumpToLog(string selector, Action<string> log)
        {
            List<ServiceEvent> matches = Select(selector);
            if (matches == null) return "TRACE: nothing retained.";
            if (matches.Count == 0)
                return "TRACE: no retained span matches '" + selector + "'. Use 'trace' for the index.";

            int characters = 0;
            foreach (ServiceEvent span in matches)
            {
                string json = JsonConvert.SerializeObject(span.Data, Formatting.Indented);
                characters += json.Length;
                log("TRACE SPAN BEGIN kind=" + Field(span, "Kind") +
                    " batch=" + Field(span, "BatchId") +
                    " attempt=" + Field(span, "AttemptId") + "\n" + json +
                    "\nTRACE SPAN END batch=" + Field(span, "BatchId"));
            }
            bool anyTruncated = matches.Any(span => Field(span, "Truncated") == "True");
            return "TRACE wrote " + matches.Count + " span(s), " + characters +
                " characters, to the runtime log" +
                (anyTruncated
                    ? ". WARNING: at least one span is marked Truncated, so its timeline is incomplete - fix the tracer before trusting the measurement."
                    : ". Both sides of a transfer share a batch id; look for TRACE SPAN BEGIN.");
        }

        private List<ServiceEvent> Select(string selector)
        {
            List<ServiceEvent> spans;
            lock (_sync) spans = _spans.ToList();
            if (spans.Count == 0) return null;

            int ordinal;
            // Both sides of one transfer are useful together, so a transaction or
            // batch selector intentionally returns every matching span.
            return int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal)
                ? (ordinal >= 1 && ordinal <= spans.Count
                    ? new List<ServiceEvent> { spans[ordinal - 1] }
                    : new List<ServiceEvent>())
                : spans.Where(span =>
                    Contains(Field(span, "TransactionId"), selector) ||
                    Contains(Field(span, "BatchId"), selector) ||
                    Contains(Field(span, "AttemptId"), selector)).ToList();
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
