using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;
using Palette = CityBankers.Shared.CityBankersChatPalette;

namespace CityManager
{
    public partial class CityManager
    {
        private bool _bankChatDebug;
        private readonly Queue<BankActivity> _bankActivityHistory = new Queue<BankActivity>();
        private static readonly HashSet<string> BankActivityCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "stock", "symb", "symbs", "spirit", "spirits", "dyna", "nano", "nanos", "phat", "phatz",
            "type", "types", "donor", "pickups", "takers", "withdraw", "get", "cru", "bankid"
        };

        private sealed class BankChatOptions
        {
            public bool BankChatDebug { get; set; }
        }

        private void InitializeBankActivity()
        {
            BankChatOptions options;
            string error;
            if (SettingsPaths.TryReadSettingsSection(_settingsDir, "Manager", out options, out error))
                _bankChatDebug = options?.BankChatDebug ?? false;
            else
                Logger.Warning("Bank activity chat uses normal mode: " + error);
            Logger.Information("Bank activity chat: " + (_bankChatDebug ? "debug" : "normal") +
                "; console diagnostics remain enabled. Startup setting: Manager.BankChatDebug.");
        }

        // Ordinary queue messages retain their existing delivery path. Only structured
        // banking telemetry is filtered here; no setting participates in native trading.
        private bool TryFormatBankActivity(ManagerChannelJob job, out List<string> messages)
        {
            messages = new List<string>();
            if (job.Kind != "bank.activity") return false;
            try
            {
                BankActivity activity = JsonConvert.DeserializeObject<BankActivity>(job.Message);
                if (activity == null) throw new InvalidOperationException("Empty bank activity.");
                bool progress = activity.Kind == "progress";
                if (progress)
                {
                    _bankActivityHistory.Enqueue(activity);
                    while (_bankActivityHistory.Count > 256) _bankActivityHistory.Dequeue();
                    if (!_bankChatDebug) return true;
                }

                string heading = progress ? "Bank progress" : "Bank transaction";
                string summary = BankActivitySummary(activity);
                string body = BankActivityDetails(activity);
                var target = ReplyTarget.ForGuest(0, Client.Chat.CharId);
                List<string> links = BuildBlobLinks(target, heading, "Details", body,
                    BankColor(heading, Palette.ActivityTextHex), Palette.ActivityLinkHex,
                    Encoding.UTF8.GetByteCount(summary) + 64);
                // A paginated detail card stays attached to one headline, not one
                // headline per item, handshake or page of diagnostics.
                for (int page = 0; page < links.Count; page++)
                    messages.Add((page == 0 ? summary : BankMarkup("Bank details continued:")) + " " + links[page]);
            }
            catch (Exception ex)
            {
                // A bad display envelope must not block the channel queue head.
                Logger.Warning("Bank activity display unavailable: " + ex.Message);
            }
            return true;
        }

        private string BankActivitySummary(BankActivity activity)
        {
            string actor = BankActor(activity);
            string item = BankItemSummary(activity);
            string text;
            if (activity.Kind == "progress")
            {
                string stage = (activity.Stage ?? "progress").Replace('_', ' ').ToLowerInvariant();
                bool transit = stage.Contains("transfer") || stage.Contains("dispatch") || stage.Contains("trade");
                text = BankColor(transit ? "In transit" : "Bank debug", Palette.ActivityLinkHex) +
                    ": " + BankColor(activity.Source ?? "Banker", Palette.ActivityNameHex) + " — " +
                    BankColor(stage, Palette.ActivityMutedHex);
            }
            else if (activity.Kind == "donation")
                text = actor + " donated " + item + " — " + BankOutcome(activity);
            else if (activity.Kind == "pickup")
                text = actor + " collected " + item + " — " + BankOutcome(activity);
            else
                text = "Withdrawal for " + actor + ": " + item + " — " + BankOutcome(activity);
            return BankMarkup(text);
        }

        private string BankActor(BankActivity activity)
        {
            string player = string.IsNullOrWhiteSpace(activity.Player) ? "Unknown player" : activity.Player;
            string main = string.IsNullOrWhiteSpace(activity.Main) ? ResolveCanonicalAltMain(player) : activity.Main;
            string label = BankColor(player, Palette.ActivityNameHex);
            if (!string.IsNullOrWhiteSpace(main) && !string.Equals(main, player, StringComparison.OrdinalIgnoreCase))
                label += " (main " + BankColor(main, Palette.ActivityNameHex) + ")";
            return label;
        }

        private static string BankOutcome(BankActivity activity)
        {
            string label = activity.Outcome == "success" ? "success" :
                activity.Outcome == "failed" ? "failed" :
                activity.Outcome == "returned" ? "returned to storage" :
                activity.Outcome == "released" ? "reservation released" : activity.Outcome ?? "recorded";
            string color = activity.Outcome == "success" ? Palette.ActivitySuccessHex :
                activity.Outcome == "failed" ? Palette.ActivityFailureHex : Palette.ActivityReturnHex;
            return BankColor(label, color);
        }

