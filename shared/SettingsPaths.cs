using System;
using System.Collections.Generic;
using System.IO;

namespace CityDwellers.Shared
{
    internal static class SettingsPaths
    {
        private const string SolutionFileName = "citydwellers.sln";
        private const string DataDirectoryName = "data";
        private const string RuntimeRootEnvironmentVariable =
            "CITYDWELLERS_RUNTIME_ROOT";

        private static readonly HashSet<string> AdministratorSettings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "manager.json",
                "flipper.json",
                "buddies.json",
                "citydwellers.json"
            };

        public static bool TryEnsureDirectories(
            out string settingsDirectory,
            out string dataDirectory,
            out string error)
        {
            settingsDirectory = GetRuntimeDirectory();
            dataDirectory = Path.Combine(settingsDirectory, DataDirectoryName);

            try
            {
                Directory.CreateDirectory(dataDirectory);
                MigrateLegacyLayout(settingsDirectory, dataDirectory);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    $"Unable to prepare the City Dwellers runtime at " +
                    $"'{settingsDirectory}'. Check that this account has write " +
                    $"permission to the executable directory and its data folder. " +
                    ex.Message;
                return false;
            }
        }

        public static void BindRuntimeDirectoryToProcess(string runtimeDirectory)
        {
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
                throw new ArgumentException(
                    "The City Dwellers runtime directory cannot be empty.",
                    nameof(runtimeDirectory));

            Environment.SetEnvironmentVariable(
                RuntimeRootEnvironmentVariable,
                Path.GetFullPath(runtimeDirectory),
                EnvironmentVariableTarget.Process);
        }

        public static bool TryEnsureDirectory(
            out string settingsDirectory,
            out string error)
        {
            string dataDirectory;
            return TryEnsureDirectories(
                out settingsDirectory,
                out dataDirectory,
                out error);
        }

        public static string GetFilePath(
            string settingsDirectory,
            string fileName)
        {
            return Path.Combine(settingsDirectory, fileName);
        }

