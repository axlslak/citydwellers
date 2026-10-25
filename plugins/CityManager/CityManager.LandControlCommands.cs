using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AOSharp.Clientless;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private static readonly HashSet<string> LandControlCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "lc", "attacks", "battles", "battle", "victory", "hot", "scout", "remscout", "needsscout", "towerstats", "towertype", "towerqty", "sites", "plant"
        };
        private const string LandControlEvidenceNote = "Local records only. Ownership and empty status describe the last observation, not a live census. Gas/penalty calculations require a recorded plant or closing offset; they are estimates from that evidence. An empty report does not prove planting is permitted. Missing values remain unknown. No external tower API is used.";

        private void ProcessTowerCommand(string[] parts, ReplyTarget target, string sender, bool mayScout)
        {
            try
            {
                string command = parts[0].ToLowerInvariant();
                switch (command)
                {
                    case "lc": ProcessLandControlCommand(parts, target); break;
                    case "hot": ProcessLandControlCommand(LcHotArguments(parts), target); break;
                    case "sites": ProcessLandControlCommand(new[] { "lc" }.Concat(parts.Length == 1 ? new[] { "all" } : parts[1].Equals("ql", StringComparison.OrdinalIgnoreCase) ? parts.Skip(1) : new[] { "org" }.Concat(parts.Skip(1))).ToArray(), target); break;
                    case "plant": ProcessLandControlCommand(new[] { "lc", "unplanted" }.Concat(parts.Skip(1)).ToArray(), target); break;
                    case "scout": case "remscout": ProcessLandControlScout(parts, target, sender, mayScout); break;
                    case "needsscout": ProcessLandControlCommand(new[] { "lc", "unknown" }.Concat(parts.Length == 1 ? new string[0] : new[] { "--playfield=" + string.Join(" ", parts.Skip(1)) }).ToArray(), target); break;
                    case "towerstats": QueuePublicWork(target, () => ShowTowerStatistics(target, string.Join(" ", parts.Skip(1)))); break;
                    case "towertype": case "towerqty": ShowTowerReference(parts, target); break;
                    default: QueuePublicWork(target, () => ShowLandControlBattles(parts, target)); break;
                }
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is OverflowException)
            { Reply(target, error.Message + " " + CommandLink(target, "help lc", "Tower help")); }
        }

        private static string LcUnquote(string value)
        { value = value.Trim(); return value.Length > 1 && ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')) ? value.Substring(1, value.Length - 2) : value; }

        private static string[] LcHotArguments(string[] parts)
        {
            string positional; var options = LcOptions(string.Join(" ", parts.Skip(1)), out positional);
            var range = Regex.Match(positional, @"(?:^|\s)(?<ql>\d+-\d+)(?=\s|$)");
            if (range.Success)
            { if (options.ContainsKey("ct_ql")) throw new ArgumentException("CT QL supplied twice."); options.Add("ct_ql", range.Groups["ql"].Value); positional = positional.Remove(range.Index, range.Length).Trim(); }
            var aliases = LandControlCatalog.ReadSites().GroupBy(s => s.PlayfieldId).SelectMany(pf =>
                new[] { pf.First().Playfield, pf.First().ShortName, pf.Key.ToString(CultureInfo.InvariantCulture) }
                    .Select(name => new { Name = name, ShortName = pf.First().ShortName })).OrderByDescending(a => a.Name.Length);
            foreach (var alias in aliases)
            {
                var match = Regex.Match(positional, @"(?:^|\s)" + Regex.Escape(alias.Name) + @"(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                if (options.ContainsKey("playfield")) throw new ArgumentException("Playfield supplied twice.");
                options.Add("playfield", alias.ShortName); positional = positional.Remove(match.Index, match.Length).Trim(); break;
            }
            string raw = "lc open" + (positional == "" ? "" : " " + positional);
            foreach (var option in options) raw += " --" + option.Key + "=" + option.Value;
            return raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static Dictionary<string, string> LcOptions(string raw, out string positional)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(raw, @"(?:^|\s)--(?<key>[a-z_]+)=(?<value>.*?)(?=\s+--[a-z_]+=|$)", RegexOptions.IgnoreCase);
            positional = matches.Count == 0 ? raw.Trim() : raw.Substring(0, matches[0].Index).Trim();
            foreach (Match match in matches)
            {
                string key = match.Groups["key"].Value;
                if (result.ContainsKey(key)) throw new ArgumentException("Repeated option --" + key + ".");
                result.Add(key, LcUnquote(match.Groups["value"].Value));
            }
            if (positional.Contains("--")) throw new ArgumentException("Named options use --name=value.");
            return result;
        }

        private static int LcPositive(string text, string label)
        { int value; if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1) throw new ArgumentException(label + " must be a positive number."); return value; }

        private static TimeSpan LcDuration(string text)
        {
            text = text.Trim().ToLowerInvariant();
            if (text == "") return TimeSpan.Zero;
            var matches = Regex.Matches(text, @"(?<n>\d+)\s*(?<u>years?|yrs?|yr|y|months?|mo|weeks?|w|days?|d|hours?|hrs?|hr|h|minutes?|mins?|min|m|seconds?|secs?|sec|s)");
            if (matches.Count == 0 || string.Concat(matches.Cast<Match>().Select(m => m.Value)).Replace(" ", "") != text.Replace(" ", ""))
                throw new ArgumentException("Use a duration such as 30m, 2h, 1d or 1d6h.");
            double seconds = 0;
            foreach (Match m in matches)
            {
                string unit = m.Groups["u"].Value;
                seconds += double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) *
                    (unit[0] == 'y' ? 31536000 : unit.StartsWith("mo", StringComparison.Ordinal) ? 2592000 : unit[0] == 'w' ? 604800 : unit[0] == 'd' ? 86400 : unit[0] == 'h' ? 3600 : unit[0] == 'm' ? 60 : 1);
            }
            if (seconds <= 0 || seconds > TimeSpan.FromDays(36500).TotalSeconds) throw new ArgumentException("Duration must be positive and at most 100 years.");
            return TimeSpan.FromSeconds(seconds);
        }

        private static int? LcLocation(string text, out int? site)
        {
            text = LcUnquote(text); site = null;
            int? pf = LandControlCatalog.FindPlayfield(text);
            if (pf.HasValue) return pf;
            int at = text.LastIndexOf(' '), number;
            if (at > 0 && int.TryParse(text.Substring(at + 1), out number) && number > 0)
            { pf = LandControlCatalog.FindPlayfield(LcUnquote(text.Substring(0, at))); if (pf.HasValue) site = number; }
            if (!pf.HasValue) throw new ArgumentException("Unknown tower playfield: " + text + ". Use lc for the playfield list.");
            if (site.HasValue && !LandControlCatalog.ReadSites().Any(s => s.PlayfieldId == pf.Value && s.Number == site.Value)) throw new ArgumentException("That site number does not exist in this playfield.");
            return pf;
        }

        private void ProcessLandControlCommand(string[] parts, ReplyTarget target)
        {
            string action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            if (action == "last" || action == "events" || action == "victories" || action == "lost" || action == "damage" || action == "destroyed")
            {
                if (action == "events") parts[1] = "last";
                ProcessLandControlHistoryCommand(parts, target); return;
            }
            if (action == "attacks" || action == "battles")
            { QueuePublicWork(target, () => ShowLandControlBattles(new[] { "attacks" }.Concat(parts.Skip(2)).ToArray(), target)); return; }
            QueuePublicWork(target, () => ShowLandControlSites(parts, target));
        }

        private void LcBlob(ReplyTarget target, string title, string body, string summary = null)
        {
            Reply(target, (summary == null ? "" : summary + "\n") + string.Join(" ", BuildBlobLinks(target, title, title, body)));
        }

        private string LcMenu(ReplyTarget target) => CommandLink(target, "lc", "Playfields") + " | " + CommandLink(target, "attacks", "Battles") + " | " +
            CommandLink(target, "lc org", "Our sites") + " | " + CommandLink(target, "lc open", "Open") + " | " + CommandLink(target, "lc unknown", "Needs scout") + " | " +
            CommandLink(target, "towerstats", "Statistics") + " | " + CommandLink(target, "lc last", "Event history") + " | " + CommandLink(target, "help lc", "Help") + "\n\n" + TowerChannelNote();

        private void ShowLandControlSites(string[] parts, ReplyTarget target)
        {
            try
            {
                string raw = string.Join(" ", parts.Skip(1)), positional;
                var options = LcOptions(raw, out positional);
                if (options.Keys.Any(k => !new[] { "page", "playfield", "ct_ql" }.Contains(k.ToLowerInvariant()))) throw new ArgumentException("Site options are --page=N, --playfield=Name and --ct_ql=low-high.");
                int page = options.ContainsKey("page") ? LcPositive(options["page"], "Page") : 1;
                var all = ManagerMemory.Current.ReadLandControlSites();
                string[] args = positional.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string action = args.Length == 0 ? "" : args[0].ToLowerInvariant();
                var body = new StringBuilder(LcMenu(target));
                if (action == "")
                {
                    foreach (var pf in all.GroupBy(s => s.PlayfieldId).OrderBy(g => g.First().ShortName))
                        body.Append(CommandLink(target, "lc " + pf.First().ShortName, pf.First().ShortName + " — " + pf.First().Playfield))
                            .Append(" [").Append(pf.Key).Append("]  ").Append(pf.Count()).Append(" lands\n");
                    body.Append("\n").Append(all.Length).Append(" sites; geographic bounds resolve attack coordinates without guessing the nearest site.\n\n").Append(LandControlEvidenceNote);
                    var recent = ManagerMemory.Current.ReadLandControlBattles(new LandControlBattleQuery()).Battles.Take(3);
                    LcBlob(target, "Land control", body.ToString(), string.Join("\n", recent.Select(b => LcBattleSummary(target, b)))); return;
                }
                IEnumerable<LandControlSite> rows = all;
                if (options.ContainsKey("playfield"))
                { int? requestedSite; int? requestedPf = LcLocation(options["playfield"], out requestedSite); rows = rows.Where(s => s.PlayfieldId == requestedPf && (!requestedSite.HasValue || s.Number == requestedSite)); }
                if (options.ContainsKey("ct_ql"))
                { var limits = options["ct_ql"].Split('-'); int low = LcPositive(limits[0], "CT QL"), high = limits.Length == 1 ? low : LcPositive(limits[1], "CT QL"); if (limits.Length > 2 || low > high || high > 300) throw new ArgumentException("CT QLs must be 1–300."); rows = rows.Where(s => s.ControllerQl >= low && s.ControllerQl <= high); }
                DateTime now = DateTime.UtcNow, at = now;
                int? pvpLevel = null;
                string faction = "all", organization = null;
                bool byOrg = action == "org" || action == "ours";
                string[] statuses = { "all", "open", "hot", "closed", "penalty", "unplanted", "disabled", "new", "unknown", "occupied" };
                if (byOrg)
                {
                    organization = action == "ours" || args.Length == 1 ? (string.IsNullOrWhiteSpace(Client.OrgName) ? OrgChannelName : Client.OrgName) : LcUnquote(string.Join(" ", args.Skip(1)));
                    if (string.IsNullOrWhiteSpace(organization)) throw new ArgumentException("Specify an organization name or ID.");
                    int orgId;
                    rows = int.TryParse(organization, out orgId) ? rows.Where(s => s.OrgId == orgId && s.State == "occupied") :
                        rows.Where(s => s.State == "occupied" && ManagerMemory.LandControlMatches(s.Organization, organization));
                    rows = rows.OrderBy(s => s.Organization).ThenBy(s => s.ControllerQl ?? int.MaxValue);
                }
                else if (statuses.Contains(action))
                {
                    if (action == "hot") action = "open";
                    int i = 1;
                    if (i < args.Length && new[] { "omni", "clan", "neutral", "all" }.Contains(args[i].ToLowerInvariant())) faction = args[i++].ToLowerInvariant();
                    int level;
                    if (i < args.Length && int.TryParse(args[i], out level)) { pvpLevel = level; i++; }
                    TimeSpan offset = i < args.Length ? LcDuration(string.Join(" ", args.Skip(i))) : TimeSpan.Zero;
                    if (offset != TimeSpan.Zero && action != "open" && action != "closed" && action != "new") throw new ArgumentException("Time is supported for open, closed and new searches.");
                    if (action == "open" || action == "closed") at = now.Add(offset);
                    if (action == "disabled") rows = rows.Where(s => !s.Enabled); else rows = rows.Where(s => s.Enabled);
                    if (faction != "all") rows = rows.Where(s => string.Equals(s.Faction, faction, StringComparison.OrdinalIgnoreCase));
                    if (pvpLevel.HasValue)
                    { var range = LandControlCatalog.PvpRange(pvpLevel.Value); rows = rows.Where(s => s.ControllerQl >= range[0] && s.ControllerQl <= range[1]); body.Append("Player level ").Append(pvpLevel).Append(": CT QL ").Append(range[0]).Append('–').Append(range[1]).Append("; unobserved CT QLs excluded.\n\n"); }
                    switch (action)
                    {
                        case "open": rows = rows.Where(s => LandControlCatalog.Gas(s, at).HasValue && LandControlCatalog.Gas(s, at) != 75); break;
                        case "closed": rows = rows.Where(s => LandControlCatalog.Gas(s, at) == 75); break;
                        case "penalty": rows = rows.Where(s => s.State == "occupied" && s.PenaltyUntilUtc > now); break;
                        case "unplanted": rows = rows.Where(s => s.State == "unplanted"); break;
                        case "new": var since = now - (offset == TimeSpan.Zero ? TimeSpan.FromHours(2) : offset); rows = rows.Where(s => s.State == "occupied" && s.PlantedUtc >= since); break;
                        case "unknown": rows = rows.Where(s => s.State == "unknown"); break;
                        case "occupied": rows = rows.Where(s => s.State == "occupied"); break;
                    }
                    body.Append("Calculation time: ").Append(LcUtc(at)).Append(". ").Append(CommandLink(target, "lc unknown", "Unknown sites are listed separately")).Append(".\n\n");
                }
                else
                {
                    int ql;
                    string rangeText = action == "ql" ? string.Join(" ", args.Skip(1)) : positional;
                    if (action == "ql" || int.TryParse(positional, out ql) && ql >= 1 && ql <= 300 && !LandControlCatalog.FindPlayfield(positional).HasValue)
                    {
                        var range = rangeText.Split('-'); int low = LcPositive(range[0], "QL"), high = range.Length == 1 ? low : LcPositive(range[1], "QL");
                        if (range.Length > 2 || low > high || high > 300) throw new ArgumentException("Use a QL or range within 1–300.");
                        rows = rows.Where(s => s.Enabled && s.MinQl <= high && s.MaxQl >= low);
                        body.Append("Site build ranges overlapping QL ").Append(low).Append('–').Append(high).Append("; these are not observed CT levels.\n\n");
                    }
                    else { int? site; var pf = LcLocation(positional, out site); rows = rows.Where(s => s.PlayfieldId == pf && (!site.HasValue || s.Number == site)); }
                }
                var selected = rows.ToArray();
                if (byOrg)
                    foreach (var group in selected.GroupBy(s => s.Organization))
                        body.Append(WorldBossText(group.Key, ColorTitle)).Append(": ").Append(group.Count()).Append(" known sites; types ")
                            .Append(string.Join(", ", group.Where(s => s.ControllerQl.HasValue).Select(s => LandControlCatalog.ControllerType(s.ControllerQl.Value))))
                            .Append("; observed CT QL total ").Append(group.Sum(s => s.ControllerQl ?? 0)).Append(" (missing QLs excluded).\n");
                int pages = Math.Max(1, (selected.Length + 19) / 20); page = Math.Min(page, pages);
                foreach (var site in selected.Skip((page - 1) * 20).Take(20)) body.Append(LcSiteInfo(target, site, at)).Append("\n");
                if (selected.Length == 0) body.Append("No matching local observations. Missing scouting is not an empty-site report.\n\n");
                string command = "lc " + positional;
                foreach (var option in options.Where(p => !p.Key.Equals("page", StringComparison.OrdinalIgnoreCase))) command += " --" + option.Key + "=" + option.Value;
                body.Append("Page ").Append(page).Append('/').Append(pages).Append(" — ").Append(selected.Length).Append(" sites\n");
                if (page > 1) body.Append(CommandLink(target, command + " --page=" + (page - 1), "Previous")).Append(" | ");
                if (page < pages) body.Append(CommandLink(target, command + " --page=" + (page + 1), "Next")).Append(" | ");
                body.Append(CommandLink(target, command + " --page=" + page, "Refresh")).Append("\n\n").Append(LandControlEvidenceNote);
                LcBlob(target, "Land control: " + positional, body.ToString(), selected.Length + " matching site(s).");
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is OverflowException)
            { Reply(target, error.Message + " " + CommandLink(target, "help lc", "Tower help")); }
        }

        private static string LcUtc(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        private string LcSiteInfo(ReplyTarget target, LandControlSite site, DateTime at)
        {
            var text = new StringBuilder();
            string command = "lc " + site.ShortName + " " + site.Number;
            text.Append(CommandLink(target, command, site.ShortName + " " + site.Number)).Append(" — ")
                .Append(WorldBossText(site.Name, ColorTitle)).Append(" (build QL ").Append(site.MinQl).Append('–').Append(site.MaxQl).Append(")\n")
                .Append("<a href='chatcmd:///waypoint ").Append(site.X).Append(' ').Append(site.Y).Append(' ').Append(site.PlayfieldId).Append("'>Waypoint ").Append(site.X).Append('×').Append(site.Y).Append("</a> | ")
                .Append(CommandLink(target, "attacks " + site.ShortName + " " + site.Number, "Battles")).Append('\n');
            if (!site.Enabled) text.Append(WorldBossText("Disabled in the static site catalogue", ColorMuted)).Append('\n');
            text.Append("Last known state: ").Append(WorldBossText(site.State, ColorCommand)).Append(". ");
            if (site.ObservedUtc.HasValue) text.Append(LcUtc(site.ObservedUtc.Value)).Append(" — ").Append(WorldBossText(site.Evidence, ColorMuted));
            text.Append('\n');
            if (site.State == "occupied")
            {
                text.Append(WorldBossText(site.Organization, ColorTitle)).Append(" [").Append(WorldBossText(site.Faction ?? "unknown faction", ColorText)).Append("]");
                if (site.OrgId.HasValue) text.Append(" Org ID ").Append(site.OrgId);
                text.Append("; CT ").Append(site.ControllerQl.HasValue ? "QL " + site.ControllerQl + " / Type " + LandControlCatalog.ControllerType(site.ControllerQl.Value) : "QL unknown").Append('\n');
                text.Append("Plant time: ").Append(site.PlantedUtc.HasValue ? LcUtc(site.PlantedUtc.Value) : "unknown").Append("; conductors ").Append(site.Conductors?.ToString() ?? "unknown").Append(", turrets ").Append(site.Turrets?.ToString() ?? "unknown").Append('\n');
                var gas = LandControlCatalog.Gas(site, at); var close = LandControlCatalog.UntilClose(site, at);
                text.Append("Calculated gas: ").Append(gas.HasValue ? gas + "%" : "unknown");
                if (site.CloseSeconds.HasValue) text.Append("; regular close ").Append(TimeSpan.FromSeconds(site.CloseSeconds.Value).ToString(@"hh\:mm\:ss")).Append(" UTC");
                if (close.HasValue) text.Append(gas == 75 ? "; opens in " + FormatDuration(close.Value - TimeSpan.FromHours(6)) : "; regular close in " + FormatDuration(close.Value));
                text.Append('\n');
                if (site.PenaltyUntilUtc > at) text.Append("Calculated/reported penalty until ").Append(LcUtc(site.PenaltyUntilUtc.Value)).Append(" (may extend with further attacks).\n");
            }
            else if (site.State == "unknown" && site.FixedCloseSeconds.HasValue)
                text.Append("Static closing group: ").Append(site.FixedCloseSeconds == 14400 ? "04:xx UTC" : "20:xx UTC").Append("; actual offset not observed.\n");
            return text.ToString();
        }

        private void ShowLandControlBattles(string[] parts, ReplyTarget target)
        {
            try
            {
                string positional; var options = LcOptions(string.Join(" ", parts.Skip(1)), out positional);
                if (parts[0].Equals("battle", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(positional, @"^\d+$")) positional = "battle " + positional;
                if (options.Keys.Any(k => !new[] { "defender_org", "attacker_org", "attacker", "victory", "page" }.Contains(k.ToLowerInvariant()))) throw new ArgumentException("Unknown battle option.");
                var query = new LandControlBattleQuery();
                if (options.ContainsKey("page")) query.Page = LcPositive(options["page"], "Page");
                if (options.ContainsKey("victory") && !string.Equals(options["victory"], "all", StringComparison.OrdinalIgnoreCase))
                { bool finished; if (!bool.TryParse(options["victory"], out finished)) throw new ArgumentException("--victory must be true, false or all."); query.Finished = finished; }
                if (options.ContainsKey("defender_org")) query.DefenderOrg = options["defender_org"];
                if (options.ContainsKey("attacker_org")) query.AttackerOrg = options["attacker_org"];
                if (options.ContainsKey("attacker")) query.Attacker = options["attacker"];
                if (positional.StartsWith("battle ", StringComparison.OrdinalIgnoreCase))
                { long id; if (!long.TryParse(positional.Substring(7), out id) || id < 1) throw new ArgumentException("Battle ID must be positive."); query.BattleId = id; }
                else if (positional.StartsWith("org ", StringComparison.OrdinalIgnoreCase)) query.EitherOrg = LcUnquote(positional.Substring(4));
                else if (positional.StartsWith("player ", StringComparison.OrdinalIgnoreCase)) query.Attacker = LcUnquote(positional.Substring(7));
                else if (positional.Length != 0)
                {
                    int number;
                    if (int.TryParse(positional, out number) && !LandControlCatalog.FindPlayfield(positional).HasValue)
                    { if (!options.ContainsKey("page")) query.Page = LcPositive(positional, "Page"); positional = ""; }
                    else
                    {
                        string[] words = positional.Split(' ');
                        if (words.Length >= 3 && int.TryParse(words[words.Length - 1], out number) && int.TryParse(words[words.Length - 2], out number) &&
                            LandControlCatalog.FindPlayfield(string.Join(" ", words.Take(words.Length - 2))).HasValue)
                        { if (!options.ContainsKey("page")) query.Page = LcPositive(words.Last(), "Page"); positional = string.Join(" ", words.Take(words.Length - 1)); }
                        int? site; query.PlayfieldId = LcLocation(positional, out site); query.SiteNumber = site;
                    }
                }
                var history = ManagerMemory.Current.ReadLandControlBattles(query);
                var localSites = ManagerMemory.Current.ReadLandControlSites().ToDictionary(s => s.Key);
                var body = new StringBuilder(LcMenu(target));
                foreach (var battle in history.Battles)
                {
                    body.Append(LcBattleSummary(target, battle)).Append('\n').Append("Battle ").Append(battle.Id).Append("; ")
                        .Append(battle.Finished ? "finished: " + battle.Outcome : "no outcome recorded").Append("; ").Append(battle.AttackCount).Append(" attack notice(s)\n")
                        .Append("First ").Append(LcUtc(battle.FirstUtc)).Append("; last ").Append(LcUtc(battle.LastUtc)).Append("; recorded duration ").Append(FormatDuration(battle.LastUtc - battle.FirstUtc)).Append('\n')
                        .Append(WorldBossText(battle.Resolution, ColorMuted)).Append('\n');
                    if (battle.WinnerOrg != null) body.Append("Winning organization: ").Append(WorldBossText(battle.WinnerOrg, ColorTitle)).Append(" [").Append(WorldBossText(battle.WinnerFaction, ColorText)).Append("]\n");
                    if (battle.PlayfieldId.HasValue && battle.SiteNumber.HasValue)
                    { var site = localSites[battle.PlayfieldId + ":" + battle.SiteNumber]; body.Append("Latest local site record:\n").Append(LcSiteInfo(target, site, DateTime.UtcNow)); }
                    else if (battle.X.HasValue && battle.Y.HasValue && battle.PlayfieldId.HasValue)
                        body.Append("<a href='chatcmd:///waypoint ").Append(battle.X).Append(' ').Append(battle.Y).Append(' ').Append(battle.PlayfieldId).Append("'>Reported attack position</a>\n");
                    body.Append("Attackers (latest first):\n");
                    foreach (var attacker in battle.Attackers)
                    {
                        body.Append("  ").Append(WorldBossText(attacker.Name ?? "Unknown character", ColorTitle));
                        if (attacker.CharacterId != 0) body.Append(" [").Append(attacker.CharacterId).Append(']');
                        body.Append(" — ").Append(WorldBossText(attacker.Organization ?? "No organization reported", ColorText)).Append(" [").Append(WorldBossText(attacker.Faction ?? "unknown faction", ColorText)).Append(']');
                        if (attacker.ProfileUtc.HasValue)
                            body.Append("; cached level ").Append(attacker.Level > 0 ? attacker.Level.ToString() : "unknown").Append('/').Append(attacker.AlienLevel >= 0 ? attacker.AlienLevel.ToString() : "unknown")
                                .Append(' ').Append(attacker.Profession > 0 && attacker.Profession < OnlineProfessionNames.Length ? OnlineProfessionNames[attacker.Profession] : "unknown profession")
                                .Append(" (profile ").Append(LcUtc(attacker.ProfileUtc.Value)).Append(')');
                        else body.Append("; level/profession unobserved");
                        body.Append("; ").Append(LcUtc(attacker.ObservedUtc)).Append(attacker.Winner ? " — winning organization" : "").Append('\n');
                    }
                    if (battle.Attackers.Length == 0) body.Append("  No associated attacker observed.\n");
                    if (query.BattleId.HasValue)
                    {
                        body.Append("Attacker page ").Append(battle.AttackerPage).Append('/').Append(battle.AttackerPages).Append('\n');
                        if (battle.AttackerPage > 1) body.Append(CommandLink(target, "attacks battle " + battle.Id + " --page=" + (battle.AttackerPage - 1), "Previous attackers")).Append(" | ");
                        if (battle.AttackerPage < battle.AttackerPages) body.Append(CommandLink(target, "attacks battle " + battle.Id + " --page=" + (battle.AttackerPage + 1), "More attackers"));
                    }
                    else if (battle.AttackerTotal > 5) body.Append(CommandLink(target, "attacks battle " + battle.Id, "All " + battle.AttackerTotal + " attack records")).Append('\n');
                    body.Append('\n');
                }
                if (history.Total == 0) body.Append("No matching battles observed.\n");
                string command = parts[0] + (positional.Length == 0 ? "" : " " + positional);
                foreach (var option in options.Where(p => !string.Equals(p.Key, "page", StringComparison.OrdinalIgnoreCase))) command += " --" + option.Key + "=" + option.Value;
                if (!query.BattleId.HasValue)
                {
                    body.Append("Page ").Append(history.Page).Append('/').Append(history.Pages).Append(" — ").Append(history.Total).Append(" battles\n");
                    if (history.Page > 1) body.Append(CommandLink(target, command + " --page=" + (history.Page - 1), "Newer")).Append(" | ");
                    if (history.Page < history.Pages) body.Append(CommandLink(target, command + " --page=" + (history.Page + 1), "Older")).Append(" | ");
                    body.Append(CommandLink(target, command + " --page=" + history.Page, "Refresh"));
                }
                body.Append("\n\nBattle groups use the same resolved land and defender, with at most eight hours between attacks. A victory is associated only with one matching recent battle; ambiguous outcomes stay separate. These are observed historical groups, not a live war-state claim. Profiles are our cached observations, not measurements at the attack time.");
                LcBlob(target, query.BattleId.HasValue ? "Tower battle " + query.BattleId : "Tower battles", body.ToString(),
                    string.Join("\n", history.Battles.Take(3).Select(b => LcBattleSummary(target, b))));
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is OverflowException)
            { Reply(target, error.Message + " " + CommandLink(target, "help lc", "Tower help")); }
        }

        private string LcBattleSummary(ReplyTarget target, LandControlBattle battle) => CommandLink(target, "attacks battle " + battle.Id,
            (battle.Finished ? battle.Outcome : "Attack") + " — " + (battle.DefenderOrg ?? "Unknown defender") + " in " + (battle.Playfield ?? "unknown playfield") +
            (battle.SiteNumber.HasValue ? " " + battle.SiteNumber : " (site unresolved)")) + " — " + WorldBossText(FormatDuration(DateTime.UtcNow > battle.LastUtc ? DateTime.UtcNow - battle.LastUtc : TimeSpan.Zero) + " ago", ColorCommand);

        private void ShowTowerStatistics(ReplyTarget target, string duration)
        {
            try
            {
                var window = duration == "" ? TimeSpan.FromHours(24) : LcDuration(duration);
                var since = DateTime.UtcNow - window; var s = ManagerMemory.Current.ReadLandControlStatistics(since);
                string body = LcMenu(target) + "Observed notices since " + LcUtc(since) + "\n\n" +
                    "Attacks: " + s.Attacks + " (Clan " + s.ClanAttacks + ", Omni " + s.OmniAttacks + ", Neutral " + s.NeutralAttacks + ", unknown " + s.UnknownAttacks + ")\n" +
                    "Victories: " + s.Victories + "; base-loss notices: " + s.BaseLosses + "\n" +
                    "Loss-notice factions: Clan " + s.ClanLosses + ", Omni " + s.OmniLosses + ", Neutral " + s.NeutralLosses + ", unknown " + s.UnknownLosses + "\n" +
                    "Tower damage: " + s.Damage + "; tower destruction: " + s.Destroyed + "; attacks without exact site: " + s.Unresolved + "\n\n" +
                    "Counts are received announcements, including repeats and complementary outcome notices; they are not deduplicated unique destroyed sites. " +
                    (s.FirstUtc.HasValue ? "First retained LC observation: " + LcUtc(s.FirstUtc.Value) + "." : "No LC traffic recorded yet.");
                LcBlob(target, "Tower statistics", body);
            }
            catch (Exception error) when (error is ArgumentException || error is OverflowException) { Reply(target, error.Message); }
        }

        private void ShowTowerReference(string[] parts, ReplyTarget target)
        {
            if (parts.Length > 2) throw new ArgumentException("Use towertype [QL] or towerqty [player level].");
            bool type = parts[0].Equals("towertype", StringComparison.OrdinalIgnoreCase);
            if (parts.Length == 2)
            {
                int number = LcPositive(parts[1], type ? "QL" : "Player level");
                if (number > (type ? 300 : 220)) throw new ArgumentException(type ? "QL must be 1–300." : "Player level must be 1–220.");
                Reply(target, type ? "QL " + number + ": Control Tower type " + LandControlCatalog.ControllerType(number) + "." :
                    "Level " + number + ": up to " + LandControlCatalog.TowerAllowance(number) + " personal towers."); return;
            }
            LcBlob(target, type ? "Control Tower types" : "Personal tower allowance", type ?
                "I: QL 1–33\nII: 34–81\nIII: 82–128\nIV: 129–176\nV: 177–200\nVI: 201–225\nVII: 226–300" :
                "Level 1–14: 0\n15–74: 1\n75–149: 2\n150–199: 3\n200–220: 4\nOrganization/site capacity and placement rules still apply.");
        }
    }
}
