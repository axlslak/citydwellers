using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using CityBankers.Shared;

namespace CityBankers
{
    /// <summary>
    /// AO-native, current-stock-driven query tree for accepted bank inventory.
    ///
    /// Canonical grammar:
    ///   stock
    ///   symb [family [slot [targetQl]]]
    ///   spirit [slot [targetQl]]
    ///   dyna|phatz [partial name|ql]
    /// </summary>
    internal static class StockCommandEngine
    {
        // Immutable AOID catalogue from game upload/cast profession requirements.
        // Discs inherit only an unambiguous exact nano-name match. Unresolved and
        // future items stay visible under Unclassified; no runtime catalogue scan.
        private static readonly Dictionary<int, string> DynaProfessionById = BuildDynaProfessionIndex();

        private static Dictionary<int, string> BuildDynaProfessionIndex()
        {
            var groups = new Dictionary<string, int[]>
            {
                { "Adventurer", new[] {
                    85146, 146778, 161091, 161093, 161097, 161148, 161150, 161152, 161156, 161158, 161160, 161164,
                    161166, 161168, 161395, 161398, 161404, 161410, 161413, 161416, 161422, 161425, 161428, 161434,
                    161437, 161440, 161677, 161679, 161681, 161691, 161693, 161885, 161888, 161891, 161897, 161921,
                    162120, 162122, 162257, 162259, 162261, 162316, 162318, 162320, 162322, 162338, 162341, 162344,
                    162347, 163395, 163397, 163410, 163413, 204311, 204313, 204317, 204319,
                } },
                { "Agent", new[] {
                    160711, 160713, 160790, 160792, 160794, 160796, 160823, 160825, 160827, 160829, 160837, 160840,
                    160846, 160849, 160852, 160855, 160861, 160864, 160867, 160870, 160896, 160898, 160910, 161383,
                    161386, 161392, 203670, 203672, 203676, 203679, 203800, 203802, 203804, 203975, 204016, 204019,
                    204022, 204028,
                } },
                { "Bureaucrat", new[] {
                    203660, 203662, 203664, 203666, 203703, 203706, 203709, 203712, 203838, 203840, 203843, 203845,
                    203847, 203853, 203856, 203858, 203860, 203878, 203881, 203884, 203887, 203896, 203899, 203902,
                    203905, 203908, 203951, 203953, 203955, 203957, 203961, 203963, 203965, 203989, 203992, 203995,
                    203998, 204004, 204007, 204010, 205298, 205300, 205302, 205304, 205320, 205323, 205326, 205329,
                    205438, 205440, 230375, 230377, 230381, 230383,
                } },
                { "Doctor", new[] {
                    204428, 204430, 204432, 204520, 204523, 204526,
                } },
                { "Enforcer / Keeper / Martial Artist / Shade", new[] {
                    210328, 210330, 210332,
                } },
                { "Enforcer", new[] {
                    202792, 202794, 202795, 202798, 202817, 202819, 202820, 202823, 202833, 202835, 202837, 202839,
                    202843, 202845, 202847, 202853, 202855, 202857, 202859, 202861, 202863, 202865, 202872, 202875,
                    202878, 202881, 202884, 202887, 202890, 202893, 202896, 202899, 202902, 202905, 202908, 202911,
                    203206, 203208, 203210, 203212, 203217, 203220, 203223, 203226,
                } },
                { "Engineer", new[] {
                    203862, 203864, 203866, 203868, 203874, 203912, 203915, 203918, 203921, 203930, 204336, 204338,
                    204342, 204344, 204348, 204350, 204352, 204354, 204356, 204365, 204367, 204369, 204371, 204421,
                    204423, 204433, 204436, 204442, 204445, 204451, 204454, 204457, 204460, 204463, 204469, 204472,
                    204475, 204478, 204487, 204490, 205242, 205244, 205246, 205248, 205250, 205272, 205275, 205278,
                    205281, 205284,
                } },
                { "Fixer", new[] {
                    155190, 155191, 155192, 155193, 155198, 155199, 155200, 155201, 162487, 162489, 162491, 162493,
                    162495, 162501, 162504, 162507, 162510, 162513, 162590, 162592, 162594, 162596, 162600, 162604,
                    162605, 162608, 162611, 162614, 162620, 162626, 162721, 162723, 162725, 162727, 162729, 162731,
                    162733, 162735, 162745, 162748, 162751, 162754, 162757, 162760, 162763, 162766, 163082, 163084,
                    163086, 163088, 163096, 163097, 163104, 163107, 163110, 163113, 163116, 163130, 203596, 203600,
                    203602, 203604, 203685, 203691, 203694, 203697, 203814, 203979, 203981, 203983, 203985, 204039,
                    204042, 204045, 204048, 204051,
                } },
                { "Keeper", new[] {
                    210493, 210495, 210620, 210622, 210682, 211163, 211169, 211171,
                } },
                { "Martial Artist", new[] {
                    162838, 162840, 162842, 163120, 163123, 163126,
                } },
                { "Meta-Physicist", new[] {
                    154985, 154996, 155000, 155026, 155031, 155035, 203608, 203610, 203718, 203721, 205188, 205190,
                    205194, 205205, 205208, 205214,
                } },
                { "Nano-Technician", new[] {
                    28821, 28822, 28823, 147792, 147793, 147796, 150632, 150667, 201522, 201524, 203808, 203810,
                    204496, 204499, 205162, 205165, 205444,
                } },
                { "Soldier", new[] {
                    203120, 203126, 203128, 203130, 203132, 203138, 203140, 203142, 203144, 203146, 203162, 203165,
                    203168, 203171, 203174, 203177, 203180, 203183, 203186, 203189, 204304, 204306, 204529, 204532,
                } },
                { "Trader", new[] {
                    203787, 203789, 203791, 203793, 203936, 203939, 203942, 203945, 203969, 203971, 204057, 204060,
                } },
            };
            var result = new Dictionary<int, string>();
            foreach (var group in groups)
                foreach (int id in group.Value) result.Add(id, group.Key);
            return result;
        }

