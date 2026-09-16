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
    public class RebuffJson : JsonFile<Dictionary<Profession, List<BuffInfo>>>
    {
        public readonly Dictionary<Profession, List<BuffInfo>> Entries;

        public List<BuffInfo> LocalProfEntries => TryGetValue((Profession)DynelManager.LocalPlayer.Profession);

        public List<BuffInfo> GenericEntries => TryGetValue(Profession.Generic);

        public List<string> LocalPlayerRebuffTags()
        {
            List<BuffInfo> buffInfo = LocalProfEntries.Concat(GenericEntries).ToList();
            return buffInfo.Count() == 0 ? new List<string>() : buffInfo.SelectMany(x => x.Buffs).ToList();
        }

        private List<BuffInfo> TryGetValue(Profession prof)
        {
            if (!Entries.TryGetValue(prof, out List<BuffInfo> entries))
                return new List<BuffInfo>();

            return entries;
        }

        public bool Contains(IEnumerable<string> tags) => LocalProfEntries != null &&
            LocalProfEntries.Any(x => tags.Any(t => x.Buffs.Contains(t))) ||
            GenericEntries != null && GenericEntries.Any(x => tags.Any(t => x.Buffs.Contains(t)));

        public RebuffJson(string jsonPath) : base(jsonPath) => Entries = _data;
    }

    public class BuffInfo
    {
        public List<string> Buffs = new List<string>();
    }
}
