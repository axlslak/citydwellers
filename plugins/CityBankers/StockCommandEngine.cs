using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using CityBankers.Shared;

namespace CityBankers
{
    /// <summary>
    /// AO-native, current-stock-driven query tree for symbiant availability.
    ///
    /// Canonical grammar:
    ///   stock [family [slot [targetQl]]]
    ///
    /// Family names are also root aliases, so "support brain 50" is exactly the
    /// same query as "stock support brain 50". Incomplete queries navigate the
    /// current stock tree; a family+slot+QL query answers availability directly.
    /// </summary>
    internal static class StockCommandEngine
    {
        private static readonly string[] FamilyOrder =
        {
            "artillery",
            "infantry",
            "control",
            "support",
            "extermination"
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
                { "ext", "extermination" }
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

            int index = 0;
            string family = null;
            if (string.Equals(raw[0], "stock", StringComparison.OrdinalIgnoreCase))
            {
                index = 1;
            }
            else if (TryNormalizeFamily(raw[0], out family))
            {
                index = 1;
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
                if (family == null)
                    response = BuildRoot(items, centralCharacter, commandPrefix);
                else
                    response = BuildFamily(items, centralCharacter, commandPrefix, family);
                return true;
            }

            if (family == null)
            {
                if (!TryNormalizeFamily(raw[index], out family))
                {
                    response = BuildRoot(items, centralCharacter, commandPrefix);
                    return true;
                }
                index++;
            }

            if (index >= raw.Length)
            {
                response = BuildFamily(items, centralCharacter, commandPrefix, family);
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
            var represented = FamilyOrder
                .Select(family => new
                {
                    Family = family,
                    Items = FamilyItems(items, family)
                })
                .Where(entry => entry.Items.Count > 0)
                .ToList();

            if (represented.Count == 0)
                return "CityBankers stock is empty right now.";

            // AO root tell has exactly one text:// window. Family labels in the tell stay
            // plain/color-coded; the general stock window itself restores the chatcmd tree.
            string inline = string.Join(
                " | ",
                represented.Select(entry =>
                    CityBankersChatPalette.Cyan(DisplayFamily(entry.Family)) +
                    " " + CityBankersChatPalette.Green(entry.Items.Count.ToString()) +
                    " copies"));

            var body = new StringBuilder();
            body.Append(CityBankersChatPalette.White("CityBankers Stock"));
            body.Append("<br><br>");
            foreach (var entry in represented)
            {
                int templates = CountTemplates(entry.Items);
                body.Append(ChatCommand(
                    DisplayFamily(entry.Family),
                    centralCharacter,
                    commandPrefix + "stock " + entry.Family));
                body.Append("  ");
                body.Append(CityBankersChatPalette.Yellow(templates.ToString()));
                body.Append(templates == 1 ? " type / " : " types / ");
                body.Append(CityBankersChatPalette.Green(entry.Items.Count.ToString()));
                body.Append(" copies<br>");
            }

            return "CityBankers currently has: " + inline + ". " +
                Blob("Open stock", body.ToString());
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
                    " symbiants are in stock right now. " +
                    ChatCommand("Stock", centralCharacter, commandPrefix + "stock");
            }

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
                    commandPrefix + "stock " + family + " " + entry.Slot));
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
                        commandPrefix + "stock " + family);
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
                    commandPrefix + "stock " + family + " " + slot + " " + template.Ql));
                body.Append("  ");
                body.Append(ItemLink(template));
                body.Append("  x");
                body.Append(Color(template.Count.ToString(), "#FFFF00"));
                body.Append("<br>");
            }
            body.Append("<br>");
            body.Append(ChatCommand(
                "Back to " + DisplayFamily(family),
                centralCharacter,
                commandPrefix + "stock " + family));

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
                commandPrefix + "stock " + family + " " + slot);

            if (templates.Count == 0)
            {
                return "No " + DisplayFamily(family) + " " + DisplaySlot(slot) +
                    " symbiants are in stock right now. " +
                    ChatCommand(
                        DisplayFamily(family) + " stock",
                        centralCharacter,
                        commandPrefix + "stock " + family);
            }

            List<StockTemplate> exact = templates
                .Where(template => template.Ql == targetQl)
                .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (exact.Count > 0)
            {
                return DisplayFamily(family) + " " + DisplaySlot(slot) +
                    " around QL " + targetQl + ": " +
                    string.Join(" | ", exact.Select(FormatTemplate)) +
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
                parts.Add("below: " + string.Join(" | ", lower.Select(FormatTemplate)));
            }
            if (higherQl.HasValue)
            {
                List<StockTemplate> higher = templates
                    .Where(template => template.Ql == higherQl.Value)
                    .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                parts.Add("above: " + string.Join(" | ", higher.Select(FormatTemplate)));
            }

            return DisplayFamily(family) + " " + DisplaySlot(slot) +
                " around QL " + targetQl + " — " + string.Join("; ", parts) +
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
                    DetectSlot(item?.Name),
                    slot,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
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

        private static string FormatTemplate(StockTemplate template)
        {
            return ItemLink(template) + " QL" + template.Ql + " x" +
                Color(template.Count.ToString(), "#FFFF00");
        }

        private static string ItemLink(StockTemplate template)
        {
            return "<a href='itemref://" + template.AoId + "/" + template.HighId + "/" +
                template.Ql + "'>" + EscapeText(template.Name) + "</a>";
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
