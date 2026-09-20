using Directory = CityDwellers.Shared.SqlDirectory;
using System;
using System.IO;

using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
    internal static class SettingsPaths
    {
        private const string RuntimeRootEnvironmentVariable =
            "CITYDWELLERS_RUNTIME_ROOT";

        public static bool TryEnsureDirectory(
            out string settingsDirectory,
            out string error)
        {
            settingsDirectory = GetSettingsDirectory();

            try
            {
                Directory.CreateDirectory(CityDwellers.Shared.SqlStore.GetDataDirectory(settingsDirectory));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    $"Unable to access MySQL runtime namespace '{settingsDirectory}'. " +
                    $"Check the required MySQL configuration and connectivity. {ex.Message}";
                return false;
            }
        }

        public static string GetFilePath(string settingsDirectory, string fileName)
        {
            return Path.Combine(settingsDirectory, fileName);
        }

        public static JObject ReadBankersSettings(string settingsDirectory)
        {
            string path = Path.Combine(settingsDirectory, "citydwellers.json");
            JObject root = JObject.Parse(System.IO.File.ReadAllText(path));
            JObject bankers = root.GetValue(
                "Bankers",
                StringComparison.OrdinalIgnoreCase) as JObject;
            if (bankers == null)
                throw new InvalidDataException(
                    "'" + path + "' requires a Bankers settings section.");

            return bankers;
        }

        public const int InitialBankTerminalInstance = 1477725977;

        public static string BankTerminalPath(string settingsDirectory) =>
            Path.Combine(RuntimeStateStore.GetDataDirectory(settingsDirectory), "citybankers-bank-terminal.json");

        public static JObject ReadBankTerminal(string settingsDirectory)
        {
            var state = RuntimeStateStore.ReadJson<JObject>(BankTerminalPath(settingsDirectory));
            if (state == null) return new JObject {
                ["Instance"] = InitialBankTerminalInstance, ["Revision"] = "initial"
            };
            int instance;
            if (!int.TryParse((string)state["Instance"], out instance) || instance <= 0)
                throw new InvalidDataException("Saved bank terminal Instance must be a positive decimal integer.");
            return state;
        }

        public static void SaveBankTerminal(string settingsDirectory, int instance, string changedBy)
        {
            if (instance <= 0) throw new ArgumentOutOfRangeException(nameof(instance));
            RuntimeStateStore.WriteJsonAtomic(BankTerminalPath(settingsDirectory), new JObject {
                ["Instance"] = instance, ["Revision"] = Guid.NewGuid().ToString("N"),
                ["ChangedBy"] = changedBy, ["ChangedUtc"] = DateTime.UtcNow
            });
        }

        public static string ReadManagerCharacter(string settingsDirectory)
        {
            string path = Path.Combine(settingsDirectory, "citydwellers.json");
            JObject root = JObject.Parse(System.IO.File.ReadAllText(path));
            JObject manager = root.GetValue(
                "Manager",
                StringComparison.OrdinalIgnoreCase) as JObject;
            JArray accounts = manager?.GetValue(
                "Accounts",
                StringComparison.OrdinalIgnoreCase) as JArray;
            string character = accounts?.First?["Character"]?.ToString();
            if (string.IsNullOrWhiteSpace(character))
                throw new InvalidDataException(
                    "'" + path + "' requires Manager.Accounts[0].Character.");
            return character.Trim();
        }

        internal static string GetSettingsDirectory()
        {
            string processRuntimeDirectory = Environment.GetEnvironmentVariable(
                RuntimeRootEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(processRuntimeDirectory))
                return Path.GetFullPath(processRuntimeDirectory);

            return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
