using File = CityDwellers.Shared.SqlFile;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
    // Linked into the host, manager and banker projects. Readiness has no
    // dependency on withdrawal transactions or trusted-operator policy.
    public static class BankerReadiness
    {
        public const string AllBankersReadyMarkerFileName = "citybankers-all-bankers-ready.json";
        private static readonly string HostGeneration = Process.GetCurrentProcess().Id + "-" +
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

        public static bool IsReadyForRequests(string directory) => IsCharacterReady(directory, null);

        // Durable stock from an offline connection remains history, not withdrawable
        // inventory. Elapsed ticks share the host's monotonic clock across domains.
        public static bool IsCharacterReady(string directory, string character)
        {
            var ready = GetReadyCharacters(directory);
            return character == null ? ready.Count > 0 : ready.Contains(character);
        }

        public sealed class ReadinessSnapshot
        {
            public HashSet<string> Ready { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Reasons { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string GlobalReason { get; internal set; } = "not admitted";
            public string ReasonFor(string character)
            {
                string reason;
                return Reasons.TryGetValue(character, out reason) ? reason : GlobalReason;
            }
        }

        public static HashSet<string> GetReadyCharacters(string directory) => Inspect(directory).Ready;

        public static ReadinessSnapshot Inspect(string directory)
        {
            var result = new ReadinessSnapshot();
            try
            {
                string data = RuntimeStateStore.GetDataDirectory(directory);
                string marker = Path.Combine(data, AllBankersReadyMarkerFileName);
                string root = Path.Combine(data, "startup-census", HostGeneration);
                string cyclePath = Path.Combine(root, "cycle.json");
                var seed = File.ReadSmallDocuments(new[] { marker });
                var initial = Json(seed, marker);
                var initialMembers = initial?["characters"] as JArray;
                if (initialMembers == null) return result;
                var paths = new List<string> { marker, cyclePath };
                foreach (var member in initialMembers)
                {
                    string character = (string)member["character"];
                    if (string.IsNullOrWhiteSpace(character)) continue;
                    foreach (string suffix in new[] { ".ready", ".presence.json", ".blocked", ".operational.json" })
                        paths.Add(Path.Combine(root, character.ToLowerInvariant() + suffix));
                }
                // Include the roster again: never combine an old connection roster
                // with independently read heartbeat/hold records. No writer lock.
                var snapshot = File.ReadSmallDocuments(paths);
                var ready = Json(snapshot, marker);
                var cycle = Json(snapshot, cyclePath);
                if (!JToken.DeepEquals(initial, ready)) { result.GlobalReason = "admission changed; retrying"; return result; }
                if ((string)ready?["generation"] != HostGeneration) { result.GlobalReason = "previous host generation"; return result; }
                if ((string)cycle?["Phase"] != "released" || (string)cycle?["Id"] != (string)ready["cycle"])
                { result.GlobalReason = "startup census not released"; return result; }
                var members = ready["characters"] as JArray;
                long now = Stopwatch.GetTimestamp();
                foreach (var member in members)
                {
                    string character = (string)member["character"];
                    if (string.IsNullOrWhiteSpace(character)) continue;
                    string reason = ConnectionReason(snapshot, root, (string)ready["cycle"], member, now);
                    if (reason == null) result.Ready.Add(character);
                    else result.Reasons[character] = reason;
                }
                var central = members.SingleOrDefault(r => string.Equals((string)r["role"], "central", StringComparison.OrdinalIgnoreCase));
                string centralName = (string)central?["character"];
                if (centralName == null || !result.Ready.Contains(centralName))
                {
                    result.GlobalReason = "Central unavailable: " + (centralName == null ? "not admitted" : result.ReasonFor(centralName));
                    foreach (string character in result.Ready) result.Reasons[character] = result.GlobalReason;
                    result.Ready.Clear();
                }
            }
            catch (IOException) { result.Ready.Clear(); result.GlobalReason = "readiness document unreadable"; }
            catch (UnauthorizedAccessException) { result.Ready.Clear(); result.GlobalReason = "readiness access denied"; }
            catch (JsonException) { result.Ready.Clear(); result.GlobalReason = "invalid readiness JSON"; }
            catch (InvalidOperationException) { result.Ready.Clear(); result.GlobalReason = "invalid readiness roster"; }
            return result;
        }

        private static string Text(Dictionary<string, byte[]> snapshot, string path)
        {
            byte[] bytes;
            if (!snapshot.TryGetValue(path, out bytes)) return null;
            if (bytes == null) throw new InvalidDataException("Readiness document exceeds snapshot limit.");
            using (var reader = new StreamReader(new MemoryStream(bytes))) return reader.ReadToEnd();
        }
        private static JObject Json(Dictionary<string, byte[]> snapshot, string path)
        {
            string text = Text(snapshot, path);
            return text == null ? null : JObject.Parse(text);
        }
        private static string AgeReason(JObject record, string kind, long now)
        {
            long stamp = (long?)record["Stamp"] ?? 0;
            if (stamp <= 0 || stamp > now) return kind + " timestamp invalid";
            double seconds = (now - stamp) / (double)Stopwatch.Frequency;
            return seconds >= 10 ? kind + " stale (" + seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s)" : null;
        }
        private static string ConnectionReason(Dictionary<string, byte[]> snapshot, string root, string cycle, JToken member, long now)
        {
            string character = (string)member?["character"], connection = (string)member?["connection"];
            if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(connection)) return "connection not admitted";
            string prefix = Path.Combine(root, character.ToLowerInvariant());
            if (snapshot.ContainsKey(prefix + ".blocked")) return "local recovery hold";
            if (Text(snapshot, prefix + ".ready") != cycle + "/" + connection) return "ready token missing or connection changed";
            var operational = Json(snapshot, prefix + ".operational.json");
            if (operational == null) return "operational heartbeat missing";
            if ((string)operational["Cycle"] != cycle || (string)operational["Connection"] != connection) return "operational connection mismatch";
            string reason = AgeReason(operational, "operational heartbeat", now);
            if (reason != null) return reason;
            var presence = Json(snapshot, prefix + ".presence.json");
            if (presence == null) return "presence missing";
            if ((string)presence["Connection"] != connection) return "presence connection mismatch";
            return AgeReason(presence, "presence", now);
        }
    }
}
