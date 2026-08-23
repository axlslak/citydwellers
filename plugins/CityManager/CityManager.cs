using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityManager
{
    public class CityManager : ClientlessPluginEntry
    {
        private string _pluginDir;
        private string _statePath;
        private string _eventsPath;

        private bool _charInPlay;

        private CloakStatus _status = CloakStatus.Unknown;
        private int _shieldTimerInSeconds;

        private DateTime? _lastObservedUtc;
        private DateTime? _lastChangedUtc;
        private DateTime? _canRaiseAtUtc;

        private bool _raiseDueLogged;

        public override void Init(string pluginDir)
        {
            _pluginDir = pluginDir;
            _statePath = Path.Combine(
                _pluginDir,
                "citymanager-cloak-state.json");
            _eventsPath = Path.Combine(
                _pluginDir,
                "citymanager-cloak-events.jsonl");

            Logger.Information("CityManager cloak observer initialized.");

            LoadState();

            Client.MessageReceived += MessageReceived;
        }

        public override void Teardown()
        {
            try
            {
                Client.MessageReceived -= MessageReceived;
                Client.Chat.PrivateMessageReceived -= HandlePrivateMessage;
                Client.OnUpdate -= Tick;
                SaveState();
            }
            catch (Exception ex)
            {
                Logger.Error($"CityManager teardown error: {ex}");
            }
        }

        private void MessageReceived(object sender, Message e)
        {
            try
            {
                if (e?.Body == null)
                    return;

                if (e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3Message = (N3Message)e.Body;

                switch (n3Message.N3MessageType)
                {
                    case N3MessageType.AOTransportSignal:
                    {
                        /*
                         * Intentionally do NOT filter on message identity.
                         *
                         * Manager is the always-online observer. It needs to
                         * see cloak events caused by Flipper or by a human,
                         * even though Manager itself is inside HQ and never
                         * opens/interacts with the City Controller.
                         */
                        var signal = (AOTransportSignalMessage)e.Body;

                        if (signal.Action == AOSignalAction.CloakInfo)
                        {
                            HandleCloakInfo(
                                (CloakInfo)signal.TransportSignalMessage);
                        }

                        break;
                    }

                    case N3MessageType.CharInPlay:
                    {
                        var charInPlay = (CharInPlayMessage)e.Body;

                        if (charInPlay.Identity.Instance != Client.LocalDynelId)
                            return;

                        OnCharInPlay();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CityManager message error: {ex}");
            }
        }

        private void OnCharInPlay()
        {
            if (_charInPlay)
                return;

            _charInPlay = true;

            Logger.Information("CityManager is in play and observing cloak events.");

            Client.Chat.PrivateMessageReceived += HandlePrivateMessage;
            Client.OnUpdate += Tick;
        }

        private void HandleCloakInfo(CloakInfo cloakInfo)
        {
            DateTime now = DateTime.UtcNow;

            CloakStatus previousStatus = _status;
            bool previousKnown = previousStatus != CloakStatus.Unknown;
            bool stateChanged =
                previousKnown && previousStatus != cloakInfo.CloakState;

            _status = cloakInfo.CloakState;
            _shieldTimerInSeconds = cloakInfo.ShieldTimerInSeconds;
            _lastObservedUtc = now;

            /*
             * AOSharp's own CanToggleCloak logic treats a positive
             * ShieldTimerInSeconds as a wait starting when CloakInfo is
             * received. A zero/negative value is already toggleable.
             *
             * For a disabled cloak this therefore gives Manager the
             * earliest time at which Flipper should be allowed to raise it.
             */
            if (_status == CloakStatus.Disabled)
            {
                int waitSeconds = Math.Max(0, _shieldTimerInSeconds);
                _canRaiseAtUtc = now.AddSeconds(waitSeconds);
                _raiseDueLogged = false;
            }
            else
            {
                _canRaiseAtUtc = null;
                _raiseDueLogged = false;
            }

            if (stateChanged)
            {
                _lastChangedUtc = now;

                AppendCloakEvent(
                    previousStatus,
                    _status,
                    now,
                    _shieldTimerInSeconds,
                    _canRaiseAtUtc,
                    "state_change");

                if (_status == CloakStatus.Disabled)
                {
                    Logger.Warning(
                        $"CLOAK LOWERED observed at {now:O}. " +
                        $"Server timer={_shieldTimerInSeconds}s. " +
                        $"Earliest raise={_canRaiseAtUtc:O}.");
                }
                else if (_status == CloakStatus.Enabled)
                {
                    Logger.Warning(
                        $"CLOAK RAISED observed at {now:O}. " +
                        $"Server timer={_shieldTimerInSeconds}s.");
                }
                else
                {
                    Logger.Information(
                        $"Cloak state changed {previousStatus} -> {_status} " +
                        $"at {now:O}.");
                }
            }
            else if (!previousKnown)
            {
                /*
                 * On a fresh install/restart the first packet is our
                 * baseline. If that baseline is Disabled, it still matters:
                 * schedule the raise from the server timer even though we
                 * cannot prove that this packet itself represents the flip.
                 */
                Logger.Information(
                    $"Initial cloak observation: {_status}, " +
                    $"timer={_shieldTimerInSeconds}s at {now:O}.");

                if (_status == CloakStatus.Disabled)
                {
                    _lastChangedUtc = now;

                    AppendCloakEvent(
                        CloakStatus.Unknown,
                        _status,
                        now,
                        _shieldTimerInSeconds,
                        _canRaiseAtUtc,
                        "disabled_baseline");
                }
            }
            else
            {
                Logger.Debug(
                    $"Cloak observation unchanged: {_status}, " +
                    $"timer={_shieldTimerInSeconds}s.");
            }

            SaveState();
        }

        private void Tick(object sender, double e)
        {
            if (_status != CloakStatus.Disabled)
                return;

            if (!_canRaiseAtUtc.HasValue)
                return;

            if (_raiseDueLogged)
                return;

            if (DateTime.UtcNow < _canRaiseAtUtc.Value)
                return;

            _raiseDueLogged = true;

            Logger.Warning(
                $"CLOAK RAISE IS NOW DUE. " +
                $"Earliest raise time was {_canRaiseAtUtc.Value:O}. " +
                "Flipper may now be asked to raise the cloak.");

            SaveState();
        }

        private void HandlePrivateMessage(object sender, PrivateMessage msg)
        {
            try
            {
                var stringIgnores = new List<string>
                {
                    "You have been auto-invited to the private channel.",
                    "Unknown",
                    "AnarchyOnline",
                    "Reconnecting you to",
                    "Darknet",
                    "<"
                };

                if (stringIgnores.Any(i => msg.Message.Contains(i)))
                    return;

                Logger.Information($"{msg.SenderName} sent {msg.Message}");

                string[] commandParts = msg.Message.Split(' ');
                string command =
                    commandParts.Length > 0
                        ? commandParts[0].ToLowerInvariant()
                        : string.Empty;

                switch (command)
                {
                    case "help":
                        SendHelpMessage(msg.SenderId);
                        break;

                    case "cloak":
                    case "status":
                        SendCloakStatus(msg.SenderId);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling private message: {ex}");
            }
        }

        private void SendHelpMessage(uint senderId)
        {
            string helpMessage =
                "Available commands:\n" +
                "help: Display this help message.\n" +
                "cloak: Show observed cloak state and raise timer.\n" +
                "status: Same as cloak.\n";

            Client.SendPrivateMessage(senderId, helpMessage);
        }

        private void SendCloakStatus(uint senderId)
        {
            if (_status == CloakStatus.Unknown)
            {
                Client.SendPrivateMessage(
                    senderId,
                    "Cloak = Unknown. No CloakInfo packet has been observed yet.");
                return;
            }

            if (_status == CloakStatus.Disabled && _canRaiseAtUtc.HasValue)
            {
                TimeSpan remaining =
                    _canRaiseAtUtc.Value - DateTime.UtcNow;

                if (remaining.TotalSeconds > 0)
                {
                    Client.SendPrivateMessage(
                        senderId,
                        $"Cloak = Disabled. " +
                        $"Raise available in {FormatDuration(remaining)}. " +
                        $"Server timer raw = {_shieldTimerInSeconds}s.");
                }
                else
                {
                    Client.SendPrivateMessage(
                        senderId,
                        $"Cloak = Disabled. Raise available now. " +
                        $"Server timer raw = {_shieldTimerInSeconds}s.");
                }

                return;
            }

            Client.SendPrivateMessage(
                senderId,
                $"Cloak = {_status}. " +
                $"Server timer raw = {_shieldTimerInSeconds}s. " +
                $"Last observed = " +
                $"{(_lastObservedUtc.HasValue ? _lastObservedUtc.Value.ToString("O") : "unknown")}.");
        }

        private string FormatDuration(TimeSpan value)
        {
            int totalSeconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;

            if (hours > 0)
                return $"{hours}h {minutes}m {seconds}s";

            if (minutes > 0)
                return $"{minutes}m {seconds}s";

            return $"{seconds}s";
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(_statePath))
                {
                    Logger.Information("No persisted cloak state found; starting Unknown.");
                    return;
                }

                string json = File.ReadAllText(_statePath);
                PersistedCloakState state =
                    JsonConvert.DeserializeObject<PersistedCloakState>(json);

                if (state == null)
                    return;

                _status = state.Status;
                _shieldTimerInSeconds = state.ShieldTimerInSeconds;
                _lastObservedUtc = state.LastObservedUtc;
                _lastChangedUtc = state.LastChangedUtc;
                _canRaiseAtUtc = state.CanRaiseAtUtc;
                _raiseDueLogged = state.RaiseDueLogged;

                Logger.Information(
                    $"Restored cloak state: {_status}, " +
                    $"timer={_shieldTimerInSeconds}s, " +
                    $"lastObserved={_lastObservedUtc:O}, " +
                    $"canRaiseAt={_canRaiseAtUtc:O}.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed loading persisted cloak state: {ex}");
            }
        }

        private void SaveState()
        {
            try
            {
                var state = new PersistedCloakState
                {
                    Status = _status,
                    ShieldTimerInSeconds = _shieldTimerInSeconds,
                    LastObservedUtc = _lastObservedUtc,
                    LastChangedUtc = _lastChangedUtc,
                    CanRaiseAtUtc = _canRaiseAtUtc,
                    RaiseDueLogged = _raiseDueLogged
                };

                string json =
                    JsonConvert.SerializeObject(
                        state,
                        Formatting.Indented);

                string tempPath = _statePath + ".tmp";

                File.WriteAllText(tempPath, json);

                if (File.Exists(_statePath))
                    File.Delete(_statePath);

                File.Move(tempPath, _statePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed saving cloak state: {ex}");
            }
        }

        private void AppendCloakEvent(
            CloakStatus previousStatus,
            CloakStatus newStatus,
            DateTime occurredUtc,
            int shieldTimerInSeconds,
            DateTime? canRaiseAtUtc,
            string eventType)
        {
            try
            {
                var evt = new CloakEvent
                {
                    EventType = eventType,
                    OccurredUtc = occurredUtc,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    ShieldTimerInSeconds = shieldTimerInSeconds,
                    CanRaiseAtUtc = canRaiseAtUtc,
                    Source = "AOTransportSignal.CloakInfo"
                };

                string line =
                    JsonConvert.SerializeObject(
                        evt,
                        Formatting.None);

                File.AppendAllText(
                    _eventsPath,
                    line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed appending cloak event: {ex}");
            }
        }

        private class PersistedCloakState
        {
            public CloakStatus Status;
            public int ShieldTimerInSeconds;
            public DateTime? LastObservedUtc;
            public DateTime? LastChangedUtc;
            public DateTime? CanRaiseAtUtc;
            public bool RaiseDueLogged;
        }

        private class CloakEvent
        {
            public string EventType;
            public DateTime OccurredUtc;
            public CloakStatus PreviousStatus;
            public CloakStatus NewStatus;
            public int ShieldTimerInSeconds;
            public DateTime? CanRaiseAtUtc;
            public string Source;
        }
    }
}
