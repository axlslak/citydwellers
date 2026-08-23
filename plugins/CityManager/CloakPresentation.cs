using System;

namespace CityManager
{
    internal static class CloakPresentation
    {
        private const string Cyan = "#66CCFF";
        private const string Green = "#66FF99";
        private const string Red = "#FF6666";
        private const string Amber = "#FFD166";
        private const string Gray = "#999999";
        private const string Separator = "#777777";

        public static string Build(
            string cloakState,
            int? shieldTimerInSeconds,
            float? controllerCharge,
            bool cached,
            DateTime? observedUtc)
        {
            string stateText = string.IsNullOrWhiteSpace(cloakState)
                ? "UNKNOWN"
                : cloakState.Trim().ToUpperInvariant();

            string stateColor = Gray;
            if (string.Equals(cloakState, "Enabled", StringComparison.OrdinalIgnoreCase))
                stateColor = Green;
            else if (string.Equals(cloakState, "Disabled", StringComparison.OrdinalIgnoreCase))
                stateColor = Red;

            string shieldText;
            string shieldColor;

            if (!shieldTimerInSeconds.HasValue)
            {
                shieldText = "UNKNOWN";
                shieldColor = Gray;
            }
            else if (shieldTimerInSeconds.Value <= 0)
            {
                shieldText = "READY";
                shieldColor = Green;
            }
            else
            {
                shieldText = FormatDuration(
                    TimeSpan.FromSeconds(shieldTimerInSeconds.Value));
                shieldColor = Amber;
            }

            string chargeText = controllerCharge.HasValue
                ? $"{controllerCharge.Value * 100:F1}%"
                : "UNKNOWN";

            string reply =
                $"<font color={Cyan}>City Cloak</font>: " +
                $"<font color={stateColor}>{stateText}</font> " +
                $"<font color={Separator}>|</font> Shield: " +
                $"<font color={shieldColor}>{shieldText}</font> " +
                $"<font color={Separator}>|</font> Charge: " +
                $"<font color={Amber}>{chargeText}</font>";

            if (cached && observedUtc.HasValue)
            {
                TimeSpan age = DateTime.UtcNow - observedUtc.Value;
                if (age < TimeSpan.Zero)
                    age = TimeSpan.Zero;

                reply +=
                    $" <font color={Separator}>|</font> " +
                    $"<font color={Gray}>last verified {FormatAge(age)} ago</font>";
            }

            return reply;
        }

        public static string RankDenied(string rank)
        {
            string rankText = string.IsNullOrWhiteSpace(rank) ? "unknown rank" : rank;

            return
                $"<font color={Red}>#cloak requires Squad Commander or higher.</font> " +
                $"<font color={Gray}>Your rank: {rankText}.</font>";
        }

        public static string RankLookupFailed()
        {
            return
                $"<font color={Amber}>Unable to verify your organization rank right now.</font> " +
                $"<font color={Gray}>Please try #cloak again.</font>";
        }

        public static string Unavailable()
        {
            return
                $"<font color={Red}>City Cloak:</font> " +
                $"<font color={Gray}>unable to verify city state right now.</font>";
        }

        private static string FormatDuration(TimeSpan value)
        {
            int totalSeconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;

            if (hours > 0)
                return $"{hours}h {minutes}m";
            if (minutes > 0)
                return $"{minutes}m {seconds}s";
            return $"{seconds}s";
        }

        private static string FormatAge(TimeSpan value)
        {
            if (value.TotalSeconds < 5)
                return "just now";
            if (value.TotalMinutes < 1)
                return $"{Math.Max(1, (int)value.TotalSeconds)}s";
            if (value.TotalHours < 1)
                return $"{Math.Max(1, (int)value.TotalMinutes)}m";
            if (value.TotalDays < 1)
                return $"{Math.Max(1, (int)value.TotalHours)}h";
            return $"{Math.Max(1, (int)value.TotalDays)}d";
        }
    }
}
