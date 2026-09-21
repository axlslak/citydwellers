using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CityDwellers.Shared;

namespace CityBankers.Shared
{
    public static class BankerReadiness
    {
        public const string AllBankersReadyMarkerFileName = "citybankers-all-bankers-ready.json";
        public static bool IsReadyForRequests(string directory) => IsCharacterReady(directory, null);
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
            { string reason; return Reasons.TryGetValue(character, out reason) ? reason : GlobalReason; }
        }
        public static HashSet<string> GetReadyCharacters(string directory) => Inspect(directory).Ready;
        public static ReadinessSnapshot Inspect(string directory)
        {
            var result = new ReadinessSnapshot();
            var snapshot = ManagerMemory.Current.BankerAvailability();
            var cycle = snapshot.Cycle;
            if (cycle?.Phase != "released" || cycle.Participants == null)
            { result.GlobalReason = "startup census not released"; return result; }
            long now = Stopwatch.GetTimestamp();
            foreach (var banker in snapshot.Bankers)
            {
                string connection;
                string reason = !cycle.Participants.TryGetValue(banker.Character, out connection) ? "connection not admitted" :
                    banker.Hold != null ? "local recovery hold: " + banker.Hold :
                    banker.ReadyCycle != cycle.Id || banker.ReadyConnection != connection ? "ready token missing or connection changed" :
                    banker.Connection != connection ? "presence connection mismatch" :
                    banker.OperationalCycle != cycle.Id || banker.OperationalConnection != connection ? "operational connection mismatch" :
                    AgeReason(banker.OperationalStamp, "operational heartbeat", now) ?? AgeReason(banker.PresenceStamp, "presence", now);
                if (reason == null) result.Ready.Add(banker.Character);
                else result.Reasons[banker.Character] = reason;
            }
            string central = snapshot.Bankers.FirstOrDefault(b => string.Equals(b.Role, "central", StringComparison.OrdinalIgnoreCase))?.Character;
            if (central == null || !result.Ready.Contains(central))
            {
                result.GlobalReason = "Central unavailable: " + (central == null ? "not admitted" : result.ReasonFor(central));
                foreach (string character in result.Ready) result.Reasons[character] = result.GlobalReason;
                result.Ready.Clear();
            }
            return result;
        }
        private static string AgeReason(long stamp, string kind, long now)
        {
            if (stamp <= 0 || stamp > now) return kind + " timestamp invalid";
            return (now - stamp) / (double)Stopwatch.Frequency >= 10 ? kind + " stale" : null;
        }
    }
}
