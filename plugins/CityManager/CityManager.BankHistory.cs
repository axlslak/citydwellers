using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CityBankers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private sealed class BankHistoryQuery
        {
            public string Player, Item;
            public int? AoId;
            public bool Top, Overview;
        }

        private sealed class BankHistoryToken
        {
            public string Text;
            public bool Quoted;
        }

        private sealed class BankHistoryRow
        {
            public string Id, Main, Character, Name;
            public DateTime When;
            public int AoId, HighId, Ql;
            public bool Available;
        }

        private const string BankHistorySyntax =
            "[last|top] [player <name>] [item <name>] [aoid <id>]";

        private static string HistoryPlayerCommand(string command, string player, BankHistoryQuery query)
        {
            string result = command + " player " + player;
            if (query.Item != null) result += " item \"" + query.Item + "\"";
            if (query.AoId.HasValue) result += " aoid " + query.AoId.Value;
            return result;
        }

        private static bool HistorySelector(BankHistoryToken token)
        {
            if (token.Quoted) return false;
            return token.Text.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                token.Text.Equals("item", StringComparison.OrdinalIgnoreCase) ||
                token.Text.Equals("aoid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HistoryAnd(BankHistoryToken token)
        {
            return !token.Quoted && (token.Text == "&" ||
                token.Text.Equals("and", StringComparison.OrdinalIgnoreCase));
        }

        // Quotes protect item words that happen to be selector names.
        private static bool TryHistoryTokens(string text, out List<BankHistoryToken> tokens)
        {
            tokens = new List<BankHistoryToken>();
            var word = new StringBuilder();
            bool quoted = false, hadQuote = false;
            foreach (char c in text)
            {
                if (c == '"') { quoted = !quoted; hadQuote = true; continue; }
                if (!quoted && (char.IsWhiteSpace(c) || c == '&'))
                {
                    if (word.Length > 0 || hadQuote)
                    {
                        tokens.Add(new BankHistoryToken { Text = word.ToString(), Quoted = hadQuote });
                        word.Clear(); hadQuote = false;
                    }
                    if (c == '&') tokens.Add(new BankHistoryToken { Text = "&" });
                }
                else word.Append(c);
            }
            if (quoted) return false;
            if (word.Length > 0 || hadQuote)
                tokens.Add(new BankHistoryToken { Text = word.ToString(), Quoted = hadQuote });
            return true;
        }

        private bool TryBankHistoryQuery(string[] parts, out BankHistoryQuery query, out string error)
        {
            query = new BankHistoryQuery();
            error = null;
            List<BankHistoryToken> tokens;
            if (!TryHistoryTokens(string.Join(" ", parts.Skip(1)), out tokens))
            { error = "Close the quoted item name."; return false; }
            if (tokens.Count == 0) { query.Overview = true; return true; }
            int index = 0;
            if (!tokens[0].Quoted && (tokens[0].Text.Equals("top", StringComparison.OrdinalIgnoreCase) ||
                tokens[0].Text.Equals("last", StringComparison.OrdinalIgnoreCase)))
            {
                query.Top = tokens[0].Text.Equals("top", StringComparison.OrdinalIgnoreCase);
                index++;
                if (index == tokens.Count) return true;
            }
            // Retain the established single-player shorthand; explicit selectors never guess.
            if (index == 0 && tokens.Count == 1 && !HistorySelector(tokens[0]) &&
                !HistoryAnd(tokens[0]) && !string.IsNullOrWhiteSpace(tokens[0].Text))
            { query.Player = ResolveCanonicalAltMain(tokens[0].Text); return true; }
            bool haveFilter = false;
            while (index < tokens.Count)
            {
                if (HistoryAnd(tokens[index]))
                {
                    if (!haveFilter || ++index == tokens.Count)
                    { error = "AND needs a filter on each side."; return false; }
                }
                var selector = tokens[index++];
                if (!HistorySelector(selector))
                { error = "Use player, item or aoid before each search value."; return false; }
                string key = selector.Text.ToLowerInvariant();
                int first = index;
                while (index < tokens.Count && !HistorySelector(tokens[index]) && !HistoryAnd(tokens[index]))
                    index++;
                string value = string.Join(" ", tokens.Skip(first).Take(index - first).Select(t => t.Text)).Trim();
                if (value.Length == 0)
                { error = "Missing value after " + key + "."; return false; }
                if (key == "player")
                {
                    if (query.Player != null || index - first != 1 || value.Any(char.IsWhiteSpace))
                    { error = "Use one player name once."; return false; }
                    query.Player = ResolveCanonicalAltMain(value);
                }
                else if (key == "item")
                {
                    if (query.Item != null)
                    { error = "Use one item-name filter."; return false; }
                    query.Item = value;
                }
                else
                {
                    int id;
                    if (query.AoId.HasValue || !int.TryParse(value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out id) || id <= 0)
                    { error = "Use one positive numeric AOID."; return false; }
                    query.AoId = id;
                }
                haveFilter = true;
            }
            return true;
        }

        private void ProcessBankHistory(string[] parts, ReplyTarget target, bool donations)
        {
            string command = donations ? "donors" : "takers";
            BankHistoryQuery query;
            string error;
            if (!TryBankHistoryQuery(parts, out query, out error))
            {
                Reply(target, EscapeBlobText(error) + " " + Usage(target, command + " " + BankHistorySyntax));
                return;
            }

            // Both sources are complete saved business history, never current-stock searches.
            List<BankHistoryRow> rows = donations
                ? LoadDonationHistory().Select(r => new BankHistoryRow {
                    Id = r.Id, Main = r.CanonicalDonor, Character = r.Donor, When = r.ReceivedUtc,
                    AoId = r.AoId, HighId = r.HighId, Ql = r.Ql, Name = r.Name, Available = r.Available
                }).ToList()
                : WithdrawalStore.LoadAll(_settingsDir)
                    .Where(r => r != null && r.DeliveredUtc.HasValue && r.Item != null)
                    .Select(r => new BankHistoryRow {
                        Id = r.Id, Main = PickupRecipient(r), Character = r.RequestedBy,
                        When = r.DeliveredUtc.Value, AoId = r.Item.AoId, HighId = r.Item.HighId,
                        Ql = r.Item.Ql, Name = r.Item.Name
                    }).ToList();
            var matches = rows.Where(r =>
                (query.Player == null || string.Equals(r.Main, query.Player, StringComparison.OrdinalIgnoreCase)) &&
                (query.Item == null || (r.Name ?? "").IndexOf(query.Item, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (!query.AoId.HasValue || r.AoId == query.AoId.Value || r.HighId == query.AoId.Value))
                .OrderByDescending(r => r.When).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();

            string title = donations ? "Donation History" : "Confirmed Pickup History";
            var body = new StringBuilder();
            body.Append(CommandLink(target, command, "Overview")).Append(" | ")
                .Append(CommandLink(target, command + " last", "Latest")).Append(" | ")
                .Append(CommandLink(target, command + " top", "Top")).Append("\n\n");
            var filters = new List<string>();
            if (query.Player != null) filters.Add("Player/main: " + query.Player);
            if (query.Item != null) filters.Add("Item contains: " + query.Item);
            if (query.AoId.HasValue) filters.Add("AOID: " + query.AoId.Value);
            body.Append(EscapeBlobText(filters.Count == 0 ? "All saved records" : string.Join(" AND ", filters)))
                .Append("\n").Append(matches.Count).Append(donations ? " donated items" : " confirmed items collected")
                .Append(" across ").Append(matches.Select(r => r.Main).Distinct(StringComparer.OrdinalIgnoreCase).Count())
                .Append(" members. Alts grouped by main. Times UTC.\n")
                .Append("All matches are paged; Next page opens by tell.\n\n");

            if (query.Overview)
            {
                body.Append(EscapeBlobText("#" + command + " " + BankHistorySyntax)).Append("\n")
                    .Append("Combine filters in any order; AND or & is optional.\n")
                    .Append(EscapeBlobText("#" + command + " player Kavey aoid 278892")).Append("\n")
                    .Append("Item matches a case-insensitive name fragment. Quote names containing selector words.\n")
                    .Append("AOID matches either template ID. A player filter includes known alts.\n")
                    .Append(donations ? "Includes donations no longer in stock; CRU is excluded from donor totals."
                        : "Only saved confirmed deliveries count; requests and returns are excluded. Includes CRU.");
            }
            else if (query.Top)
            {
                title = donations ? "Top Donors" : "Top Pickup Recipients";
                int rank = 0;
                foreach (var group in matches.GroupBy(r => r.Main, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    body.Append("#").Append(++rank).Append("  ")
                        .Append(CommandLink(target, HistoryPlayerCommand(command, group.Key, query), group.Key))
                        .Append("  ").Append(group.Count()).Append(" items\n");
            }
            else
            {
                foreach (var r in matches)
                {
                    body.Append(FormatDonationUtc(r.When)).Append("  ")
                        .Append(CommandLink(target, HistoryPlayerCommand(command, r.Main, query), r.Main));
                    if (!string.IsNullOrWhiteSpace(r.Character) &&
                        !string.Equals(r.Character, r.Main, StringComparison.OrdinalIgnoreCase))
                        body.Append(donations ? " (donated as " : " (requested as ")
                            .Append(EscapeBlobText(r.Character)).Append(")");
                    body.Append("  ").Append(CityBankersChatPalette.ItemLabel(r.AoId, r.HighId, r.Ql, r.Name, true))
                        .Append("  AOID ").Append(r.AoId);
                    if (r.HighId > 0 && r.HighId != r.AoId) body.Append("/").Append(r.HighId);
                    if (donations && r.Available)
                        body.Append("  ").Append(CommandLink(target, "get " + r.AoId, "GET"));
                    body.Append("\n");
                }
            }
            if (!query.Overview && matches.Count == 0) body.Append("No matching saved records.\n");
            ReplyBankHistoryPage(target, title, body.ToString());
        }

        private void ReplyBankHistoryPage(ReplyTarget target, string title, string body)
        {
            if (target.IsOrg)
            {
                // One public page; the existing org transport can fall back to its private copy.
                var links = BuildOrgBlobPreview(target, title, title, body, null);
                lock (_orgOutputSync)
                {
                    var cached = _replyPages.Values.FirstOrDefault(p => p.PreviewLink == links[0]);
                    if (cached != null) cached.HistoryRequester = target.SenderName;
                }
                Reply(target, links[0]);
                return;
            }
            // Use the smaller tell budget even for a guest preview, because every continuation is a tell.
            int budget = Math.Max(256, TellBlobPageSize - BlobEnvelope(title, title, null) - 256);
            var reply = new ReplyPages {
                Token = Guid.NewGuid().ToString("N"), Title = title, Label = title,
                Pages = PaginateBlob(body, budget).ToArray(), AdminOnly = target.AdminOnly,
                HistoryRequester = target.SenderName
            };
            reply.PreviewLink = RenderBlobPage(title, title, reply.Pages[0], null, 0,
                reply.Pages.Length, ReplyPageNavigation(reply, 0));
            lock (_orgOutputSync)
            {
                foreach (string old in _replyPages.Where(p => p.Value.CreatedUtc < DateTime.UtcNow.AddMinutes(-30))
                    .Select(p => p.Key).ToArray()) _replyPages.Remove(old);
                while (_replyPages.Count >= 64)
                    _replyPages.Remove(_replyPages.OrderBy(p => p.Value.CreatedUtc).First().Key);
                _replyPages.Add(reply.Token, reply);
            }
            Reply(target, reply.PreviewLink);
        }

        // An authorized org/guest query remains browsable by its requester in tells,
        // even before their membership cache catches up. This grants only that cached result.
        private bool IsBankHistoryContinuation(string[] parts, string sender)
        {
            int page;
            if (parts.Length != 3 || !int.TryParse(parts[2], out page)) return false;
            lock (_orgOutputSync)
            {
                ReplyPages reply;
                return _replyPages.TryGetValue(parts[1], out reply) && !reply.AdminOnly &&
                    !string.IsNullOrWhiteSpace(reply.HistoryRequester) &&
                    string.Equals(reply.HistoryRequester, sender, StringComparison.OrdinalIgnoreCase) &&
                    reply.CreatedUtc >= DateTime.UtcNow.AddMinutes(-30) && page >= 1 && page <= reply.Pages.Length;
            }
        }
    }
}
