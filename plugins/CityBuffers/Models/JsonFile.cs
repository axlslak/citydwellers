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
using CityDwellers.Shared;

namespace MalisBuffBots
{
    public class JsonFile<T>
    {
        private readonly string _path;
        private readonly bool _mutable;
        protected readonly T _data;
        protected string Raw;

        public JsonFile(string jsonPath)
        {
            try
            {
                _path = jsonPath;
                _mutable = string.Equals(jsonPath, Path.BAN_JSON, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(jsonPath, Path.USERRANK_JSON, StringComparison.OrdinalIgnoreCase);

                if (_mutable && !SqlFile.Exists(jsonPath))
                {
                    if (string.Equals(jsonPath, Path.BAN_JSON, StringComparison.OrdinalIgnoreCase))
                    {
                        SqlFile.TryCreateNew(jsonPath, JsonConvert.SerializeObject(new List<string>()));
                    }
                    else
                    {
                        SqlFile.TryCreateNew(jsonPath, JsonConvert.SerializeObject(new Dictionary<Rank, List<string>>
                        {
                            { Rank.Admin, new List<string> {  } },
                            { Rank.Moderator, new List<string> {  } },
                            { Rank.Warper, new List<string> {  } },
                            { Rank.Unranked, new List<string> {  } },
                        }, Formatting.Indented));
                    }
                }

                Raw = _mutable ? SqlFile.ReadAllText(jsonPath) : System.IO.File.ReadAllText(jsonPath);
                _data = JsonConvert.DeserializeObject<T>(Raw);
                if (ReferenceEquals(_data, null)) throw new InvalidOperationException("JSON contains null.");
            }
            catch (Exception ex)
            {
                if (_mutable)
                    SqlStore.FailClosed("The MySQL buffer authority record is invalid: " + jsonPath, ex);
                throw new InvalidOperationException("Cannot load buffer JSON: " + jsonPath, ex);
            }
        }

        public void Save()
        {
            if (!_mutable)
                throw new InvalidOperationException("Deployed buffer definitions are read-only.");
            SqlFile.WriteAllText(_path, JsonConvert.SerializeObject(_data));
        }
    }
}
