using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json.Linq;

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
                "citydwellers.json"
            };

        private static readonly HashSet<string> LegacyAdministratorSettings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "manager.json",
                "flipper.json",
                "buddies.json"
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

                if (LegacyAdministratorSettings.Contains(name))
                {
                    warnings.Add(
                        $"Obsolete administrator setting in runtime root: '{name}'. " +
                        "Its settings belong in citydwellers.json and this file is ignored.");
                    continue;
                }

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
                    string.Equals(name, "Buffers", StringComparison.OrdinalIgnoreCase) ||
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
                else if (LegacyAdministratorSettings.Contains(name))
                {
                    warnings.Add(
                        $"Obsolete administrator setting in data: '{name}'. " +
                        "Its settings belong in citydwellers.json and this file is ignored.");
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
            return string.Equals(name, "buffers", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "transaction-traces", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "incident-dumps", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "colonist-backpack-repair-v1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "NavigationTraces", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "diagnostic-dumps", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "ledger", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "history", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "tell-queue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "storage-baselines", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "storage-enrollments", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "startup-census", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "custody-transactions", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "banker-returns", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "banker-extractions", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "physical-states", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "physical-census-v2", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBotDataFile(string name)
        {
            string publishedName;
            if (TryGetAtomicTargetName(name, out publishedName))
                return IsBotDataFile(publishedName);
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
                "citydwellers-events.jsonl",
                "items.json",
                "items.json.index-v1.bin",
                "items-pairs.json",
                "citydwellers-manager-restart.request",
                "citydwellers-manager-restart.request.tmp",
                "cityflipper-cache.json",
                "cityflipper-cache.json.tmp",
                "cityflipper-toggle.request",
                "cityflipper-operation.id",
                "cityflipper-cancel.request",
                "cityflipper-result.json",
                "cityflipper-result.json.tmp",
                "storage-state.json",
                "current-stock.json",
                "dispatch-queue.json",
                "ledger.json",
                "lost.json",
                "symbiant-index.json",
                "withdrawal.json",
                "recovery-reservations.json",
                "storage-baseline.json",
                "physical-state.json",
                "route-repair-active.json",
                "route-repair-plan.json",
                "route-repair-last-completed.json",
                "bagaudit-recovery-hold.json",
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
                   (MatchesPrefix(name, "citybankers-") &&
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

        private static bool TryGetAtomicTargetName(string name, out string target)
        {
            target = null;
            int marker = name.LastIndexOf(".tmp-", StringComparison.OrdinalIgnoreCase);
            Guid token;
            if (marker <= 0 || !Guid.TryParseExact(name.Substring(marker + 5), "N", out token)) return false;
            target = name.Substring(0, marker);
            return true;
        }

        private static void InspectBotDataDirectory(
            string directory,
            string directoryName,
            List<string> warnings)
        {
            if (string.Equals(directoryName, "buffers", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(directory.Length).TrimStart(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (parts.Length == 2 && (parts[1].Equals("UserRanks.json", StringComparison.OrdinalIgnoreCase) ||
                        parts[1].Equals("BanList.json", StringComparison.OrdinalIgnoreCase))) continue;
                    warnings.Add("Unrecognized buffer data file: '" + relative + "'.");
                }
                return;
            }
            if (string.Equals(
                    directoryName,
                    "tell-queue",
                    StringComparison.OrdinalIgnoreCase))
            {
                var allowedChildren = new HashSet<string>(
                    new[]
                    {
                        "pending",
                        "assigned",
                        "acknowledgements",
                        "senders",
                        "failed",
                        "manager-channel"
                    },
                    StringComparer.OrdinalIgnoreCase);

                foreach (string child in Directory.GetDirectories(directory))
                {
                    if (!allowedChildren.Contains(Path.GetFileName(child)))
                    {
                        warnings.Add(
                            $"Alien directory in data\\{directoryName}: " +
                            $"'{Path.GetFileName(child)}'. City Dwellers does not use it.");
                    }
                }

                foreach (string file in Directory.GetFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories))
                {
                    string parent = Path.GetFileName(Path.GetDirectoryName(file));
                    if (string.Equals(
                            file,
                            Path.Combine(directory, "sequence.txt"),
                            StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(
                             parent,
                             "manager-channel",
                             StringComparison.OrdinalIgnoreCase) &&
                         (string.Equals(
                              Path.GetFileName(file),
                              "sequence.txt",
                              StringComparison.OrdinalIgnoreCase) ||
                          Path.GetFileName(file).StartsWith(
                              "sequence.txt.tmp-",
                              StringComparison.OrdinalIgnoreCase))) ||
                        Path.GetFileName(file).StartsWith(
                            "sequence.txt.tmp-",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (allowedChildren.Contains(parent) &&
                        (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(file).Contains(".json.tmp-")))
                    {
                        continue;
                    }

                    string relative = file.Substring(directory.Length).TrimStart(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    warnings.Add(
                        $"Alien file in data\\{directoryName}: '{relative}'. " +
                        "City Dwellers does not use it.");
                }

                return;
            }

            if (string.Equals(
                    directoryName,
                    "physical-census-v2",
                    StringComparison.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.GetFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relative = file.Substring(directory.Length).TrimStart(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    warnings.Add(
                        $"Alien file in data\\{directoryName}: '{relative}'. " +
                        "City Dwellers does not use it.");
                }

                return;
            }

            if (string.Equals(directoryName, "startup-census", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directoryName, "custody-transactions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directoryName, "banker-returns", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directoryName, "banker-extractions", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string extension = Path.GetExtension(file);
                    if (extension == ".json" || extension == ".ready" || extension == ".blocked" || extension == ".observed") continue;
                    string target;
                    if (TryGetAtomicTargetName(Path.GetFileName(file), out target) &&
                        (target.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                         target.EndsWith(".ready", StringComparison.OrdinalIgnoreCase) ||
                         target.EndsWith(".blocked", StringComparison.OrdinalIgnoreCase) ||
                         target.EndsWith(".observed", StringComparison.OrdinalIgnoreCase)))
                    {
                        warnings.Add("Retained atomic-write temporary in safety evidence directory: '" + file +
                            "'. It is not a published result; retained for inspection.");
                        continue;
                    }
                    warnings.Add("Alien file in safety evidence directory: '" + file + "'.");
                }
                return;
            }

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] expectedPatterns;
            if (string.Equals(directoryName, "NavigationTraces", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "*.jsonl" };
            else if (string.Equals(directoryName, "diagnostic-dumps", StringComparison.OrdinalIgnoreCase))
                // The banker writes its own duplicate-bag evidence here; a clientless
                // character cannot be inspected with the game client.
                expectedPatterns = new[] { "apcmanager-dump-*.log", "citybankers-bagaudit-*.log",
                    "ambiguous-bags-*.json" };
            else if (string.Equals(directoryName, "incident-dumps", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "incident-*.log", "incident-*.log.signature" };
            else if (string.Equals(directoryName, "transaction-traces", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "*.jsonl", "*.problem", "*.state", "evidence-warning.txt" };
            else if (string.Equals(directoryName, "colonist-backpack-repair-v1", StringComparison.OrdinalIgnoreCase))
                // Historical migration evidence, deliberately retained after removal of the one-run helper.
                expectedPatterns = new[] { "*.done.json", "*.progress.json" };
            else if (string.Equals(directoryName, "history", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "history-*.jsonl", "census-*.json" };
            else if (string.Equals(directoryName, "logs", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "citybankers-*.log" };
            else if (string.Equals(directoryName, "storage-baselines", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "storage-baseline-*.json", "physical-state-*.json" };
            else if (string.Equals(directoryName, "storage-enrollments", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "storage-enrollment-*.json" };
            else if (string.Equals(directoryName, "physical-states", StringComparison.OrdinalIgnoreCase))
                expectedPatterns = new[] { "physical-state-*.json" };
            else
                expectedPatterns = new[] { "citybankers-*.jsonl" };

            foreach (string expectedPattern in expectedPatterns)
            {
                foreach (string file in Directory.GetFiles(
                    directory,
                    expectedPattern,
                    SearchOption.TopDirectoryOnly))
                {
                    expected.Add(file);
                }
                foreach (string file in Directory.GetFiles(directory, expectedPattern + ".tmp-*", SearchOption.TopDirectoryOnly))
                {
                    string target;
                    if (TryGetAtomicTargetName(Path.GetFileName(file), out target)) expected.Add(file);
                }
            }

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
                    if (LegacyAdministratorSettings.Contains(name))
                        continue;

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
