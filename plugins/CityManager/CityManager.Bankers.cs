using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private BankerStatusSnapshot BuildBankerStatusSnapshot()
        {
            try
            {
                JObject bankers = CityBankers.Shared.SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = bankers.GetValue("Roles", StringComparison.OrdinalIgnoreCase) as JObject;
                StorageState storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                List<WithdrawalState> withdrawals = WithdrawalStore.LoadAll(_settingsDir);
                bool allReady = File.Exists(Path.Combine(
                    _dataDir, "citybankers-all-bankers-ready.json"));
                int processId = Process.GetCurrentProcess().Id;
                int totalUsed = 0;
                int totalCapacity = 0;
                bool allUsable = true;
                var lines = new StringBuilder();
                var diagnostics = new List<string>();

                foreach (string role in new[]
                {
                    "central", "artillery", "infantry", "control", "support", "extermination",
                    "spirit", "dyna", "phatz"
                })
                {
                    JObject roleConfig = roles?.GetValue(role, StringComparison.OrdinalIgnoreCase) as JObject;
                    string character = roleConfig?.GetValue(
                        "Character", StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (string.IsNullOrWhiteSpace(character))
                    {
                        allUsable = false;
                        lines.Append(StatusLine(false, role, "Not configured"));
                        diagnostics.Add(role + "=not-configured");
                        continue;
                    }

                    string token = string.Concat(character.Where(char.IsLetterOrDigit));
                    JObject heartbeat = RuntimeStateStore.ReadJson<JObject>(Path.Combine(
                        _dataDir, "citybankers-health-" + token + ".json"));
                    bool sameProcess = ParseDonationInt(heartbeat?["ProcessId"]) == processId;
                    bool online = sameProcess && ParseBool(heartbeat?["InPlay"]);
                    bool bankOpen = online && ParseBool(heartbeat?["BankOpen"]);
                    bool roleReady = string.Equals(role, "central", StringComparison.OrdinalIgnoreCase)
                        ? allReady
                        : File.Exists(Path.Combine(
                            _dataDir, "citybankers-storage-writefront-ready-" + token + ".json"));

                    List<DispatchBatchState> roleBatches = (queue?.Batches ?? new List<DispatchBatchState>())
                        .Where(batch => batch != null &&
                            (string.Equals(batch.Character, character, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(batch.Role, role, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    int failed = roleBatches.Count(batch => string.Equals(
                        batch.Status, "failed", StringComparison.OrdinalIgnoreCase));
                    int pending = roleBatches.Count - failed;
                    List<WithdrawalState> localOrders = withdrawals.Where(row =>
                        WithdrawalStore.IsActive(row) &&
                        (string.Equals(row.SourceCharacter, character, StringComparison.OrdinalIgnoreCase) ||
                         role == "central")).ToList();
                    bool withdrawalHere = localOrders.Count > 0;
                    bool withdrawalFailed = localOrders.Any(row => WithdrawalStore.HasStatus(row, "failed"));
                    bool stuck = failed > 0 || (withdrawalFailed && role != "central");
                    bool usable = online && bankOpen && roleReady && !stuck;
                    bool busy = !stuck && (pending > 0 || withdrawalHere);
                    allUsable &= usable;

                    StorageWorkerState worker = (storage?.Workers ?? new List<StorageWorkerState>())
                        .FirstOrDefault(value => value != null &&
                            string.Equals(value.Character, character, StringComparison.OrdinalIgnoreCase));
                    int used = worker?.Bags?.Sum(bag => bag?.Items?.Count ?? 0) ?? 0;
                    int capacity = worker?.Bags?.Sum(bag => Math.Max(0, bag?.Capacity ?? 0)) ?? 0;
                    List<SymbiantCatalog.AcceptanceRule> roleRules = SymbiantCatalog
                        .GetRules(_settingsDir)
                        .Where(rule => string.Equals(rule.Role, role, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    bool unboundedPolicy = roleRules.Any(rule =>
                        rule.MaxCopies == SymbiantCatalog.KeepAllCopies);
                    int policySlots = roleRules.Where(rule => rule.MaxCopies > 0)
                        .Sum(rule => rule.MaxCopies);
                    totalUsed += used;
                    totalCapacity += capacity;

                    string state = !online ? "OFFLINE" : stuck ? "STUCK" :
                        !bankOpen ? "ONLINE, bank unavailable" :
                        !roleReady ? "ONLINE, starting" : withdrawalFailed ? "USABLE, some items held" :
                        busy ? "USABLE, busy" : "USABLE";
                    string capacityText = capacity > 0
                        ? "; storage " + used + "/" + capacity
                        : string.Empty;
                    string workText = failed > 0 ? "; failed " + failed :
                        pending > 0 ? "; queued " + pending : string.Empty;
                    if (withdrawalHere)
                        workText += "; reserved items " + localOrders.Count;
                    if (unboundedPolicy)
                        workText += "; retention unbounded - monitor free space";
                    else if (policySlots > capacity && capacity > 0)
                        workText += "; policy ceiling " + policySlots + " exceeds capacity by " +
                            (policySlots - capacity);

                    lines.Append(StatusLine(usable, character + " (" + role + ")",
                        state + capacityText + workText));
                    diagnostics.Add(character + "=" + state.ToLowerInvariant() +
                        (capacity > 0 ? " " + used + "/" + capacity : string.Empty));
                }

                string total = "Storage " + totalUsed + "/" + totalCapacity +
                    " slots used; " + Math.Max(0, totalCapacity - totalUsed) + " free. Orders " +
                    withdrawals.Where(WithdrawalStore.IsActive).Select(row => row.OrderId).Distinct().Count() +
                    "/4; ready items " + withdrawals.Count(row => WithdrawalStore.HasStatus(row, "central-ready")) +
                    "; held items " + withdrawals.Count(row => WithdrawalStore.HasStatus(row, "failed")) + ".";
                return new BankerStatusSnapshot
                {
                    IsUsable = allUsable,
                    Summary = total,
                    Blob = lines.ToString() + "  <font color='" + ColorMuted + "'>" +
                        EscapeBlobText(total) + "</font>\n",
                    DiagnosticText = string.Join(", ", diagnostics)
                };
            }
            catch (Exception ex)
            {
                return new BankerStatusSnapshot
                {
                    IsUsable = false,
                    Summary = "Banker health unavailable",
                    Blob = StatusLine(false, "Bankers", "Health unavailable: " + ex.Message),
                    DiagnosticText = "unavailable: " + ex.Message
                };
            }
        }

        private static bool ParseBool(JToken token)
        {
            bool value;
            return token != null && bool.TryParse(token.ToString(), out value) && value;
        }

        private sealed class BankerStatusSnapshot
        {
            public bool IsUsable;
            public string Summary;
            public string Blob;
            public string DiagnosticText;
        }

        private void ProcessBankerStockCommand(string rawCommand, ReplyTarget target)
        {
            CurrentStockState stock = RuntimeStateStore.LoadCurrentStock(_settingsDir);
            foreach (WithdrawalState row in WithdrawalStore.LoadAll(_settingsDir))
                HideReservedWithdrawalCopy(stock, row);
            string response;
            if (!StockCommandEngine.TryBuildResponse(
                    rawCommand,
                    stock,
                    Client.CharacterName,
                    out response,
                    CommandPrefix))
            {
                Reply(target, Usage(target, "stock [family [slot [targetQl]]]"));
                return;
            }

            Reply(target, response);
        }

        private void ProcessBankerWithdrawalCommand(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            if (parts == null || parts.Length != 2)
            {
                Reply(target, Usage(target, "get [AO item ID]"));
                return;
            }

            int aoId;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out aoId) ||
                aoId <= 0)
            {
                Reply(target, "That is not a valid Anarchy Online item ID.");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    BeginBankerWithdrawal(senderName, aoId, target);
                }
                catch (Exception ex)
                {
                    Logger.Error("WITHDRAW command failed: " + ex);
                    Reply(target, "The bank could not start that withdrawal safely.");
                }
            });
        }

        private void BeginBankerWithdrawal(string senderName, int aoId, ReplyTarget target)
        {
            List<WithdrawalState> reservations = WithdrawalStore.LoadAll(_settingsDir);
            var reservedIds = new HashSet<string>(reservations.Where(WithdrawalStore.IsActive)
                .Select(row => row.ActiveLedgerId), StringComparer.Ordinal);

            JObject ledger = RuntimeStateStore.ReadJson<JObject>(
                Path.Combine(_dataDir, "ledger.json"));
            JObject selected = (ledger?["Items"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Where(item => ParseDonationInt(item["AoId"]) == aoId &&
                    !reservedIds.Contains(item["Id"]?.ToString()))
                .OrderBy(item => ParseDonationUtc(item["ReceivedUtc"]))
                .ThenBy(item => item["Id"]?.ToString(), StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected == null)
            {
                Reply(target, "That item is not currently available in CityBankers stock.");
                return;
            }

            CurrentStockState stock = RuntimeStateStore.LoadCurrentStock(_settingsDir);
            string transactionId = selected["TransactionId"]?.ToString();
            string character = selected["Character"]?.ToString();
            string location = selected["Location"]?.ToString();
            int bag = ParseDonationInt(selected["Bag"]);
            int slot = ParseDonationInt(selected["Slot"]);
            StockItemState physical = (stock.Items ?? new List<StockItemState>())
                .FirstOrDefault(item => item != null &&
                    item.AoId == aoId &&
                    string.Equals(item.TransactionId, transactionId, StringComparison.Ordinal) &&
                    string.Equals(item.Character, character, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.BagSource, location, StringComparison.OrdinalIgnoreCase) &&
                    item.BagOuterSlot == bag && item.InnerSlot == slot);
            if (physical == null || string.IsNullOrWhiteSpace(physical.Character))
            {
                Reply(target,
                    "That ledger entry has no exact live storage location. Run reconciliation before withdrawing it.");
                return;
            }

            string canonical = ResolveCanonicalAltMain(senderName);
            List<string> allowed = GetAltIdentityCandidates(senderName);
            if (!allowed.Any(name => string.Equals(name, senderName, StringComparison.OrdinalIgnoreCase)))
                allowed.Add(senderName);
            if (!allowed.Any(name => string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase)))
                allowed.Add(canonical);

            var request = new WithdrawalState
            {
                Id = "wd-" + Guid.NewGuid().ToString("N"),
                Status = "requested",
                CreatedUtc = DateTime.UtcNow,
                RequestedBy = senderName,
                RecipientMain = canonical,
                AllowedCharacters = allowed.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ActiveLedgerId = selected["Id"]?.ToString(),
                DonationTransactionId = transactionId,
                SourceRole = physical.PhysicalRole ?? physical.Role,
                SourceCharacter = physical.Character,
                SourceBag = physical.BagSource,
                SourceBagOuterSlot = physical.BagOuterSlot,
                SourceInnerSlot = physical.InnerSlot,
                SourceItemIdentity = physical.UniqueIdentity,
                Item = new TransferItemState
                {
                    UniqueIdentity = physical.UniqueIdentity,
                    AoId = physical.AoId,
                    HighId = physical.HighId,
                    Ql = physical.Ql,
                    Name = physical.Name
                }
            };
            string admissionError;
            if (!WithdrawalStore.TryAdd(_settingsDir, request, out admissionError))
            {
                Reply(target, admissionError);
                return;
            }
            Reply(target,
                "Withdrawal " + request.Id.Substring(request.Id.Length - 8) +
                " started for " + (physical.Name ?? ("AOID " + aoId)) +
                ". Added to your order (maximum three items). Ready items remain collectible; " +
                "the pickup clock resets to three minutes now and when this item arrives.");
        }

        private static void HideReservedWithdrawalCopy(
            CurrentStockState stock,
            WithdrawalState withdrawal)
        {
            if (stock == null || stock.Items == null || !WithdrawalStore.IsActive(withdrawal))
                return;
            StockItemState reserved = stock.Items.FirstOrDefault(item => item != null &&
                item.AoId == (withdrawal.Item?.AoId ?? 0) &&
                string.Equals(item.TransactionId, withdrawal.DonationTransactionId, StringComparison.Ordinal) &&
                string.Equals(item.Character, withdrawal.SourceCharacter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.BagSource, withdrawal.SourceBag, StringComparison.OrdinalIgnoreCase) &&
                item.BagOuterSlot == withdrawal.SourceBagOuterSlot &&
                item.InnerSlot == withdrawal.SourceInnerSlot);
            if (reserved != null)
                stock.Items.Remove(reserved);
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
                AddDonationRecord(records, item, metadata, "active-" + ordinal++, true);
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
                                    Path.GetFileName(path) + "-" + lineNumber,
                                    false);
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

            List<WithdrawalState> withdrawals = WithdrawalStore.LoadAll(_settingsDir);
            foreach (WithdrawalState withdrawal in withdrawals.Where(WithdrawalStore.IsActive))
            {
                DonationRecord reserved;
                if (!string.IsNullOrWhiteSpace(withdrawal.ActiveLedgerId) &&
                    records.TryGetValue(withdrawal.ActiveLedgerId, out reserved))
                    reserved.Available = false;
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
            string fallbackId,
            bool available)
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
                Name = detail?.Name ?? ("Item " + aoId),
                Available = available
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
                    .Append(record.Available
                        ? "  " + CommandLink(target, "get " + record.AoId, "GET")
                        : string.Empty)
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
            public bool Available;
        }

        private sealed class DonationItemMetadata
        {
            public int HighId;
            public int Ql;
            public string Name;
        }
    }
}
