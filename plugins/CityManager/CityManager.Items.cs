using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private const int ItemsPageSize = 30;
        private int _itemsSearchBusy;

        private void ProcessItemsCommand(string[] parts, ReplyTarget target)
        {
            bool byId = string.Equals(parts[0], "itemid", StringComparison.OrdinalIgnoreCase);
            int id;
            if (byId && (parts.Length != 2 || !int.TryParse(parts[1], out id) || id <= 0))
            { Reply(target, Usage(target, "itemid <AOID>")); return; }
            if (!byId && parts.Length < 2)
            { Reply(target, Usage(target, "items [QL] <name words> [-excluded-word] [--page N]")); return; }
            if (string.Join(" ", parts).Length > 300)
            { Reply(target, "Please shorten the item search to 300 characters."); return; }

            var arguments = parts.Skip(1).ToList();
            int page = 1;
            if (!byId && arguments.Count >= 2 && arguments[arguments.Count - 2] == "--page")
            {
                if (!int.TryParse(arguments.Last(), out page) || page < 1)
                { Reply(target, "Page must be a positive number."); return; }
                arguments.RemoveRange(arguments.Count - 2, 2);
            }
            int? quality = null; int parsed;
            if (!byId && arguments.Count > 1 && int.TryParse(arguments[0], out parsed))
            {
                if (parsed < 1 || parsed > 500)
                { Reply(target, "QL must be between 1 and 500."); return; }
                quality = parsed; arguments.RemoveAt(0);
            }
            string query = string.Join(" ", arguments);
            if (!byId && (arguments.Count == 0 || !arguments.Any(a => !a.StartsWith("-", StringComparison.Ordinal))))
            { Reply(target, "Include at least one name word in the search."); return; }
            // A lone number is an exact AOID; QL searches also require a name.
            bool numeric = int.TryParse(query, out id) && id > 0;
            if (byId && !numeric) { Reply(target, Usage(target, "itemid <AOID>")); return; }
            var catalog = ItemCatalog.Current;
            if (catalog == null) { Reply(target, ItemCatalog.Status); return; }
            if (Interlocked.CompareExchange(ref _itemsSearchBusy, 1, 0) != 0)
            { Reply(target, "An item search is running; try again shortly."); return; }
            QueuePublicWork(target, () =>
            {
                try
                {
                    var families = CityBankers.Shared.SymbiantCatalog.GetPhatzFamilies(_settingsDir);
                    if (numeric)
                    {
                        ItemDefinition item = catalog.Find(id);
                        if (item == null) { Reply(target, "No item template with AOID " + id + " in this dump."); return; }
                        string details = ItemRow(item) + "\n" +
                            "NoDrop: " + ItemFact(item.NoDrop) + "\nUnique: " + ItemFact(item.Unique) +
                            "\nStackable: " + ItemFact(item.Stackable) + "\nCantSplit: " + ItemFact(item.CantSplit) +
                            "\nSplittable: " + ItemFact(item.Splittable) +
                            "\nFlags: " + ItemBits(item.Flags) + "\nCan: " + ItemBits(item.Can) +
                            "\n\nObserved family AOIDs: " + string.Join(", ", families.Members(id)) +
                            "\n" + string.Join("\n", families.PairsFor(id).Select(p =>
                                "Observed pair " + p.LowId + "/" + p.HighId + " | template QLs " +
                                ItemQuality(catalog.Find(p.LowId)?.Quality) + "-" +
                                ItemQuality(catalog.Find(p.HighId)?.Quality)));
                        Reply(target, BuildBlobLinks(target, "Item " + id, "Item " + id, details));
                        return;
                    }
                    int total;
                    ItemSearchMatch[] results = catalog.SearchFamilies(query, quality, families, page, ItemsPageSize, out total);
                    if (total == 0) { Reply(target, "No items match " + EscapeBlobText(query) +
                        (quality.HasValue ? " at QL " + quality.Value : "") + "."); return; }
                    int pages = (total + ItemsPageSize - 1) / ItemsPageSize;
                    if (page > pages) { Reply(target, "This search has " + pages + " pages."); return; }
                    var body = new StringBuilder();
                    body.Append(HelpHeader("Items", total + " results; page " + page + "/" + pages + "."));
                    foreach (ItemSearchMatch item in results)
                    {
                        body.Append(ItemMatchRow(item)).Append(" ")
                            .Append(CommandLink(target, "itemid " + item.LowId, "INFO")).Append("\n");
                    }
                    string command = "items " + (quality.HasValue ? quality.Value + " " : "") + query;
                    if (page > 1) body.Append("\n").Append(CommandLink(target, command + " --page " + (page - 1), "Previous"));
                    if (page < pages) body.Append("\n").Append(CommandLink(target, command + " --page " + (page + 1), "Next"));
                    body.Append("\n\nRanges use low/high pairs observed in local policy or ledger. Unpaired templates keep their exact QL; names alone never establish a range.");
                    Reply(target, BuildBlobLinks(target, "Items", "Items: " + query + " (" + total + ")", body.ToString()));
                }
                catch (Exception ex)
                {
                    AOSharp.Clientless.Logging.Logger.Error("Item search failed: " + ex);
                    Reply(target, "Item search failed; see the Manager log.");
                }
                finally { Interlocked.Exchange(ref _itemsSearchBusy, 0); }
            });
        }
        private static string ItemMatchRow(ItemSearchMatch item)
        {
            string label = EscapeBlobText(item.Template.Name);
            if (item.Quality > 0)
                label = "<a href='itemref://" + item.LowId + "/" + item.HighId + "/" + item.Quality.Value + "'>" + label + "</a>";
            return "QL " + ItemQuality(item.Quality) + "  " + label + "  [" + item.LowId +
                (item.ObservedPair ? "/" + item.HighId + "; range " + item.LowQuality + "-" + item.HighQuality : "") + "]";
        }

        private static string ItemRow(ItemDefinition item)
        {
            string label = EscapeBlobText(item.Name);
            if (item.Quality > 0)
                label = "<a href='itemref://" + item.AoId + "/" + item.AoId + "/" + item.Quality.Value + "'>" + label + "</a>";
            return "QL " + (item.Quality.HasValue ? item.Quality.Value.ToString(CultureInfo.InvariantCulture) : "?") +
                "  " + label + "  [" + item.AoId + "]" + (item.NoDrop == true ? " NoDrop" : "") +
                (item.Unique == true ? " Unique" : "");
        }
        private static string ItemQuality(int? quality) => quality.HasValue ? quality.Value.ToString(CultureInfo.InvariantCulture) : "?";
        private static string ItemFact(bool? value) => value.HasValue ? (value.Value ? "yes" : "no") : "unknown";
        private static string ItemBits(uint? value) => value.HasValue ? "0x" + value.Value.ToString("X8", CultureInfo.InvariantCulture) : "unknown";
    }
}
