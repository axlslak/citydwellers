using System;

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

