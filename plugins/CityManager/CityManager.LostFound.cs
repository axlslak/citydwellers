using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessLostFoundCommand(string[] parts, ReplyTarget target)
        {
            bool lost = string.Equals(parts[0], "lost", StringComparison.OrdinalIgnoreCase);
            string[] words = parts.Skip(1).Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Read the journal first so a newly added claim cannot look absent in an older ledger snapshot.
                    var losses = lost ? LostItemsStore.Read(_settingsDir) : null;
                    var ledger = RuntimeStateStore.ReadJsonStrict<JObject>(Path.Combine(_dataDir, "ledger.json"));
                    var items = ledger?["Items"] as JArray;
                    if (ledger != null && (items == null || items.Any(i => !(i is JObject) ||
                        string.IsNullOrWhiteSpace(i["Id"]?.ToString()))))
                        throw new InvalidDataException("Invalid ledger.");
                    // Without a ledger, pending write-ahead records cannot be classified as removed.
                    var active = new HashSet<string>((items ?? new JArray()).Select(i => i["Id"].ToString()),
                        StringComparer.Ordinal);
                    var index = RuntimeStateStore.ReadJsonStrict<JObject>(Path.Combine(_dataDir, "symbiant-index.json"));
                    var names = (index?["Items"] as JArray ?? new JArray()).OfType<JObject>()
                        .GroupBy(i => i["AoId"]?.ToString() ?? "")
                        .ToDictionary(g => g.Key, g => g.First()["Name"]?.ToString());
                    Func<JObject, string> nameOf = item =>
                    {
                        string name;
                        return names.TryGetValue(item["AoId"]?.ToString() ?? "", out name) &&
                            !string.IsNullOrWhiteSpace(name) ? name : "AOID " + item["AoId"];
                    };
                    var rows = new List<Tuple<JObject, LostItemRecord, string, DateTime>>();
                    if (lost)
                    {
                        // Exclusions are committed before delivery/deletion; refresh them after the ledger snapshot.
                        var excluded = new HashSet<string>(LostItemsStore.Read(_settingsDir).Entries
                            .Where(e => e.ExcludedReason != null).Select(e => e.IncidentId), StringComparer.Ordinal);
                        foreach (var record in losses.Entries.Where(e =>
                            !excluded.Contains(e.IncidentId) &&
                            e.ExcludedReason == null && (e.RemovalRecordedUtc.HasValue ||
                                (ledger != null && !active.Contains(e.LedgerId)))))
                        {
                            string name = string.IsNullOrWhiteSpace(record.ItemName)
                                ? nameOf(record.PreviousLedgerEntry) : record.ItemName;
                            rows.Add(Tuple.Create(record.PreviousLedgerEntry, record, name, record.DiscoveredUtc));
                        }
                    }
                    else
                    {
                        foreach (var item in (items ?? new JArray()).OfType<JObject>()
                            .Where(i => string.IsNullOrWhiteSpace(i["From"]?.ToString())))
                            rows.Add(Tuple.Create(item, (LostItemRecord)null, nameOf(item),
                                BookkeepingDate(item["ReceivedUtc"])));
                    }
                    var matches = rows.Where(r => (int?)r.Item1["AoId"] != CruPolicy.AoId && words.All(w =>
                        r.Item3.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                        .OrderByDescending(r => r.Item4).ThenBy(r => r.Item1["Id"]?.ToString(), StringComparer.Ordinal).ToList();
                    string title = lost ? "Lost items" : "Found items";
                    string count = matches.Count.ToString(CultureInfo.InvariantCulture) + " incidents";
                    if (matches.Count == 0) { Reply(target, count + "."); return; }
                    var body = new StringBuilder();
                    body.Append(BookkeepingColor(title, "#89D2E8")).Append("\n")
                        .Append("Showing newest ").Append(Math.Min(25, matches.Count)).Append(" of ")
                        .Append(matches.Count).Append(words.Length == 0 ? " incidents.\n" : " matching incidents.\n")
                        .Append(lost ? "Missing discovered times; actual loss time and cause may be unknown.\n\n"
                            : "Current ledger items without a recorded donor.\n\n");
                    foreach (var row in matches.Take(25))
                    {
                        JObject item = row.Item1;
                        body.Append(CityBankersChatPalette.ItemLabel(
                            (int?)item["AoId"] ?? 0, (int?)item["HighId"] ?? 0,
                            (int?)item["Ql"] ?? 0, BookkeepingEscape(row.Item3))).Append("\n");
                        string donor = item["From"]?.ToString();
                        body.Append("  ").Append(string.IsNullOrWhiteSpace(donor) ? "First recorded " : "Received ")
                            .Append(BookkeepingColor(BookkeepingTime(BookkeepingDate(item["ReceivedUtc"])), "#FFFF00"))
                            .Append(" · ").Append(BookkeepingColor(string.IsNullOrWhiteSpace(donor) ? "Donor unknown" : donor, "#89D2E8")).Append("\n");
                        if (lost)
                            body.Append("  ").Append(BookkeepingColor("Missing discovered " + BookkeepingTime(row.Item4), "#FF4040"))
                                .Append("\n  ").Append(BookkeepingEscape(row.Item2.Reason ?? "Cause unknown.")).Append("\n");
                        body.Append("  ").Append(BookkeepingColor(
                            (lost ? "Last location: " : "Location: ") + (item["Character"]?.ToString() ?? "unknown") +
                            " / " + (item["Location"]?.ToString() ?? "unknown") +
                            " / bag " + (item["Bag"]?.ToString() ?? "?") +
                            " / slot " + (item["Slot"]?.ToString() ?? "?"), "#AAB8C5")).Append("\n")
                            .Append("  ").Append(BookkeepingColor("Ledger " + item["Id"] +
                                " · Transaction " + (item["TransactionId"]?.ToString() ?? "unknown"), "#AAB8C5")).Append("\n");
                        if (lost && !string.IsNullOrWhiteSpace(row.Item2.Evidence))
                            body.Append("  ").Append(BookkeepingColor(row.Item2.Evidence, "#AAB8C5")).Append("\n");
                        body.Append("\n");
                    }
                    Reply(target, BuildBlobLinks(target, title, "View " + title.ToLowerInvariant(), body.ToString())
                        .Select(link => count + " " + link));
                }
                catch (Exception ex)
                {
                    Logger.Error("LOST/FOUND command failed: " + ex);
                    Reply(target, "Item records could not be read safely. Please try again later.");
                }
            });
        }

        private static DateTime BookkeepingDate(JToken token)
        {
            DateTime value;
            return DateTime.TryParse(token?.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value) ? value : DateTime.MinValue;
        }

        private static string BookkeepingTime(DateTime value) => value == DateTime.MinValue
            ? "unknown" : value.ToString("dd-MMM-yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        private static string BookkeepingEscape(string value) => (value ?? "").Replace("&", "&amp;")
            .Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        private static string BookkeepingColor(string value, string color) =>
            "<font color='" + color + "'>" + BookkeepingEscape(value) + "</font>";
    }
}
