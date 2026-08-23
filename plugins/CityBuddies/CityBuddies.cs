using AOSharp.Clientless;
using AOSharp.Clientless.Logging;

namespace CityBuddies
{
    public class CityBuddies : ClientlessPluginEntry
    {
        public override void Init(string pluginDir)
        {
            Logger.Information("CityBuddies runtime helper initialized.");

            Client.Config.AutoReconnect = true;

            Logger.Information(
                $"CityBuddies AutoReconnect={Client.Config.AutoReconnect}.");
        }

        public override void Teardown()
        {
            Logger.Information("CityBuddies runtime helper teardown.");
        }
    }
}
