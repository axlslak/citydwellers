using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace CityDwellers.Shared
{
    // Transfer instrumentation. Its only job is to answer "where did the time go"
    // for one transfer, precisely enough that nobody has to guess from a text log.
    //
    // Three rules this file exists to enforce:
    //
    //  - Durations are monotonic. Every elapsed value comes from Stopwatch, never
    //    from DateTime. Wall-clock UTC is carried once per span for correlation
    //    only; an operator clock change must not be able to invent or erase time.
    //  - Waiting is measured, not just acting. A state machine that spends two
    //    seconds re-evaluating the same unmet condition records one wait stage
    //    naming that condition, its total, and how many observations it took.
    //    A hidden timer that fires ten times matters more than ten log lines.
    //  - A span costs one event, not one per stage. The ServiceEvents relay is a
    //    serialized named-pipe round trip per event behind a 256-item bounded
    //    queue; emitting sixty stages separately would drop the data it is meant
    //    to capture. Stages accumulate in the span and ship together when it ends.
    //
    // This type never changes game behaviour. Every entry point swallows its own
    // failures: a broken tracer must not fail a transfer.
    public static class TradeTrace
    {
        // Bounded so a stuck span cannot grow without limit. A ten-item batch
        // needs roughly 120 stages; this leaves room for retries and waits while
        // keeping the serialized span far below the relay's 60000-character cap.
        private const int MaxStages = 600;

        public sealed class Stage
        {
            public string Name;
            // Monotonic milliseconds since the span began.
            public double AtMs;
            // Monotonic milliseconds since the previous stage.
            public double SinceMs;
            // Present on wait stages only: how long the condition stayed unmet and
            // how many times the state machine looked at it.
            public double WaitedMs;
            public int Observations;
            public int Attempt;
            public string Detail;
            public int? AoId;
            public int? Ql;
            public string Occurrence;
        }

        public sealed class Span
        {
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal readonly object Sync = new object();
            internal double LastMarkMs;
            internal string OpenWaitCondition;
            internal double OpenWaitStartMs;
            internal int OpenWaitObservations;
            internal bool Ended;

            public string Kind;
            public DateTime StartedUtc;
            public string TransactionId;
            public string BatchId;
            public string AttemptId;
            public string Source;
            public string Destination;
            public string Role;
            public int ManifestCount;
            public string Outcome;
            public string Error;
            public double TotalMs;
            public bool Truncated;
            public readonly List<Stage> Stages = new List<Stage>();
            public readonly Dictionary<string, int> Counters =
                new Dictionary<string, int>(StringComparer.Ordinal);
        }

        // Counter names. These are the quantities that let two designs be compared
        // without re-reading either implementation.
        public const string AoOperations = "ao_operations";
        public const string IpcRoundTrips = "ipc_round_trips";
        public const string AccountingCommits = "accounting_commits";
        public const string Waits = "waits";
        public const string Retries = "retries";
        public const string BagOutOfBank = "bag_bank_to_inventory";
        public const string BagIntoBank = "bag_inventory_to_bank";
        public const string BagOpens = "bag_opens";
        public const string ItemsPlaced = "items_placed";

        public static Span Begin(
            string kind,
            string transactionId,
            string batchId,
            string attemptId,
            string source,
            string destination,
            string role,
            int manifestCount)
        {
            try
            {
                var span = new Span
                {
                    Kind = kind,
                    StartedUtc = DateTime.UtcNow,
                    TransactionId = transactionId,
                    BatchId = batchId,
                    AttemptId = attemptId,
                    Source = source,
                    Destination = destination,
                    Role = role,
                    ManifestCount = manifestCount
                };
                Mark(span, "span.begin");
                return span;
            }
            catch (Exception) { return null; }
        }

        // The attempt id is only known once Central assigns one; the batch id only
        // once a worker has been told which batch it is storing.
        public static void Identify(Span span, string attemptId = null, string batchId = null,
            string destination = null, int? manifestCount = null)
        {
            if (span == null) return;
            try
            {
                lock (span.Sync)
                {
                    if (!string.IsNullOrWhiteSpace(attemptId)) span.AttemptId = attemptId;
                    if (!string.IsNullOrWhiteSpace(batchId)) span.BatchId = batchId;
                    if (!string.IsNullOrWhiteSpace(destination)) span.Destination = destination;
                    if (manifestCount.HasValue) span.ManifestCount = manifestCount.Value;
                }
            }
            catch (Exception) { }
        }

        public static void Count(Span span, string counter, int by = 1)
        {
            if (span == null || string.IsNullOrEmpty(counter)) return;
            try
            {
                lock (span.Sync)
                {
                    int current;
                    span.Counters.TryGetValue(counter, out current);
                    span.Counters[counter] = current + by;
                }
            }
            catch (Exception) { }
        }

        public static void Mark(Span span, string stage, string detail = null,
            int attempt = 0, int? aoId = null, int? ql = null, string occurrence = null)
        {
            if (span == null || string.IsNullOrEmpty(stage)) return;
            try
            {
                lock (span.Sync)
                {
                    if (span.Ended) return;
                    // An action ends whatever the machine was waiting for. Closing
                    // the wait first keeps the timeline additive: wait totals and
                    // stage deltas sum to the span, with nothing counted twice.
                    CloseWaitLocked(span);
                    AddLocked(span, new Stage
                    {
                        Name = stage,
                        Detail = detail,
                        Attempt = attempt,
                        AoId = aoId,
                        Ql = ql,
                        Occurrence = occurrence
                    });
                }
            }
            catch (Exception) { }
        }

        // Called every time the state machine re-evaluates an unmet condition. The
        // same condition coalesces into one stage; a different one closes the
        // previous wait and opens a new one. This is what turns "nothing happened
        // for two seconds" into "waited 2043ms for local-trade-window, 33 looks".
        public static void Wait(Span span, string condition, string detail = null)
        {
            if (span == null || string.IsNullOrEmpty(condition)) return;
            try
            {
                lock (span.Sync)
                {
                    if (span.Ended) return;
                    if (string.Equals(span.OpenWaitCondition, condition, StringComparison.Ordinal))
                    {
                        span.OpenWaitObservations++;
                        return;
                    }
                    CloseWaitLocked(span);
                    span.OpenWaitCondition = condition;
                    span.OpenWaitStartMs = span.Clock.Elapsed.TotalMilliseconds;
                    span.OpenWaitObservations = 1;
                    if (detail != null)
                        AddLocked(span, new Stage { Name = "wait.begin:" + condition, Detail = detail });
                }
            }
            catch (Exception) { }
        }

        private static void CloseWaitLocked(Span span)
        {
            if (span.OpenWaitCondition == null) return;
            double now = span.Clock.Elapsed.TotalMilliseconds;
            var stage = new Stage
            {
                Name = "wait:" + span.OpenWaitCondition,
                WaitedMs = Round(now - span.OpenWaitStartMs),
                Observations = span.OpenWaitObservations
            };
            span.OpenWaitCondition = null;
            span.OpenWaitObservations = 0;
            int current;
            span.Counters.TryGetValue(Waits, out current);
            span.Counters[Waits] = current + 1;
            AddLocked(span, stage);
        }

        private static void AddLocked(Span span, Stage stage)
        {
            if (span.Stages.Count >= MaxStages)
            {
                span.Truncated = true;
                return;
            }
            double now = span.Clock.Elapsed.TotalMilliseconds;
            stage.AtMs = Round(now);
            stage.SinceMs = Round(now - span.LastMarkMs);
            span.LastMarkMs = now;
            span.Stages.Add(stage);
        }

        // Ends the span and ships the whole timeline as one structured event.
        // Severity follows the outcome so a failed transfer is findable without
        // reading every successful one.
        public static void End(Span span, string outcome, string error = null)
        {
            if (span == null) return;
            try
            {
                object payload;
                string severity;
                string summary;
                lock (span.Sync)
                {
                    if (span.Ended) return;
                    CloseWaitLocked(span);
                    span.Ended = true;
                    span.Outcome = outcome;
                    span.Error = error;
                    span.TotalMs = Round(span.Clock.Elapsed.TotalMilliseconds);
                    AddLocked(span, new Stage { Name = "span.end", Detail = outcome });
                    severity = string.Equals(outcome, "completed", StringComparison.Ordinal)
                        ? "info" : "warning";
                    summary = Summary(span);
                    payload = new
                    {
                        Kind = span.Kind,
                        StartedUtc = span.StartedUtc,
                        TransactionId = span.TransactionId,
                        BatchId = span.BatchId,
                        AttemptId = span.AttemptId,
                        Source = span.Source,
                        Destination = span.Destination,
                        Role = span.Role,
                        ManifestCount = span.ManifestCount,
                        Outcome = span.Outcome,
                        Error = span.Error,
                        TotalMs = span.TotalMs,
                        Truncated = span.Truncated,
                        Counters = span.Counters.OrderBy(p => p.Key, StringComparer.Ordinal)
                            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                        Stages = span.Stages
                    };
                }
                ServiceEvents.Report("bank.trace", severity, summary, payload);
            }
            catch (Exception) { }
        }

        // One readable line for the chat/log path; the JSON payload carries the detail.
        private static string Summary(Span span)
        {
            string counters = string.Join(" ", span.Counters
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
            return "TRACE " + span.Kind +
                " batch=" + (span.BatchId ?? "-") +
                " " + (span.Source ?? "?") + "->" + (span.Destination ?? "?") +
                " items=" + span.ManifestCount.ToString(CultureInfo.InvariantCulture) +
                " total=" + span.TotalMs.ToString("F0", CultureInfo.InvariantCulture) + "ms" +
                " outcome=" + (span.Outcome ?? "?") +
                (span.Truncated ? " TRUNCATED" : string.Empty) +
                (counters.Length == 0 ? string.Empty : " | " + counters);
        }

        private static double Round(double value)
        {
            double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            return rounded < 0 ? 0 : rounded;
        }
    }
}
