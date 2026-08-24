using System;
using System.IO;

namespace CityDwellers.Shared
{
    internal static class SettingsPaths
    {
        private const string SolutionFileName = "citydwellers.sln";

        public static bool TryEnsureDirectory(
            out string settingsDirectory,
            out string error)
        {
            settingsDirectory = GetSettingsDirectory();

            try
            {
                Directory.CreateDirectory(settingsDirectory);
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
            string repositoryRoot = FindRepositoryRoot();
            return Path.Combine(repositoryRoot, "settings");
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                    return directory.FullName;

                directory = directory.Parent;
            }

            return Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
        }
    }
}
