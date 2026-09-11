using System;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Process-lifetime safety gate for the one-shot route repair.
    ///
    /// AOSharp exposes Client.OnUpdate and Trade callbacks as public delegate fields. When
    /// the shared repair sentinel appears, this agent removes only BankingServiceAgent's
    /// update/trade delegates from the current client domain. RouteRepairAgent can then own
    /// worker -> Central return trades without the normal banking service declining them.
    ///
    /// The handlers are intentionally NOT restored in the same process after the sentinel
    /// disappears. The repair coordinator may already have prepared the corrected dispatch
    /// queue; keeping this process inert creates a deterministic restart barrier. A clean
    /// Banker.exe restart creates fresh domains and fresh BankingServiceAgent callbacks,
    /// which then consume the corrected queue under the Identity.None-safe matcher.
    /// </summary>
    public class RouteRepairBankingGateAgent : ClientlessPluginEntry
    {
        private const string SentinelFile = "route-repair-active.json";

        private string _settingsDir;
        private string _sentinelPath;
        private bool _enabled;
        private bool _bankingDetached;
        private bool _restartNoticeLogged;

        public override void Init(string pluginDir)
        {
            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            if (ServicePolicy.IsBagAuditMode())
            {
                return;
            }

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _sentinelPath = Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                SentinelFile);

            _enabled = true;
            Client.OnUpdate += Tick;
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;

            // Deliberately do not restore BankingServiceAgent handlers here. The client
            // domain is being unloaded; the next process/domain performs the clean reset.
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || !Client.InPlay)
                return;

            try
            {
                if (!_bankingDetached && File.Exists(_sentinelPath))
                {
                    DetachBankingServiceCallbacks();
                    return;
                }

                if (_bankingDetached &&
                    !File.Exists(_sentinelPath) &&
                    !_restartNoticeLogged)
                {
                    _restartNoticeLogged = true;
                    const string notice =
                        "Route repair handoff: all repair returns are verified and the corrected " +
                        "AOID-routed queue is prepared. This process remains intentionally frozen. " +
                        "Cleanly restart CityDwellers once; corrected dispatch begins after restart.";

                    Logger.Information(
                        $"[CityBankers] ROUTE REPAIR GATE character={Client.CharacterName}: " + notice);

                    if (IsCentralCharacter())
                    {
                        TellQueueClient.Enqueue(
                            _settingsDir,
                            Client.CharacterName,
                            TrustedOperators.BootstrapAdmin,
                            notice);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"[CityBankers] ROUTE REPAIR GATE failed character={Client.CharacterName}: {ex}");
            }
        }

        private void DetachBankingServiceCallbacks()
        {
            int updateCount = 0;
            int openCount = 0;
            int statusCount = 0;

            if (Client.OnUpdate != null)
            {
                foreach (Delegate callback in Client.OnUpdate.GetInvocationList())
                {
                    if (!IsBankingServiceCallback(callback))
                        continue;
                    Client.OnUpdate -= (EventHandler<double>)callback;
                    updateCount++;
                }
            }

            if (Trade.TradeOpened != null)
            {
                foreach (Delegate callback in Trade.TradeOpened.GetInvocationList())
                {
                    if (!IsBankingServiceCallback(callback))
                        continue;
                    Trade.TradeOpened -= (Action<Identity>)callback;
                    openCount++;
                }
            }

            if (Trade.TradeStatusChanged != null)
            {
                foreach (Delegate callback in Trade.TradeStatusChanged.GetInvocationList())
                {
                    if (!IsBankingServiceCallback(callback))
                        continue;
                    Trade.TradeStatusChanged -= (Action<Identity, TradeStatus>)callback;
                    statusCount++;
                }
            }

            if (updateCount == 0 || openCount == 0 || statusCount == 0)
            {
                throw new InvalidOperationException(
                    "Could not detach all BankingServiceAgent callbacks. " +
                    "update=" + updateCount + " tradeOpen=" + openCount +
                    " tradeStatus=" + statusCount + ". Repair must not proceed.");
            }

            _bankingDetached = true;
            Logger.Information(
                $"[CityBankers] ROUTE REPAIR GATE armed character={Client.CharacterName}; " +
                $"detached banking callbacks update={updateCount} tradeOpen={openCount} " +
                $"tradeStatus={statusCount}. This process is repair-owned until restart.");
        }

        private bool IsCentralCharacter()
        {
            try
            {
                JObject config = SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = config.GetValue("Roles", StringComparison.OrdinalIgnoreCase) as JObject;
                if (roles == null)
                    return false;

                JProperty central = roles.Properties().FirstOrDefault(property =>
                    string.Equals(property.Name, "central", StringComparison.OrdinalIgnoreCase));
                JObject value = central?.Value as JObject;
                string character = value?.GetValue(
                    "Character",
                    StringComparison.OrdinalIgnoreCase)?.ToString();
                return string.Equals(
                    character,
                    Client.CharacterName,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBankingServiceCallback(Delegate callback)
        {
            object target = callback?.Target;
            Type type = target?.GetType();
            return type != null && string.Equals(
                type.FullName,
                "CityBankers.BankingServiceAgent",
                StringComparison.Ordinal);
        }
    }
}
