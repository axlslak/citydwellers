using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    // Root configuration and immutable assets stay beside the executable. Runtime
    // data paths identify MySQL keys only; this class never creates a data folder.
    internal static class SettingsPaths
    {
        private const string RuntimeRootEnvironmentVariable = "CITYDWELLERS_RUNTIME_ROOT";

        public static bool TryEnsureDirectories(out string settingsDirectory,
            out string dataDirectory, out string error)
        {
            settingsDirectory = GetRuntimeDirectory();
            dataDirectory = GetDataDirectory(settingsDirectory);
            error = null;
            return true;
        }

        public static void BindRuntimeDirectoryToProcess(string runtimeDirectory)
        {
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
                throw new ArgumentException("The City Dwellers runtime directory cannot be empty.", nameof(runtimeDirectory));
            Environment.SetEnvironmentVariable(RuntimeRootEnvironmentVariable,
                Path.GetFullPath(runtimeDirectory), EnvironmentVariableTarget.Process);
        }

        public static bool TryEnsureDirectory(out string settingsDirectory, out string error)
        {
            string dataDirectory;
            return TryEnsureDirectories(out settingsDirectory, out dataDirectory, out error);
        }

        public static string GetFilePath(string settingsDirectory, string fileName) => Path.Combine(settingsDirectory, fileName);
        public static string GetDataFilePath(string dataDirectory, string fileName) => Path.Combine(dataDirectory, fileName);
        public static string GetDataDirectory(string settingsDirectory) => Path.Combine(settingsDirectory, "data");

        public static bool TryReadSettingsSection<T>(
            string settingsDirectory,
            string sectionName,
            out T settings,
            out string error)
        {
            settings = default(T);
            string path = Path.Combine(settingsDirectory, "citydwellers.json");

            if (!File.Exists(path))
            {
                error = $"The unified settings file was not found at '{path}'.";
                return false;
            }

            try
            {
                JObject root = JObject.Parse(File.ReadAllText(path));
                JToken section = root.GetValue(
                    sectionName,
                    StringComparison.OrdinalIgnoreCase);
                if (section == null || section.Type == JTokenType.Null)
                {
                    error =
                        $"'{path}' requires a '{sectionName}' settings section.";
                    return false;
                }

                settings = section.ToObject<T>();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    $"Unable to read the '{sectionName}' section from " +
                    $"'{path}'. {ex.Message}";
                return false;
            }
        }

        public static List<string> InspectRuntimeLayout(string runtimeDirectory, string dataDirectory)
        {
            // The data directory is permitted for the item catalogue, logs and dumps.
            // Its existence is not evidence of an incomplete SQL migration.
            return new List<string>();
        }

        private static string GetRuntimeDirectory()
        {
            string configured = Environment.GetEnvironmentVariable(RuntimeRootEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? AppDomain.CurrentDomain.BaseDirectory : configured);
        }
    }
}
