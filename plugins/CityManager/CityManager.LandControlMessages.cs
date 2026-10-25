using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    internal sealed class NativeServiceMessage
    {
        public string Source, RawText, Organization;
        public int? GroupType, ChannelId, Category, Instance;
        public List<object> Arguments;
        public JObject Fields;
        public string DecodeError;
    }

    public partial class CityManager
    {
        // AO protocol templates independently implemented from the service-message
        // cases documented by BeBot, Tyrbot and Nadybot. No watcher/API is used.
        // References: Budabot/Tyrbot docs/mmdb.txt and tower_messages_controller.py;
        // J-Soft/BeBot Modules/Ao/TowerAttack.php; Nadybot TOWER_MODULE/TowerController.php.
        private static bool TryParseLandControl(NativeServiceMessage notice, out LandControlEvent row)
        {
            row = null;
            if (notice.Source != "AO.Npc" &&
                !(notice.Source == "AO.Group" && (notice.GroupType == 10 || notice.GroupType == 3))) return false;
            if (notice.RawText.StartsWith("~&", StringComparison.Ordinal))
            {
                int category, instance;
                List<object> args;
                string error;
                if (!CityExtendedMessageParser.TryReadExtended(notice.RawText, out category, out instance, out args, out error))
                { notice.DecodeError = error; return false; }
                notice.Category = category; notice.Instance = instance; notice.Arguments = args;
                if (category != 506) return false;
                try { return TryParseLandControlTemplate(notice, instance, args, out row); }
                catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is ArgumentOutOfRangeException)
                { notice.DecodeError = ex.Message; row = null; return false; }
            }

            string text = Regex.Replace(Regex.Replace(notice.RawText, "<[^>]*>", ""), @"\s+", " ").Trim();
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var match = Regex.Match(text, @"^The (?<aside>Clan|Neutral|Omni) organization (?<aorg>.+) just entered a state of war! (?<actor>.+) attacked the (?<dside>Clan|Neutral|Omni) organization (?<dorg>.+)'s tower in (?<pf>.+) at location \((?<x>\d+),\s*(?<y>\d+)\)\.?$", options);
            string kind = "attack";
            if (!match.Success) match = Regex.Match(text, @"^(?<actor>.+) just attacked the (?<dside>Clan|Neutral|Omni) organization (?<dorg>.+)'s tower in (?<pf>.+) at location \((?<x>\d+),\s*(?<y>\d+)\)\.?$", options);
            if (!match.Success)
            {
                kind = "victory";
                match = Regex.Match(text, @"^The (?<aside>Clan|Neutral|Omni) organization (?<aorg>.+) attacked the (?<dside>Clan|Neutral|Omni) (?<dorg>.+) at their base in (?<pf>.+)\. The attackers won!!$", options);
            }
            if (!match.Success)
            {
                kind = "base-lost";
                match = Regex.Match(text, @"^Notum Wars Update: The (?<dside>Clan|Neutral|Omni) organization (?<dorg>.+) lost their base in (?<pf>.+)\.$", options);
            }
            if (!match.Success)
            {
                kind = "tower-damage";
                match = Regex.Match(text, @"^The tower (?<tower>.+) in (?<pf>.+) was just reduced to (?<health>\d+)\s*% health(?: by (?<actor>.+?)(?: from the (?<aorg>.+) organization)?)?!$", options);
            }
            if (!match.Success)
            {
                kind = "tower-destroyed";
                match = Regex.Match(text, @"^Your tower (?<tower>.+) at X:(?<x>\d+) and Z:(?<y>\d+) in (?<pf>.+) was just destroyed(?: by (?<actor>.+?)(?:,? from the (?<aorg>.+) organization)?)?!$", options);
            }
            if (!match.Success)
            {
                kind = "attack";
                match = Regex.Match(text, @"^(?<actor>.+) just initiated an attack (?:in (?<pf>.+)|on (?<pf>playfield \d+)) at location \((?<x>\d+),\s*(?<y>\d+)\)\. That area is controlled by (?<dorg>.+)\. All districts controlled by your organization are open to attack! You are in a state of war\. Leader chat informed\.$", options);
                if (match.Success) row = new LandControlEvent { AttackerOrg = notice.Organization };
            }
            if (!match.Success)
            {
                kind = "faction-victory";
                match = Regex.Match(text, @"^Notum Wars Update: Victory to the (?<aside>Clan|Neutral|Omni)s!!!$", options);
            }
            if (!match.Success) return false;
            row = row ?? new LandControlEvent();
            row.Kind = kind; row.Message = text;
            row.AttackerSide = LcGroup(match, "aside"); row.AttackerOrg = LcGroup(match, "aorg") ?? row.AttackerOrg;
            row.Attacker = LcGroup(match, "actor"); row.DefenderSide = LcGroup(match, "dside");
            row.DefenderOrg = LcGroup(match, "dorg"); row.Playfield = LcGroup(match, "pf"); row.Tower = LcGroup(match, "tower");
            int value;
            string x = LcGroup(match, "x"), y = LcGroup(match, "y"), health = LcGroup(match, "health");
            if (x != null) { if (!int.TryParse(x, out value)) return false; row.X = value; }
            if (y != null) { if (!int.TryParse(y, out value)) return false; row.Y = value; }
            if (health != null) { if (!int.TryParse(health, out value) || value > 100) return false; row.HealthPercent = value; }
            if (kind == "tower-damage" || kind == "tower-destroyed") row.DefenderOrg = notice.Organization;
            return true;
        }

        private static string LcGroup(Match match, string name) => match.Groups[name].Success ? match.Groups[name].Value : null;
        private static string LcArg(List<object> args, int index)
        {
            string text = args[index] as string;
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Missing text or unresolved message reference.");
            return text;
        }

        private static string LcSide(List<object> args, int index)
        {
            string side = LcArg(args, index);
            if (side != "Clan" && side != "Neutral" && side != "Omni") throw new FormatException("Unknown tower faction.");
            return side;
        }

        private static int LcNumber(List<object> args, int index)
        {
            if (!(args[index] is long)) throw new FormatException("Tower number is not an encoded integer.");
            return checked((int)(long)args[index]);
        }

        private static bool TryParseLandControlTemplate(NativeServiceMessage notice, int instance, List<object> a, out LandControlEvent row)
        {
            row = new LandControlEvent();
            switch (instance)
            {
                case 12753364:
                    if (a.Count != 8) return false;
                    row.Kind = "attack"; row.AttackerSide = LcSide(a, 0); row.AttackerOrg = LcArg(a, 1); row.Attacker = LcArg(a, 2);
                    row.DefenderSide = LcSide(a, 3); row.DefenderOrg = LcArg(a, 4); row.Playfield = LcArg(a, 5); row.X = LcNumber(a, 6); row.Y = LcNumber(a, 7);
                    row.Message = $"The {row.AttackerSide} organization {row.AttackerOrg} just entered a state of war! {row.Attacker} attacked the {row.DefenderSide} organization {row.DefenderOrg}'s tower in {row.Playfield} at location ({row.X},{row.Y}).";
                    break;
                case 147506468:
                    if (a.Count != 3) return false;
                    row.Kind = "base-lost"; row.DefenderSide = LcSide(a, 0); row.DefenderOrg = LcArg(a, 1); row.Playfield = LcArg(a, 2);
                    row.Message = $"Notum Wars Update: The {row.DefenderSide} organization {row.DefenderOrg} lost their base in {row.Playfield}.";
                    break;
                case 118352306:
                case 24174231:
                    if (a.Count != 5) return false;
                    row.Kind = "attack"; row.Attacker = LcArg(a, 0); row.AttackerOrg = notice.Organization;
                    row.Playfield = instance == 24174231 ? "Playfield " + LcNumber(a, 1).ToString(CultureInfo.InvariantCulture) : LcArg(a, 1);
                    row.X = LcNumber(a, 2); row.Y = LcNumber(a, 3); row.DefenderOrg = LcArg(a, 4);
                    row.Message = $"{row.Attacker} initiated an attack in {row.Playfield} at ({row.X},{row.Y}), controlled by {row.DefenderOrg}. Your organization entered a state of war.";
                    break;
                case 94492169:
                case 224009576:
                case 265658836:
                    int count = instance == 94492169 ? 5 : instance == 224009576 ? 4 : 3;
                    if (a.Count != count) return false;
                    row.Kind = "tower-damage"; row.Tower = LcArg(a, 0); row.Playfield = LcArg(a, 1); row.HealthPercent = LcNumber(a, 2);
                    if (row.HealthPercent < 0 || row.HealthPercent > 100) return false;
                    row.DefenderOrg = notice.Organization;
                    if (count > 3) row.Attacker = LcArg(a, 3);
                    if (count > 4) row.AttackerOrg = LcArg(a, 4);
                    row.Message = $"The tower {row.Tower} in {row.Playfield} was reduced to {row.HealthPercent}% health" + LcAttackerSuffix(row) + ".";
                    break;
                case 83936697:
                case 105909337:
                case 105909336:
                case 207945924:
                    int destroyedCount = instance == 207945924 ? 4 : instance == 105909336 ? 5 : 6;
                    if (a.Count != destroyedCount) return false;
                    row.Kind = "tower-destroyed"; row.Tower = LcArg(a, 0); row.X = LcNumber(a, 1); row.Y = LcNumber(a, 2); row.Playfield = LcArg(a, 3);
                    row.DefenderOrg = notice.Organization;
                    if (destroyedCount > 4) row.Attacker = LcArg(a, 4);
                    if (destroyedCount > 5) row.AttackerOrg = LcArg(a, 5);
                    row.Message = $"Your tower {row.Tower} at ({row.X},{row.Y}) in {row.Playfield} was destroyed" + LcAttackerSuffix(row) + ".";
                    break;
                default:
                    row = null;
                    return false;
            }
            return (!row.X.HasValue || row.X.Value >= 0) && (!row.Y.HasValue || row.Y.Value >= 0);
        }

        private static string LcAttackerSuffix(LandControlEvent row) => row.Attacker == null ? "" :
            " by " + row.Attacker + (row.AttackerOrg == null ? "" : " from " + row.AttackerOrg);
    }
}
