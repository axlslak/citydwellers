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

        public static bool TryBuildResponse(
            string input,
            CurrentStockState stock,
            string centralCharacter,
            out string response,
            string commandPrefix = "")
        {
            response = null;
            string[] raw = (input ?? string.Empty)
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
                return "No symbiants are in stock right now. " +
                    ChatCommand("Stock", centralCharacter, commandPrefix + "stock");

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
            {
                return "No " + DisplayFamily(family) + " " + DisplaySlot(slot) +
                    " symbiants are in stock right now. " +
                    ChatCommand(
                        DisplayFamily(family) + " stock",
                        centralCharacter,
                        commandPrefix + FamilyCommand(family, null));
            }

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
            string back = ChatCommand(
                "All " + DisplayFamily(family) + " " + DisplaySlot(slot),
                centralCharacter,
                commandPrefix + FamilyCommand(family, slot));

            if (templates.Count == 0)
            {
                return "No " + DisplayFamily(family) + " " + DisplaySlot(slot) +
                    " symbiants are in stock right now. " +
                    ChatCommand(
                        DisplayFamily(family) + " stock",
                        centralCharacter,
                        commandPrefix + FamilyCommand(family, null));
            }

            List<StockTemplate> exact = templates
                .Where(template => template.Ql == targetQl)
                .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (exact.Count > 0)
            {
                return DisplayFamily(family) + " " + DisplaySlot(slot) +
                    " around QL " + targetQl + ": " +
                    string.Join(" | ", exact.Select(template => FormatTemplate(
                        template, centralCharacter, commandPrefix))) +
                    ". " + back;
            }

            int? lowerQl = templates
                .Where(template => template.Ql < targetQl)
                .Select(template => (int?)template.Ql)
                .OrderByDescending(value => value)
                .FirstOrDefault();
            int? higherQl = templates
                .Where(template => template.Ql > targetQl)
                .Select(template => (int?)template.Ql)
                .OrderBy(value => value)
                .FirstOrDefault();

            var parts = new List<string>();
            if (lowerQl.HasValue)
            {
                List<StockTemplate> lower = templates
                    .Where(template => template.Ql == lowerQl.Value)
                    .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                parts.Add("below: " + string.Join(" | ", lower.Select(template =>
                    FormatTemplate(template, centralCharacter, commandPrefix))));
            }
            if (higherQl.HasValue)
            {
                List<StockTemplate> higher = templates
                    .Where(template => template.Ql == higherQl.Value)
                    .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                parts.Add("above: " + string.Join(" | ", higher.Select(template =>
                    FormatTemplate(template, centralCharacter, commandPrefix))));
            }

            return DisplayFamily(family) + " " + DisplaySlot(slot) +
                " around QL " + targetQl + " - " + string.Join("; ", parts) +
                ". " + back;
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
            return (items ?? Enumerable.Empty<StockItemState>())
                .Where(item => item != null)
                .Select(item => item.AoId + ":" + item.HighId + ":" + item.Ql)
                .Distinct(StringComparer.Ordinal)
                .Count();
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
            if (value.Contains("eye")) return "eye";
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
