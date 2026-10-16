using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private static readonly string[] WorldBossNames = { "Tarasque", "Vizaresh", "The Hollow Reaper" };

        private void HandleWorldBossVicinity(object sender, VicinityMsg message)
        {
            if (message != null && message.SenderId == 0)
                ObserveWorldBossText(message.Message, "AO.SystemVicinity");
        }

        private void ObserveWorldBossText(string rawText, string source)
        {
            string boss, phase, text;
            int immortalSeconds;
            if (!TryParseWorldBossAnnouncement(rawText, out boss, out phase, out immortalSeconds, out text))
                return;
            bool changed = ManagerAccounting.Transaction("World boss announcement", () =>
                ManagerMemory.Current.ObserveWorldBoss(ManagerAccounting.TransactionId, boss, phase,
                    DateTime.UtcNow, immortalSeconds, source, text));
            if (changed)
            {
                Logger.Information($"World boss {boss}: {phase} via {source}: {text}");
                DevTrace("WORLDBOSS " + boss + " " + phase + ": " + text);
            }
        }

        // Called only for server text, never tells, guest messages or player org chat.
        internal static bool TryParseWorldBossAnnouncement(string rawText, out string boss,
            out string phase, out int immortalSeconds, out string text)
        {
            boss = phase = text = null;
            immortalSeconds = 0;
            if (string.IsNullOrWhiteSpace(rawText) || rawText.Length > 4096) return false;
            text = Regex.Replace(Regex.Replace(rawText, "<[^>]*>", ""), @"\s+", " ").Trim();
            if (Regex.IsMatch(text, @"^A dark creature rises from its slumber in Deep Artery Valley\.", RegexOptions.IgnoreCase))
                boss = "The Hollow Reaper";
            else
                foreach (string name in WorldBossNames)
                    if (Regex.IsMatch(text, @"^(?:The )?" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase))
                    { boss = name; break; }
            if (boss == null) return false;

            if (Regex.IsMatch(text, @"\b(?:has been|was|is) (?:killed|defeated|slain)\b|\bhas died\b", RegexOptions.IgnoreCase))
                phase = "killed";
            else if (Regex.IsMatch(text, @"\b(?:is|are) no longer (?:immortal|invulnerable)\b|\b(?:is now|has become) vulnerable\b", RegexOptions.IgnoreCase))
                phase = "vulnerable";
            else
            {
                var duration = Regex.Match(text, @"\b(?:immortal|invulnerable) for (\d+) (minutes?|mins?|seconds?|secs?)\b", RegexOptions.IgnoreCase);
                bool spawned = Regex.IsMatch(text, @"\b(?:has (?:arrived|spawned|appeared|risen)|rises from its slumber)\b", RegexOptions.IgnoreCase);
                if (!duration.Success && !spawned) return false;
                if (duration.Success)
                {
                    int amount;
                    if (!int.TryParse(duration.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount > 3600)
                        return false;
                    immortalSeconds = amount * (duration.Groups[2].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? 60 : 1);
                }
                else
                    immortalSeconds = boss == "Tarasque" ? 1800 : boss == "Vizaresh" ? 420 : 900;
                phase = spawned ? "spawn" : "immortal";
            }
            return true;
        }

        private void ProcessWorldBossCommand(string[] parts, ReplyTarget target)
        {
            string filter = string.Join(" ", parts.Skip(1)).ToLowerInvariant();
            string selected = filter == "" ? null :
                filter == "tara" || filter == "tarasque" ? "Tarasque" :
                filter == "vizaresh" || filter == "viza" || filter == "gauntlet" ? "Vizaresh" :
                filter == "reaper" || filter == "hollow reaper" || filter == "the hollow reaper" ? "The Hollow Reaper" : "";
            if (selected == "") { Reply(target, Usage(target, "wb [tara|vizaresh|reaper]")); return; }
            var records = ManagerMemory.Current.ReadWorldBosses(ManagerAccounting.TransactionId);
            var body = new StringBuilder();
            var summary = new System.Collections.Generic.List<string>();
            var now = DateTime.UtcNow;
            foreach (string boss in WorldBossNames.Where(name => selected == null || name == selected))
            {
                var record = records.SingleOrDefault(row => row.Boss == boss);
                string status;
                if (record == null)
                    status = WorldBossText("not observed yet", ColorMuted);
                else if (record.Phase == "killed")
                {
                    var next = record.KilledUtc.AddHours(boss == "Vizaresh" ? 17 : 9);
                    status = next > now
                        ? WorldBossText("next spawn expected in ", ColorText) + WorldBossText(FormatDuration(next - now), ColorCommand)
                        : WorldBossText("next spawn due; awaiting announcement", ColorWarn);
                }
                else if (record.VulnerableUtc > now)
                    status = WorldBossText("vulnerable in ", ColorWarn) +
                        WorldBossText(FormatDuration(record.VulnerableUtc - now), ColorCommand);
                else
                    status = WorldBossText("immunity ended ", ColorGood) +
                        WorldBossText(FormatDuration(now - record.VulnerableUtc), ColorCommand) +
                        WorldBossText(" ago", ColorText) + WorldBossText("; kill unobserved", ColorMuted);
                string line = WorldBossText(boss, ColorTitle) + ": " + status;
                summary.Add(line);
                body.Append(line).Append("\n");
                string waypoint = boss == "Tarasque" ? "2092 3822 505" : boss == "Vizaresh" ? "299 28 4328" : "1760 2840 595";
                body.Append("<a href='chatcmd:///waypoint ").Append(waypoint).Append("'>")
                    .Append(WorldBossText("Waypoint", ColorCommand)).Append("</a>\n");
                if (record != null)
                    body.Append(WorldBossText("Last announcement: ", ColorMuted))
                        .Append(WorldBossText(record.ObservedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), ColorCommand))
                        .Append("\n").Append(WorldBossText(record.Message, ColorMuted)).Append("\n");
                body.Append("\n");
            }
            body.Append(WorldBossText("Tracks native AO announcements. Saved across restarts. A spawn notice supplies an immunity timer; respawn estimates require an observed kill.", ColorMuted)).Append("\n")
                .Append(CommandLink(target, "wb", "Refresh"));
            string details = string.Join(" ", BuildBlobLinks(target, "World bosses", "World boss details", body.ToString()));
            Reply(target, string.Join("\n", summary) + "\n" + details);
        }

        private static string WorldBossText(string text, string color) =>
            "<font color='" + color + "'>" + EscapeBlobText(text) + "</font>";
    }
}
