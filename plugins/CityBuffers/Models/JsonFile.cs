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
                _mutable = string.Equals(jsonPath, Path.BAN_JSON, StringComparison.OrdinalIgnoreCase);

                if (_mutable && !DiskFiles.Exists(jsonPath))
                    DiskFiles.TryCreateNew(jsonPath, JsonConvert.SerializeObject(new List<string>()));

                Raw = _mutable ? DiskFiles.ReadAllText(jsonPath) : System.IO.File.ReadAllText(jsonPath);
                _data = JsonConvert.DeserializeObject<T>(Raw);
                if (ReferenceEquals(_data, null)) throw new InvalidOperationException("JSON contains null.");
            }
            catch (Exception ex)
            {
                if (_mutable)
                    HostFailure.Stop("The buffer authority record is invalid: " + jsonPath, ex);
                throw new InvalidOperationException("Cannot load buffer JSON: " + jsonPath, ex);
            }
        }

        public void Save()
        {
            if (!_mutable)
                throw new InvalidOperationException("Deployed buffer definitions are read-only.");
            DiskFiles.WriteAllText(_path, JsonConvert.SerializeObject(_data));
        }
    }
}
