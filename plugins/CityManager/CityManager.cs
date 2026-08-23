using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
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
        private const string FlipperPipeName = "citydwellers-flipper";
        private const string BuddiesPipeName = "citydwellers-buddies";
        private const int WorkerConnectTimeoutMs = 1000;
        private const string OrgChannelName = "Athen Paladins";
        private const string CommandPrefix = "#";
        private const string DeveloperCharacter = "Kavem";
        private const int DevBacklogLimit = 25;

        private static readonly HashSet<string> AllowedCommandSenders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Kavem",
                "Doczy"
            };

        private readonly object _stateSync = new object();
        private readonly object _devSync = new object();
        private readonly Queue<string> _devBacklog = new Queue<string>();

        private string _pluginDir;
        private string _statePath;
        private string _eventsPath;
        private bool _charInPlay;

        private bool _devInviteSent;
        private bool _devChannelConfirmed;
        private DateTime _nextDevLookupUtc = DateTime.MinValue;

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
            _statePath = Path.Combine(_pluginDir, "citymanager-cloak-state.json");
            _eventsPath = Path.Combine(_pluginDir, "citymanager-cloak-events.jsonl");

            Logger.Information("CityManager initialized.");
            LoadState();
            Client.MessageReceived += MessageReceived;
        }

        public override void Teardown()
        {
            try
            {
                Client.MessageReceived -= MessageReceived;

                if (Client.Chat != null)
                {
                    Client.Chat.PrivateMessageReceived -= HandlePrivateMessage;
                    Client.Chat.GroupMessageReceived -= HandleGroupMessage;
                    Client.Chat.PrivateGroupMessageReceived -= HandlePrivateGroupMessage;
                }

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
                if (e?.Body == null || e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3Message = (N3Message)e.Body;

                if (n3Message.N3MessageType == N3MessageType.AOTransportSignal)
                {
                    var signal = (AOTransportSignalMessage)e.Body;
                    if (signal.Action == AOSignalAction.CloakInfo)
                        HandleCloakInfo((CloakInfo)signal.TransportSignalMessage);
                    return;
                }

                if (n3Message.N3MessageType == N3MessageType.CharInPlay)
                {
                    var charInPlay = (CharInPlayMessage)e.Body;
                    if (charInPlay.Identity.Instance == Client.LocalDynelId)
                        OnCharInPlay();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CityManager message error: {ex}");
                DevTrace($"ERROR manager message: {ex.Message}");
            }
        }

        private void OnCharInPlay()
        {
            if (_charInPlay)
                return;

            _charInPlay = true;
            Logger.Information("CityManager is in play and observing cloak packets, tells, org chat, and dev private chat.");

            Client.Chat.PrivateMessageReceived += HandlePrivateMessage;
            Client.Chat.GroupMessageReceived += HandleGroupMessage;
            Client.Chat.PrivateGroupMessageReceived += HandlePrivateGroupMessage;
            Client.OnUpdate += Tick;

            DevTrace("MANAGER online. Dev telemetry initialized.");
            _nextDevLookupUtc = DateTime.UtcNow;
            TryInviteDeveloper();
        }

        private void HandlePrivateMessage(object sender, PrivateMessage msg)
        {
            try
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.Message))
                    return;

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

                Logger.Information($"TELL {msg.SenderName}: {msg.Message}");

                ProcessCommand(
                    msg.SenderName,
                    msg.Message,
                    ReplyTarget.ForTell(msg.SenderId));
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling private message: {ex}");
                DevTrace($"ERROR tell handler: {ex.Message}");
            }
        }

        private void HandleGroupMessage(object sender, GroupMsg msg)
        {
            try
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.Message))
                    return;

                if (TryHandleCloakAnnouncement(msg))
                    return;

                if (!string.Equals(msg.ChannelName, OrgChannelName, StringComparison.OrdinalIgnoreCase))
                    return;

                string text = msg.Message.TrimStart();
                bool isCommand = text.StartsWith(CommandPrefix, StringComparison.Ordinal);

                if (!isCommand)
                {
                    if (string.Equals(msg.SenderName, "<Unknown>", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(msg.SenderName, "Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Information(
                            $"ORG SYSTEM [{msg.ChannelName}] {msg.SenderName}: {msg.Message}");
                        DevTrace($"CITY RAW: {msg.Message}");
                    }

                    return;
                }

                Logger.Information(
                    $"ORG COMMAND [{msg.ChannelName}] {msg.SenderName}: {msg.Message}");

                string commandText = text.Substring(CommandPrefix.Length).TrimStart();

                ProcessCommand(
                    msg.SenderName,
                    commandText,
                    ReplyTarget.ForOrg(msg.SenderId, msg.ChannelId, msg.ChannelName));
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling group message: {ex}");
                DevTrace($"ERROR org handler: {ex.Message}");
            }
        }

        private void HandlePrivateGroupMessage(object sender, PrivateGroupMsg msg)
        {
            try
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.Message) || Client.Chat == null)
                    return;

                if (msg.ChannelId != Client.Chat.CharId)
                    return;

                if (!string.Equals(msg.SenderName, DeveloperCharacter, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning($"Ignoring private-channel traffic from unauthorized sender {msg.SenderName}.");
                    return;
                }

                ConfirmDevChannel();

                string text = msg.Message.TrimStart();
                if (!text.StartsWith(CommandPrefix, StringComparison.Ordinal))
                    return;

                string commandText = text.Substring(CommandPrefix.Length).TrimStart();
                Logger.Information($"DEV COMMAND {msg.SenderName}: {msg.Message}");

                ProcessCommand(
                    msg.SenderName,
                    commandText,
                    ReplyTarget.ForDev(msg.SenderId, msg.ChannelId));
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling private group message: {ex}");
                DevTrace($"ERROR dev handler: {ex.Message}");
            }
        }

        private bool TryHandleCloakAnnouncement(GroupMsg msg)
        {
            const string cloakOffSuffix = " turned the cloaking device in your city off.";
            const string cloakOnSuffix = " turned the cloaking device in your city on.";

            if (msg.Message.EndsWith(cloakOffSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string actor = msg.Message.Substring(0, msg.Message.Length - cloakOffSuffix.Length).Trim();
                HandleCloakAnnouncement(CloakStatus.Disabled, actor, msg.ChannelName, msg.Message);
                return true;
            }

            if (msg.Message.EndsWith(cloakOnSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string actor = msg.Message.Substring(0, msg.Message.Length - cloakOnSuffix.Length).Trim();
                HandleCloakAnnouncement(CloakStatus.Enabled, actor, msg.ChannelName, msg.Message);
                return true;
            }

            return false;
        }

        private void ProcessCommand(string senderName, string rawCommand, ReplyTarget replyTarget)
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
                return;

            if (replyTarget.IsDev)
            {
                if (!string.Equals(senderName, DeveloperCharacter, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            else if (!AllowedCommandSenders.Contains(senderName ?? string.Empty))
            {
                Logger.Warning($"Ignoring command from unauthorized sender {senderName}.");
                Reply(replyTarget, "You are not authorized to use this bot yet.");
                return;
            }

            string[] parts = rawCommand.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string command = parts[0].ToLowerInvariant();

            if (replyTarget.IsOrg && command != "help" && command != "cloak")
            {
                Reply(replyTarget, $"Unknown command '{parts[0]}'. Try #help.");
                return;
            }

            switch (command)
            {
                case "help":
                    Reply(replyTarget, BuildHelpMessage(replyTarget));
                    break;

                case "cloak":
                    BeginFlipperProbe(replyTarget);
                    break;

                case "status":
                    Reply(replyTarget, BuildCloakStatus());
                    break;

                case "probe":
                case "observe":
                    BeginFlipperProbe(replyTarget);
                    break;

                case "wakeup":
                {
                    int level;
                    int index;
                    if (parts.Length != 3 ||
                        !int.TryParse(parts[1], out level) ||
                        !int.TryParse(parts[2], out index))
                    {
                        Reply(replyTarget, "Usage: wakeup [level] [index]");
                        break;
                    }

                    BeginBuddiesCommand(replyTarget, "wakeup", level, index);
                    break;
                }

                case "sleep":
                {
                    int index;
                    if (parts.Length != 2 || !int.TryParse(parts[1], out index))
                    {
                        Reply(replyTarget, "Usage: sleep [index]");
                        break;
                    }

                    BeginBuddiesCommand(replyTarget, "sleep", null, index);
                    break;
                }

                default:
                    Reply(replyTarget, $"Unknown command '{parts[0]}'. Try #help.");
                    break;
            }
        }

        private string BuildHelpMessage(ReplyTarget target)
        {
            if (target.IsOrg)
                return "Commands: #help, #cloak.";

            if (target.IsDev)
            {
                return
                    "Dev: #help, #cloak, #status, #probe, " +
                    "#wakeup [level] [index], #sleep [index].";
            }

            return
                "Available commands:\n" +
                "help: Display this help message.\n" +
                "cloak/probe: Ask Flipper for a fresh City Controller reading.\n" +
                "status: Show Manager's remembered cloak state.\n" +
                "wakeup [level] [index]: Start one buddy.\n" +
                "sleep [index]: Unload one buddy.\n";
        }

        private void BeginFlipperProbe(ReplyTarget target)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = new WorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = "observe"
                    };

                    string shortId = ShortId(request.Id);
                    Logger.Information($"IPC -> Flipper {request.Id}: observe");
                    DevTrace($"FLIPPER -> observe [{shortId}]");

                    WorkerResponse response = SendWorkerRequest(
                        FlipperPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    if (!response.Ok)
                    {
                        Logger.Warning($"IPC <- Flipper {request.Id}: FAIL {response.Message}");
                        DevTrace($"FLIPPER FAIL [{shortId}]: {response.Message}");
                        Reply(target, $"Cloak check failed: {response.Message}");
                        return;
                    }

                    ApplyFlipperObservation(response);

                    string chargeText = response.ControllerCharge.HasValue
                        ? $"{response.ControllerCharge.Value * 100:F1}%"
                        : "unknown";
                    string timerText = response.ShieldTimerInSeconds.HasValue
                        ? $"{response.ShieldTimerInSeconds.Value}s"
                        : "unknown";

                    string reply =
                        $"Cloak = {response.CloakState ?? "Unknown"}. " +
                        $"Shield timer = {timerText}. Charge = {chargeText}.";

                    Logger.Information($"IPC <- Flipper {request.Id}: {reply}");
                    DevTrace($"FLIPPER OK [{shortId}]: {reply}");
                    Reply(target, reply);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Flipper IPC failed: {ex.Message}");
                    DevTrace($"FLIPPER ERROR: {ex.Message}");
                    Reply(target, $"Cloak check unavailable: {ex.Message}");
                }
            });
        }

        private void BeginBuddiesCommand(ReplyTarget target, string command, int? level, int index)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = new WorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = command,
                        Level = level,
                        Index = index
                    };

                    string shortId = ShortId(request.Id);
                    Logger.Information($"IPC -> Buddies {request.Id}: {command} level={level} index={index}");
                    DevTrace(
                        level.HasValue
                            ? $"BUDDIES -> {command} level={level.Value} index={index} [{shortId}]"
                            : $"BUDDIES -> {command} index={index} [{shortId}]");

                    WorkerResponse response = SendWorkerRequest(
                        BuddiesPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    Logger.Information(
                        $"IPC <- Buddies {request.Id}: {(response.Ok ? "OK" : "FAIL")} {response.Message}");

                    DevTrace(
                        $"BUDDIES {(response.Ok ? "OK" : "FAIL")} [{shortId}]: {response.Message}");

                    Reply(target, response.Ok
                        ? $"Buddies: {response.Message}"
                        : $"Buddies failed: {response.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Buddies IPC failed: {ex.Message}");
                    DevTrace($"BUDDIES ERROR: {ex.Message}");
                    Reply(target, $"Buddies service unavailable: {ex.Message}");
                }
            });
        }

        private WorkerResponse SendWorkerRequest(string pipeName, WorkerRequest request, int connectTimeoutMs)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(connectTimeoutMs);

                var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };

                writer.WriteLine(JsonConvert.SerializeObject(request));
                string line = reader.ReadLine();

                if (string.IsNullOrWhiteSpace(line))
                    throw new IOException($"Worker '{pipeName}' closed without a response.");

                WorkerResponse response = JsonConvert.DeserializeObject<WorkerResponse>(line);
                if (response == null)
                    throw new IOException($"Worker '{pipeName}' returned invalid JSON.");

                return response;
            }
        }

        private void Reply(ReplyTarget target, string text)
        {
            try
            {
                if (target.IsDev)
                {
                    SendDevMessage(text);
                    return;
                }

                if (target.IsOrg)
                {
                    if (TrySendOrgMessage(text))
                        return;

                    Logger.Warning("Unable to send org reply; falling back to tell.");
                    if (target.SenderId != 0)
                        Client.SendPrivateMessage(target.SenderId, "[org reply fallback] " + text);
                    return;
                }

                Client.SendPrivateMessage(target.SenderId, text);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed sending command reply: {ex}");
                DevTrace($"ERROR reply: {ex.Message}");
            }
        }

        private bool TrySendOrgMessage(string text)
        {
            try
            {
                Client.SendOrgMessage(text);
                Logger.Information("Org reply sent through AOSharp.Clientless.Client.SendOrgMessage.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Client.SendOrgMessage failed: {ex.Message}");
                DevTrace($"ORG SEND ERROR: {ex.Message}");
                return false;
            }
        }

        private void TryInviteDeveloper()
        {
            if (_devInviteSent || Client.Chat == null)
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextDevLookupUtc)
                return;

            uint developerId;
            if (!Client.Chat.NameToIdMap.TryGetValue(DeveloperCharacter, out developerId))
            {
                try
                {
                    Client.Chat.RequestCharacterId(DeveloperCharacter);
                    _nextDevLookupUtc = now.AddSeconds(2);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Developer lookup failed: {ex.Message}");
                    _nextDevLookupUtc = now.AddSeconds(5);
                }

                return;
            }

            try
            {
                Client.Chat.InvitePrivateGroup(developerId);
                _devInviteSent = true;
                Logger.Information($"Dev private-channel invite sent to {DeveloperCharacter} ({developerId}).");
                DevTrace($"DEV invite sent to {DeveloperCharacter}.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Dev private-channel invite failed: {ex.Message}");
                _nextDevLookupUtc = now.AddSeconds(5);
            }
        }

        private void ConfirmDevChannel()
        {
            bool shouldFlush = false;

            lock (_devSync)
            {
                if (!_devChannelConfirmed)
                {
                    _devChannelConfirmed = true;
                    shouldFlush = true;
                }
            }

            if (!shouldFlush)
                return;

            SendDevMessage("DEV channel confirmed. Flushing buffered telemetry.");
            FlushDevBacklog();
        }

        private void DevTrace(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (_devSync)
            {
                if (!_devChannelConfirmed)
                {
                    while (_devBacklog.Count >= DevBacklogLimit)
                        _devBacklog.Dequeue();

                    _devBacklog.Enqueue(text);
                    return;
                }
            }

            SendDevMessage(text);
        }

        private void FlushDevBacklog()
        {
            while (true)
            {
                string message;

                lock (_devSync)
                {
                    if (_devBacklog.Count == 0)
                        return;

                    message = _devBacklog.Dequeue();
                }

                SendDevMessage(message);
            }
        }

        private void SendDevMessage(string text)
        {
            try
            {
                if (Client.Chat == null)
                    return;

                Client.Chat.SendPrivateGroupMessage(Client.Chat.CharId, text);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Dev private-channel send failed: {ex.Message}");
            }
        }

        private string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "no-id";

            return id.Length <= 8 ? id : id.Substring(0, 8);
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
                Logger.Warning($"CLOAK LOWERED announced at {now:O}. Actor={actor}. Provisional Flipper check={_canRaiseAtUtc:O}.");
                DevTrace($"CITY cloak DISABLED by {actor}; provisional check in 1h.");
            }
            else
            {
                _shieldTimerInSeconds = 0;
                _canRaiseAtUtc = null;
                _raiseTimeIsProvisional = false;
                Logger.Warning($"CLOAK RAISED announced at {now:O}. Actor={actor}.");
                DevTrace($"CITY cloak ENABLED by {actor}.");
            }

            AppendCloakEvent(
                previousStatus,
                newStatus,
                now,
                null,
                _canRaiseAtUtc,
                newStatus == CloakStatus.Disabled ? "cloak_off_announcement" : "cloak_on_announcement",
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
            bool stateChanged = previousKnown && previousStatus != cloakInfo.CloakState;

            _status = cloakInfo.CloakState;
            _shieldTimerInSeconds = cloakInfo.ShieldTimerInSeconds;
            _lastObservedUtc = now;
            _observationSource = "AOTransportSignal.CloakInfo";

            if (_status == CloakStatus.Disabled)
            {
                _canRaiseAtUtc = now.AddSeconds(Math.Max(0, _shieldTimerInSeconds));
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
                Logger.Warning($"CloakInfo changed {previousStatus} -> {_status} at {now:O}. Server timer={_shieldTimerInSeconds}s.");
                DevTrace($"CITY CloakInfo changed {previousStatus} -> {_status}; timer={_shieldTimerInSeconds}s.");
            }
            else if (!previousKnown)
            {
                Logger.Information($"Initial cloak observation: {_status}, timer={_shieldTimerInSeconds}s at {now:O}.");
                DevTrace($"CITY initial cloak={_status}; timer={_shieldTimerInSeconds}s.");
            }

            SaveState();
        }

        private void ApplyFlipperObservation(WorkerResponse response)
        {
            CloakStatus parsedStatus;
            if (!Enum.TryParse(response.CloakState, true, out parsedStatus))
            {
                Logger.Warning($"Flipper returned unknown cloak state '{response.CloakState}'.");
                DevTrace($"FLIPPER WARN: unknown cloak state '{response.CloakState}'.");
                return;
            }

            DateTime now = DateTime.UtcNow;

            lock (_stateSync)
            {
                CloakStatus previousStatus = _status;

                _status = parsedStatus;
                _shieldTimerInSeconds = response.ShieldTimerInSeconds ?? 0;
                _lastObservedUtc = now;
                _observationSource = "Flipper.Probe";
                _raiseTimeIsProvisional = false;
                _raiseDueLogged = false;

                _canRaiseAtUtc = _status == CloakStatus.Disabled
                    ? now.AddSeconds(Math.Max(0, _shieldTimerInSeconds))
                    : (DateTime?)null;

                if (previousStatus != CloakStatus.Unknown && previousStatus != _status)
                    _lastChangedUtc = now;

                AppendCloakEvent(
                    previousStatus,
                    _status,
                    now,
                    response.ShieldTimerInSeconds,
                    _canRaiseAtUtc,
                    "flipper_probe",
                    "Flipper.Probe",
                    response.Character,
                    null,
                    response.Message);

                SaveState();
            }
        }

        private string BuildCloakStatus()
        {
            lock (_stateSync)
            {
                if (_status == CloakStatus.Unknown)
                    return "Cloak = Unknown. No cloak event or fresh Flipper probe has been observed yet.";

                if (_status == CloakStatus.Disabled && _canRaiseAtUtc.HasValue)
                {
                    TimeSpan remaining = _canRaiseAtUtc.Value - DateTime.UtcNow;
                    string timerKind = _raiseTimeIsProvisional
                        ? "provisional Flipper check"
                        : "server-derived raise time";

                    return remaining.TotalSeconds > 0
                        ? $"Cloak = Disabled. {timerKind} in {FormatDuration(remaining)}. Source = {_observationSource}."
                        : $"Cloak = Disabled. {timerKind} is due now. Source = {_observationSource}.";
                }

                return
                    $"Cloak = {_status}. Source = {_observationSource}. Last observed = " +
                    $"{(_lastObservedUtc.HasValue ? _lastObservedUtc.Value.ToString("O") : "unknown")}.";
            }
        }

        private void Tick(object sender, double e)
        {
            TryInviteDeveloper();

            if (_status != CloakStatus.Disabled || !_canRaiseAtUtc.HasValue || _raiseDueLogged)
                return;

            if (DateTime.UtcNow < _canRaiseAtUtc.Value)
                return;

            _raiseDueLogged = true;
            string message = _raiseTimeIsProvisional
                ? $"CLOAK CHECK IS NOW DUE. Provisional time reached at {_canRaiseAtUtc.Value:O}."
                : $"CLOAK RAISE IS NOW DUE. Server-derived earliest raise time was {_canRaiseAtUtc.Value:O}.";

            Logger.Warning(message);
            DevTrace(message);
            SaveState();
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

                PersistedCloakState state =
                    JsonConvert.DeserializeObject<PersistedCloakState>(File.ReadAllText(_statePath));

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
                    $"Restored cloak state: {_status}, lastObserved={_lastObservedUtc:O}, " +
                    $"canRaiseAt={_canRaiseAtUtc:O}, provisional={_raiseTimeIsProvisional}, source={_observationSource}.");
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

                string tempPath = _statePath + ".tmp";
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(state, Formatting.Indented));

                if (File.Exists(_statePath))
                    File.Delete(_statePath);
                File.Move(tempPath, _statePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed saving cloak state: {ex}");
                DevTrace($"ERROR save state: {ex.Message}");
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

                File.AppendAllText(_eventsPath, JsonConvert.SerializeObject(record) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed appending cloak event: {ex}");
                DevTrace($"ERROR append cloak event: {ex.Message}");
            }
        }

        private enum ReplyKind
        {
            Tell,
            Org,
            Dev
        }

        private class ReplyTarget
        {
            public ReplyKind Kind;
            public uint SenderId;
            public object ChannelId;
            public string ChannelName;

            public bool IsOrg => Kind == ReplyKind.Org;
            public bool IsDev => Kind == ReplyKind.Dev;

            public static ReplyTarget ForTell(uint senderId)
            {
                return new ReplyTarget
                {
                    Kind = ReplyKind.Tell,
                    SenderId = senderId
                };
            }

            public static ReplyTarget ForOrg(uint senderId, object channelId, string channelName)
            {
                return new ReplyTarget
                {
                    Kind = ReplyKind.Org,
                    SenderId = senderId,
                    ChannelId = channelId,
                    ChannelName = channelName
                };
            }

            public static ReplyTarget ForDev(uint senderId, object channelId)
            {
                return new ReplyTarget
                {
                    Kind = ReplyKind.Dev,
                    SenderId = senderId,
                    ChannelId = channelId,
                    ChannelName = "Apcmanager private"
                };
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

        private class WorkerRequest
        {
            public string Id;
            public string Command;
            public int? Level;
            public int? Index;
        }

        private class WorkerResponse
        {
            public string Id;
            public bool Ok;
            public string Message;
            public string Character;
            public string CloakState;
            public int? ShieldTimerInSeconds;
            public float? ControllerCharge;
            public int? Level;
            public int? Index;
        }
    }
}
