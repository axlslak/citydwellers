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
                Directory.CreateDirectory(settingsDirectory);
                Directory.CreateDirectory(Path.Combine(settingsDirectory, "data"));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    $"Unable to create settings directory '{settingsDirectory}'. " +
                    $"Check that this account has write permission. {ex.Message}";
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
            JObject root = JObject.Parse(File.ReadAllText(path));
            JObject bankers = root.GetValue(
                "Bankers",
                StringComparison.OrdinalIgnoreCase) as JObject;
            if (bankers == null)
                throw new InvalidDataException(
                    "'" + path + "' requires a Bankers settings section.");

            return bankers;
        }

        public static bool TryCreateFile(
            string path,
            string contents,
            out string error)
        {
            try
            {
                File.WriteAllText(path, contents);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    $"Unable to create settings file '{path}'. " +
                    $"Check that this account has write permission. {ex.Message}";
                return false;
            }
        }

        private static string GetSettingsDirectory()
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
