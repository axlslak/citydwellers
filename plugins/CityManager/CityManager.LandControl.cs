using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessLandControlCommand(string[] parts, ReplyTarget target)
        {
            var arguments = parts.Skip(1).ToList();
            int page = 1;
            if (arguments.Count >= 2 && string.Equals(arguments[arguments.Count - 2], "page", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(arguments.Last(), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1)
                { Reply(target, Usage(target, "lc [last|attacks|victories|lost|damage|destroyed|ours|org <name>] [page <number>]")); return; }
                arguments.RemoveRange(arguments.Count - 2, 2);
            }
            string action = arguments.Count == 0 ? "last" : arguments[0].ToLowerInvariant();
            string kind = null, organization = null;
            if (action == "org" && arguments.Count > 1)
                organization = string.Join(" ", arguments.Skip(1));
            else if (arguments.Count <= 1)
                switch (action)
                {
                    case "last": break;
                    case "attacks": kind = "attack"; break;
                    case "victories": kind = "victory"; break;
                    case "lost": kind = "base-lost"; break;
                    case "damage": kind = "tower-damage"; break;
                    case "destroyed": kind = "tower-destroyed"; break;
                    case "ours":
                        organization = string.IsNullOrWhiteSpace(Client.OrgName) ? OrgChannelName : Client.OrgName;
                        if (string.IsNullOrWhiteSpace(organization))
                        { Reply(target, "Manager's organization is not known yet."); return; }
                        break;
                    default: action = null; break;
                }
            else action = null;
            if (action == null || action == "org" && organization == null)
            { Reply(target, Usage(target, "lc [last|attacks|victories|lost|damage|destroyed|ours|org <name>] [page <number>]")); return; }
            string command = organization == null ? "lc " + action : action == "ours" ? "lc ours" : "lc org " + organization;
            int requestedPage = page;
            QueuePublicWork(target, () => ShowLandControl(target, kind, organization, command, requestedPage));
        }

        private void ShowLandControl(ReplyTarget target, string kind, string organization, string command, int page)
        {
            var history = ManagerMemory.Current.ReadLandControl(kind, organization, page);
            var now = DateTime.UtcNow;
            var body = new StringBuilder();
            body.Append(CommandLink(target, "lc", "Latest")).Append(" | ")
                .Append(CommandLink(target, "lc attacks", "Attacks")).Append(" | ")
                .Append(CommandLink(target, "lc victories", "Victories")).Append(" | ")
                .Append(CommandLink(target, "lc lost", "Base losses")).Append(" | ")
                .Append(CommandLink(target, "lc ours", "Our organization")).Append("\n\n");
            if (organization != null)
                body.Append(WorldBossText(organization, ColorTitle)).Append("\n");
            body.Append(history.Total).Append(" observed announcement(s), saved across restarts.\n");
            if (history.FirstUtc.HasValue)
                body.Append(WorldBossText("First matching observation: " + history.FirstUtc.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), ColorMuted)).Append("\n");
            body.Append("\n");
            var summary = new List<string>();
            foreach (var row in history.Recent)
            {
                string age = row.ObservedUtc <= now ? FormatDuration(now - row.ObservedUtc) + " ago" : "timestamp ahead of current clock";
                string title = LandControlLabel(row.Kind);
                body.Append(WorldBossText(title, ColorTitle)).Append(" — ")
                    .Append(WorldBossText(age, ColorCommand)).Append("\n")
                    .Append(WorldBossText(row.Message, ColorText)).Append("\n")
                    .Append(WorldBossText(row.ObservedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), ColorMuted)).Append("\n");
                if (row.X.HasValue && row.Y.HasValue)
                    body.Append(WorldBossText("Reported position: " + row.X + ", " + row.Y, ColorMuted)).Append("\n");
                if (summary.Count < 3)
                {
                    string where = row.Playfield == null ? "" : " in " + row.Playfield;
                    string who = row.DefenderOrg ?? row.AttackerOrg ?? row.AttackerSide ?? "";
                    if (row.Kind == "attack" || row.Kind == "victory")
                        who = (row.AttackerOrg ?? row.Attacker ?? "Unknown attacker") + " → " + (row.DefenderOrg ?? "Unknown defender");
                    summary.Add(WorldBossText(title, ColorTitle) + ": " + WorldBossText(who + where, ColorText) +
                        " — " + WorldBossText(age, ColorCommand));
                }
                body.Append("\n");
            }
            if (history.Total == 0)
            {
                body.Append("No matching tower announcements have been observed yet.\n\n");
                summary.Add(WorldBossText("Land control: no matching announcements observed yet.", ColorMuted));
            }
            body.Append("Page ").Append(history.Page).Append('/').Append(history.Pages);
            if (history.Page > 1) body.Append(" | ").Append(CommandLink(target, command + " page " + (history.Page - 1), "Newer"));
            if (history.Page < history.Pages) body.Append(" | ").Append(CommandLink(target, command + " page " + (history.Page + 1), "Older"));
            body.Append("\n\n").Append(WorldBossText(
                "Times show when the announcement was received. A victory or base-loss notice may identify only an organization and playfield, not a site number. A destroyed personal tower does not prove its Control Tower fell. Current ownership, gas and planting availability are not established by these notices alone.", ColorMuted))
                .Append("\n").Append(CommandLink(target, command, "Refresh"));
            string details = string.Join(" ", BuildBlobLinks(target, "Land control", "Tower details", body.ToString()));
            Reply(target, string.Join("\n", summary) + "\n" + details);
        }

        private static string LandControlLabel(string kind)
        {
            switch (kind)
            {
                case "attack": return "Attack";
                case "victory": return "Victory";
                case "base-lost": return "Base lost";
                case "tower-damage": return "Tower damaged";
                case "tower-destroyed": return "Tower destroyed";
                case "faction-victory": return "Faction victory announcement";
                default: return kind;
            }
        }
    }
}