        private static string BankItemSummary(BankActivity activity)
        {
            var items = (activity.Items ?? new List<CityBankers.Shared.LedgerItem>()).Where(item => item != null).ToList();
            if (items.Count == 1) return BankItem(items[0]);
            if (items.Count > 1)
                return string.Join(", ", items.Take(3).Select(BankItem)) +
                    (items.Count > 3 ? BankColor(" (and " + (items.Count - 3) + " more)", Palette.ActivityMutedHex) : string.Empty);
            return BankColor(items.Sum(item => (long)Math.Max(1, item.Quantity)).ToString(CultureInfo.InvariantCulture) +
                " items", Palette.ActivityItemHex);
        }

        private static string BankItem(CityBankers.Shared.LedgerItem item)
        {
            string name = BiomaterialTypes.DisplayName(item.AoId, item.Name) ?? "Unnamed item";
            string label = BankColor(name, Palette.ActivityItemHex);
            if (item.AoId > 0 && item.HighId > 0 && item.Ql > 0)
                label = "<a href='itemref://" + item.AoId + "/" + item.HighId + "/" + item.Ql + "'>" + label + "</a>";
            return (item.Quantity > 1 ? BankColor(item.Quantity + " × ", Palette.ActivityTextHex) : string.Empty) +
                label + (item.Ql > 0 ? BankColor(" (QL " + item.Ql + ")", Palette.ActivityMutedHex) : string.Empty);
        }

        private string BankActivityDetails(BankActivity activity)
        {
            var body = new StringBuilder();
            body.AppendLine(BankActivitySummary(activity));
            body.AppendLine(BankMarkup("Time: " + BankColor(activity.Utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'",
                CultureInfo.InvariantCulture), Palette.ActivityMutedHex)));
            if (activity.StartedUtc.HasValue)
                body.AppendLine(BankMarkup("Started: " + BankColor(activity.StartedUtc.Value.ToUniversalTime().ToString("O"),
                    Palette.ActivityMutedHex)));
            if (!string.IsNullOrWhiteSpace(activity.Player)) body.AppendLine(BankMarkup("Player: " + BankActor(activity)));
            if (!string.IsNullOrWhiteSpace(activity.Source)) body.AppendLine(BankMarkup("Reported by: " +
                BankColor(activity.Source, Palette.ActivityNameHex)));
            if (!string.IsNullOrWhiteSpace(activity.Destination)) body.AppendLine(BankMarkup("To: " +
                BankColor(activity.Destination, Palette.ActivityNameHex)));
            body.AppendLine();
            foreach (var item in activity.Items ?? new List<CityBankers.Shared.LedgerItem>())
                if (item != null) body.AppendLine(BankMarkup(BankItem(item)));
            body.AppendLine();
            AppendBankDetail(body, activity.Detail);

            var ids = new HashSet<string>((activity.Correlations ?? new string[0])
                .Concat(new[] { activity.OperationId }).Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            List<BankActivity> steps = _bankActivityHistory.Where(step =>
                (step.Correlations ?? new string[0]).Concat(new[] { step.OperationId }).Any(id => id != null && ids.Contains(id)))
                .ToList();
            if (steps.Count > 0)
            {
                body.AppendLine();
                body.AppendLine(BankColor("Recent diagnostic steps (full diagnostics remain in the console)", Palette.ActivityMutedHex));
                foreach (BankActivity step in steps.Skip(Math.Max(0, steps.Count - 12)))
                {
                    body.AppendLine(BankColor(step.Utc.ToUniversalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                        "  " + (step.Source ?? "") + "  " + (step.Stage ?? ""), Palette.ActivityMutedHex));
                    AppendBankDetail(body, step.Detail);
                }
            }
            body.AppendLine();
            AppendBankDetail(body, "Diagnostic reference: " + activity.OperationId);
            return body.ToString();
        }

        private static void AppendBankDetail(StringBuilder body, string text)
        {
            // Chunk plain text before escaping; markup and links are never split.
            foreach (string row in (text ?? string.Empty).Replace("\r", "").Split('\n'))
                for (int start = 0; start < row.Length; start += 160)
                    body.AppendLine(BankColor(row.Substring(start, Math.Min(160, row.Length - start)), Palette.ActivityTextHex));
        }

        private static string BankColor(string text, string color) =>
            "<font color='" + color + "'>" + EscapeBlobText(text ?? string.Empty) + "</font>";
        private static string BankMarkup(string text) =>
            "<font color='" + Palette.ActivityTextHex + "'>" + text + "</font>";
    }
}
