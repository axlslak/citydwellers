using System;
using System.Globalization;
using System.Linq;
using System.Text;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private bool TryProcessBuffHistoryCommand(string[] parts, ReplyTarget target)
        {
            if (parts.Length < 2 || parts.Length > 3) return false;
            string action = parts[1].ToLowerInvariant();
            if (new[] { "list", "mine", "add", "cast", "del", "rem", "remove", "redo", "rebuff", "macro" }.Contains(action))
                return false;
            int page = 1;
            if (parts.Length == 3 && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
                return false;
            if (action == "top" && parts.Length != 2) return false;
            string person = null;
            if (action != "last" && action != "top")
            {
                string error;
                if (!TryNormalizeMemberName(parts[1], out person, out error)) return false;
            }
            string recipient = person;
            int requestedPage = page;
            QueuePublicWork(target, () => ShowBuffHistory(target, action, recipient, requestedPage));
            return true;
        }

        private void ShowBuffHistory(ReplyTarget target, string action, string person, int page)
        {
            var history = ManagerMemory.Current.ReadBuffHistory(person, page);
            string title = person != null ? "Buffs for " + history.Recipient : action == "top" ? "Top Buffs and Recipients" : "Recent Buffs";
            var body = new StringBuilder();
            body.Append(CommandLink(target, "buff last", "Last")).Append(" | ")
                .Append(CommandLink(target, "buff top", "Top")).Append(" | ")
                .Append(CommandLink(target, "buff list", "Buff catalogue")).Append("\n\n");
            body.Append(history.Total).Append(" completed casts: ")
                .Append(history.Total - history.Paid).Append(" froob, ").Append(history.Paid).Append(" paid.\n");
            if (history.FirstUtc.HasValue)
                body.Append("First saved buff: ").Append(BuffHistoryTime(history.FirstUtc.Value)).Append(".\n");
            body.Append("History is saved across restarts. Team casts count once for the requesting character.\n\n");
            if (history.Total == 0)
                body.Append(person == null ? "No completed buffs have been saved yet.\n" :
                    "No saved buff deliveries for " + EscapeBlobText(person) + ".\n");
            else if (action == "top")
            {
                body.Append("<font color='").Append(ColorTitle).Append("'>Most-given buffs (top 20)</font>\n");
                AppendBuffHistoryRanks(body, history.Buffs);
                body.Append("\n<font color='").Append(ColorTitle).Append("'>Most-served characters (top 20)</font>\n");
                int rank = 0;
                foreach (var row in history.Recipients)
                    body.Append(++rank).Append(". ").Append(CommandLink(target, "buff " + row.Name, row.Name))
                        .Append(" — ").Append(row.Count).Append(" casts\n");
            }
            else
            {
                if (person != null)
                {
                    body.Append("<font color='").Append(ColorTitle).Append("'>Most received buffs (top 20)</font>\n");
                    AppendBuffHistoryRanks(body, history.Buffs);
                    body.Append("\n");
                }
                body.Append("<font color='").Append(ColorTitle).Append("'>Recent deliveries</font>\n");
                foreach (var row in history.Recent)
                {
                    body.Append("<font color='").Append(ColorMuted).Append("'>").Append(BuffHistoryTime(row.CompletedUtc))
                        .Append("</font> — ").Append(CommandLink(target, "buff " + row.Recipient, row.Recipient))
                        .Append(": ").Append(BuffHistoryLabel(row.NanoName)).Append(" (AOID ").Append(row.NanoId)
                        .Append(") from ").Append(EscapeBlobText(row.Caster))
                        .Append(row.Paid ? " [paid]\n" : " [froob]\n");
                }
                string command = person == null ? "buff last" : "buff " + person;
                body.Append("\nPage ").Append(history.Page).Append('/').Append(history.Pages);
                if (history.Page > 1)
                    body.Append(" | ").Append(CommandLink(target, command + " " + (history.Page - 1), "Newer"));
                if (history.Page < history.Pages)
                    body.Append(" | ").Append(CommandLink(target, command + " " + (history.Page + 1), "Older"));
                body.Append("\n");
            }
            body.Append("\nUse #buff <person> for one character. Older casts from before saved history are not included.");
            ReplyBufferCatalogue(target, BuildBlobLinks(target, title, title, body.ToString()));
        }

        private static string BuffHistoryTime(DateTime value)
            => value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        private string BuffHistoryLabel(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            return EscapeBlobText(value.Length > 160 ? value.Substring(0, 160) + "…" : value);
        }

        private void AppendBuffHistoryRanks(StringBuilder body, BuffHistoryRank[] rows)
        {
            int rank = 0;
            foreach (var row in rows)
                body.Append(++rank).Append(". ").Append(BuffHistoryLabel(row.Name))
                    .Append(" (AOID ").Append(row.NanoId).Append(") — ").Append(row.Count).Append(" casts\n");
        }
    }
}
