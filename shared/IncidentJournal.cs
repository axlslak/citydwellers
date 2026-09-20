using File = CityDwellers.Shared.SqlFile;
using Directory = CityDwellers.Shared.SqlDirectory;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    // SQL transaction evidence. Only Manager renders
    // user-facing dumps. Never let observational I/O retry a physical operation.
    public static class IncidentJournal
    {
        public sealed class Entry
        {
            public string Id, Trace, Actor, Event;
            public DateTime Utc;
            public bool Problem;
            public string[] Links;
            public JToken Detail;
        }
        public sealed class Summary
        {
            public string Id, Trace;
            public DateTime UpdatedUtc;
        }
        private static string Root(string data) => Path.Combine(data, "transaction-traces");
        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, string> StateCache = new Dictionary<string, string>();
        public static bool IsProblem(string text)
        {
            text = (text ?? "").ToLowerInvariant();
            return new[] { "fail", "error", "declin", "reject", "timeout", "timed out", "reconcil", "mismatch", "cancel" }.Any(text.Contains);
        }
        public static void ObserveWrite(string path, object value)
        {
            try
            {
                string name = Path.GetFileName(path), data = Path.GetDirectoryName(path);
                if (name != "withdrawal.json" && name != "dispatch-queue.json" && name != "lost.json") return;
                var root = JObject.FromObject(value);
                string property = name == "withdrawal.json" ? "Withdrawals" : name == "lost.json" ? "Entries" : "Batches";
                foreach (var row in (root[property] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string trace = name == "lost.json" ? "lost:" + (string)row["IncidentId"] :
                        name == "withdrawal.json" ? (string)row["Id"] : (string)row["TransactionId"] ?? (string)row["BatchId"];
                    var links = new List<string> { (string)row["DonationTransactionId"], (string)row["BatchId"],
                        (string)row["ReturnBatchId"] };
                    string ledger = (string)row["ActiveLedgerId"] ?? (string)row["LedgerId"];
                    if (ledger != null) links.Add("ledger:" + ledger);
                    if (name == "lost.json")
                    {
                        links.Add((string)row["PreviousLedgerEntry"]?["TransactionId"]);
                        links.Add("recovery:" + (string)row["Evidence"]);
                    }
                    string status = (string)row["Status"];
                    bool problem = name == "lost.json" || IsProblem(status) ||
                        !string.IsNullOrWhiteSpace((string)row["Error"] ?? (string)row["LastError"]);
                    Record(data, trace, "persisted-state", name + ":" + (status ?? "updated"), row, problem, links, true);
                    if (name == "dispatch-queue.json" && !string.IsNullOrWhiteSpace((string)row["BatchId"]))
                        Record(data, (string)row["BatchId"], "persisted-state", "dispatch.link", new { Transaction = trace }, false, new[] { trace }, true);
                    // Reverse item link lets a later lost/found entry locate its
                    // earlier retrieval. It does not assert that retrieval caused it.
                    if (ledger != null && name == "withdrawal.json")
                        Record(data, "ledger:" + ledger, "persisted-state", "retrieval.link", new { Request = trace }, false, new[] { trace }, true);
                }
            }
            catch { /* Evidence must not change a committed state transition. */ }
        }
        public static string Id(string trace)
        {
            using (var hash = SHA256.Create())
                return "incident-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(trace))).Replace("-", "").ToLowerInvariant();
        }
        private static void Locked(string key, Action action)
        {
            SqlStore.WithLock("incident-evidence", action);
        }

        public static void Record(string data, string trace, string actor, string stage, object detail,
            bool problem = false, IEnumerable<string> links = null, bool state = false)
        {
            if (string.IsNullOrWhiteSpace(trace)) return;
            try
            {
                string root = Root(data), key = Id(trace);
                Directory.CreateDirectory(root);
                var entry = new Entry { Id = Guid.NewGuid().ToString("N"), Trace = trace, Actor = actor,
                    Event = stage, Utc = DateTime.UtcNow, Problem = problem,
                    Links = (links ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x) && x != trace).Distinct().ToArray(),
                    Detail = detail == null ? JValue.CreateNull() : JToken.FromObject(detail) };
                string fingerprint = JsonConvert.SerializeObject(new { actor, stage, entry.Detail, problem, entry.Links });
                string cacheKey = root + "/" + key + "/" + stage;
                if (state) lock (CacheSync)
                {
                    string cached;
                    if (StateCache.TryGetValue(cacheKey, out cached) && cached == fingerprint) return;
                }
                Locked(root + key, () =>
                {
                    string statePath = Path.Combine(root, key + "." + Id(stage) + ".state");
                    if (!state || !File.Exists(statePath) || File.ReadAllText(statePath) != fingerprint)
                    {
                        File.AppendAllText(Path.Combine(root, key + ".jsonl"), JsonConvert.SerializeObject(entry) + "\n", new UTF8Encoding(false));
                        if (problem) File.WriteAllText(Path.Combine(root, key + ".problem"), trace, new UTF8Encoding(false));
                        if (state) File.WriteAllText(statePath, fingerprint, new UTF8Encoding(false));
                    }
                    if (state) lock (CacheSync)
                    {
                        if (StateCache.Count >= 512) StateCache.Clear();
                        StateCache[cacheKey] = fingerprint;
                    }
                });
            }
            catch (Exception ex)
            {
                // A visible gap marker is preferable to silently claiming complete evidence.
                try { Directory.CreateDirectory(Root(data)); File.WriteAllText(Path.Combine(Root(data), "evidence-warning.txt"),
                    DateTime.UtcNow.ToString("O") + " Evidence could not be recorded; trace=" + trace + "; stage=" + stage + "; type=" + ex.GetType().Name); } catch { }
            }
        }
        public static List<Summary> Recent(string data, int count = 20)
        {
            string root = Root(data);
            if (!Directory.Exists(root)) return new List<Summary>();
            return Directory.EnumerateFiles(root, "*.problem").Select(p => new Summary {
                Id = Path.GetFileNameWithoutExtension(p), Trace = File.ReadAllText(p),
                UpdatedUtc = File.GetLastWriteTimeUtc(Path.ChangeExtension(p, ".jsonl")) })
                .OrderByDescending(x => x.UpdatedUtc).Take(count).ToList();
        }
        public static string Export(string data, string id, bool onlyIfChanged = false)
        {
            if (id == null || !id.StartsWith("incident-", StringComparison.Ordinal) || id.Length != 73 ||
                id.Substring(9).Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Use an incident ID from dump incidents.");
            string root = Root(data), path = Path.Combine(root, id + ".jsonl");
            if (!File.Exists(path)) throw new FileNotFoundException("Incident evidence not found.");
            var pending = new Queue<string>(); pending.Enqueue(id);
            var visited = new HashSet<string>(); var events = new List<Entry>(); var gaps = new List<string>();
            var versions = new List<string>();
            while (pending.Count > 0 && visited.Count < 64 && events.Count < 5000)
            {
                string next = pending.Dequeue(); if (!visited.Add(next)) continue;
                string source = Path.Combine(root, next + ".jsonl");
                if (!File.Exists(source)) { gaps.Add("No retained earlier evidence for " + next); continue; }
                Locked(root + next, () =>
                {
                    versions.Add(next + "/" + File.GetLastWriteTimeUtc(source).Ticks + "/" + File.GetLength(source));
                    foreach (string line in File.ReadLines(source))
                    {
                        if (events.Count >= 5000) { gaps.Add("Event limit reached; original SQL trace records retained."); break; }
                        try
                        {
                            var e = JsonConvert.DeserializeObject<Entry>(line);
                            if (e == null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Trace) ||
                                string.IsNullOrWhiteSpace(e.Event)) throw new InvalidDataException();
                            events.Add(e);
                            foreach (string link in e.Links ?? new string[0]) pending.Enqueue(Id(link));
                        }
                        catch (Exception) { gaps.Add("Unreadable/partial evidence line in " + next); }
                    }
                });
            }
            if (pending.Count > 0) gaps.Add("Linked trace limit reached; original SQL trace records retained.");
            string directory = Path.Combine(data, "incident-dumps"); Directory.CreateDirectory(directory);
            string output = Path.Combine(directory, id + ".log");
            string signature = string.Join(";", versions.OrderBy(v => v)) + "|" + string.Join(";", gaps.Distinct());
            if (onlyIfChanged && File.Exists(output) && File.Exists(output + ".signature") && File.ReadAllText(output + ".signature") == signature) return output;
            var text = new StringBuilder("City Dwellers transaction incident\n");
            text.AppendLine("ID: " + id).AppendLine("Trace: " + events.FirstOrDefault(e => Id(e.Trace) == id)?.Trace);
            text.AppendLine("Snapshot UTC: " + DateTime.UtcNow.ToString("O"));
            text.AppendLine("Resolution is recorded only by explicit events below. An audit starting is not a resolution.");
            text.AppendLine("If no terminal trade/storage/retrieval or recovery.applied event follows a problem, resolution is unconfirmed (still pending or evidence missing).");
            text.AppendLine("Links identify related evidence, not proof of causation. Unopened historical gaps cannot be reconstructed.");
            foreach (var state in events.Where(e => e.Event.StartsWith("withdrawal.json:") || e.Event.StartsWith("dispatch-queue.json:"))
                .GroupBy(e => e.Trace + "/" + (string)e.Detail?["BatchId"]))
            {
                var last = state.OrderByDescending(e => e.Utc).First();
                text.AppendLine("Latest recorded state: " + state.Key + " = " + (string)last.Detail?["Status"]);
            }
            foreach (string gap in gaps.Distinct()) text.AppendLine("EVIDENCE GAP: " + gap);
            string warning = Path.Combine(root, "evidence-warning.txt");
            if (File.Exists(warning)) text.AppendLine("Recorder warning (may concern another trace): " + File.ReadAllText(warning));
            foreach (var e in events.GroupBy(e => e.Id).Select(g => g.First()).OrderBy(e => e.Utc).ThenBy(e => e.Id))
                text.AppendLine(e.Utc.ToString("O") + " [" + e.Actor + "] " + e.Event + " trace=" + e.Trace +
                    (e.Problem ? " [INCIDENT]" : "") + " " + e.Detail?.ToString(Formatting.None));
            SqlStore.WithLock("incident-dump", () =>
            {
                File.WriteAllText(output, text.ToString(), new UTF8Encoding(false));
                File.WriteAllText(output + ".signature", signature);
            });
            return output;
        }
    }
}
