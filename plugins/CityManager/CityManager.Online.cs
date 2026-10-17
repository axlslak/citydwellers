using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        // Shared with Bobsan presence under _altsSync. Never persisted as live state.
        private readonly Dictionary<uint, string> _observedOnlineGuests =
            new Dictionary<uint, string>();
        private bool _orgOnlineSnapshotReceived;

        private sealed class OnlineRow
        {
            internal string Name, Main;
            internal bool Org, Guest;
        }

        private void ObserveGuestOnline(uint characterId, string name)
        {
            string normalized;
            string error;
            if (characterId == 0 ||
                string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                !TryNormalizeAltName(name, out normalized, out error))
                return;

            lock (_altsSync)
                _observedOnlineGuests[characterId] = normalized;
        }

        private void ProcessOnlineCommand(string[] parts, ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "online"));
                return;
            }

            OnlineRow[] rows;
            bool snapshotReceived;
            lock (_altsSync)
            {
                var guests = new HashSet<string>(_observedOnlineGuests.Values, StringComparer.OrdinalIgnoreCase);
                rows = _onlineCharacters.Concat(guests).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(name => new OnlineRow { Name = name, Main = ResolveCanonicalAltMainLocked(name),
                        Org = _onlineCharacters.Contains(name), Guest = guests.Contains(name) }).ToArray();
                snapshotReceived = _orgOnlineSnapshotReceived;
            }

            var wanted = new HashSet<string>(rows.Select(row => row.Name), StringComparer.OrdinalIgnoreCase);
            var nearby = ManagerMemory.Current.ReadNearbyPlayers().Where(p => wanted.Contains(p.Name))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(p => p.ObservedUtc).First()).ToArray();
            ManagerMemory.Current.RememberPlayerProfiles(nearby.Select(p => new PlayerProfile {
                Name = p.Name, Level = p.Level, Profession = p.Profession, UpdatedUtc = p.ObservedUtc }).ToArray());
            var profiles = ManagerMemory.Current.ReadPlayerProfiles(wanted.ToArray())
                .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            // In-game eyes override cached display data, including a newer but
            // stale external roster. Optional/missing fields do not hide anyone.
            foreach (var player in nearby)
            {
                PlayerProfile profile;
                if (!profiles.TryGetValue(player.Name, out profile)) profile = new PlayerProfile { Name = player.Name };
                if (player.Level > 0 && player.Level <= 220) profile.Level = player.Level;
                if (player.Profession > 0 && player.Profession <= 15 && player.Profession != 13) profile.Profession = player.Profession;
                profiles[player.Name] = profile;
            }
            var groups = rows.GroupBy(row => row.Main, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            var body = new StringBuilder();
            body.Append(HelpHeader("Online", groups.Length + " players · " + rows.Length + " characters"));
            body.Append(OnlineColor("Org: " + rows.Count(r => r.Org), ColorGood)).Append("   ")
                .Append(OnlineColor("Guest: " + rows.Count(r => r.Guest), ColorCommand));
            int overlap = rows.Count(r => r.Org && r.Guest);
            if (overlap != 0) body.Append(OnlineColor("   " + overlap + " in both", ColorMuted));
            body.Append("\n").Append(CommandLink(target, "online", "Refresh"))
                .Append("   ").Append(CommandLink(target, "help online", "About this list")).Append("\n");
            if (!snapshotReceived)
                body.Append(OnlineColor("Organization list is still loading; counts may be incomplete.", ColorWarn)).Append("\n");
            if (rows.Length == 0) body.Append("\n").Append(OnlineColor("Nobody known online yet.", ColorMuted)).Append("\n");
            foreach (var group in groups)
            {
                body.Append("\n<img src=tdb://id:GFX_GUI_FRIENDLIST_SPLITTER>\n<b>")
                    .Append(OnlineColor(group.Key, ColorTitle)).Append("</b> ")
                    .Append(OnlineColor("· " + group.Count() + (group.Count() == 1 ? " character" : " characters"), ColorMuted))
                    .Append("   ").Append(CommandLink(target, "alts " + group.Key, "Alts")).Append("\n");
                foreach (var row in group.OrderBy(r => string.Equals(r.Name, group.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    PlayerProfile profile;
                    profiles.TryGetValue(row.Name, out profile);
                    AppendOnlineCharacter(body, row, profile);
                }
            }
            body.Append("\n").Append(OnlineColor("Org: last reported online. Guest: observed speaking; silent departures may be missing.\nUnknown character details are shown as ?.", ColorMuted));
            string label = "Online · " + groups.Length + " players / " + rows.Length + " characters";
            Reply(target, BuildBlobLinks(target, "Online", label, body.ToString()));
        }

        private static void AppendOnlineCharacter(StringBuilder body, OnlineRow row, PlayerProfile profile)
        {
            int profession = profile?.Profession ?? 0;
            bool knownProfession = profession > 0 && profession < OnlineProfessionNames.Length && OnlineProfessionNames[profession].Length != 0;
            body.Append("<tab>");
            if (knownProfession) body.Append("<img src=tdb://id:GFX_GUI_ICON_PROFESSION_").Append(profession).Append("> ");
            else body.Append(OnlineColor("? ", ColorMuted));
            body.Append("<a href='user://").Append(EscapeBlobText(row.Name)).Append("'>")
                .Append(OnlineColor(row.Name, ColorCommand)).Append("</a>  ")
                .Append(OnlineColor(profile != null && profile.Level > 0 ? profile.Level.ToString() : "?", ColorText));
            if (profile != null && profile.AlienLevel >= 0) body.Append("/").Append(OnlineColor(profile.AlienLevel.ToString(), ColorGood));
            body.Append("  ").Append(OnlineColor(knownProfession ? OnlineProfessionNames[profession] : "?", knownProfession ? ColorTitle : ColorMuted))
                .Append("  ").Append(OnlineColor(row.Org && row.Guest ? "[Org + Guest]" : row.Org ? "[Org]" : "[Guest]", ColorMuted)).Append("\n");
        }

        private static string OnlineColor(string text, string color) =>
            "<font color='" + color + "'>" + EscapeBlobText(text) + "</font>";
    }
}
