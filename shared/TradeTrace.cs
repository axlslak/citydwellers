using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

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
        // Bounded so a stuck span cannot grow without limit. A ten-item batch needs
        // roughly 120 stages. ServiceEvents.Router.Add silently discards any report
        // serializing over 60000 characters, so the cap plus the empty-field
        // suppression below must keep a full span well under that; End also
        // re-emits a trimmed span rather than let one be dropped for size.
        private const int MaxStages = 400;
        private const int MaxPayloadCharacters = 50000;

        public sealed class Stage
        {
            public string Name;
            // Monotonic milliseconds since the span began.
            public double AtMs;
            // Monotonic milliseconds since the previous stage.
            public double SinceMs;

            // Wait stages only.
            //
            // WaitedMs is how long the condition stayed unmet. On its own it cannot
            // distinguish "AO took 503ms" from "AO took 70ms and we only looked
            // again at 503ms", so the look pattern is recorded with it:
            //   Observations  - how many times the machine evaluated the condition
            //   FirstLookMs   - first look that found the condition UNSATISFIED
            //   LastLookMs    - last look that found the condition UNSATISFIED
            //   MaxLookGapMs  - largest interval between consecutive unsatisfied looks
            //
            // `[INVARIANT]` LastLookMs is LastUnsatisfiedLookMs. Wait() is only ever
            // called on a not-yet-satisfied path; the satisfying observation calls
            // Mark(), which closes the wait without touching LastLookMs. If the
            // satisfying look were recorded here the blind spot would collapse toward
            // zero and falsely exonerate our own polling, which is the single thing
            // this field exists to expose. Any new Wait() call site must be on an
            // unsatisfied path, and must not wrap a synchronous call: a blocking
            // operation has no unsatisfied looks, so its blind spot would equal its
            // whole duration and read as our latency. Bracket those with two Marks.
            //
            // The blind spot is AtMs - LastLookMs: the window in which the condition
            // may already have been satisfied without anyone looking. That window,
            // not WaitedMs, is the real uncertainty about when the external condition
            // changed. A small blind spot and a small MaxLookGapMs mean the wait is
            // genuinely external; a large one means we were not watching, which is our
            // latency, not AO's.
            public double WaitedMs;
            public int Observations;
            public double FirstLookMs;
            public double LastLookMs;
            public double MaxLookGapMs;

            public int Attempt;
            public string Detail;
            public int? AoId;
            public int? Ql;
            public string Occurrence;

            // Newtonsoft honours these through JObject.FromObject, so an ordinary
            // action stage serializes to a handful of fields instead of fourteen.
            // Without this a full span can exceed the relay's size limit and be
            // dropped without trace - the one failure this file cannot tolerate.
            public bool ShouldSerializeWaitedMs() => WaitedMs != 0;
            public bool ShouldSerializeObservations() => Observations != 0;
            public bool ShouldSerializeFirstLookMs() => Observations != 0;
            public bool ShouldSerializeLastLookMs() => Observations != 0;
            public bool ShouldSerializeMaxLookGapMs() => Observations != 0;
            public bool ShouldSerializeAttempt() => Attempt != 0;
            public bool ShouldSerializeDetail() => Detail != null;
            public bool ShouldSerializeAoId() => AoId.HasValue;
            public bool ShouldSerializeQl() => Ql.HasValue;
            public bool ShouldSerializeOccurrence() => Occurrence != null;
        }

        // Idle time by reason, so the critical-path idle breakdown does not have to
        // be re-derived by summing stages by hand.
        public sealed class WaitTotal
        {
            public double TotalMs;
            public int Episodes;
            public int Observations;
            public double MaxEpisodeMs;
            public double MaxLookGapMs;
            public double BlindSpotMs;
        }

        public sealed class Span
        {
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal readonly object Sync = new object();
            internal double LastMarkMs;
            internal string OpenWaitCondition;
            internal double OpenWaitStartMs;
            internal int OpenWaitObservations;
            internal double OpenWaitFirstLookMs;
            internal double OpenWaitLastLookMs;
            internal double OpenWaitMaxGapMs;
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
        //
        // `[INVARIANT]` A span has at most one open wait, because a different
        // condition closes the previous one and any Mark closes whatever is open.
        // Waits within a span therefore cannot overlap, so summing them is
        // critical-path waiting with no double counting. This does NOT hold across
        // spans: Central and the worker run concurrently, so adding a wait from each
        // would double-count real wall time.
        public static void Wait(Span span, string condition, string detail = null)
        {
            if (span == null || string.IsNullOrEmpty(condition)) return;
            try
            {
                lock (span.Sync)
                {
                    if (span.Ended) return;
                    double at = span.Clock.Elapsed.TotalMilliseconds;
                    if (string.Equals(span.OpenWaitCondition, condition, StringComparison.Ordinal))
                    {
                        double gap = at - span.OpenWaitLastLookMs;
                        if (gap > span.OpenWaitMaxGapMs) span.OpenWaitMaxGapMs = gap;
                        span.OpenWaitLastLookMs = at;
                        span.OpenWaitObservations++;
                        return;
                    }
                    CloseWaitLocked(span);
                    span.OpenWaitCondition = condition;
                    span.OpenWaitStartMs = at;
                    span.OpenWaitFirstLookMs = at;
                    span.OpenWaitLastLookMs = at;
                    span.OpenWaitMaxGapMs = 0;
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
                Observations = span.OpenWaitObservations,
                FirstLookMs = Round(span.OpenWaitFirstLookMs),
                LastLookMs = Round(span.OpenWaitLastLookMs),
                MaxLookGapMs = Round(span.OpenWaitMaxGapMs)
            };
            span.OpenWaitCondition = null;
            span.OpenWaitObservations = 0;
            span.OpenWaitMaxGapMs = 0;
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
                string severity;
                string summary;
                List<Stage> stages;
                Dictionary<string, int> counters;
                Dictionary<string, WaitTotal> waitTotals;
                lock (span.Sync)
                {
                    if (span.Ended) return;
                    CloseWaitLocked(span);
                    span.Ended = true;
                    span.Outcome = outcome;
                    span.Error = error;
                    // The final stage first, then TotalMs from that same stage, so no
                    // stage can read above the span total. Everything after this
                    // point - serializing, relaying, storing - is trace transport and
                    // is deliberately outside the measurement: a span must never
                    // include the cost of shipping itself.
                    AddLocked(span, new Stage { Name = "span.end", Detail = outcome });
                    span.TotalMs = span.Stages.Count > 0
                        ? span.Stages[span.Stages.Count - 1].AtMs
                        : Round(span.Clock.Elapsed.TotalMilliseconds);
                    severity = string.Equals(outcome, "completed", StringComparison.Ordinal)
                        ? "info" : "warning";
                    summary = Summary(span);
                    stages = new List<Stage>(span.Stages);
                    counters = span.Counters.OrderBy(p => p.Key, StringComparer.Ordinal)
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    waitTotals = WaitTotals(span.Stages);
                }

                // A span that is too large to relay is worse than a coarse one: the
                // relay drops an oversized report without saying so. Shed stage
                // detail until it fits, and mark the span so nobody reads a trimmed
                // timeline as a complete one.
                bool trimmed = span.Truncated;
                string serialized = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    serialized = JsonConvert.SerializeObject(new
                    {
                        Kind = span.Kind, StartedUtc = span.StartedUtc,
                        TransactionId = span.TransactionId, BatchId = span.BatchId,
                        AttemptId = span.AttemptId, Source = span.Source,
                        Destination = span.Destination, Role = span.Role,
                        ManifestCount = span.ManifestCount, Outcome = span.Outcome,
                        Error = span.Error, TotalMs = span.TotalMs, Truncated = trimmed,
                        Counters = counters, WaitTotals = waitTotals, Stages = stages
                    });
                    if (serialized.Length <= MaxPayloadCharacters) break;
                    trimmed = true;
                    // Drop the least diagnostic detail first, then halve the stage
                    // list from the middle, keeping the beginning and the end.
                    if (attempt == 0)
                        foreach (var stage in stages) { stage.Detail = null; stage.Occurrence = null; }
                    else
                        stages = stages.Take(stages.Count / 4)
                            .Concat(stages.Skip(stages.Count - stages.Count / 4)).ToList();
                }

                ServiceEvents.Report("bank.trace", severity, summary, new
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
                    Truncated = trimmed,
                    PayloadCharacters = serialized == null ? 0 : serialized.Length,
                    Counters = counters,
                    WaitTotals = waitTotals,
                    Stages = stages
                });
            }
            catch (Exception) { }
        }

        // Groups wait stages by condition. BlindSpotMs is the total time in which a
        // condition may already have been satisfied with nobody looking - the part of
        // a wait that is our polling cadence rather than external latency.
        private static Dictionary<string, WaitTotal> WaitTotals(List<Stage> stages)
        {
            var totals = new Dictionary<string, WaitTotal>(StringComparer.Ordinal);
            foreach (Stage stage in stages)
            {
                if (stage.Name == null || !stage.Name.StartsWith("wait:", StringComparison.Ordinal))
                    continue;
                string condition = stage.Name.Substring(5);
                WaitTotal total;
                if (!totals.TryGetValue(condition, out total))
                    totals[condition] = total = new WaitTotal();
                total.TotalMs = Round(total.TotalMs + stage.WaitedMs);
                total.Episodes++;
                total.Observations += stage.Observations;
                if (stage.WaitedMs > total.MaxEpisodeMs) total.MaxEpisodeMs = stage.WaitedMs;
                if (stage.MaxLookGapMs > total.MaxLookGapMs) total.MaxLookGapMs = stage.MaxLookGapMs;
                total.BlindSpotMs = Round(total.BlindSpotMs + Math.Max(0, stage.AtMs - stage.LastLookMs));
            }
            return totals.OrderByDescending(p => p.Value.TotalMs)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
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