        public static string GetDataFilePath(
            string dataDirectory,
            string fileName)
        {
            return Path.Combine(dataDirectory, fileName);
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

        public static List<string> InspectRuntimeLayout(
            string runtimeDirectory,
            string dataDirectory)
        {
            var warnings = new List<string>();

            foreach (string file in Directory.GetFiles(runtimeDirectory))
            {
                string name = Path.GetFileName(file);
                if (AdministratorSettings.Contains(name) || IsRuntimeArtifact(name))
                    continue;

                warnings.Add(
                    IsBotDataFile(name)
                        ? $"Misplaced data file in settings/runtime root: '{name}'. " +
                          $"It belongs under data and is ignored here."
                        : $"Alien file in settings/runtime root: '{name}'. " +
                          $"City Dwellers does not use it."
                );
            }

            foreach (string directory in Directory.GetDirectories(runtimeDirectory))
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, DataDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "GameData", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "NavMeshes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool belongsInData = IsBotDataDirectory(name);
                warnings.Add(
                    belongsInData
                        ? $"Misplaced data directory in settings/runtime root: '{name}'. " +
                          $"It belongs under data and is ignored here."
                        : $"Alien directory in settings/runtime root: '{name}'. " +
                          $"City Dwellers does not use it."
                );
            }

            foreach (string file in Directory.GetFiles(dataDirectory))
            {
                string name = Path.GetFileName(file);
                if (IsBotDataFile(name))
                    continue;

                if (AdministratorSettings.Contains(name))
                {
                    warnings.Add(
                        $"Misplaced administrator setting in data: '{name}'. " +
                        $"It belongs beside CityDwellers.exe and is ignored here.");
                }
                else if (IsRuntimeArtifact(name))
                {
                    warnings.Add(
                        $"Misplaced runtime file in data: '{name}'. " +
                        $"It belongs beside CityDwellers.exe and is ignored here.");
                }
                else
                {
                    warnings.Add(
                        $"Alien file in data: '{name}'. City Dwellers does not use it.");
                }
            }

            foreach (string directory in Directory.GetDirectories(dataDirectory))
            {
                string name = Path.GetFileName(directory);
                if (IsBotDataDirectory(name))
                {
                    InspectBotDataDirectory(directory, name, warnings);
                    continue;
                }

                if (string.Equals(name, "GameData", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "NavMeshes", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(
                        $"Misplaced runtime directory in data: '{name}'. " +
                        $"It belongs beside CityDwellers.exe and is ignored here.");
                }
                else
                {
                    warnings.Add(
                        $"Alien directory in data: '{name}'. City Dwellers does not use it.");
                }
            }

            warnings.Sort(StringComparer.OrdinalIgnoreCase);
            return warnings;
        }

        private static bool IsRuntimeArtifact(string name)
        {
            foreach (string legacyHost in new[] { "Manager", "Flipper", "Buddies" })
            {
                if (string.Equals(name, legacyHost + ".exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, legacyHost + ".exe.config", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, legacyHost + ".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string extension = Path.GetExtension(name);
            return string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".config", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "CityDwellers.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBotDataDirectory(string name)
        {
            return string.Equals(name, "NavigationTraces", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "diagnostic-dumps", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBotDataFile(string name)
        {
            string[] exactNames =
            {
                "adminlist.json",
                "banlist.json",
                "memberlist.json",
                "alts.json",
                "citymanager-cloak-state.json",
                "citymanager-cloak-events.jsonl",
                "citymanager-diagnostics.log",
                "citymanager-diagnostics.log.previous",
                "citymanager-membership-state.json",
                "citymanager-raid-state.json",
                "citydwellers.log",
                "citydwellers.log.previous",
                "citydwellers-manager-restart.request",
                "citydwellers-manager-restart.request.tmp",
                "cityflipper-cache.json",
                "cityflipper-cache.json.tmp",
                "cityflipper-toggle.request",
                "cityflipper-operation.id",
                "cityflipper-cancel.request",
                "cityflipper-result.json",
                "cityflipper-result.json.tmp",
                "portable-layout-v2.migrated"
            };

            foreach (string exactName in exactNames)
            {
                if (string.Equals(name, exactName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return MatchesPrefix(name, "adminlist.json.invalid-") ||
                   MatchesPrefix(name, "banlist.json.invalid-") ||
                   MatchesPrefix(name, "memberlist.json.invalid-") ||
                   MatchesPrefix(name, "citymanager-membership-state.json.invalid-") ||
                   MatchesPrefix(name, "alts.json.invalid-") ||
                   MatchesPrefix(name, "cityflipper-cache.json.invalid-clock-") ||
                   (MatchesPrefix(name, "cityflipper-cancel.request.") &&
                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) ||
                   (MatchesPrefix(name, "citybuddies-ready-") &&
                    name.EndsWith(".ready", StringComparison.OrdinalIgnoreCase)) ||
                   (MatchesPrefix(name, "citybuddies-position-") &&
                    (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase))) ||
                   (MatchesPrefix(name, "citybuddies-home-") &&
                    (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase))) ||
                   (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                    (MatchesPrefix(name, "adminlist.json") ||
                     MatchesPrefix(name, "banlist.json") ||
                     MatchesPrefix(name, "memberlist.json") ||
                     MatchesPrefix(name, "alts.json") ||
                     MatchesPrefix(name, "citymanager-cloak-state.json") ||
                     MatchesPrefix(name, "citymanager-membership-state.json") ||
                     MatchesPrefix(name, "citymanager-raid-state.json")));
        }

        private static bool MatchesPrefix(string name, string prefix)
        {
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void InspectBotDataDirectory(
            string directory,
            string directoryName,
            List<string> warnings)
        {
            string expectedPattern = string.Equals(
                directoryName,
                "NavigationTraces",
                StringComparison.OrdinalIgnoreCase)
                ? "*.jsonl"
                : "apcmanager-dump-*.log";

            var expected = new HashSet<string>(
                Directory.GetFiles(directory, expectedPattern, SearchOption.TopDirectoryOnly),
                StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.GetFiles(
                directory,
                "*",
                SearchOption.AllDirectories))
            {
                if (expected.Contains(file))
                    continue;

                string relativePath = file.Substring(directory.Length).TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                warnings.Add(
                    $"Alien file in data\\{directoryName}: '{relativePath}'. " +
                    $"City Dwellers does not use it.");
            }

            foreach (string childDirectory in Directory.GetDirectories(
                directory,
                "*",
                SearchOption.AllDirectories))
            {
                string relativePath = childDirectory.Substring(directory.Length).TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                warnings.Add(
                    $"Alien directory in data\\{directoryName}: '{relativePath}'. " +
                    $"City Dwellers does not use it.");
            }
        }

        private static string GetRuntimeDirectory()
        {
            string processRuntimeDirectory = Environment.GetEnvironmentVariable(
                RuntimeRootEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(processRuntimeDirectory))
                return Path.GetFullPath(processRuntimeDirectory);

            return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static void MigrateLegacyLayout(
            string runtimeDirectory,
            string dataDirectory)
        {
            string migrationMarker = Path.Combine(
                dataDirectory,
                "portable-layout-v2.migrated");
            if (File.Exists(migrationMarker))
                return;

            string repositoryRoot = FindRepositoryRoot(runtimeDirectory);
            if (repositoryRoot == null)
                return;

            string legacySettings = Path.Combine(repositoryRoot, "settings");
            if (Directory.Exists(legacySettings))
            {
                foreach (string file in Directory.GetFiles(legacySettings))
                {
                    string name = Path.GetFileName(file);
                    string destination = AdministratorSettings.Contains(name)
                        ? Path.Combine(runtimeDirectory, name)
                        : Path.Combine(dataDirectory, name);
                    CopyIfMissing(file, destination);
                }

                foreach (string directory in Directory.GetDirectories(legacySettings))
                {
                    CopyDirectoryIfMissing(
                        directory,
                        Path.Combine(dataDirectory, Path.GetFileName(directory)));
                }
            }

            MigrateLooseRuntimeData(
                Path.Combine(repositoryRoot, "bin", "Release"),
                dataDirectory);
            MigrateLooseRuntimeData(
                Path.Combine(repositoryRoot, "bin", "Debug"),
                dataDirectory);

            File.WriteAllText(
                migrationMarker,
                "Legacy settings and runtime data were copied without overwrite." +
                Environment.NewLine);
        }

        private static string FindRepositoryRoot(string runtimeDirectory)
        {
            var directory = new DirectoryInfo(runtimeDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                    return directory.FullName;

                directory = directory.Parent;
            }

            return null;
        }

        private static void MigrateLooseRuntimeData(
            string legacyRuntimeDirectory,
            string dataDirectory)
        {
            if (!Directory.Exists(legacyRuntimeDirectory))
                return;

            foreach (string file in Directory.GetFiles(legacyRuntimeDirectory))
            {
                string name = Path.GetFileName(file);
                if (IsMutableRuntimeFile(name))
                    CopyIfMissing(file, Path.Combine(dataDirectory, name));
            }

            foreach (string name in new[] { "NavigationTraces", "diagnostic-dumps" })
            {
                CopyDirectoryIfMissing(
                    Path.Combine(legacyRuntimeDirectory, name),
                    Path.Combine(dataDirectory, name));
            }
        }

        private static bool IsMutableRuntimeFile(string name)
        {
            return name.StartsWith("cityflipper-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("citybuddies-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("citymanager-", StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyIfMissing(string source, string destination)
        {
            if (!File.Exists(source) || File.Exists(destination))
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination, false);
            }
            catch (IOException)
            {
                // Another City Dwellers process may have completed the same
                // first-run copy. Existing destinations always win.
            }
        }

        private static void CopyDirectoryIfMissing(
            string sourceDirectory,
            string destinationDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
                return;

            foreach (string sourceFile in Directory.GetFiles(
                sourceDirectory,
                "*",
                SearchOption.AllDirectories))
            {
                string relativePath = sourceFile.Substring(
                    sourceDirectory.Length).TrimStart(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                CopyIfMissing(
                    sourceFile,
                    Path.Combine(destinationDirectory, relativePath));
            }
        }
    }
}