        private static readonly string[] FamilyOrder =
        {
            "artillery",
            "infantry",
            "control",
            "support",
            "extermination",
            "spirit",
            "dyna",
            "phatz"
        };

        private static readonly string[] SlotOrder =
        {
            "brain",
            "eye",
            "ear",
            "chest",
            "waist",
            "leftarm",
            "rightarm",
            "leftwrist",
            "rightwrist",
            "lefthand",
            "righthand",
            "thigh",
            "feet"
        };

        private static readonly Dictionary<string, string> FamilyAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "artillery", "artillery" },
                { "art", "artillery" },
                { "arty", "artillery" },
                { "arti", "artillery" },
                { "infantry", "infantry" },
                { "inf", "infantry" },
                { "infa", "infantry" },
                { "control", "control" },
                { "ctrl", "control" },
                { "support", "support" },
                { "supp", "support" },
                { "extermination", "extermination" },
                { "exterm", "extermination" },
                { "ext", "extermination" },
                { "spirit", "spirit" },
                { "spirits", "spirit" },
                { "dyna", "dyna" },
                { "dynas", "dyna" },
                { "nano", "dyna" },
                { "nanos", "dyna" },
                { "phatz", "phatz" },
                { "phat", "phatz" }
            };

        private static readonly Dictionary<string, string> SlotAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "brain", "brain" },
                { "head", "brain" },
                { "eye", "eye" },
                { "eyes", "eye" },
                { "ocular", "eye" },
                { "occular", "eye" },
                { "ear", "ear" },
                { "ears", "ear" },
                { "chest", "chest" },
                { "waist", "waist" },
                { "leftarm", "leftarm" },
                { "left-arm", "leftarm" },
                { "larm", "leftarm" },
                { "rightarm", "rightarm" },
                { "right-arm", "rightarm" },
                { "rarm", "rightarm" },
                { "leftwrist", "leftwrist" },
                { "left-wrist", "leftwrist" },
                { "lwrist", "leftwrist" },
                { "rightwrist", "rightwrist" },
                { "right-wrist", "rightwrist" },
                { "rwrist", "rightwrist" },
                { "lefthand", "lefthand" },
                { "left-hand", "lefthand" },
                { "lhand", "lefthand" },
                { "righthand", "righthand" },
                { "right-hand", "righthand" },
                { "rhand", "righthand" },
                { "thigh", "thigh" },
                { "leg", "thigh" },
                { "legs", "thigh" },
                { "feet", "feet" },
                { "foot", "feet" }
            };

        public static string NormalizeSymbiantCommand(string input)
        {
            string text = (input ?? string.Empty).Trim();
            string first = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            string family;
            return TryNormalizeFamily(first, out family) && IsSymbiantFamily(family)
                ? "symb " + text : text;
        }

        public static bool TryBuildResponse(
            string input,
            CurrentStockState stock,
            string centralCharacter,
            out string response,
            string commandPrefix = "")
        {
            response = null;
            string[] raw = NormalizeSymbiantCommand(input)
                .Trim()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (raw.Length == 0)
                return false;

            int index = 1;
            string family = null;
            if (string.Equals(raw[0], "stock", StringComparison.OrdinalIgnoreCase))
            {
                if (raw.Length != 1)
                    return false;
            }
            else if (string.Equals(raw[0], "symb", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw[0], "symbs", StringComparison.OrdinalIgnoreCase))
            {
                // Family is selected from the five symbiant destinations below.
            }
            else if (TryNormalizeFamily(raw[0], out family) &&
                (string.Equals(family, "spirit", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(family, "dyna", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(family, "phatz", StringComparison.OrdinalIgnoreCase)))
            {
            }
            else
            {
                return false;
            }

            List<StockItemState> items = (stock?.Items ?? new List<StockItemState>())
                .Where(item => item != null)
                .ToList();

            if (index >= raw.Length)
            {
                if (family == null && string.Equals(raw[0], "stock", StringComparison.OrdinalIgnoreCase))
                    response = BuildRoot(items, centralCharacter, commandPrefix);
                else if (family == null)
                    response = BuildSymbiantRoot(items, centralCharacter, commandPrefix);
                else
                    response = BuildFamily(items, centralCharacter, commandPrefix, family);
                return true;
            }

            if (family == null)
            {
                if (!TryNormalizeFamily(raw[index], out family) || !IsSymbiantFamily(family))
                {
                    response = BuildSymbiantRoot(items, centralCharacter, commandPrefix);
                    return true;
                }
                index++;
            }

            if (index >= raw.Length)
            {
                response = BuildFamily(items, centralCharacter, commandPrefix, family);
                return true;
            }

            if (!IsSlottedFamily(family))
            {
                int familyQl;
                string search = string.Join(" ", raw.Skip(index));
                if (string.Equals(search, "list", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(search, "print", StringComparison.OrdinalIgnoreCase))
                    response = BuildFamily(items, centralCharacter, commandPrefix, family);
                else if (int.TryParse(search, out familyQl) && familyQl > 0)
                    response = BuildGenericQl(items, centralCharacter, commandPrefix, family, familyQl);
                else
                    response = BuildGenericSearch(
                        items, centralCharacter, commandPrefix, family, search);
                return true;
            }

            string slot;
            int consumed;
            if (!TryNormalizeSlot(raw, index, out slot, out consumed))
            {
                response = BuildFamily(items, centralCharacter, commandPrefix, family);
                return true;
            }
            index += consumed;

            if (index >= raw.Length)
            {
                response = BuildSlot(items, centralCharacter, commandPrefix, family, slot);
                return true;
            }

            int targetQl;
            if (!int.TryParse(raw[index], out targetQl) || targetQl <= 0)
            {
                response = BuildSlot(
                    items, centralCharacter, commandPrefix, family, slot);
                return true;
            }

            response = BuildQlAvailability(
                items, centralCharacter, commandPrefix, family, slot, targetQl);
            return true;
        }

        private static string BuildRoot(
            List<StockItemState> items,
            string centralCharacter,
            string commandPrefix)
        {
            var represented = new[]
                {
                    new { Key = "symb", Label = "Symbiants", Families = FamilyOrder.Where(IsSymbiantFamily).ToArray() },
                    new { Key = "spirit", Label = "Spirits", Families = new[] { "spirit" } },
                    new { Key = "dyna", Label = "Dyna Nanos", Families = new[] { "dyna" } },
                    new { Key = "phatz", Label = "Phatz", Families = new[] { "phatz" } },
                    new { Key = (string)null, Label = "Central", Families = new[] { "central" } }
                }
                .Select(module => new
                {
                    module.Key,
                    module.Label,
                    Items = items.Where(item => module.Families.Any(family => string.Equals(
                        item.Role, family, StringComparison.OrdinalIgnoreCase))).ToList()
                })
                .ToList();

            // AO root tell has exactly one text:// window. Family labels in the tell stay
            // plain/color-coded; the general stock window itself restores the chatcmd tree.
            string inline = string.Join(
                " | ",
                represented.Select(entry =>
                    CityBankersChatPalette.Cyan(entry.Label) +
                    " " + CityBankersChatPalette.Green(entry.Items.Count.ToString()) +
                    " copies"));

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White("CityBankers Stock"));
            body.Append("<br><br>");
            foreach (var entry in represented)
            {
                int templates = CountTemplates(entry.Items);
                body.Append(entry.Key == null
                    ? CityBankersChatPalette.Cyan(entry.Label)
                    : ChatCommand(entry.Label, centralCharacter, commandPrefix + entry.Key));
                body.Append("  ");
                body.Append(CityBankersChatPalette.Yellow(templates.ToString()));
                body.Append(templates == 1 ? " type / " : " types / ");
                body.Append(CityBankersChatPalette.Green(entry.Items.Count.ToString()));
                body.Append(" copies<br>");
            }

            return "CityBankers currently has: " + inline + ". " +
                Blob("Open stock", body.ToString());
        }

        private static string BuildSymbiantRoot(
            List<StockItemState> items,
            string centralCharacter,
            string commandPrefix)
        {
            var represented = FamilyOrder.Where(IsSymbiantFamily)
                .Select(family => new { Family = family, Items = FamilyItems(items, family) })
                .Where(entry => entry.Items.Count > 0).ToList();
            if (represented.Count == 0)
                return "Symbiants: 0 offers.";

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White("Symbiant Stock"));
            body.Append("<br><br>");
            foreach (var entry in represented)
            {
                body.Append(ChatCommand(DisplayFamily(entry.Family), centralCharacter,
                    commandPrefix + "symb " + entry.Family));
                body.Append("  ");
                body.Append(CityBankersChatPalette.Yellow(CountTemplates(entry.Items).ToString()));
                body.Append(" types / ");
                body.Append(CityBankersChatPalette.Green(entry.Items.Count.ToString()));
                body.Append(" copies<br>");
            }
            return "Symbiants: " + represented.Sum(entry => entry.Items.Count) +
                " copies in stock. " + Blob("Open symbiants", body.ToString());
        }

        private static string BuildFamily(
            List<StockItemState> items,
            string centralCharacter,
            string commandPrefix,
            string family)
        {
            List<StockItemState> familyItems = FamilyItems(items, family);
            if (familyItems.Count == 0)
            {
                if (IsSlottedFamily(family))
                    return DisplayFamily(family) + ": 0 offers.";
                return "No " + DisplayFamily(family) +
                    " items are in stock right now. " +
                    ChatCommand("Stock", centralCharacter, commandPrefix + "stock");
            }

            if (!IsSlottedFamily(family))
                return BuildGenericFamily(familyItems, centralCharacter, commandPrefix, family);

            var slots = SlotOrder
                .Select(slot => new
                {
                    Slot = slot,
                    Items = SlotItems(familyItems, slot)
                })
                .Where(entry => entry.Items.Count > 0)
                .ToList();

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White(DisplayFamily(family) + " Stock"));
            body.Append("<br><br>");
            foreach (var entry in slots)
            {
                body.Append(ChatCommand(
                    DisplaySlot(entry.Slot),
                    centralCharacter,
                    commandPrefix + FamilyCommand(family, entry.Slot)));
                body.Append("  ");
                int templates = CountTemplates(entry.Items);
                body.Append(Color(templates.ToString(), "#FFFF00"));
                body.Append(templates == 1 ? " type / " : " types / ");
                body.Append(Color(entry.Items.Count.ToString(), "#00FF00"));
                body.Append(" copies<br>");
            }
            body.Append("<br>");
            body.Append(ChatCommand(
                "Back to stock", centralCharacter, commandPrefix + "stock"));

            return DisplayFamily(family) + ": " +
                familyItems.Count + " copies in " + CountTemplates(familyItems) +
                " stocked types. " + Blob("Open " + DisplayFamily(family), body.ToString());
        }

        private static string BuildGenericFamily(
            List<StockItemState> familyItems,
            string centralCharacter,
            string commandPrefix,
            string family)
        {
            if (string.Equals(family, "dyna", StringComparison.OrdinalIgnoreCase))
                return BuildDynaProfessions(familyItems, centralCharacter, commandPrefix);

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White(DisplayFamily(family) + " Stock"));
            body.Append("<br><br>");
            foreach (var tier in familyItems.GroupBy(item => item.Ql).OrderBy(group => group.Key))
            {
                body.Append(ChatCommand("QL " + tier.Key, centralCharacter,
                    commandPrefix + FamilyCommand(family, tier.Key.ToString())));
                body.Append("  ");
                body.Append(Color(CountTemplates(tier).ToString(), "#FFFF00"));
                body.Append(" types / ");
                body.Append(Color(tier.Count().ToString(), "#00FF00"));
                body.Append(" copies<br>");
            }
            return DisplayFamily(family) + ": " + familyItems.Count + " copies in " +
                CountTemplates(familyItems) + " stocked types. " +
                Blob("Open " + DisplayFamily(family), body.ToString());
        }

        private static string BuildDynaProfessions(
            List<StockItemState> items, string centralCharacter, string commandPrefix)
        {
            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White("Dyna Nanos by Profession"))
                .Append("<br><br>Crystals and instruction discs. Search by name with dyna [name], or by QL with dyna [QL].<br><br>");
            var groups = GroupTemplates(items).GroupBy(item => DynaProfession(item.AoId, item.HighId))
                .OrderBy(group => group.Key == "Unclassified" ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                body.Append(CityBankersChatPalette.Cyan(group.Key)).Append(" - ")
                    .Append(group.Count()).Append(" types / ").Append(group.Sum(item => item.Count))
                    .Append(" copies<br>");
                foreach (var item in group.OrderBy(item => item.Ql)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.AoId))
                {
                    body.Append(ChatCommand("QL " + item.Ql, centralCharacter, commandPrefix + "dyna " + item.Ql))
                        .Append("  ").Append(ItemLink(item)).Append("  x")
                        .Append(Color(item.Count.ToString(), "#FFFF00")).Append("  ")
                        .Append(ChatCommand("GET", centralCharacter, commandPrefix + "get " + item.AoId))
                        .Append("<br>");
                }
                body.Append("<br>");
            }
            body.Append(ChatCommand("Back to stock", centralCharacter, commandPrefix + "stock"));
            return "Dyna: " + items.Count + " copies in " + CountTemplates(items) +
                " stocked types. " + Blob("Dyna by profession", body.ToString());
        }

        private static string DynaProfession(int aoId, int highId)
        {
            string profession;
            return DynaProfessionById.TryGetValue(aoId, out profession) ||
                DynaProfessionById.TryGetValue(highId, out profession) ? profession : "Unclassified";
        }

        private static string BuildGenericQl(
            List<StockItemState> items,
            string centralCharacter,
            string commandPrefix,
            string family,
            int ql)
        {
            List<StockTemplate> templates = GroupTemplates(FamilyItems(items, family)
                    .Where(item => item.Ql == ql))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (templates.Count == 0)
                return "No " + DisplayFamily(family) + " items at QL " + ql + ".";
            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White(DisplayFamily(family) + " QL " + ql));
            body.Append("<br><br>");
            foreach (StockTemplate template in templates)
            {
                body.Append(ItemLink(template));
                body.Append("  x");
                body.Append(Color(template.Count.ToString(), "#FFFF00"));
                body.Append("  ");
                body.Append(ChatCommand("GET", centralCharacter,
                    commandPrefix + "get " + template.AoId));
                body.Append("<br>");
            }
            return DisplayFamily(family) + " QL " + ql + ": " + templates.Count +
                " stocked types. " + Blob("Open items", body.ToString());
        }

        private static string BuildGenericSearch(
            List<StockItemState> items,
            string centralCharacter,
            string commandPrefix,
            string family,
            string search)
        {
            string needle = (search ?? string.Empty).Trim();
            List<StockTemplate> templates = GroupTemplates(FamilyItems(items, family)
                    .Where(item => (item.Name ?? string.Empty).IndexOf(
                        needle, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Ql)
                .ToList();
            if (templates.Count == 0)
                return "No " + DisplayFamily(family) + " stock matches '" + needle + "'.";

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White(
                DisplayFamily(family) + " matching " + needle));
            body.Append("<br><br>");
            foreach (StockTemplate template in templates)
            {
                body.Append(ItemLink(template));
                body.Append("  x");
                body.Append(Color(template.Count.ToString(), "#FFFF00"));
                body.Append("  ");
                body.Append(ChatCommand("GET", centralCharacter,
                    commandPrefix + "get " + template.AoId));
                body.Append("<br>");
            }
            return DisplayFamily(family) + " search '" + needle + "': " +
                templates.Count + " stocked types. " + Blob("Open matches", body.ToString());
        }

        private static bool IsSlottedFamily(string family)
        {
            return !string.Equals(family, "dyna", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(family, "phatz", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSymbiantFamily(string family)
        {
            return IsSlottedFamily(family) &&
                !string.Equals(family, "spirit", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSlot(
            List<StockItemState> items,
            string centralCharacter,
            string commandPrefix,
            string family,
            string slot)
        {
            List<StockItemState> matches = SlotItems(FamilyItems(items, family), slot);
            if (matches.Count == 0)
                return DisplayFamily(family) + " " + DisplaySlot(slot) + ": 0 offers.";

            List<StockTemplate> templates = GroupTemplates(matches)
                .OrderBy(template => template.Ql)
                .ThenBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White(
                DisplayFamily(family) + " " + DisplaySlot(slot)));
            body.Append("<br><br>");
            foreach (StockTemplate template in templates)
            {
                body.Append(ChatCommand(
                    "QL " + template.Ql,
                    centralCharacter,
                    commandPrefix + FamilyCommand(family, slot + " " + template.Ql)));
                body.Append("  ");
                body.Append(ItemLink(template));
                body.Append("  x");
                body.Append(Color(template.Count.ToString(), "#FFFF00"));
                body.Append("  ");
                body.Append(ChatCommand(
                    "GET",
                    centralCharacter,
                    commandPrefix + "get " + template.AoId));
                body.Append("<br>");
            }
            body.Append("<br>");
            body.Append(ChatCommand(
                "Back to " + DisplayFamily(family),
                centralCharacter,
                commandPrefix + FamilyCommand(family, null)));

            return DisplayFamily(family) + " " + DisplaySlot(slot) + ": " +
                templates.Count + (templates.Count == 1 ? " stocked QL" : " stocked QLs") +
                ", " + matches.Count + " copies. " +
                Blob("Open " + DisplaySlot(slot), body.ToString());
        }

        private static string BuildQlAvailability(
            List<StockItemState> items,
            string centralCharacter,
            string commandPrefix,
            string family,
            string slot,
            int targetQl)
        {
            List<StockTemplate> templates = GroupTemplates(
                SlotItems(FamilyItems(items, family), slot));
            string description = DisplayFamily(family) + " " + DisplaySlot(slot) + " around QL " + targetQl;
            if (templates.Count == 0)
                return description + ": 0 offers.";

            int? lowerQl = templates.Where(template => template.Ql < targetQl)
                .Select(template => (int?)template.Ql).OrderByDescending(value => value).FirstOrDefault();
            int? higherQl = templates.Where(template => template.Ql > targetQl)
                .Select(template => (int?)template.Ql).OrderBy(value => value).FirstOrDefault();
            List<StockTemplate> offers = templates
                .Where(template => template.Ql == targetQl ||
                    (lowerQl.HasValue && template.Ql == lowerQl.Value) ||
                    (higherQl.HasValue && template.Ql == higherQl.Value))
                .OrderBy(template => template.Ql)
                .ThenBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(template => template.AoId)
                .ToList();

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.Cyan(description)).Append("<br><br>");
            foreach (var tier in offers.GroupBy(template => template.Ql))
            {
                string label = tier.Key < targetQl ? "Lower" : tier.Key > targetQl ? "Higher" : "Requested";
                body.Append(CityBankersChatPalette.Yellow(label + " - QL " + tier.Key)).Append("<br>");
                foreach (StockTemplate template in tier)
                    body.Append(FormatTemplate(template, centralCharacter, commandPrefix)).Append("<br>");
                body.Append("<br>");
            }
            body.Append(ChatCommand("All " + DisplayFamily(family) + " " + DisplaySlot(slot),
                centralCharacter, commandPrefix + FamilyCommand(family, slot)));
            return description + ": " + offers.Count + (offers.Count == 1 ? " offer. " : " offers. ") +
                Blob("View offers", body.ToString());
        }

        private static List<StockItemState> FamilyItems(
            IEnumerable<StockItemState> items,
            string family)
        {
            return (items ?? Enumerable.Empty<StockItemState>())
                .Where(item => item != null && string.Equals(
                    item.Role,
                    family,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static List<StockItemState> SlotItems(
            IEnumerable<StockItemState> items,
            string slot)
        {
            return (items ?? Enumerable.Empty<StockItemState>())
                .Where(item => string.Equals(
                    ResolveSlot(item),
                    slot,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static string ResolveSlot(StockItemState item)
        {
            if (item == null)
                return null;
            if (!string.Equals(item.Role, "spirit", StringComparison.OrdinalIgnoreCase))
                return DetectSlot(item.Name);

            string slot;
            return SymbiantCatalog.TryGetSpiritSlot(item.AoId, out slot) ? slot : null;
        }

        private static List<StockTemplate> GroupTemplates(IEnumerable<StockItemState> items)
        {
            return (items ?? Enumerable.Empty<StockItemState>())
                .Where(item => item != null)
                .GroupBy(
                    item => item.AoId + ":" + item.HighId + ":" + item.Ql,
                    StringComparer.Ordinal)
                .Select(group =>
                {
                    StockItemState first = group.First();
                    return new StockTemplate
                    {
                        AoId = first.AoId,
                        HighId = first.HighId,
                        Ql = first.Ql,
                        Name = first.Name ?? string.Empty,
                        Count = group.Count()
                    };
                })
                .ToList();
        }

        private static int CountTemplates(IEnumerable<StockItemState> items)
        {
            var source = (items ?? Enumerable.Empty<StockItemState>()).Where(i => i != null).ToList();
            var families = source.Any(i => string.Equals(i.Role, "phatz", StringComparison.OrdinalIgnoreCase))
                ? SymbiantCatalog.GetPhatzFamilies(SettingsPaths.GetSettingsDirectory()) : null;
            return source.Select(i => families != null && string.Equals(i.Role, "phatz", StringComparison.OrdinalIgnoreCase)
                ? "phatz:" + families.Key(i.AoId) : i.AoId + ":" + i.HighId + ":" + i.Ql).Distinct().Count();
        }

        private static bool TryNormalizeFamily(string token, out string family)
        {
            return FamilyAliases.TryGetValue(token ?? string.Empty, out family);
        }

        private static bool TryNormalizeSlot(
            string[] tokens,
            int index,
            out string slot,
            out int consumed)
        {
            slot = null;
            consumed = 0;
            if (tokens == null || index < 0 || index >= tokens.Length)
                return false;

            if (index + 1 < tokens.Length)
            {
                string joined = tokens[index] + tokens[index + 1];
                if (SlotAliases.TryGetValue(joined, out slot))
                {
                    consumed = 2;
                    return true;
                }
            }

            if (SlotAliases.TryGetValue(tokens[index], out slot))
            {
                consumed = 1;
                return true;
            }
            return false;
        }

        private static string DetectSlot(string name)
        {
            string value = (name ?? string.Empty).ToLowerInvariant();
            if (value.Contains("left arm")) return "leftarm";
            if (value.Contains("right arm")) return "rightarm";
            if (value.Contains("left wrist")) return "leftwrist";
            if (value.Contains("right wrist")) return "rightwrist";
            if (value.Contains("left hand")) return "lefthand";
            if (value.Contains("right hand")) return "righthand";
            if (value.Contains("brain")) return "brain";
            if (value.Contains("ocular") || value.Contains("eye")) return "eye";
            if (value.Contains("ear")) return "ear";
            if (value.Contains("chest")) return "chest";
            if (value.Contains("waist")) return "waist";
            if (value.Contains("thigh")) return "thigh";
            if (value.Contains("feet") || value.Contains("foot")) return "feet";
            return null;
        }

        private static string DisplayFamily(string family)
        {
            if (string.IsNullOrWhiteSpace(family))
                return "Unknown";
            return char.ToUpperInvariant(family[0]) + family.Substring(1).ToLowerInvariant();
        }

        private static string FamilyCommand(string family, string suffix)
        {
            string root = IsSymbiantFamily(family) ? "symb " + family : family;
            return string.IsNullOrWhiteSpace(suffix) ? root : root + " " + suffix;
        }

        private static string DisplaySlot(string slot)
        {
            switch (slot)
            {
                case "brain": return "Brain";
                case "eye": return "Eye";
                case "ear": return "Ear";
                case "chest": return "Chest";
                case "waist": return "Waist";
                case "leftarm": return "Left Arm";
                case "rightarm": return "Right Arm";
                case "leftwrist": return "Left Wrist";
                case "rightwrist": return "Right Wrist";
                case "lefthand": return "Left Hand";
                case "righthand": return "Right Hand";
                case "thigh": return "Thigh";
                case "feet": return "Feet";
                default: return slot ?? "Unknown";
            }
        }

        private static string FormatTemplate(
            StockTemplate template,
            string centralCharacter,
            string commandPrefix)
        {
            return ItemLink(template, false) + " x" +
                Color(template.Count.ToString(), "#FFFF00") + " " +
                ChatCommand("GET", centralCharacter, commandPrefix + "get " + template.AoId);
        }

        private static string ItemLink(StockTemplate template, bool icon = true)
        {
            return CityBankersChatPalette.ItemLabel(template.AoId, template.HighId,
                template.Ql, template.Name, icon);
        }

        private static string ChatCommand(
            string label,
            string centralCharacter,
            string command)
        {
            string target = EscapeAttribute(centralCharacter ?? string.Empty);
            string body = EscapeAttribute(command ?? string.Empty);
            return "<a href='chatcmd:///tell " + target + " " + body + "'>" +
                EscapeText(label) + "</a>";
        }

        private static string Blob(string label, string content)
        {
            string body = CityBankersChatPalette.WhiteBaseMarkup(content ?? string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", "<br>")
                .Replace("\"", "&quot;");
            return "<a href=\"text://" + body + "\">" + EscapeText(label) + "</a>";
        }

        private static string Color(string text, string color)
        {
            return "<font color='" + color + "'>" + EscapeText(text) + "</font>";
        }

        private static string EscapeText(string text)
        {
            return (text ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static string EscapeAttribute(string text)
        {
            return EscapeText(text)
                .Replace("'", "&#39;")
                .Replace("\"", "&quot;");
        }

        private sealed class StockTemplate
        {
            public int AoId;
            public int HighId;
            public int Ql;
            public string Name;
            public int Count;
        }
    }
}
