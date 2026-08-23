using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using System;
using System.IO;
using System.Text;

namespace CityBuddies
{
    public class CityBuddies : ClientlessPluginEntry
    {
        private static string _pluginDir;

        public override void Init(string pluginDir)
        {
            _pluginDir = pluginDir;

            Logger.Information("CityBuddies POC initialized.");

            Logger.Warning($"PLUGIN before set: AutoReconnect={Client.Config.AutoReconnect}");

            Client.Config.AutoReconnect = true;

            Logger.Warning($"PLUGIN after set: AutoReconnect={Client.Config.AutoReconnect}");

            Client.CharacterSelect += OnCharacterSelect;
        }

        private void OnCharacterSelect(CharacterSelect characterSelect)
        {
            Logger.Information("Character list received.");

            var sb = new StringBuilder();

            sb.AppendLine(
                $"AllowedCharacters={characterSelect.AllowedCharacters}");

            sb.AppendLine(
                $"Expansions={characterSelect.Expansions}");

            if (characterSelect.Characters != null)
            {
                foreach (CharacterSelect.Character character
                    in characterSelect.Characters)
                {
                    Logger.Information(
                        $"Character: {character.Name} ID: {character.Id}");

                    sb.AppendLine(
                        $"{character.Id}|{character.Name}");
                }
            }

            string resultPath =
                Path.Combine(_pluginDir, "buddy-scan.result");

            string tempPath =
                resultPath + ".tmp";

            // Write completely first...
            File.WriteAllText(tempPath, sb.ToString());

            // ...then rename, so Buddies.exe never sees
            // a half-written result.
            if (File.Exists(resultPath))
                File.Delete(resultPath);

            File.Move(tempPath, resultPath);

            Logger.Information(
                "Discovery complete. Waiting for host to unload client.");

            Client.CharacterSelect -= OnCharacterSelect;

            // IMPORTANT:
            // Do NOT Client.Disconnect() here.
            // The host owns this ClientDomain's lifetime.
        }
    }
}