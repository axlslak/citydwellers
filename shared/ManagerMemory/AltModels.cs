using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class AltState
    {
        public int Version = 2;
        public List<AltGroupState> Groups = new List<AltGroupState>();

        internal AltState Copy() => new AltState
        {
            Version = Version,
            Groups = (Groups ?? new List<AltGroupState>())
                .Where(group => group != null)
                .Select(group => group.Copy())
                .ToList()
        };
    }

    [Serializable]
    public sealed class AltGroupState
    {
        public string Main;
        public List<string> ObservedCharacters = new List<string>();
        public List<string> AddedCharacters = new List<string>();
        public List<string> RemovedCharacters = new List<string>();
        public DateTime? LastUpdatedUtc;

        internal AltGroupState Copy() => new AltGroupState
        {
            Main = Main,
            ObservedCharacters = (ObservedCharacters ?? new List<string>()).ToList(),
            AddedCharacters = (AddedCharacters ?? new List<string>()).ToList(),
            RemovedCharacters = (RemovedCharacters ?? new List<string>()).ToList(),
            LastUpdatedUtc = LastUpdatedUtc
        };
    }
}
