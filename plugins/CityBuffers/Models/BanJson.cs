using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Serialization;
using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MalisBuffBots
{
    public class BanJson : JsonFile<List<string>>
    {
        public readonly List<string> Entries;

        public BanJson(string jsonPath) : base(jsonPath)
        {
            Entries = _data;
        }

        public bool Contains(string name) => _data.Contains(name);

        public bool TryAdd(string name, bool save = true)
        {
            try
            {
                if (_data.Contains(name))
                    return false;

                _data.Add(name);

                if (save)
                    Save();

                return true;
            }
            catch (Exception ex)
            {
                Logger.Information(ex.Message);
                return false;
            }
        }

        public bool TryRemove(string name, bool save = true)
        {
            if (!_data.Contains(name))
                return false;

            _data.Remove(name);

            if (save)
                Save();

            return true;
        }
    }
}
