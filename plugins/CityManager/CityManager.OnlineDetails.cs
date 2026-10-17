using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private static readonly string[] OnlineProfessionNames = {
            "", "Soldier", "Martial Artist", "Engineer", "Fixer", "Agent", "Adventurer", "Trader",
            "Bureaucrat", "Enforcer", "Doctor", "Nano-Technician", "Meta-Physicist", "", "Keeper", "Shade"
        };

        private static int OnlineProfessionId(string value)
        {
            value = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            for (int i = 1; i < OnlineProfessionNames.Length; i++)
                if (value.Length != 0 && value == new string(OnlineProfessionNames[i].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()) return i;
            switch (value)
            {
                case "sol": case "sold": return 1; case "ma": return 2; case "engi": return 3; case "fix": return 4;
                case "agt": return 5; case "adv": case "advy": return 6; case "trad": return 7;
                case "crat": return 8; case "enf": return 9; case "doc": return 10;
                case "nt": return 11; case "mp": return 12; case "keep": return 14;
            }
            int id;
            return int.TryParse(value, out id) && id > 0 && id < OnlineProfessionNames.Length && id != 13 ? id : 0;
        }

        private static int OnlineNumber(JToken value)
        {
            int number;
            return value != null && int.TryParse(value.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number) ? number : -1;
        }

        private static PlayerProfile OnlineRosterProfile(string name, JToken member) => new PlayerProfile {
            Name = name, Level = OnlineNumber(member["LEVELX"] ?? member["LEVEL"]),
            AlienLevel = OnlineNumber(member["ALIENLEVEL"] ?? member["AI_LEVEL"]),
            Profession = OnlineProfessionId((string)member["PROF"] ?? (string)member["PROFESSION"])
        };

        // Called only after identifying a reply from the configured alt bot.
        // Keep useful detail in its existing character rows, without making a
        // website call or interpreting metadata as new online/alt evidence.
        private void RememberOnlineReplyDetails(string message)
        {
            var profiles = new List<PlayerProfile>();
            foreach (Match character in OnlineCharacterRegex.Matches(message))
            {
                string tail = message.Substring(character.Index);
                Match end = Regex.Match(tail, @"<br\s*/?>|\r?\n", RegexOptions.IgnoreCase);
                if (end.Success && end.Index > 0) tail = tail.Substring(0, end.Index);
                // The match itself may start with the preceding <br>/newline.
                int nameAt = tail.IndexOf(character.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
                if (nameAt < 0) continue;
                tail = tail.Substring(nameAt);
                end = Regex.Match(tail, @"<br\s*/?>|\r?\n", RegexOptions.IgnoreCase);
                if (end.Success) tail = tail.Substring(0, end.Index);
                string plain = WebUtility.HtmlDecode(Regex.Replace(tail, "<[^>]*>", ""));
                Match level = Regex.Match(plain, @"\(\s*(\d{1,3})(?:\s*/\s*(\d{1,2}))?");
                if (!level.Success) continue;
                Match profession = Regex.Match(plain.Substring(level.Index),
                    @"\b(Soldier|Martial\s*Artist|Engineer|Fixer|Agent|Adventurer|Trader|Bureaucrat|Enforcer|Doctor|Nano[- ]?Technician|Meta[- ]?Physicist|Keeper|Shade|Sold|Sol|MA|Engi|Fix|Adv|Advy|Trad|Crat|Enf|Doc|NT|MP)\b", RegexOptions.IgnoreCase);
                profiles.Add(new PlayerProfile {
                    Name = character.Groups[1].Value, Level = int.Parse(level.Groups[1].Value, CultureInfo.InvariantCulture),
                    AlienLevel = level.Groups[2].Success ? int.Parse(level.Groups[2].Value, CultureInfo.InvariantCulture) : -1,
                    Profession = profession.Success ? OnlineProfessionId(profession.Value) : 0, UpdatedUtc = DateTime.UtcNow
                });
            }
            ManagerMemory.Current.RememberPlayerProfiles(profiles.ToArray());
        }
    }
}
