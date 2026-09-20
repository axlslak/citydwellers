using System;
using System.Linq;
using System.Threading;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private void BeginBufferStatus(ReplyTarget target)
        {
            QueuePublicWork(target, () =>
            {
                try
                {
                    var accounts = BufferSettings.Read().Active.ToList();
                    if (accounts.Count == 0) { Reply(target, "No froob buffers enabled."); return; }
                    foreach (var account in accounts)
                    {
                        string message;
                        try
                        {
                            string response = LocalIpc.RequestLineAsync(BufferSettings.PipeName(account.Character),
                                "{\"Kind\":\"status\"}", 1000, 3000).GetAwaiter().GetResult();
                            var state = JObject.Parse(response);
                            DateTime? observed = (DateTime?)state["ObservedUtc"];
                            if (!observed.HasValue || observed.Value < DateTime.UtcNow.AddSeconds(-5))
                                message = account.Character + ": waiting for a fresh buffer update.";
                            else
                                message = account.Character + ": " + ((bool?)state["Ready"] == true ? "ready" : "starting") +
                                    ", " + (string)state["Profession"] + ", " + (int?)state["NanoCount"] +
                                    " known nanos, " + (int?)state["QueueLength"] + " queued buffs.";
                        }
                        catch (Exception) { message = account.Character + ": buffer status unavailable."; }
                        Reply(target, message);
                    }
                }
                catch (Exception) { Reply(target, "Unable to read Buffers configuration; check the host log."); }
            });
        }
    }
}
