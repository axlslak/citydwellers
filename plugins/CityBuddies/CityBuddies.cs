using System;
using System.IO;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBuddies
{
    public class CityBuddies : ClientlessPluginEntry
    {
        private string _readyPath;
        private bool _readyWritten;

        public override void Init(string pluginDir)
        {
            _readyPath = Path.Combine(
                pluginDir,
                $"citybuddies-ready-{Client.CharacterName}.ready");

            Logger.Information("CityBuddies runtime helper initialized.");

            Client.Config.AutoReconnect = true;
            Client.MessageReceived += MessageReceived;

            Logger.Information(
                $"CityBuddies AutoReconnect={Client.Config.AutoReconnect}.");
        }

        public override void Teardown()
        {
            Client.MessageReceived -= MessageReceived;
            Logger.Information("CityBuddies runtime helper teardown.");
        }

        private void MessageReceived(object sender, Message e)
        {
            try
            {
                if (_readyWritten || e?.Body == null || e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3Message = (N3Message)e.Body;
                if (n3Message.N3MessageType != N3MessageType.CharInPlay)
                    return;

                var charInPlay = (CharInPlayMessage)e.Body;
                if (charInPlay.Identity.Instance != Client.LocalDynelId)
                    return;

                File.WriteAllText(
                    _readyPath,
                    $"{Client.CharacterName}|{DateTime.UtcNow:O}");

                _readyWritten = true;
                Logger.Information(
                    $"CityBuddies ready: {Client.CharacterName} reached InPlay.");
            }
            catch (Exception ex)
            {
                Logger.Error($"CityBuddies readiness signal failed: {ex}");
            }
        }
    }
}
