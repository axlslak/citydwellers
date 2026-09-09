using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using CityBankers;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessBankerStockCommand(string rawCommand, ReplyTarget target)
        {
            string response;
            if (!StockCommandEngine.TryBuildResponse(
                    rawCommand,
                    RuntimeStateStore.LoadCurrentStock(_settingsDir),
                    Client.CharacterName,
                    out response,
                    CommandPrefix))
            {
                Reply(target, Usage(target, "stock [family [slot [targetQl]]]"));
                return;
            }

            Reply(target, response);
        }

        private void ProcessBankerDonorCommand(string[] parts, ReplyTarget target)
        {
            if (parts == null || parts.Length != 1)
            {
                Reply(target, Usage(target, "donor"));
                return;
            }

            JObject ledger = RuntimeStateStore.ReadJson<JObject>(
                Path.Combine(_dataDir, "ledger.json"));
            IEnumerable<JToken> items = ledger?["Items"] as JArray ?? new JArray();
            int donorCount = items
                .Select(item => item?["From"]?.ToString()?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            Reply(target, "CityBankers active donors: " + donorCount + ".");
        }
    }
}
