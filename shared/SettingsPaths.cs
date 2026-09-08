using System;
using System.Collections.Generic;
using System.IO;

namespace CityDwellers.Shared
{
    internal static class SettingsPaths
    {
        private const string SolutionFileName = "citydwellers.sln";
        private const string DataDirectoryName = "data";

        private static readonly HashSet<string> AdministratorSettings =
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

        private static string GetRuntimeDirectory()
        {
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
