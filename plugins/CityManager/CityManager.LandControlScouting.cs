using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessLandControlScout(string[] parts, ReplyTarget target, string sender, bool mayScout)
        {
            if (!mayScout) { Reply(target, "Scouting changes require organization membership."); return; }
            string raw = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(string.Join(" ", parts.Skip(1)), @"<(?:br\s*/?|/?p)\s*>", " ", RegexOptions.IgnoreCase), "<[^>]*>", ""));
            bool remove = parts[0].Equals("remscout", StringComparison.OrdinalIgnoreCase);
            var action = Regex.Match(raw, @"^(?<action>rem|remove|add)\s+", RegexOptions.IgnoreCase);
            if (action.Success)
            { remove = action.Groups["action"].Value.StartsWith("rem", StringComparison.OrdinalIgnoreCase); raw = raw.Substring(action.Length); }
            LandControlSite site = null; string input = null;
            var facts = LandControlCatalog.ReadSites();
            foreach (var pf in facts.GroupBy(s => s.PlayfieldId))
            {
                foreach (var name in new[] { pf.Key.ToString(CultureInfo.InvariantCulture), pf.First().ShortName, pf.First().Playfield })
                {
                    var match = Regex.Match(raw, "^(?:" + Regex.Escape(name) + "|\"" + Regex.Escape(name) + "\"|'" + Regex.Escape(name) + "')" +
                        @"\s+(?<site>\d+)(?:\s+(?<input>.*))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    int number = LcPositive(match.Groups["site"].Value, "Site");
                    site = pf.SingleOrDefault(s => s.Number == number);
                    if (site == null) throw new ArgumentException("Unknown site number in " + pf.First().Playfield + ".");
                    input = match.Groups["input"].Value; break;
                }
                if (site != null) break;
            }
            if (site == null) throw new ArgumentException("Use scout <playfield> <site> <CT inspection text|empty>, scout rem <playfield> <site>, or scout add <playfield> <site> --ql=N --org=Name --faction=Clan [--planted=UTC].");
            if (remove && !string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Scout removal takes only playfield and site.");
            var report = new LandControlReport { Id = Guid.NewGuid().ToString("N"), PlayfieldId = site.PlayfieldId, SiteNumber = site.Number,
                Reporter = sender, ObservedUtc = DateTime.UtcNow, Action = remove ? "remove" : "scout", Details = string.Join(" ", parts) };
            string[] empty = { "empty", "free", "unplanted", "un-planted", "clear", "clean", "none" };
            if (!remove && empty.Contains(input.Trim().ToLowerInvariant())) report.Action = "empty";
            else if (!remove)
            {
                string positional; var options = LcOptions(input, out positional);
                if (options.Count != 0)
                {
                    string[] allowed = { "ql", "org", "faction", "org_id", "planted", "x", "y", "conductors", "turrets", "close", "penalty" };
                    if (options.Keys.Any(k => !allowed.Contains(k.ToLowerInvariant())) || !string.IsNullOrWhiteSpace(positional)) throw new ArgumentException("Unknown scouting option or extra text before options.");
                    if (!options.ContainsKey("ql") || !options.ContainsKey("org") || !options.ContainsKey("faction")) throw new ArgumentException("Scouting requires --ql, --org and --faction.");
                    report.ControllerQl = LcPositive(options["ql"], "CT QL"); report.Organization = options["org"]; report.Faction = LcFaction(options["faction"]);
                    if (options.ContainsKey("org_id")) report.OrgId = LcPositive(options["org_id"], "Organization ID");
                    if (options.ContainsKey("planted")) report.PlantedUtc = LcScoutUtc(options["planted"]);
                    if (options.ContainsKey("penalty")) report.PenaltyUntilUtc = LcScoutUtc(options["penalty"]);
                    if (options.ContainsKey("x")) report.X = LcNonnegative(options["x"], "X");
                    if (options.ContainsKey("y")) report.Y = LcNonnegative(options["y"], "Y");
                    if (report.X.HasValue != report.Y.HasValue) throw new ArgumentException("Supply both --x and --y.");
                    if (report.X.HasValue && LandControlCatalog.FindSite(site.PlayfieldId, report.X.Value, report.Y.Value) != site.Number) throw new ArgumentException("The supplied coordinates do not resolve uniquely to that land.");
                    if (options.ContainsKey("conductors")) report.Conductors = LcNonnegative(options["conductors"], "Conductor count");
                    if (options.ContainsKey("turrets")) report.Turrets = LcNonnegative(options["turrets"], "Turret count");
                    if (options.ContainsKey("close"))
                    { TimeSpan close; if (!TimeSpan.TryParse(options["close"], CultureInfo.InvariantCulture, out close) || close < TimeSpan.Zero || close >= TimeSpan.FromDays(1)) throw new ArgumentException("Closing time must be HH:mm:ss UTC."); report.CloseSeconds = (int)close.TotalSeconds; }
                }
                else
                {
                    // The legacy local-scout input is the game's CT inspection
                    // text. The explicit UTC label must never use the host timezone.
                    var match = Regex.Match(input, @"Control Tower\s*-\s*(?<side>\S+)\s+Level:\s*(?<ql>\d+)\s+.*?Organization:\s*(?<org>.+?)\s+Created at UTC:\s*(?<date>\S+\s+\S+)",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
                    if (!match.Success) throw new ArgumentException("Paste the CT inspection text including Control Tower, Level, Organization and Created at UTC; or use explicit scouting options. Use 'empty' only after observing an empty land.");
                    report.ControllerQl = LcPositive(match.Groups["ql"].Value, "CT QL"); report.Faction = LcFaction(match.Groups["side"].Value);
                    report.Organization = match.Groups["org"].Value.Trim(); report.PlantedUtc = LcScoutUtc(match.Groups["date"].Value);
                }
            }
            if (report.Action == "scout" && (report.ControllerQl < site.MinQl || report.ControllerQl > site.MaxQl)) throw new ArgumentException("CT QL must be within this site's " + site.MinQl + "–" + site.MaxQl + " build range.");
            if (report.PlantedUtc > report.ObservedUtc) throw new ArgumentException("Plant time cannot be in the future.");
            QueuePublicWork(target, () =>
            {
                ManagerMemory.Current.RecordLandControlReport(report);
                Reply(target, (report.Action == "remove" ? "Scouting state removed; original reports and events retained." : "Local " + report.Action + " report saved.") + " " +
                    CommandLink(target, "lc " + site.ShortName + " " + site.Number, "View " + site.ShortName + " " + site.Number));
            });
        }

        private static string LcFaction(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            { case "clan": return "Clan"; case "omni": case "omni-tek": return "Omni"; case "neutral": case "neut": return "Neutral"; default: throw new ArgumentException("Faction must be Clan, Omni or Neutral."); }
        }
        private static int LcNonnegative(string value, string label)
        { int number; if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)) throw new ArgumentException(label + " must be zero or a positive integer."); return number; }
        private static DateTime LcScoutUtc(string value)
        {
            DateTime time;
            if (!Regex.IsMatch(value, @"\d{4}") || !Regex.IsMatch(value, @"(?:T|\s)\d{1,2}:\d{2}(?::\d{2})?") ||
                !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time))
                throw new ArgumentException("Use a full UTC date and time, for example 2026-10-25T10:30:00Z.");
            return time;
        }
    }
}
