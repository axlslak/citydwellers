using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
    /// <summary>
    /// Bootstrap administrator policy plus the public-donation beta exception.
    /// Kavem remains the only trusted administrator for tells/admin operations.
    ///
    /// Trade-open callers may admit public donation partners only after readiness, but
    /// configured CityBankers characters are never public donation partners. This keeps
    /// the player-donation handshake completely out of Central <-> worker internal trades.
    /// </summary>
    public static class TrustedOperators
    {
        public const string BootstrapAdmin = ServicePolicy.TrustedAdminName;
        public const string AllBankersReadyMarkerFileName = "citybankers-all-bankers-ready.json";

        public static bool IsTrustedAdmin(
            string characterName,
            [CallerMemberName] string callerMemberName = null)
        {
            if (string.Equals(
                callerMemberName,
                "OnTradeOpened",
                StringComparison.Ordinal))
            {
                return IsAllBankersReady() &&
                    !string.IsNullOrWhiteSpace(characterName) &&
                    !IsConfiguredBankerCharacter(characterName);
            }

            return !string.IsNullOrWhiteSpace(characterName) &&
                string.Equals(
                    characterName.Trim(),
                    BootstrapAdmin,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsConfiguredBankerCharacter(string characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName))
                return true;

            string settingsDir;
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out error))
                return true;

            try
            {
                JObject root = SettingsPaths.ReadBankersSettings(settingsDir);
                JObject roles = root.GetValue(
                    "Roles",
                    StringComparison.OrdinalIgnoreCase) as JObject;
                if (roles == null)
                    return true;

                return roles.Properties()
                    .Select(property => property.Value as JObject)
                    .Where(role => role != null)
                    .Select(role => role.GetValue(
                        "Character",
                        StringComparison.OrdinalIgnoreCase)?.ToString())
                    .Any(configured =>
                        !string.IsNullOrWhiteSpace(configured) &&
                        string.Equals(
                            configured.Trim(),
                            characterName.Trim(),
                            StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Public trade admission must never become broader because the Bankers section
                // could not be read. Treat unresolved roster state as internal/unsafe.
                return true;
            }
        }

        public static bool IsAllBankersReady()
        {
            string settingsDir;
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out error))
                return false;

            return File.Exists(Path.Combine(
                RuntimeStateStore.GetDataDirectory(settingsDir),
                AllBankersReadyMarkerFileName));
        }
    }
}
