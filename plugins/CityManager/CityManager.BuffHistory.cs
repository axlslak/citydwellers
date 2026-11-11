using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private readonly HashSet<string> _buffServiceCharacters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _buffBuddyAccounts;

        private void InitializeBuffHistoryReporting()
        {
            // Bootstrap configuration is read once, not polled during queries.
            // Include disabled/offline service characters as well as active ones.
            var root = JObject.Parse(File.ReadAllText(Path.Combine(_settingsDir, "citydwellers.json")));
            foreach (string sectionName in new[] { "Manager", "Buffers", "Bankers", "Buddies", "Raiders", "Flipper" })
            {
                var section = root.GetValue(sectionName, StringComparison.OrdinalIgnoreCase) as JContainer;
                if (section == null) continue;
                foreach (var property in section.Descendants().OfType<JProperty>().Where(p =>
                    p.Name.Equals("Character", StringComparison.OrdinalIgnoreCase) && p.Value.Type == JTokenType.String))
                {
                    string name = ((string)property.Value ?? "").Trim();
                    if (name.Length > 0) _buffServiceCharacters.Add(name);
                }
            }
            var buddies = root.GetValue("Buddies", StringComparison.OrdinalIgnoreCase) as JObject;
            _buffBuddyAccounts = (int?)buddies?.GetValue("AccountCount", StringComparison.OrdinalIgnoreCase) ?? 0;
            if (!string.IsNullOrWhiteSpace(Client.CharacterName)) _buffServiceCharacters.Add(Client.CharacterName);
        }

        private bool IsBuffServiceCharacter(string name, HashSet<string> serviceCharacters)
        {
            if (serviceCharacters.Contains(name ?? "")) return true;
            // BuddiesHost.BuildCharacterName uses Apcr{level:D3}{index:D2}.
            // Match its configured account slots, not a broad "kb" prefix.
            if (_buffBuddyAccounts <= 0 || name == null || name.Length < 9 ||
                !name.StartsWith("Apcr", StringComparison.OrdinalIgnoreCase)) return false;
            int level, index;
            return int.TryParse(name.Substring(4, 3), NumberStyles.None, CultureInfo.InvariantCulture, out level) &&
                int.TryParse(name.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out index) &&
                level >= 1 && level <= 220 && index >= 0 && index < _buffBuddyAccounts &&
                name.Equals("Apcr" + level.ToString("D3", CultureInfo.InvariantCulture) +
                    index.ToString("D2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        private bool TryProcessBuffHistoryCommand(string[] parts, ReplyTarget target)
        {
            if (parts.Length < 2 || parts.Length > 3) return false;
            string action = parts[1].ToLowerInvariant();
            if (new[] { "list", "mine", "add", "cast", "del", "rem", "remove", "redo", "rebuff", "macro" }.Contains(action))
                return false;
            int page = 1;
            if (parts.Length == 3 && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
                return false;
            string person = null;
            if (action != "last" && action != "top")
            {
                string error;
                if (!TryNormalizeMemberName(parts[1], out person, out error)) return false;
            }
            string recipient = person;
            int requestedPage = page;
            QueuePublicWork(target, () => ShowBuffHistory(target, action, recipient, requestedPage));
            return true;
        }

        private string BuffActivityRecipientName(int id)
        {
            var name = ManagerMemory.Current.ReadLearnedBuffProfile(id)?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = ManagerMemory.Current.ReadNearbyPlayers().FirstOrDefault(p => p.CharacterId == id)?.Name;
            try
            {
                if (string.IsNullOrWhiteSpace(name) && Client.Chat != null)
                    name = Client.Chat.NameToIdMap.FirstOrDefault(p => p.Value == unchecked((uint)id)).Key;
            }
            catch { /* Reporting tolerates a changing chat-name cache. */ }
            return string.IsNullOrWhiteSpace(name) ? "an unresolved player" : name;
        }

        private void ShowBuffHistory(ReplyTarget target, string action, string person, int page)
        {
            var entries = ManagerMemory.Current.ReadBuffHistoryEntries();
            var bots = new HashSet<string>(_buffServiceCharacters, StringComparer.OrdinalIgnoreCase);
            foreach (var row in entries)
                if (!string.IsNullOrWhiteSpace(row.Caster)) bots.Add(row.Caster);
            foreach (var bot in ManagerMemory.Current.BufferBotInfos()) bots.Add(bot.Character);
            foreach (var bot in ManagerMemory.Current.ReadPaidBuffers()) bots.Add(bot.Character);

            // Freeze the current known main/alt mapping for this query.
            Dictionary<string, string> mains;
            lock (_altsSync)
                mains = new Dictionary<string, string>(_altToMain, StringComparer.OrdinalIgnoreCase);
            Func<string, string> mainOf = name => {
                string main;
                return mains.TryGetValue(name ?? "", out main) ? main : (name ?? "").Trim();
            };
            string canonical = person == null ? null : mainOf(person);
            var filtered = entries.Where(e => canonical != null
                ? string.Equals(mainOf(e.Recipient), canonical, StringComparison.OrdinalIgnoreCase)
                : !IsBuffServiceCharacter(e.Recipient, bots)).ToList();
            var members = filtered.GroupBy(e => mainOf(e.Recipient), StringComparer.OrdinalIgnoreCase).ToList();
            string title = canonical != null ? "Buffs for " + canonical
                : action == "top" ? "Top Buff Recipients" : "Recent Buff Recipients";
            var body = new StringBuilder();
            List<string> memberPages = null;
            body.Append(CommandLink(target, "buff last", "Last")).Append(" | ")
                .Append(CommandLink(target, "buff top", "Top")).Append(" | ")
                .Append(CommandLink(target, "buff list", "Buff catalogue")).Append("\n\n");
            body.Append(filtered.Count).Append(" saved completed buffs for ").Append(members.Count)
                .Append(" mains. Counts cover all saved history. Times UTC.\n")
                .Append(canonical == null ? "Our service toons are excluded. " : "")
                .Append("Known alts are grouped under their main.\n")
                .Append("Team casts count once for the requesting character.\n\n");

            if (canonical == null)
            {
                var ordered = action == "top"
                    ? members.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    : members.OrderByDescending(g => g.Max(e => e.CompletedUtc)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
                if (action == "top")
                {
                    int rank = 0;
                    foreach (var group in ordered)
                    {
                        body.Append("#").Append(++rank).Append("  ");
                        AppendBuffMember(body, target, group.Key, group.ToArray());
                    }
                }
                else
                {
                    const int membersPerPage = 10;
                    int pages = Math.Max(1, (int)((members.Count + (long)membersPerPage - 1) / membersPerPage));
                    var sorted = ordered.ToArray();
                    memberPages = new List<string>();
                    for (int memberPage = 0; memberPage < pages; memberPage++)
                    {
                        var section = new StringBuilder(body.ToString());
                        section.Append("Ten distinct mains per member page, newest served first.\n")
                            .Append("Member page ").Append(memberPage + 1).Append('/').Append(pages).Append("\n\n");
                        foreach (var group in sorted.Skip(memberPage * membersPerPage).Take(membersPerPage))
                            AppendBuffMember(section, target, group.Key, group.ToArray());
                        if (sorted.Length == 0) section.Append("No matching saved completed buffs.\n");
                        memberPages.Add(section.ToString());
                    }
                }
            }
            else
            {
                var personRows = filtered.OrderByDescending(e => e.CompletedUtc)
                    .ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
                AppendBuffMember(body, target, canonical, personRows);
                body.Append("\n<font color='").Append(ColorTitle).Append("'>Completed deliveries</font>\n");
                foreach (var row in personRows)
                    body.Append("<font color='").Append(ColorMuted).Append("'>")
                        .Append(BuffHistoryTime(row.CompletedUtc)).Append("</font>  ")
                        .Append(BuffHistoryLabel(row.Caster)).Append(" buffed ")
                        .Append(BuffHistoryLabel(row.Recipient)).Append(" with ")
                        .Append(BuffHistoryLabel(row.NanoName))
                        .Append(row.Paid ? " [paid]\n" : " [froob]\n");
            }
            if (filtered.Count == 0) body.Append("No matching saved completed buffs.\n");
            body.Append("\nClick a main for their alts and complete delivery history. Next window pages arrive by tell.\n")
                .Append("Casts from before saved history began are not included.");
            if (memberPages != null)
            {
                // Keep one stable snapshot across clicks as new customers arrive.
                // A large ten-main section may need more than one byte-sized window.
                const string footer = "\nClick a main for complete delivery history. Next page continues by tell.\n" +
                    "Casts from before saved history began are not included.";
                ReplyHistoryPage(target, title, memberPages[0], page, memberPages.Select(section => section + footer));
            }
            else ReplyHistoryPage(target, title, body.ToString(), page);
        }

        private void AppendBuffMember(StringBuilder body, ReplyTarget target, string main, BuffHistoryEntry[] rows)
        {
            body.Append(CommandLink(target, "buff " + main, main)).Append(" — ")
                .Append(rows.Length).Append(" buffs");
            if (rows.Length > 0)
                body.Append("; last ").Append(BuffHistoryTime(rows.Max(e => e.CompletedUtc)));
            body.Append("\n");
            foreach (var alt in rows.GroupBy(e => e.Recipient, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                body.Append("    → ").Append(BuffHistoryLabel(alt.Key)).Append(": ")
                    .Append(alt.Count()).Append(" buffs\n");
            body.Append("\n");
        }

        private static string BuffHistoryTime(DateTime value)
            => value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        private string BuffHistoryLabel(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            return EscapeBlobText(value.Length > 160 ? value.Substring(0, 160) + "…" : value);
        }
    }
}
