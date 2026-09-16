using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MalisBuffBots
{
    public class UserRank : JsonFile<Dictionary<Rank, List<string>>>
    {
        public UserRank(string jsonPath) : base(jsonPath) { }

        public bool MeetsRank(Rank rank, string name)
        {
            switch (rank)
            {
                case Rank.Unranked:
                    return true;
                case Rank.Warper:
                    return HasUser(Rank.Warper, name);
                case Rank.Moderator:
                    return HasUser(Rank.Moderator, name) || HasUser(Rank.Admin, name);
                case Rank.Admin:
                    return HasUser(Rank.Admin, name);
            }

            return false;
        }

        private bool HasUser(Rank rank, string name) => _data.TryGetValue(rank, out List<string> mods) && mods.Select(x => x.ToLower()).Contains(name.ToLower());
    }
}
