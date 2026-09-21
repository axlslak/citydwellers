using CityDwellers.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AOSharp.Clientless.Logging;
using Newtonsoft.Json;

namespace CityManager
{
    internal static class AdminListStore
    {
        private const int CurrentVersion = 1;
        private const string FileName = "adminlist.json";

        private static readonly object Sync = new object();
        private static readonly HashSet<string> Administrators =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string _path;

        public static void Initialize(string settingsDirectory)
        {
            lock (Sync)
            {
                _path = Path.Combine(settingsDirectory, FileName);
                Administrators.Clear();

                if (DiskFiles.Exists(_path))
                {
                    // Existing authority state must remain authoritative. Invalid
                    // imported records require repair; never reset to defaults.
                    try { LoadLocked(); }
                    catch (Exception ex)
                    {
                        HostFailure.Stop("The authority configuration is invalid: " + _path, ex);
                        throw;
                    }
                }
                else
                {
                    SeedDefaultsLocked();
                    TrySaveBootstrapLocked();
                }

                Logger.Information(
                    $"Administrator list initialized from {_path}: " +
                    $"{string.Join(", ", SnapshotLocked())}.");
            }
        }

        public static bool Contains(string characterName)
        {
            lock (Sync)
                return Administrators.Contains(characterName ?? string.Empty);
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
                if (Administrators.Contains(normalized))
                {
                    message = $"{normalized} is already an administrator.";
                    return false;
                }

                Administrators.Add(normalized);

                try
                {
                    SaveLocked();
                }
                catch (Exception ex)
                {
                    Administrators.Remove(normalized);
                    message = $"Administrator list was not changed: {ex.Message}";
                    return false;
                }

                message = $"Added {normalized} to the administrator list.";
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
                string existing = Administrators.FirstOrDefault(
                    name => string.Equals(
                        name,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    message = $"{normalized} is not an administrator.";
                    return false;
                }

                if (Administrators.Count == 1)
                {
                    message = "The final administrator cannot be removed.";
                    return false;
                }

                Administrators.Remove(existing);

                try
                {
                    SaveLocked();
                }
                catch (Exception ex)
                {
                    Administrators.Add(existing);
                    message = $"Administrator list was not changed: {ex.Message}";
                    return false;
                }

                message = $"Removed {existing} from the administrator list.";
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

            if (replacement.Count == 0)
            {
                message = "Administrator list cannot be empty.";
                return false;
            }

            lock (Sync)
            {
                if (Administrators.SetEquals(replacement))
                {
                    message = "Administrator list already uses canonical main names.";
                    return true;
                }

                List<string> previous = SnapshotLocked();
                Administrators.Clear();
                Administrators.UnionWith(replacement);

                try
                {
                    SaveLocked();
                }
                catch (Exception ex)
                {
                    Administrators.Clear();
                    Administrators.UnionWith(previous);
                    message = $"Administrator list was not changed: {ex.Message}";
                    return false;
                }

                changed = true;
                message = "Administrator list was reduced to canonical main names.";
                return true;
            }
        }

        private static void LoadLocked()
        {
            PersistedAdminList state =
                JsonConvert.DeserializeObject<PersistedAdminList>(
                    DiskFiles.ReadAllText(_path));

            if (state == null || state.Version != CurrentVersion)
                throw new InvalidDataException("Unsupported administrator-list file.");

            if (state.Administrators == null)
                throw new InvalidDataException("Administrator-list file has no list.");

            foreach (string candidate in state.Administrators)
            {
                string normalized;
                string error;
                if (!TryNormalizeName(candidate, out normalized, out error))
                    throw new InvalidDataException(error);

                Administrators.Add(normalized);
            }

            if (Administrators.Count == 0)
                throw new InvalidDataException("Administrator list cannot be empty.");
        }

        private static void SeedDefaultsLocked()
        {
            Administrators.Clear();
            Administrators.Add("Kavem");
            Administrators.Add("Doczy");
        }

        private static void TrySaveBootstrapLocked()
        {
            SaveLocked();
        }

        private static void SaveLocked()
        {
            if (string.IsNullOrWhiteSpace(_path))
                throw new InvalidOperationException("Administrator list is not initialized.");

            var state = new PersistedAdminList
            {
                Version = CurrentVersion,
                Administrators = SnapshotLocked()
            };

            DiskFiles.WriteAllText(_path,
                JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        private static List<string> SnapshotLocked()
        {
            return Administrators
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
                error = "Administrator name must be between 1 and 30 characters.";
                return false;
            }

            foreach (char character in normalized)
            {
                if (!CityDwellers.Shared.CharacterNames.IsCharacter(character))
                {
                    error = "Administrator names may contain only letters, digits and hyphens.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private sealed class PersistedAdminList
        {
            public int Version;
            public List<string> Administrators;
        }
    }
}
