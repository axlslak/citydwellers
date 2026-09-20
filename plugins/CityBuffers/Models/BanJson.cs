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
        private readonly object _sync = new object();

        public BanJson(string jsonPath) : base(jsonPath)
        {
            Entries = _data;
        }

        public bool Contains(string name)
        {
            lock (_sync)
                return _data.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        }

        public bool TryAdd(string name, bool save = true)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(name) || Contains(name))
                    return false;

                _data.Add(name);
                try
                {
                    if (save)
                        Save();
                    return true;
                }
                catch (Exception ex)
                {
                    _data.Remove(name);
                    Logger.Information("Ban was not changed: " + ex.Message);
                    return false;
                }
            }
        }

        public bool TryRemove(string name, bool save = true)
        {
            lock (_sync)
            {
                int index = _data.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return false;

                string existing = _data[index];
                _data.RemoveAt(index);
                try
                {
                    if (save)
                        Save();
                    return true;
                }
                catch (Exception ex)
                {
                    _data.Insert(index, existing);
                    Logger.Information("Ban was not changed: " + ex.Message);
                    return false;
                }
            }
        }
    }
}
