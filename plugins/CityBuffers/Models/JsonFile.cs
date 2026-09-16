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
    public class JsonFile<T>
    {
        private readonly string _path;
        protected readonly T _data;
        protected string Raw;

        public JsonFile(string jsonPath)
        {
            try
            {
                _path = jsonPath;

                if (!File.Exists(jsonPath))
                {
                    if (jsonPath.Contains("BanList"))
                    {
                        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new List<string>()));
                    }
                    else if (jsonPath.Contains("UserRanks"))
                    {
                        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new Dictionary<Rank, List<string>>
                        {
                            { Rank.Admin, new List<string> {  } },
                            { Rank.Moderator, new List<string> {  } },
                            { Rank.Warper, new List<string> {  } },
                            { Rank.Unranked, new List<string> {  } },
                        }, Formatting.Indented));
                    }
                }

                Raw = File.ReadAllText(jsonPath);
                _data = JsonConvert.DeserializeObject<T>(Raw);
                if (ReferenceEquals(_data, null)) throw new InvalidOperationException("JSON contains null.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Cannot load buffer JSON: " + jsonPath, ex);
            }
        }

        public void Save()
        {
            File.WriteAllText(_path, JsonConvert.SerializeObject(_data));
        }
    }
}
