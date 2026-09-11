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

        public static HashSet<string> GetReadyCharacters(string directory)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string data = RuntimeStateStore.GetDataDirectory(directory);
                string path = Path.Combine(data, AllBankersReadyMarkerFileName);
                if (!File.Exists(path)) return available;
                var ready = RuntimeStateStore.ReadJsonStrict<JObject>(path);
                if (ready == null) return available;
                if ((string)ready["generation"] != HostGeneration) return available;
                string root = Path.Combine(data, "startup-census", HostGeneration);
                var cycle = RuntimeStateStore.ReadJsonStrict<JObject>(Path.Combine(root, "cycle.json"));
                if (cycle == null) return available;
                if ((string)cycle["Phase"] != "released" || (string)cycle["Id"] != (string)ready["cycle"] ||
                    Directory.EnumerateFiles(root, "*.recovery.json").Any()) return available;
                var members = ready["characters"] as JArray;
                var central = members?.SingleOrDefault(r => string.Equals((string)r["role"], "central", StringComparison.OrdinalIgnoreCase));
                if (!ReadyConnection(root, (string)ready["cycle"], central)) return available;
                foreach (var member in members)
                    if (ReadyConnection(root, (string)ready["cycle"], member)) available.Add((string)member["character"]);
            }
            catch (IOException) { available.Clear(); }
            catch (UnauthorizedAccessException) { available.Clear(); }
            catch (JsonException) { available.Clear(); }
            catch (InvalidOperationException) { available.Clear(); }
            return available;
        }

        private static bool ReadyConnection(string root, string cycle, JToken member)
        {
            string character = (string)member?["character"], connection = (string)member?["connection"];
            if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(connection)) return false;
            character = character.ToLowerInvariant();
            if (!File.Exists(Path.Combine(root, character + ".ready")) ||
                !File.Exists(Path.Combine(root, character + ".presence.json")) ||
                File.Exists(Path.Combine(root, character + ".blocked")) ||
                RuntimeStateStore.ReadTextStrict(Path.Combine(root, character + ".ready")) != cycle + "/" + connection) return false;
            var presence = RuntimeStateStore.ReadJsonStrict<JObject>(Path.Combine(root, character + ".presence.json"));
            if (presence == null) return false;
            long age = Stopwatch.GetTimestamp() - ((long?)presence["Stamp"] ?? long.MaxValue);
            return (string)presence["Connection"] == connection && age >= 0 && age < Stopwatch.Frequency * 10;
        }

    }
}
