using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers;
using CityBankers.Shared;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessBankerStockCommand(string rawCommand, ReplyTarget target)
        {
            string response;
            if (!StockCommandEngine.TryBuildResponse(
                    rawCommand,
                    RuntimeStateStore.LoadCurrentStock(_settingsDir),
                    Client.CharacterName,
                    out response,
                    CommandPrefix))
            {
                Reply(target, Usage(target, "stock [family [slot [targetQl]]]"));
                return;
            }

            Reply(target, response);
        }

        private void ProcessBankerDonorCommand(string[] parts, ReplyTarget target)
        {
            if (parts == null || parts.Length > 2)
            {
                Reply(target, Usage(target, "donor [top|last|member]"));
                return;
            }

            string[] commandParts = (string[])parts.Clone();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ProcessBankerDonorCommandCore(commandParts, target);
                }
                catch (Exception ex)
                {
                    Logger.Error("DONOR command failed: " + ex);
                    Reply(target, "Donor history is temporarily unavailable.");
                }
            });
        }

        private void ProcessBankerDonorCommandCore(
            string[] parts,
            ReplyTarget target)
        {
            List<DonationRecord> donations = LoadDonationHistory();
            string view = parts.Length == 1
                ? "overview"
                : parts[1].Trim();
            string body;
            string title;
            string label;

            if (string.Equals(view, "top", StringComparison.OrdinalIgnoreCase))
            {
                title = "Top Donors";
                label = "Open top donors";
                body = BuildTopDonorsWindow(donations, target);
            }
            else if (string.Equals(view, "last", StringComparison.OrdinalIgnoreCase))
            {
                title = "Latest Donations";
                label = "Open latest donations";
                body = BuildLatestDonationsWindow(donations, target, null, 25);
            }
            else if (string.Equals(view, "overview", StringComparison.OrdinalIgnoreCase))
            {
                title = "CityBankers Donors";
                label = "Open donor records";
                body = BuildDonorOverviewWindow(donations, target);
            }
            else
            {
                string canonical = ResolveCanonicalAltMain(view);
                title = canonical + " — Donations";
                label = "Open " + canonical + " donations";
                body = BuildLatestDonationsWindow(
                    donations,
                    target,
                    canonical,
                    10);
            }

            Reply(
                target,
                "<font color='" + ColorTitle + "'>CityBankers donors</font> " +
                BuildBlobLinks(target, title, label, body));
        }

        private List<DonationRecord> LoadDonationHistory()
        {
            var records = new Dictionary<string, DonationRecord>(
                StringComparer.Ordinal);
            JObject index = RuntimeStateStore.ReadJson<JObject>(
                Path.Combine(_dataDir, "symbiant-index.json"));
            Dictionary<int, DonationItemMetadata> metadata = LoadDonationMetadata(index);

            JObject ledger = RuntimeStateStore.ReadJson<JObject>(
                Path.Combine(_dataDir, "ledger.json"));
            int ordinal = 0;
            foreach (JObject item in (ledger?["Items"] as JArray ?? new JArray())
                .OfType<JObject>())
            {
                AddDonationRecord(records, item, metadata, "active-" + ordinal++);
            }

            string historyDirectory = Path.Combine(_dataDir, "history");
            if (Directory.Exists(historyDirectory))
            {
                foreach (string path in Directory.GetFiles(
                    historyDirectory,
                    "history-*.jsonl").OrderBy(value => value, StringComparer.Ordinal))
                {
                    int lineNumber = 0;
                    try
                    {
                        foreach (string line in File.ReadLines(path))
                        {
                            lineNumber++;
                            if (string.IsNullOrWhiteSpace(line))
                                continue;
                            JObject history;
                            try
                            {
                                history = JObject.Parse(line);
                            }
                            catch
                            {
                                continue;
                            }

                            JObject item = history["Item"] as JObject;
                            if (item != null)
                            {
                                AddDonationRecord(
                                    records,
                                    item,
                                    metadata,
                                    Path.GetFileName(path) + "-" + lineNumber);
                            }
                        }
                    }
                    catch (IOException ex)
                    {
                        Logger.Warning(
                            "DONOR HISTORY skipped " + Path.GetFileName(path) +
                            ": " + ex.Message);
                    }
                }
            }

            return records.Values
                .OrderByDescending(record => record.ReceivedUtc)
                .ThenBy(record => record.Id, StringComparer.Ordinal)
                .ToList();
        }

        private void AddDonationRecord(
            IDictionary<string, DonationRecord> records,
            JObject item,
            IDictionary<int, DonationItemMetadata> metadata,
            string fallbackId)
        {
            if (item == null)
                return;
            string donor = item["From"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(donor))
                return;

            string id = item["Id"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(id))
                id = fallbackId;
            if (records.ContainsKey(id))
                return;

            int aoId = ParseDonationInt(item["AoId"]);
            DonationItemMetadata detail;
            metadata.TryGetValue(aoId, out detail);
            records[id] = new DonationRecord
            {
                Id = id,
                Donor = donor,
                CanonicalDonor = ResolveCanonicalAltMain(donor),
                ReceivedUtc = ParseDonationUtc(item["ReceivedUtc"]),
                AoId = aoId,
                HighId = detail?.HighId ?? 0,
                Ql = detail?.Ql ?? 0,
                Name = detail?.Name ?? ("Item " + aoId)
            };
        }

        private string BuildDonorOverviewWindow(
            IList<DonationRecord> donations,
            ReplyTarget target)
        {
            int donorCount = donations
                .Select(record => record.CanonicalDonor)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var body = new StringBuilder();
            body.Append("<font color='").Append(ColorText)
                .Append("'>Every item ever received is counted, including items that " +
                        "have since left active stock. Known alts are grouped under their " +
                        "canonical main.</font>\n\n")
                .Append("<font color='").Append(ColorTitle).Append("'>Recorded</font>  ")
                .Append("<font color='").Append(ColorGood).Append("'>")
                .Append(donations.Count).Append(" items</font> from ")
                .Append("<font color='").Append(ColorGood).Append("'>")
                .Append(donorCount).Append(" donors</font>\n\n")
                .Append("  ").Append(CommandLink(target, "donor top", "Top donors"))
                .Append("\n    <font color='").Append(ColorMuted)
                .Append("'>Ranked by all-time donated items.</font>\n")
                .Append("  ").Append(CommandLink(target, "donor last", "Latest 25"))
                .Append("\n    <font color='").Append(ColorMuted)
                .Append("'>Most recently received items.</font>\n\n")
                .Append("<font color='").Append(ColorMuted)
                .Append("'>Use #donor &lt;member&gt; for one member's latest 10 and " +
                        "all-time total.</font>");
            return body.ToString();
        }

        private string BuildTopDonorsWindow(
            IList<DonationRecord> donations,
            ReplyTarget target)
        {
            var ranked = donations
                .Where(record => !string.IsNullOrWhiteSpace(record.CanonicalDonor))
                .GroupBy(record => record.CanonicalDonor, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Name = group.Key, Count = group.Count() })
                .OrderByDescending(entry => entry.Count)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var body = new StringBuilder();
            body.Append("<font color='").Append(ColorText)
                .Append("'>All-time donated item totals. Alts count toward their main.</font>\n\n");
            for (int index = 0; index < ranked.Count; index++)
            {
                var entry = ranked[index];
                body.Append("<font color='").Append(index < 3 ? ColorCommand : ColorMuted)
                    .Append("'>#").Append(index + 1).Append("</font>  ")
                    .Append(CommandLink(target, "donor " + entry.Name, entry.Name))
                    .Append("  <font color='").Append(ColorGood).Append("'>")
                    .Append(entry.Count).Append(entry.Count == 1 ? " item" : " items")
                    .Append("</font>\n");
            }
            if (ranked.Count == 0)
                body.Append("<font color='").Append(ColorMuted)
                    .Append("'>No donation records yet.</font>\n");
            body.Append("\n").Append(CommandLink(target, "donor last", "Latest donations"));
            return body.ToString();
        }

        private string BuildLatestDonationsWindow(
            IList<DonationRecord> donations,
            ReplyTarget target,
            string canonicalDonor,
            int limit)
        {
            List<DonationRecord> matches = donations
                .Where(record => string.IsNullOrWhiteSpace(canonicalDonor) ||
                    string.Equals(
                        record.CanonicalDonor,
                        canonicalDonor,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            var body = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(canonicalDonor))
            {
                body.Append("<font color='").Append(ColorTitle).Append("'>")
                    .Append(EscapeBlobText(canonicalDonor)).Append("</font> donated ")
                    .Append("<font color='").Append(ColorGood).Append("'>")
                    .Append(matches.Count).Append(matches.Count == 1 ? " item" : " items")
                    .Append("</font> all time.\n")
                    .Append("<font color='").Append(ColorMuted)
                    .Append("'>Showing the latest ").Append(Math.Min(limit, matches.Count))
                    .Append(".</font>\n\n");
            }
            else
            {
                body.Append("<font color='").Append(ColorText)
                    .Append("'>Latest ").Append(Math.Min(limit, matches.Count))
                    .Append(" donated items across CityBankers.</font>\n\n");
            }

            foreach (DonationRecord record in matches.Take(limit))
            {
                body.Append("<font color='").Append(ColorMuted).Append("'>")
                    .Append(FormatDonationUtc(record.ReceivedUtc))
                    .Append("</font>  ")
                    .Append(BuildDonationItemLink(record))
                    .Append("\n    by ")
                    .Append(CommandLink(
                        target,
                        "donor " + record.CanonicalDonor,
                        record.CanonicalDonor));
                if (!string.Equals(
                        record.Donor,
                        record.CanonicalDonor,
                        StringComparison.OrdinalIgnoreCase))
                {
                    body.Append(" <font color='").Append(ColorMuted).Append("'>(via ")
                        .Append(EscapeBlobText(record.Donor)).Append(")</font>");
                }
                body.Append("\n\n");
            }
            if (matches.Count == 0)
                body.Append("<font color='").Append(ColorMuted)
                    .Append("'>No matching donation records.</font>\n\n");
            body.Append(CommandLink(target, "donor top", "Top donors"))
                .Append("  |  ")
                .Append(CommandLink(target, "donor last", "Latest 25"));
            return body.ToString();
        }

        private static Dictionary<int, DonationItemMetadata> LoadDonationMetadata(
            JObject index)
        {
            var result = new Dictionary<int, DonationItemMetadata>();
            foreach (JObject item in (index?["Items"] as JArray ?? new JArray())
                .OfType<JObject>())
            {
                int aoId = ParseDonationInt(item["AoId"]);
                if (aoId == 0 || result.ContainsKey(aoId))
                    continue;
                result[aoId] = new DonationItemMetadata
                {
                    HighId = ParseDonationInt(item["HighId"]),
                    Ql = ParseDonationInt(item["Ql"]),
                    Name = item["Name"]?.ToString() ?? ("Item " + aoId)
                };
            }
            return result;
        }

        private static string BuildDonationItemLink(DonationRecord record)
        {
            string label = EscapeBlobText(record.Name) +
                (record.Ql > 0 ? " (QL " + record.Ql + ")" : string.Empty);
            if (record.AoId <= 0 || record.HighId <= 0 || record.Ql <= 0)
                return "<font color='" + ColorText + "'>" + label + "</font>";
            return "<a href='itemref://" + record.AoId + "/" + record.HighId +
                "/" + record.Ql + "'><font color='" + ColorText + "'>" +
                label + "</font></a>";
        }

        private static DateTime ParseDonationUtc(JToken token)
        {
            DateTime parsed;
            if (token != null && DateTime.TryParse(
                    token.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out parsed))
            {
                return UtcTimestamp.Normalize(parsed);
            }
            return DateTime.MinValue;
        }

        private static string FormatDonationUtc(DateTime value)
        {
            return value == DateTime.MinValue
                ? "time unavailable"
                : value.ToString(
                    "yyyy-MM-dd HH:mm 'UTC'",
                    CultureInfo.InvariantCulture);
        }

        private static int ParseDonationInt(JToken token)
        {
            int value;
            return token != null && int.TryParse(token.ToString(), out value)
                ? value
                : 0;
        }

        private sealed class DonationRecord
        {
            public string Id;
            public string Donor;
            public string CanonicalDonor;
            public DateTime ReceivedUtc;
            public int AoId;
            public int HighId;
            public int Ql;
            public string Name;
        }

        private sealed class DonationItemMetadata
        {
            public int HighId;
            public int Ql;
            public string Name;
        }
    }
}
