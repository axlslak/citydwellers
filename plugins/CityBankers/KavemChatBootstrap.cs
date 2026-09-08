using System;
using System.IO;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// AOSharp chat reports an unresolved private-message sender as &lt;Unknown&gt; until
    /// its name/id cache has been populated.  The first CityBankers trust policy is
    /// intentionally name-based and hardcodes Kavem, so Central proactively resolves
    /// that name after login rather than risking that the first trusted tell is ignored.
    /// </summary>
    public class KavemChatBootstrap : ClientlessPluginEntry
    {
        private string _settingsDir;
        private bool _enabled;
        private DateTime _nextLookupUtc;

        public override void Init(string pluginDir)
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            string central = ResolveConfiguredCentral();
            if (string.IsNullOrWhiteSpace(central) ||
                !string.Equals(Client.CharacterName, central, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _enabled = true;
            _nextLookupUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || Client.Chat == null)
                return;

            if (Client.Chat.NameToIdMap.ContainsKey(TrustedOperators.BootstrapAdmin))
            {
                uint id = Client.Chat.NameToIdMap[TrustedOperators.BootstrapAdmin];
                Logger.Information(
                    $"CityBankers trusted bootstrap admin resolved: " +
                    $"{TrustedOperators.BootstrapAdmin}:{id}.");
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    "central",
                    "Trusted bootstrap admin chat identity resolved: " +
                    TrustedOperators.BootstrapAdmin + ":" + id + ".");
                Client.OnUpdate -= Tick;
                _enabled = false;
                return;
            }

            if (DateTime.UtcNow < _nextLookupUtc)
                return;

            _nextLookupUtc = DateTime.UtcNow.AddSeconds(5);
            Client.Chat.RequestCharacterId(TrustedOperators.BootstrapAdmin);
        }

        private string ResolveConfiguredCentral()
        {
            try
            {
                JObject root = SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = root["Roles"] as JObject;
                if (roles == null)
                    return null;

                foreach (JProperty property in roles.Properties())
                {
                    if (!string.Equals(
                        property.Name,
                        "central",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JObject role = property.Value as JObject;
                    return role?["Character"]?.ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    $"Unable to resolve configured Central for Kavem chat bootstrap: {ex.Message}");
            }

            return null;
        }
    }
}
