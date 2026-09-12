using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CityBankers.Shared
{
    /// <summary>
    /// CityBankers AO tell palette. Normal prose is explicitly bright white so bot output
    /// remains visually distinct from AO's default tell color while scrolling chat history.
    /// Semantic colors are intentionally few and stable: cyan=progress/count,
    /// green=success/accepted, yellow=attention/current state, red=failure/rejection/delete.
    /// </summary>
    public static class CityBankersChatPalette
    {
        public const string WhiteHex = "#FFFFFF";
        public const string CyanHex = "#00FFFF";
        public const string GreenHex = "#00FF00";
        public const string YellowHex = "#FFFF00";
        public const string RedHex = "#FF4040";

        public static string White(string text)
        {
            return Color(text, WhiteHex);
        }

        public static string Cyan(string text)
        {
            return Color(text, CyanHex);
        }

        public static string Green(string text)
        {
            return Color(text, GreenHex);
        }

        public static string Yellow(string text)
        {
            return Color(text, YellowHex);
        }

        public static string Red(string text)
        {
            return Color(text, RedHex);
        }

        /// <summary>
        /// Wraps an already-marked-up AO chat message in an explicit white base color.
        /// Nested semantic font tags remain intact.
        /// </summary>
        public static string WhiteBaseMarkup(string markup)
        {
            if (string.IsNullOrEmpty(markup))
                return markup ?? string.Empty;

            return "<font color='" + WhiteHex + "'>" + markup + "</font>";
        }

        public const string MutedHex = "#AAB8C5";
        public const string NameHex = "#89D2E8";
        public const string WaitHex = "#FFB347";
        private static readonly Lazy<Dictionary<int, int>> Icons =
            new Lazy<Dictionary<int, int>>(LoadIcons);

        private static Dictionary<int, int> LoadIcons()
        {
            var result = new Dictionary<int, int>();
            using (Stream stream = typeof(CityBankersChatPalette).Assembly
                .GetManifestResourceStream("CityDwellers.ItemIcons.csv.gz"))
            {
                if (stream == null) return result;
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] parts = line.Split(',');
                        int id, icon;
                        if (parts.Length == 2 && int.TryParse(parts[0], out id) && int.TryParse(parts[1], out icon))
                            result[id] = icon;
                    }
                }
            }
            return result;
        }

        public static string ItemIcon(int aoId, int highId)
        {
            int icon;
            return Icons.Value.TryGetValue(aoId, out icon) || Icons.Value.TryGetValue(highId, out icon)
                ? "<img src='rdb://" + icon + "'> " : string.Empty;
        }

        public static string ItemLabel(int aoId, int highId, int ql, string name, bool icon = false)
        {
            string label = Color(name ?? ("AOID " + aoId), NameHex);
            if (aoId > 0 && highId > 0 && ql > 0)
                label = "<a href='itemref://" + aoId + "/" + highId + "/" + ql + "'>" + label + "</a>";
            return (icon ? ItemIcon(aoId, highId) : string.Empty) + label +
                (ql > 0 ? " " + Color("(", MutedHex) + "QL " + Cyan(ql.ToString()) + Color(")", MutedHex) : string.Empty);
        }

        // Only plain text is tokenised. Existing fonts and link attributes retain
        // their markup; text:// payloads are formatted by their window builders.
        public static string StyleMarkup(string markup)
        {
            if (string.IsNullOrEmpty(markup)) return markup ?? string.Empty;
            if (markup.IndexOf("text://", StringComparison.OrdinalIgnoreCase) >= 0)
                return WhiteBaseMarkup(markup);
            var result = new StringBuilder();
            int fontDepth = 0;
            foreach (string part in Regex.Split(markup, "(<[^>]*>|&[A-Za-z0-9#]+;)"))
            {
                if (part.StartsWith("<"))
                {
                    if (part.StartsWith("<font", StringComparison.OrdinalIgnoreCase)) fontDepth++;
                    else if (part.StartsWith("</font", StringComparison.OrdinalIgnoreCase)) fontDepth = Math.Max(0, fontDepth - 1);
                    result.Append(part);
                }
                else if (fontDepth > 0 || part.StartsWith("&")) result.Append(part);
                else result.Append(Regex.Replace(part,
                    @"\b(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{8}-[a-fA-F0-9-]{27,}|Kb\w+|Apcmanager|\d+(?:/\d+)?|ERROR|FAILED|FAILURE|REJECTED|DELETING|DENIED|WAITING|RETRY|QUEUED|BUSY|VERIFIED|COMPLETE|SUCCESS)\b|(?<key>\b(?:requester|collector|partner|source|destination|character|sender|actor|target|requested|by|main|from|to)=|\b(?:Guest|Tell) )(?<value>[A-Za-z][A-Za-z0-9_-]*)",
                    match => {
                        if (match.Groups["key"].Success)
                            return match.Groups["key"].Value + Color(match.Groups["value"].Value, NameHex);
                        string token = match.Value;
                        string color = Regex.IsMatch(token, @"^(ERROR|FAILED|FAILURE|REJECTED|DELETING|DENIED)$") ? RedHex :
                            Regex.IsMatch(token, @"^(WAITING|RETRY|QUEUED|BUSY)$") ? WaitHex :
                            Regex.IsMatch(token, @"^(VERIFIED|COMPLETE|SUCCESS)$") ? GreenHex :
                            Regex.IsMatch(token, @"^[0-9a-fA-F-]{32,}$") ? MutedHex :
                            char.IsDigit(token[0]) ? YellowHex : NameHex;
                        return Color(token, color);
                    }));
            }
            return WhiteBaseMarkup(result.ToString());
        }

        public static string Stage(string stage)
        {
            string upper = (stage ?? string.Empty).ToUpperInvariant();
            string color = upper.Contains("FAIL") || upper.Contains("DECLIN") || upper.Contains("ERROR") ? RedHex :
                upper.Contains("WAIT") || upper.Contains("RETRY") || upper.Contains("QUEUED") ? WaitHex :
                upper.Contains("VERIFIED") || upper.Contains("COMPLETE") || upper == "WITHDRAWAL READY" ? GreenHex : NameHex;
            return Color(stage, color);
        }

        public static string DonationProgress(int count, int maximum)
        {
            int safeCount = Math.Max(0, count);
            int safeMaximum = Math.Max(1, maximum);
            string progress = safeCount + "/" + safeMaximum;
            string coloredProgress = safeCount > safeMaximum
                ? Red(progress)
                : Cyan(progress);

            return White("DONATION ") + coloredProgress + White(" — ");
        }

        private static string Color(string text, string color)
        {
            return "<font color='" + color + "'>" + Escape(text) + "</font>";
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}

