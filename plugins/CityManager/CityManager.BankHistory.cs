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
            public readonly HashSet<string> Players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<int> AoIds = new HashSet<int>();
            public readonly List<string> ItemWords = new List<string>();
            public bool Top, Overview;
        }

        private sealed class BankHistoryRow
        {
            public string Id, Main, Character, Name;
            public DateTime When;
            public int AoId, HighId, Ql;
            public bool Available;
        }

        private const string BankHistorySyntax =
            "[last|top|overview] [player <name ...>] [item <word ...>] [aoid <id ...>]";

        private static bool HistoryPlayerMatches(BankHistoryRow row, string player)
        {
            // A named main includes its alts; an explicitly named alt selects that
            // recorded donor/requester only, without silently expanding to its main.
            return string.Equals(row.Main, player, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.Character, player, StringComparison.OrdinalIgnoreCase);
        }

        private static string HistoryPlayerCommand(string command, string main, BankHistoryQuery query,
            IEnumerable<BankHistoryRow> rows)
        {
            var players = query.Players.Where(p => rows.Any(r => HistoryPlayerMatches(r, p)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            string result = command + " player " + (players.Length == 0 ? main : string.Join(" ", players));
            if (query.ItemWords.Count > 0) result += " item " + string.Join(" ", query.ItemWords);
            if (query.AoIds.Count > 0) result += " aoid " + string.Join(" ", query.AoIds.OrderBy(id => id));
            return result;
        }

        private static bool HistorySelector(string token)
        {
            return token.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("item", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("aoid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HistoryAnd(string token)
        {
            return token == "&" || token.Equals("and", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryBankHistoryQuery(string[] parts, out BankHistoryQuery query, out string error)
        {
            query = new BankHistoryQuery();
            error = null;
            // Verbs delimit arguments. Quotes neither group words nor request an
            // exact phrase; tolerate old wrapping quotes without requiring them.
            var tokens = string.Join(" ", parts.Skip(1)).Replace("&", " & ")
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Trim('"')).Where(word => word.Length > 0).ToArray();
            if (tokens.Length == 0) return true; // Bare command: all saved rows.
            int index = 0;
            if (tokens[0].Equals("top", StringComparison.OrdinalIgnoreCase) ||
                tokens[0].Equals("last", StringComparison.OrdinalIgnoreCase) ||
                tokens[0].Equals("overview", StringComparison.OrdinalIgnoreCase))
            {
                query.Top = tokens[0].Equals("top", StringComparison.OrdinalIgnoreCase);
                query.Overview = tokens[0].Equals("overview", StringComparison.OrdinalIgnoreCase);
                if (++index == tokens.Length) return true;
            }
            // Preserve the old bare-name shorthand as a main-group query.
            if (index == 0 && tokens.Length == 1 && !HistorySelector(tokens[0]) && !HistoryAnd(tokens[0]))
            {
                string name;
                if (!TryNormalizeMemberName(tokens[0], out name, out error)) return false;
                query.Players.Add(ResolveCanonicalAltMain(name));
                return true;
            }
            bool haveFilter = false;
            while (index < tokens.Length)
            {
                if (HistoryAnd(tokens[index]))
                {
                    if (!haveFilter || ++index == tokens.Length || !HistorySelector(tokens[index]))
                    { error = "AND separates filters; put a verb after it."; return false; }
                }
                string key = tokens[index++].ToLowerInvariant();
                if (!HistorySelector(key))
                { error = "Use player, item or aoid before the filter arguments."; return false; }
                int first = index;
                while (index < tokens.Length && !HistorySelector(tokens[index]) &&
                    !(HistoryAnd(tokens[index]) && (index + 1 == tokens.Length || HistorySelector(tokens[index + 1]))))
                    index++;
                if (first == index)
                { error = "Missing value after " + key + "."; return false; }
                for (int value = first; value < index; value++)
                {
                    if (key == "player")
                    {
                        string name;
                        if (!TryNormalizeMemberName(tokens[value], out name, out error))
                        { error = "Invalid player " + tokens[value] + ": " + error; return false; }
                        query.Players.Add(name);
                    }
                    else if (key == "aoid")
                    {
                        int id;
                        if (!int.TryParse(tokens[value], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
                        { error = "Invalid AOID " + tokens[value] + ": use positive integers."; return false; }
                        query.AoIds.Add(id);
                    }
                    else query.ItemWords.Add(tokens[value]);
                }
                haveFilter = true;
            }
            return true;
        }

        private bool ValidateHistoryPlayers(BankHistoryQuery query, List<BankHistoryRow> rows, out string error)
        {
            error = null;
            if (query.Players.Count == 0) return true;
            var known = new HashSet<string>(rows.SelectMany(row => new[] { row.Main, row.Character })
                .Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
            lock (_altsSync)
            {
                known.UnionWith(_altToMain.Keys);
                known.UnionWith(_altToMain.Values);
                foreach (var group in _altGroups.Values) known.UnionWith(GetEffectiveAltCharactersLocked(group));
            }
            lock (_membershipSync)
            {
                known.UnionWith(_permanentMembers);
                known.UnionWith(_officialMembers);
                known.UnionWith(_liveAddedMembers);
                known.UnionWith(_liveRemovedMembers);
            }
            var unknown = query.Players.Where(name => !known.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (unknown.Length == 0) return true;
            error = "Unknown in our saved history/member/alt records: " + string.Join(", ", unknown) +
                ". Check the spelling; the query was not run. This does not establish whether a character exists in AO.";
            return false;
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
            if (!ValidateHistoryPlayers(query, rows, out error))
            {
                Reply(target, EscapeBlobText(error));
                return;
            }
            var matches = rows.Where(r =>
                (query.Players.Count == 0 || query.Players.Any(player => HistoryPlayerMatches(r, player))) &&
                query.ItemWords.All(word => (r.Name ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (query.AoIds.Count == 0 || query.AoIds.Contains(r.AoId) || query.AoIds.Contains(r.HighId)))
                .OrderByDescending(r => r.When).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();

            string title = donations ? "Donation History" : "Confirmed Pickup History";
            var body = new StringBuilder();
            body.Append(CommandLink(target, command, "All")).Append(" | ")
                .Append(CommandLink(target, command + " last", "Latest")).Append(" | ")
                .Append(CommandLink(target, command + " top", "Top")).Append(" | ")
                .Append(CommandLink(target, command + " overview", "Help")).Append("\n\n");
            var filters = new List<string>();
            if (query.Players.Count > 0) filters.Add("Players (any): " + string.Join(", ", query.Players.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)));
            if (query.ItemWords.Count > 0) filters.Add("Item words (all): " + string.Join(" ", query.ItemWords));
            if (query.AoIds.Count > 0) filters.Add("AOIDs (any): " + string.Join(", ", query.AoIds.OrderBy(id => id)));
            body.Append(EscapeBlobText(filters.Count == 0 ? "All saved records" : string.Join(" AND ", filters)))
                .Append("\n").Append(matches.Count).Append(donations ? " donated items" : " confirmed items collected")
                .Append(" across ").Append(matches.Select(r => r.Main).Distinct(StringComparer.OrdinalIgnoreCase).Count())
                .Append(" members. Alts grouped by main. Times UTC.\n")
                .Append("A named main includes alts; a named alt selects that recorded donor/requester only.\n")
                .Append("All matches are paged; Next page opens by tell.\n\n");

            if (query.Overview)
            {
                body.Append(EscapeBlobText("#" + command + " " + BankHistorySyntax)).Append("\n")
                    .Append("Combine filters in any order; AND or & is optional.\n")
                    .Append(EscapeBlobText("#" + command + " player Kavey aoid 278892")).Append("\n")
                    .Append("Each verb owns arguments until the next verb. Every item word must match a name fragment, in any order; no quotes needed.\n")
                    .Append("Players and AOIDs accept lists (OR within each list; AND between filters). AOID matches either template ID. A named main includes its alts; a named alt selects only that donor/requester.\n")
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
                        .Append(CommandLink(target, HistoryPlayerCommand(command, group.Key, query, group), group.Key))
                        .Append("  ").Append(group.Count()).Append(" items\n");
            }
            else
            {
                foreach (var r in matches)
                {
                    body.Append(FormatDonationUtc(r.When)).Append("  ")
                        .Append(CommandLink(target, HistoryPlayerCommand(command, r.Main, query, new[] { r }), r.Main));
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
            ReplyHistoryPage(target, title, body.ToString());
        }

        private void ReplyHistoryPage(ReplyTarget target, string title, string body, int page = 1,
            IEnumerable<string> sections = null)
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
                Pages = (sections ?? new[] { body }).SelectMany(section => PaginateBlob(section, budget)).ToArray(),
                AdminOnly = target.AdminOnly,
                HistoryRequester = target.SenderName
            };
            int index = Math.Max(0, Math.Min(reply.Pages.Length - 1, page - 1));
            reply.PreviewLink = RenderBlobPage(title, title, reply.Pages[index], null, index,
                reply.Pages.Length, ReplyPageNavigation(reply, index));
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
        private bool IsHistoryContinuation(string[] parts, string sender)
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
