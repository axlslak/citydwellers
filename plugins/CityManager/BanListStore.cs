using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AOSharp.Clientless.Logging;
using Newtonsoft.Json;

namespace CityManager
{
    internal static class BanListStore
    {
        private const int CurrentVersion = 1;
        private const string FileName = "banlist.json";

        private static readonly object Sync = new object();
        private static readonly HashSet<string> BannedCharacters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string _path;

        public static void Initialize(string settingsDirectory)
        {
            lock (Sync)
            {
                _path = Path.Combine(settingsDirectory, FileName);
                BannedCharacters.Clear();

                if (File.Exists(_path))
                {
                    try
                    {
                        LoadLocked();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Unable to load ban list: {ex.Message}");
                        PreserveInvalidFileLocked();
                        TrySaveBootstrapLocked();
                    }
                }
                else
                {
                    TrySaveBootstrapLocked();
                }

                Logger.Information(
                    $"Ban list initialized from {_path}: " +
                    $"{BannedCharacters.Count} banned characters.");
            }
        }

        public static bool Contains(string characterName)
        {
            lock (Sync)
                return BannedCharacters.Contains(characterName ?? string.Empty);
        }

        public static List<string> Snapshot()
        {
            lock (Sync)
                return SnapshotLocked();
        }

        public static bool TryAdd(string characterName, out string message)
        {
            string normalized;
            if (!TryNormalizeName(characterName, out normalized, out message))
                return false;

            lock (Sync)
            {
                if (BannedCharacters.Contains(normalized))
                {
                    message = $"{normalized} is already banned.";
                    return false;
                }

                BannedCharacters.Add(normalized);

                try
                {
                    SaveLocked();
                }
                catch (Exception ex)
                {
                    BannedCharacters.Remove(normalized);
                    message = $"Ban list was not changed: {ex.Message}";
                    return false;
                }

                message = $"Banned {normalized}.";
                return true;
            }
        }

        public static bool TryRemove(string characterName, out string message)
        {
            string normalized;
            if (!TryNormalizeName(characterName, out normalized, out message))
                return false;

            lock (Sync)
            {
                string existing = BannedCharacters.FirstOrDefault(
                    name => string.Equals(
                        name,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    message = $"{normalized} is not banned.";
                    return false;
                }

                BannedCharacters.Remove(existing);

                try
                {
                    SaveLocked();
                }
                catch (Exception ex)
                {
                    BannedCharacters.Add(existing);
                    message = $"Ban list was not changed: {ex.Message}";
                    return false;
                }

                message = $"Unbanned {existing}.";
                return true;
            }
        }

        public static bool TryReplace(
            IEnumerable<string> characterNames,
            out bool changed,
            out string message)
        {
            changed = false;
            var replacement = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string characterName in characterNames ?? Enumerable.Empty<string>())
            {
                string normalized;
                if (!TryNormalizeName(characterName, out normalized, out message))
                    return false;

                replacement.Add(normalized);
            }

            lock (Sync)
            {
                if (BannedCharacters.SetEquals(replacement))
                {
                    message = "Ban list already uses canonical main names.";
                    return true;
                }

                List<string> previous = SnapshotLocked();
                BannedCharacters.Clear();
                BannedCharacters.UnionWith(replacement);

                try
                {
                    SaveLocked();
                }
                catch (Exception ex)
                {
                    BannedCharacters.Clear();
                    BannedCharacters.UnionWith(previous);
                    message = $"Ban list was not changed: {ex.Message}";
                    return false;
                }

                changed = true;
                message = "Ban list was reduced to canonical main names.";
                return true;
            }
        }

        private static void LoadLocked()
        {
            PersistedBanList state =
                JsonConvert.DeserializeObject<PersistedBanList>(
                    File.ReadAllText(_path));

            if (state == null || state.Version != CurrentVersion)
                throw new InvalidDataException("Unsupported ban-list file.");

            if (state.BannedCharacters == null)
                throw new InvalidDataException("Ban-list file has no list.");

            foreach (string candidate in state.BannedCharacters)
            {
                string normalized;
                string error;
                if (!TryNormalizeName(candidate, out normalized, out error))
                    throw new InvalidDataException(error);

                BannedCharacters.Add(normalized);
            }
        }

        private static void TrySaveBootstrapLocked()
        {
            try
            {
                SaveLocked();
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to create ban-list file {_path}: {ex.Message}");
            }
        }

        private static void SaveLocked()
        {
            if (string.IsNullOrWhiteSpace(_path))
                throw new InvalidOperationException("Ban list is not initialized.");

            var state = new PersistedBanList
            {
                Version = CurrentVersion,
                BannedCharacters = SnapshotLocked()
            };

            string tempPath = _path + ".tmp";
            string json = JsonConvert.SerializeObject(state, Formatting.Indented);

            File.WriteAllText(tempPath, json);

            if (File.Exists(_path))
                File.Delete(_path);

            File.Move(tempPath, _path);
        }

        private static void PreserveInvalidFileLocked()
        {
            if (!File.Exists(_path))
                return;

            try
            {
                string backupPath =
                    _path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(_path, backupPath);
                Logger.Warning($"Preserved invalid ban list as {backupPath}.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to preserve invalid ban list: {ex.Message}");
            }
        }

        private static List<string> SnapshotLocked()
        {
            return BannedCharacters
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryNormalizeName(
            string characterName,
            out string normalized,
            out string error)
        {
            normalized = (characterName ?? string.Empty).Trim();

            if (normalized.Length == 0 || normalized.Length > 30)
            {
                error = "Character name must be between 1 and 30 characters.";
                return false;
            }

            foreach (char character in normalized)
            {
                if (!char.IsLetterOrDigit(character))
                {
                    error = "Character names may contain only letters and digits.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private sealed class PersistedBanList
        {
            public int Version;
            public List<string> BannedCharacters;
        }
    }
}
