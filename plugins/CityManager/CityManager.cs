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
        private const int ProvisionalCloakDownSeconds = 3600;

        private static readonly HashSet<string> AllowedTellSenders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Kavem",
                "Doczy"
            };

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
        private bool _raiseTimeIsProvisional;
        private string _observationSource = "Unknown";

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
                Client.Chat.GroupMessageReceived -= HandleGroupMessage;
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

            Logger.Information(
                "CityManager is in play and observing cloak packets and org chat events.");

            Client.Chat.PrivateMessageReceived += HandlePrivateMessage;
            Client.Chat.GroupMessageReceived += HandleGroupMessage;
            Client.OnUpdate += Tick;
        }

        private void HandleGroupMessage(object sender, GroupMsg msg)
        {
            try
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.Message))
                    return;

                Logger.Information(
                    $"GROUP [{msg.ChannelName}] {msg.SenderName}: {msg.Message}");

                const string cloakOffSuffix =
                    " turned the cloaking device in your city off.";
                const string cloakOnSuffix =
                    " turned the cloaking device in your city on.";

                if (msg.Message.EndsWith(
                    cloakOffSuffix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    string actor = msg.Message.Substring(
                        0,
                        msg.Message.Length - cloakOffSuffix.Length).Trim();

                    HandleCloakAnnouncement(
                        CloakStatus.Disabled,
                        actor,
                        msg.ChannelName,
                        msg.Message);

                    return;
                }

                if (msg.Message.EndsWith(
                    cloakOnSuffix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    string actor = msg.Message.Substring(
                        0,
                        msg.Message.Length - cloakOnSuffix.Length).Trim();

                    HandleCloakAnnouncement(
                        CloakStatus.Enabled,
                        actor,
                        msg.ChannelName,
                        msg.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling group message: {ex}");
            }
        }

        private void HandleCloakAnnouncement(
            CloakStatus newStatus,
            string actor,
            string channelName,
            string rawMessage)
        {
            DateTime now = DateTime.UtcNow;
            CloakStatus previousStatus = _status;

            _status = newStatus;
            _lastObservedUtc = now;
            _lastChangedUtc = now;
            _observationSource = "OrgChat.CloakAnnouncement";
            _raiseDueLogged = false;

            if (newStatus == CloakStatus.Disabled)
            {
                _shieldTimerInSeconds = 0;
                _canRaiseAtUtc = now.AddSeconds(ProvisionalCloakDownSeconds);
                _raiseTimeIsProvisional = true;

                Logger.Warning(
                    $"CLOAK LOWERED announced at {now:O}. " +
                    $"Actor={actor}. " +
                    $"Provisional Flipper check={_canRaiseAtUtc:O}.");
            }
            else
            {
                _shieldTimerInSeconds = 0;
                _canRaiseAtUtc = null;
                _raiseTimeIsProvisional = false;

                Logger.Warning(
                    $"CLOAK RAISED announced at {now:O}. Actor={actor}.");
            }

            AppendCloakEvent(
                previousStatus,
                newStatus,
                now,
                null,
                _canRaiseAtUtc,
                newStatus == CloakStatus.Disabled
                    ? "cloak_off_announcement"
                    : "cloak_on_announcement",
                "OrgChat.CloakAnnouncement",
                actor,
                channelName,
                rawMessage);

            SaveState();
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
            _observationSource = "AOTransportSignal.CloakInfo";

            if (_status == CloakStatus.Disabled)
            {
                int waitSeconds = Math.Max(0, _shieldTimerInSeconds);
                _canRaiseAtUtc = now.AddSeconds(waitSeconds);
                _raiseDueLogged = false;
                _raiseTimeIsProvisional = false;
            }
            else
            {
                _canRaiseAtUtc = null;
                _raiseDueLogged = false;
                _raiseTimeIsProvisional = false;
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
                    "state_change",
                    "AOTransportSignal.CloakInfo",
                    null,
                    null,
                    null);

                Logger.Warning(
                    $"CloakInfo changed {previousStatus} -> {_status} at {now:O}. " +
                    $"Server timer={_shieldTimerInSeconds}s.");
            }
            else if (!previousKnown)
            {
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
                        "disabled_baseline",
                        "AOTransportSignal.CloakInfo",
                        null,
                        null,
                        null);
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

            if (_raiseTimeIsProvisional)
            {
                Logger.Warning(
                    $"CLOAK CHECK IS NOW DUE. " +
                    $"One-hour provisional time reached at {_canRaiseAtUtc.Value:O}. " +
                    "Flipper should now inspect the controller and raise only if the server permits it.");
            }
            else
            {
                Logger.Warning(
                    $"CLOAK RAISE IS NOW DUE. " +
                    $"Server-derived earliest raise time was {_canRaiseAtUtc.Value:O}. " +
                    "Flipper may now be asked to raise the cloak.");
            }

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

                if (!AllowedTellSenders.Contains(msg.SenderName))
                {
                    Logger.Warning(
                        $"Ignoring tell command from unauthorized sender {msg.SenderName}.");
                    return;
                }

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
                "cloak: Show observed cloak state and raise/check timer.\n" +
                "status: Same as cloak.\n";

            Client.SendPrivateMessage(senderId, helpMessage);
        }

        private void SendCloakStatus(uint senderId)
        {
            if (_status == CloakStatus.Unknown)
            {
                Client.SendPrivateMessage(
                    senderId,
                    "Cloak = Unknown. No cloak event or CloakInfo snapshot has been observed yet.");
                return;
            }

            if (_status == CloakStatus.Disabled && _canRaiseAtUtc.HasValue)
            {
                TimeSpan remaining = _canRaiseAtUtc.Value - DateTime.UtcNow;

                string timerKind = _raiseTimeIsProvisional
                    ? "provisional Flipper check"
                    : "server-derived raise time";

                if (remaining.TotalSeconds > 0)
                {
                    Client.SendPrivateMessage(
                        senderId,
                        $"Cloak = Disabled. " +
                        $"{timerKind} in {FormatDuration(remaining)}. " +
                        $"Source = {_observationSource}.");
                }
                else
                {
                    Client.SendPrivateMessage(
                        senderId,
                        $"Cloak = Disabled. {timerKind} is due now. " +
                        $"Source = {_observationSource}.");
                }

                return;
            }

            Client.SendPrivateMessage(
                senderId,
                $"Cloak = {_status}. " +
                $"Source = {_observationSource}. " +
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
                _raiseTimeIsProvisional = state.RaiseTimeIsProvisional;
                _observationSource = state.ObservationSource ?? "Unknown";

                Logger.Information(
                    $"Restored cloak state: {_status}, " +
                    $"lastObserved={_lastObservedUtc:O}, " +
                    $"canRaiseAt={_canRaiseAtUtc:O}, " +
                    $"provisional={_raiseTimeIsProvisional}, " +
                    $"source={_observationSource}.");
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
                    RaiseDueLogged = _raiseDueLogged,
                    RaiseTimeIsProvisional = _raiseTimeIsProvisional,
                    ObservationSource = _observationSource
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
            int? shieldTimerInSeconds,
            DateTime? canRaiseAtUtc,
            string eventType,
            string source,
            string actor,
            string channelName,
            string rawMessage)
        {
            try
            {
                var record = new CloakEventRecord
                {
                    OccurredUtc = occurredUtc,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    ShieldTimerInSeconds = shieldTimerInSeconds,
                    CanRaiseAtUtc = canRaiseAtUtc,
                    EventType = eventType,
                    Source = source,
                    Actor = actor,
                    ChannelName = channelName,
                    RawMessage = rawMessage
                };

                string line = JsonConvert.SerializeObject(record);
                File.AppendAllText(_eventsPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed appending cloak event: {ex}");
            }
        }

        private class PersistedCloakState
        {
            public CloakStatus Status { get; set; }
            public int ShieldTimerInSeconds { get; set; }
            public DateTime? LastObservedUtc { get; set; }
            public DateTime? LastChangedUtc { get; set; }
            public DateTime? CanRaiseAtUtc { get; set; }
            public bool RaiseDueLogged { get; set; }
            public bool RaiseTimeIsProvisional { get; set; }
            public string ObservationSource { get; set; }
        }

        private class CloakEventRecord
        {
            public DateTime OccurredUtc { get; set; }
            public CloakStatus PreviousStatus { get; set; }
            public CloakStatus NewStatus { get; set; }
            public int? ShieldTimerInSeconds { get; set; }
            public DateTime? CanRaiseAtUtc { get; set; }
            public string EventType { get; set; }
            public string Source { get; set; }
            public string Actor { get; set; }
            public string ChannelName { get; set; }
            public string RawMessage { get; set; }
        }
    }
}
