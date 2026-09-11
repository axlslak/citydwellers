using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Safety interlock for RouteRepairAgent.
    ///
    /// A worker repair command is executable only while the shared repair plan is active.
    /// Earlier live recovery showed that a failed plan could leave a worker command behind;
    /// RouteRepairAgent would then reset locally and read that same command again, repeating
    /// extraction/return work while Central intentionally ignored failed plans.
    ///
    /// This interlock removes RouteRepairAgent update/trade callbacks whenever the shared
    /// plan is failed and deletes this character's stale repair command. When recovery makes
    /// the plan active again, the exact saved callbacks are restored. No AO item is moved.
    /// </summary>
    public class RouteRepairExecutionGateAgent : ClientlessPluginEntry
    {
        private const string PlanFile = "route-repair-plan.json";
        private const string SentinelFile = "route-repair-active.json";
        private const int PollMilliseconds = 100;

        private string _settingsDir;
        private string _dataDir;
        private bool _enabled;
        private bool _detached;
        private DateTime _nextPollUtc;
        private readonly List<EventHandler<double>> _updateCallbacks = new List<EventHandler<double>>();
        private readonly List<Action<Identity>> _tradeOpenCallbacks = new List<Action<Identity>>();
        private readonly List<Action<Identity, TradeStatus>> _tradeStatusCallbacks = new List<Action<Identity, TradeStatus>>();

        public override void Init(string pluginDir)
        {
            // Full physical census and BankingService now own normal-mode recovery.
            if (StartupCensusGate.UsesPhysicalRecovery) return;

            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            if (ServicePolicy.IsBagAuditMode())
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _dataDir = RuntimeStateStore.GetDataDirectory(_settingsDir);
            _enabled = true;
            _nextPollUtc = DateTime.MinValue;
            Client.OnUpdate += Tick;

            // Apply the failed-plan fence synchronously as far as possible. CharacterName
            // and callback lists are already available during plugin initialization.
            TryApplyGate();
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
            // Client domain is unloading. Do not restore callbacks during teardown.
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);
            TryApplyGate();
        }

        private void TryApplyGate()
        {
            try
            {
                if (!File.Exists(Path.Combine(_dataDir, SentinelFile)))
                    return;

                JObject plan = ReadPlan();
                if (plan == null)
                    return;

                string status = Text(plan, "Status");
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    DeleteOwnWorkerCommand();
                    if (!_detached)
                        DetachRouteRepairCallbacks();
                    return;
                }

                if (_detached && string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                    RestoreRouteRepairCallbacks();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"[CityBankers] ROUTE REPAIR EXECUTION GATE failed character={Client.CharacterName}: {ex}");
            }
        }

        private void DeleteOwnWorkerCommand()
        {
            if (string.IsNullOrWhiteSpace(Client.CharacterName))
                return;

            string path = Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "citybankers-route-repair-command-" + SafeFileToken(Client.CharacterName) + ".json");
            if (!File.Exists(path))
                return;

            RuntimeStateStore.DeleteIfExists(path);
            Logger.Information(
                $"[CityBankers] ROUTE REPAIR EXECUTION GATE quarantined failed-plan command " +
                $"character={Client.CharacterName}. No AO item was moved by the gate.");
        }

        private void DetachRouteRepairCallbacks()
        {
            _updateCallbacks.Clear();
            _tradeOpenCallbacks.Clear();
            _tradeStatusCallbacks.Clear();

            if (Client.OnUpdate != null)
            {
                foreach (Delegate callback in Client.OnUpdate.GetInvocationList())
                {
                    if (!IsRouteRepairCallback(callback))
                        continue;
                    var typed = callback as EventHandler<double>;
                    if (typed == null)
                        continue;
                    _updateCallbacks.Add(typed);
                    Client.OnUpdate -= typed;
                }
            }

            if (Trade.TradeOpened != null)
            {
                foreach (Delegate callback in Trade.TradeOpened.GetInvocationList())
                {
                    if (!IsRouteRepairCallback(callback))
                        continue;
                    var typed = callback as Action<Identity>;
                    if (typed == null)
                        continue;
                    _tradeOpenCallbacks.Add(typed);
                    Trade.TradeOpened -= typed;
                }
            }

            if (Trade.TradeStatusChanged != null)
            {
                foreach (Delegate callback in Trade.TradeStatusChanged.GetInvocationList())
                {
                    if (!IsRouteRepairCallback(callback))
                        continue;
                    var typed = callback as Action<Identity, TradeStatus>;
                    if (typed == null)
                        continue;
                    _tradeStatusCallbacks.Add(typed);
                    Trade.TradeStatusChanged -= typed;
                }
            }

            if (_updateCallbacks.Count == 0)
                throw new InvalidOperationException("RouteRepairAgent update callback was not found for failed-plan fencing.");

            _detached = true;
            Logger.Information(
                $"[CityBankers] ROUTE REPAIR EXECUTION GATE froze character={Client.CharacterName}; " +
                $"plan is failed; route-repair callbacks detached update={_updateCallbacks.Count} " +
                $"tradeOpen={_tradeOpenCallbacks.Count} tradeStatus={_tradeStatusCallbacks.Count}.");
        }

        private void RestoreRouteRepairCallbacks()
        {
            foreach (EventHandler<double> callback in _updateCallbacks)
                Client.OnUpdate += callback;
            foreach (Action<Identity> callback in _tradeOpenCallbacks)
                Trade.TradeOpened += callback;
            foreach (Action<Identity, TradeStatus> callback in _tradeStatusCallbacks)
                Trade.TradeStatusChanged += callback;

            Logger.Information(
                $"[CityBankers] ROUTE REPAIR EXECUTION GATE resumed character={Client.CharacterName}; " +
                "shared plan is active again.");

            _updateCallbacks.Clear();
            _tradeOpenCallbacks.Clear();
            _tradeStatusCallbacks.Clear();
            _detached = false;
        }

        private JObject ReadPlan()
        {
            string path = Path.Combine(_dataDir, PlanFile);
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsRouteRepairCallback(Delegate callback)
        {
            Type type = callback?.Target?.GetType();
            return type != null && string.Equals(
                type.FullName,
                "CityBankers.RouteRepairAgent",
                StringComparison.Ordinal);
        }

        private static string Text(JObject value, string name)
        {
            JToken token = value?.GetValue(name, StringComparison.OrdinalIgnoreCase);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        }
    }
}
