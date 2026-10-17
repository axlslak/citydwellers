using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class PlayerProfile
    {
        public string Name;
        public int Level = -1, AlienLevel = -1, Profession;
        public DateTime UpdatedUtc;
        internal PlayerProfile Copy() => (PlayerProfile)MemberwiseClone();
    }

    public interface IPlayerProfilePersistence
    {
        void SavePlayerProfiles(PlayerProfile[] profiles);
    }

    public sealed partial class ManagerMemory
    {
        private readonly Dictionary<string, PlayerProfile> _playerProfiles =
            new Dictionary<string, PlayerProfile>(StringComparer.OrdinalIgnoreCase);
        private IPlayerProfilePersistence _profilePersistence;

        public static void InitializePlayerProfiles(PlayerProfile[] profiles, IPlayerProfilePersistence persistence)
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only ManagerHost owns player profile persistence.");
            lock (Current._accountingSync)
            {
                Current._profilePersistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
                foreach (var profile in profiles) Current._playerProfiles.Add(profile.Name, profile.Copy());
            }
        }

        public PlayerProfile[] ReadPlayerProfiles(string[] names)
        {
            lock (_accountingSync)
                return names.Distinct(StringComparer.OrdinalIgnoreCase).Where(_playerProfiles.ContainsKey)
                    .Select(name => _playerProfiles[name].Copy()).ToArray();
        }

        public bool HavePlayerProfiles(string[] names)
        {
            lock (_accountingSync) return names.All(_playerProfiles.ContainsKey);
        }

        // Metadata does not add buddies or establish online presence. Missing
        // fields may be filled later without holding up any command or cast.
        public void RememberPlayerProfiles(PlayerProfile[] profiles)
        {
            lock (_accountingSync)
            {
                var changed = new Dictionary<string, PlayerProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var incoming in profiles)
                {
                    string name = PresenceName(incoming.Name);
                    PlayerProfile old;
                    if (!changed.TryGetValue(name, out old)) _playerProfiles.TryGetValue(name, out old);
                    var next = old?.Copy() ?? new PlayerProfile { Name = name };
                    bool newer = old == null || incoming.UpdatedUtc >= old.UpdatedUtc;
                    if (incoming.Level > 0 && incoming.Level <= 220 && (newer || next.Level <= 0)) next.Level = incoming.Level;
                    if (incoming.AlienLevel >= 0 && incoming.AlienLevel <= 30 && (newer || next.AlienLevel < 0)) next.AlienLevel = incoming.AlienLevel;
                    if (incoming.Profession > 0 && incoming.Profession <= 15 && incoming.Profession != 13 &&
                        (newer || next.Profession == 0)) next.Profession = incoming.Profession;
                    if (next.Level <= 0 && next.AlienLevel < 0 && next.Profession == 0) continue;
                    if (old != null && old.Level == next.Level && old.AlienLevel == next.AlienLevel && old.Profession == next.Profession) continue;
                    next.UpdatedUtc = old != null && old.UpdatedUtc > incoming.UpdatedUtc ? old.UpdatedUtc : incoming.UpdatedUtc;
                    changed[name] = next;
                }
                if (changed.Count == 0) return;
                _profilePersistence.SavePlayerProfiles(changed.Values.ToArray());
                foreach (var profile in changed.Values) _playerProfiles[profile.Name] = profile;
            }
        }
    }
}
