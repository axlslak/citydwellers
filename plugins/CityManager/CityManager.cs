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
    public partial class CityManager : ClientlessPluginEntry
    {
        private const int ProvisionalCloakDownSeconds = 3600;
        private const string FlipperPipeName = "citydwellers-flipper";
        private const string BuddiesPipeName = "citydwellers-buddies";
        private const int WorkerConnectTimeoutMs = 1000;
        private const int GuestLookupTimeoutMs = 5000;
        private const string OrgChannelName = "Athen Paladins";
        private const string CommandPrefix = "#";
        private const string DeveloperCharacter = "Kavem";
        private const int DevBacklogLimit = 25;

        private static readonly HashSet<string> PublicCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "help",
                "cloak",
                "status",
                "leave",
                "join",
                "raid",
                "raidassist"
            };

        private static readonly HashSet<string> AdminCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "invite",
                "kick",
                "wakeup",
                "sleep",
                "spinup",
                "spindown",
                "cancel",
                "recoverraid",
                "adminlist",
                "admin",
                "memberlist",
                "member"
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
            AdminListStore.Initialize(_pluginDir);
            DevTrace(
                $"ADMIN LIST initialized file=adminlist.json " +
                $"count={AdminListStore.Snapshot().Count}.");
            InitializeMembership();
            LoadState();
            InitializeRaidCoordinator();
            OrgRankAuthorizer.Initialize();
            Client.MessageReceived += MessageReceived;
        }

        public override void Teardown()
        {
            try
            {
                Client.MessageReceived -= MessageReceived;
                OrgRankAuthorizer.Shutdown();
                ShutdownRaidCoordinator();
                ShutdownMembership();

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
            Logger.Information("CityManager is in play and observing cloak packets, tells, org chat, and guest private chat.");

            BeginMembershipAfterInPlay();

            Client.Chat.PrivateMessageReceived += HandlePrivateMessage;
            Client.Chat.GroupMessageReceived += HandleGroupMessage;
            Client.Chat.PrivateGroupMessageReceived += HandlePrivateGroupMessage;
            Client.OnUpdate += Tick;

            DevTrace("MANAGER online. Dev telemetry initialized.");
            ResumeRaidCoordinatorAfterInPlay();
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

                string commandText;
                if (!TryExtractTellCommand(msg.Message, out commandText))
                {
                    Logger.Information($"TELL CHAT {msg.SenderName}: {msg.Message}");
                    return;
                }

                Logger.Information($"TELL COMMAND {msg.SenderName}: {msg.Message}");

                ProcessCommand(
                    msg.SenderName,
                    commandText,
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

                string cityMessage =
                    CityExtendedMessageParser.DecodeOrOriginal(msg.Message);

                if (!string.Equals(cityMessage, msg.Message, StringComparison.Ordinal))
                    DevTrace($"CITY DECODED: {cityMessage}");

                if (TryHandleCloakAnnouncement(msg, cityMessage))
                    return;

                if (!IsOrganizationChannel(msg.ChannelId, msg.ChannelName))
                    return;

                ObserveOrganizationMembershipMessage(cityMessage);
                ObserveRaidCityMessage(cityMessage, msg.ChannelId);

                string text = msg.Message.TrimStart();
                bool isCommand = text.StartsWith(CommandPrefix, StringComparison.Ordinal);

                if (!isCommand)
                {
                    if (string.Equals(msg.SenderName, "<Unknown>", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(msg.SenderName, "Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Information(
                            $"ORG SYSTEM [{msg.ChannelName}] {msg.SenderName}: {msg.Message}");
                        DevTrace($"CITY RAW: {cityMessage}");
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

                // AO echoes our own private-channel messages back to us. They are not commands.
                if (msg.SenderId == Client.Chat.CharId)
                    return;

                // Any incoming guest-channel traffic proves the diagnostic channel
                // is live. Confirm it without treating ordinary chatter as commands.
                ConfirmDevChannel();

                string text = msg.Message.TrimStart();
                if (!text.StartsWith(CommandPrefix, StringComparison.Ordinal))
                {
                    Logger.Information($"GUEST CHAT {msg.SenderName}: {msg.Message}");
                    return;
                }

                string commandText = text.Substring(CommandPrefix.Length).TrimStart();

                if (string.IsNullOrWhiteSpace(commandText))
                    return;

                Logger.Information($"GUEST COMMAND {msg.SenderName}: {msg.Message}");

                ProcessCommand(
                    msg.SenderName,
                    commandText,
                    ReplyTarget.ForGuest(msg.SenderId, msg.ChannelId));
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling private group message: {ex}");
                DevTrace($"ERROR guest handler: {ex.Message}");
            }
        }

        private bool TryHandleCloakAnnouncement(GroupMsg msg, string messageText)
        {
            const string cloakOffSuffix = " turned the cloaking device in your city off.";
            const string cloakOnSuffix = " turned the cloaking device in your city on.";

            if (messageText.EndsWith(cloakOffSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string actor = messageText.Substring(0, messageText.Length - cloakOffSuffix.Length).Trim();
                ObserveRaidCloakLowered(actor);
                HandleCloakAnnouncement(CloakStatus.Disabled, actor, msg.ChannelName, msg.Message);
                return true;
            }

            if (messageText.EndsWith(cloakOnSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string actor = messageText.Substring(0, messageText.Length - cloakOnSuffix.Length).Trim();
                HandleCloakAnnouncement(CloakStatus.Enabled, actor, msg.ChannelName, msg.Message);
                return true;
            }

            return false;
        }

        private bool TryExtractTellCommand(string rawText, out string commandText)
        {
            commandText = null;

            if (string.IsNullOrWhiteSpace(rawText))
                return false;

            string text = rawText.Trim();
            if (text.StartsWith(CommandPrefix, StringComparison.Ordinal))
            {
                commandText = text.Substring(CommandPrefix.Length).TrimStart();
                return !string.IsNullOrWhiteSpace(commandText);
            }

            string[] parts = text.Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return false;

            string command = parts[0].ToLowerInvariant();
            bool hasCommandShape =
                ((command == "help" ||
                  command == "cloak" ||
                  command == "status" ||
                  command == "leave" ||
                  command == "join" ||
                  command == "adminlist" ||
                  command == "memberlist") && parts.Length == 1) ||
                (command == "raid" && HasTellRaidCommandShape(parts)) ||
                (command == "raidassist" && parts.Length == 3) ||
                (command == "cancel" && (parts.Length == 1 || parts.Length == 2)) ||
                (command == "recoverraid" && parts.Length == 5) ||
                (command == "admin" && parts.Length == 3 &&
                 (string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(parts[1], "del", StringComparison.OrdinalIgnoreCase))) ||
                (command == "member" && parts.Length == 3 &&
                 (string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(parts[1], "del", StringComparison.OrdinalIgnoreCase))) ||
                ((command == "invite" ||
                  command == "kick" ||
                  command == "sleep" ||
                  command == "spindown") && parts.Length == 2) ||
                ((command == "wakeup" ||
                  command == "spinup") && parts.Length == 3);

            if (!hasCommandShape)
                return false;

            commandText = text;
            return true;
        }

        private bool IsKnownCommand(string command)
        {
            return PublicCommands.Contains(command ?? string.Empty) ||
                   AdminCommands.Contains(command ?? string.Empty);
        }

        private void ProcessCommand(string senderName, string rawCommand, ReplyTarget replyTarget)
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
                return;

            string[] parts = rawCommand.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string command = parts[0].ToLowerInvariant();
            DevTrace($"COMMAND {replyTarget.Kind} {senderName}: {rawCommand}");

            bool isAdmin = AdminListStore.Contains(senderName);

            if (!IsCommandSourceAuthorized(
                    senderName,
                    command,
                    parts,
                    replyTarget,
                    isAdmin))
            {
                DevTrace(
                    $"COMMAND DENIED {replyTarget.Kind} {senderName}: not a bot member.");
                Reply(replyTarget, "You are not a member of this bot.");
                return;
            }

            if (!IsKnownCommand(command))
            {
                DevTrace($"COMMAND UNKNOWN {replyTarget.Kind} {senderName}: {command}");
                Reply(replyTarget, UnknownCommandMessage(replyTarget));
                return;
            }

            if (string.Equals(command, "cloak", StringComparison.OrdinalIgnoreCase) &&
                !isAdmin &&
                !replyTarget.IsOrg &&
                !replyTarget.IsGuest)
            {
                DevTrace(
                    $"COMMAND DENIED {replyTarget.Kind} {senderName}: cloak requires org or guest chat.");
                Reply(replyTarget, "Use #cloak in organization or guest chat.");
                return;
            }

            if (string.Equals(command, "raid", StringComparison.OrdinalIgnoreCase))
            {
                ProcessRaidCommand(senderName, parts, replyTarget, isAdmin);
                return;
            }

            if (string.Equals(command, "raidassist", StringComparison.OrdinalIgnoreCase))
            {
                ProcessRaidAssistCommand(senderName, parts, replyTarget, isAdmin);
                return;
            }

            if (AdminCommands.Contains(command) && !isAdmin)
            {
                Logger.Warning(
                    $"Ignoring admin command '{command}' from unauthorized sender {senderName}.");
                DevTrace(
                    $"COMMAND DENIED {replyTarget.Kind} {senderName}: {command} is admin-only.");
                Reply(replyTarget, "You are not authorized to use that command.");
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
                    BeginServiceStatus(replyTarget);
                    break;

                case "leave":
                    if (parts.Length != 1)
                    {
                        Reply(replyTarget, Usage(replyTarget, "leave"));
                        break;
                    }

                    LeaveGuestChannel(senderName, replyTarget);
                    break;

                case "join":
                    if (parts.Length != 1)
                    {
                        Reply(replyTarget, Usage(replyTarget, "join"));
                        break;
                    }

                    JoinGuestChannel(senderName, replyTarget);
                    break;

                case "invite":
                {
                    if (parts.Length != 2)
                    {
                        Reply(replyTarget, Usage(replyTarget, "invite [character]"));
                        break;
                    }

                    BeginGuestChannelAction(replyTarget, parts[1], false);
                    break;
                }

                case "kick":
                {
                    if (parts.Length != 2)
                    {
                        Reply(replyTarget, Usage(replyTarget, "kick [character]"));
                        break;
                    }

                    BeginGuestChannelAction(replyTarget, parts[1], true);
                    break;
                }

                case "wakeup":
                {
                    int level;
                    int index;
                    if (parts.Length != 3 ||
                        !int.TryParse(parts[1], out level) ||
                        !int.TryParse(parts[2], out index))
                    {
                        Reply(replyTarget, Usage(replyTarget, "wakeup [level] [index]"));
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
                        Reply(replyTarget, Usage(replyTarget, "sleep [index]"));
                        break;
                    }

                    BeginBuddiesCommand(replyTarget, "sleep", null, index);
                    break;
                }

                case "spinup":
                {
                    int level;
                    int count;
                    if (parts.Length != 3 ||
                        !int.TryParse(parts[1], out level) ||
                        !int.TryParse(parts[2], out count) ||
                        level <= 0 ||
                        count <= 0)
                    {
                        Reply(replyTarget, Usage(replyTarget, "spinup [level] [count]"));
                        break;
                    }

                    BeginBuddiesCommand(replyTarget, "spinup", level, count);
                    break;
                }

                case "spindown":
                {
                    int count;
                    if (parts.Length != 2 ||
                        !int.TryParse(parts[1], out count) ||
                        count <= 0)
                    {
                        Reply(replyTarget, Usage(replyTarget, "spindown [count]"));
                        break;
                    }

                    BeginBuddiesCommand(replyTarget, "spindown", null, count);
                    break;
                }

                case "cancel":
                    ProcessRaidCancel(senderName, parts, replyTarget);
                    break;

                case "recoverraid":
                    ProcessRaidRecovery(senderName, parts, replyTarget);
                    break;

                case "adminlist":
                    ProcessAdminListCommand(senderName, parts, replyTarget);
                    break;

                case "admin":
                    ProcessAdminCommand(senderName, parts, replyTarget);
                    break;

                case "memberlist":
                    ProcessMemberListCommand(senderName, parts, replyTarget);
                    break;

                case "member":
                    ProcessMemberCommand(senderName, parts, replyTarget);
                    break;
            }
        }

        private string BuildHelpMessage(ReplyTarget target)
        {
            string prefix = target.RequiresPrefix ? "#" : string.Empty;
            string suffix = target.RequiresPrefix
                ? " Commands in this channel must start with #."
                : " # is optional in tells.";

            return
                $"Members: {prefix}help, {prefix}status, {prefix}leave, {prefix}join. " +
                $"In organization or guest chat: #cloak, #raid. " +
                $"Raid-assist buttons are available to Squad Commanders and higher. " +
                $"Admins may also use cloak and raid in tells. " +
                $"Admin: {prefix}invite [character], {prefix}kick [character], " +
                $"{prefix}wakeup [level] [index], {prefix}sleep [index], " +
                $"{prefix}spinup [level] [count], {prefix}spindown [count], {prefix}cancel, " +
                $"{prefix}adminlist, {prefix}admin [add|del] [character], " +
                $"{prefix}memberlist, {prefix}member [add|del] [character]. " +
                $"Recovery: {prefix}recoverraid [owner] [all|general] [level] [count]." +
                suffix;
        }

        private string UnknownCommandMessage(ReplyTarget target)
        {
            return target.RequiresPrefix
                ? "No such command. Try #help."
                : "No such command. Try help.";
        }

        private string Usage(ReplyTarget target, string syntax)
        {
            return $"Usage: {(target.RequiresPrefix ? CommandPrefix : string.Empty)}{syntax}";
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

                        Reply(target, CloakPresentation.Unavailable());

                        return;
                    }

                    ApplyFlipperObservation(response);

                    string reply = CloakPresentation.Build(
                        response.CloakState,
                        response.ShieldTimerInSeconds,
                        response.ControllerCharge,
                        response.Cached,
                        response.ObservedUtc);

                    string chargeText = response.ControllerCharge.HasValue
                        ? $"{response.ControllerCharge.Value * 100:F1}%"
                        : "unknown";
                    string rawTimerText = response.ShieldTimerInSeconds.HasValue
                        ? $"{response.ShieldTimerInSeconds.Value}s"
                        : "unknown";
                    string sourceText = response.Cached
                        ? $"cache observed={response.ObservedUtc:O}"
                        : "fresh";

                    string diagnosticReply =
                        $"Cloak = {response.CloakState ?? "Unknown"}. " +
                        $"Raw shield timer = {rawTimerText}. Charge = {chargeText}. Source = {sourceText}.";

                    Logger.Information($"IPC <- Flipper {request.Id}: {diagnosticReply}");
                    DevTrace($"FLIPPER OK [{shortId}]: {diagnosticReply}");

                    Reply(target, reply);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Flipper IPC failed: {ex.Message}");
                    DevTrace($"FLIPPER ERROR: {ex.Message}");

                    Reply(target, CloakPresentation.Unavailable());
                }
            });
        }

        private void BeginServiceStatus(ReplyTarget target)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                DevTrace("STATUS -> ping Flipper and Buddies.");

                WorkerLinkStatus flipper = PingWorker("Flipper", FlipperPipeName);
                WorkerLinkStatus buddies = PingWorker("Buddies", BuddiesPipeName);

                string reply =
                    $"Manager = online/usable. " +
                    $"Flipper = {flipper.PublicText}. " +
                    $"Buddies = {buddies.PublicText}.";

                string diagnostic =
                    $"STATUS Manager=online/usable; " +
                    $"Flipper={flipper.DiagnosticText}; " +
                    $"Buddies={buddies.DiagnosticText}.";

                Logger.Information(diagnostic);
                DevTrace(diagnostic);
                Reply(target, reply);
            });
        }

        private WorkerLinkStatus PingWorker(string workerName, string pipeName)
        {
            var request = new WorkerRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = "ping"
            };

            string shortId = ShortId(request.Id);

            try
            {
                Logger.Information($"IPC -> {workerName} {request.Id}: ping");
                DevTrace($"{workerName.ToUpperInvariant()} -> ping [{shortId}]");

                WorkerResponse response = SendWorkerRequest(
                    pipeName,
                    request,
                    WorkerConnectTimeoutMs);

                if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                {
                    return WorkerLinkStatus.Unusable(
                        $"response id mismatch ({response.Id ?? "missing"})");
                }

                if (!response.Ok)
                    return WorkerLinkStatus.Unusable(response.Message ?? "ping failed");

                return WorkerLinkStatus.Usable(response.Message ?? "ping succeeded");
            }
            catch (Exception ex)
            {
                return WorkerLinkStatus.Unusable(ex.Message);
            }
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

                    bool usesCount =
                        string.Equals(command, "spinup", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(command, "spindown", StringComparison.OrdinalIgnoreCase);

                    string quantity = usesCount
                        ? $"count={index}"
                        : $"index={index}";

                    string shortId = ShortId(request.Id);
                    Logger.Information(
                        $"IPC -> Buddies {request.Id}: {command} level={level} {quantity}");

                    DevTrace(
                        level.HasValue
                            ? $"BUDDIES -> {command} level={level.Value} {quantity} [{shortId}]"
                            : $"BUDDIES -> {command} {quantity} [{shortId}]");

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
                if (target.IsGuest)
                {
                    SendGuestMessage(text);
                    return;
                }

                if (target.IsOrg)
                {
                    if (TrySendOrgMessage(text))
                        return;

                    Logger.Warning("Unable to send command reply in the originating org channel.");
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

        private void JoinGuestChannel(string senderName, ReplyTarget target)
        {
            if (Client.Chat == null || target.SenderId == 0)
            {
                Reply(target, "Guest channel invite is unavailable right now.");
                DevTrace($"GUEST join failed for {senderName}: chat or sender id unavailable.");
                return;
            }

            try
            {
                Client.Chat.InvitePrivateGroup(target.SenderId);
                Reply(target, "Guest channel invite sent.");

                Logger.Information(
                    $"Guest private-channel join invite sent to {senderName} ({target.SenderId}).");
                DevTrace($"GUEST join invite sent to {senderName} ({target.SenderId}).");
            }
            catch (Exception ex)
            {
                Reply(target, $"Guest channel invite failed: {ex.Message}");
                Logger.Warning($"Guest private-channel join failed: {ex.Message}");
                DevTrace($"GUEST join error for {senderName}: {ex.Message}");
            }
        }

        private void LeaveGuestChannel(string senderName, ReplyTarget target)
        {
            if (Client.Chat == null || target.SenderId == 0)
            {
                Reply(target, "Unable to leave the guest channel right now.");
                DevTrace($"GUEST leave failed for {senderName}: chat or sender id unavailable.");
                return;
            }

            try
            {
                // A guest-channel reply must be queued before the kick packet or
                // the departing user will not see it. Tells and org replies can
                // safely be sent after the kick.
                if (target.IsGuest)
                    Reply(target, "You have left Apcmanager's guest channel.");

                SendPrivateGroupKick(target.SenderId);

                if (!target.IsGuest)
                    Reply(target, "You have left Apcmanager's guest channel.");

                Logger.Information(
                    $"Guest private-channel leave sent for {senderName} ({target.SenderId}).");
                DevTrace($"GUEST leave sent for {senderName} ({target.SenderId}).");
            }
            catch (Exception ex)
            {
                Reply(target, $"Unable to leave the guest channel: {ex.Message}");
                Logger.Warning($"Guest private-channel leave failed: {ex.Message}");
                DevTrace($"GUEST leave error for {senderName}: {ex.Message}");
            }
        }

        private void BeginGuestChannelAction(
            ReplyTarget target,
            string characterName,
            bool kick)
        {
            string normalizedName = NormalizeCharacterName(characterName);

            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                Reply(target, Usage(target, kick ? "kick [character]" : "invite [character]"));
                DevTrace(kick ? "GUEST kick failed: missing character name." : "GUEST invite failed: missing character name.");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    uint characterId;
                    if (!TryResolveCharacterId(normalizedName, out characterId))
                    {
                        Reply(
                            target,
                            $"Unable to {(kick ? "kick" : "invite")} {normalizedName}: character lookup failed.");
                        DevTrace(
                            $"GUEST {(kick ? "kick" : "invite")} failed: could not resolve {normalizedName}.");
                        return;
                    }

                    if (Client.Chat == null)
                    {
                        Reply(target, "Guest channel action failed: chat is unavailable.");
                        DevTrace($"GUEST {(kick ? "kick" : "invite")} failed: chat is unavailable.");
                        return;
                    }

                    if (characterId == Client.Chat.CharId)
                    {
                        Reply(target, "Apcmanager cannot invite or kick itself.");
                        DevTrace("GUEST action refused: Apcmanager cannot invite or kick itself.");
                        return;
                    }

                    if (kick)
                    {
                        SendPrivateGroupKick(characterId);
                        Reply(target, $"{normalizedName} was kicked from the guest channel.");
                        Logger.Information($"Guest private-channel kick sent for {normalizedName} ({characterId}).");
                        DevTrace($"GUEST kick sent: {normalizedName}.");
                    }
                    else
                    {
                        Client.Chat.InvitePrivateGroup(characterId);
                        Reply(target, $"Guest channel invite sent to {normalizedName}.");
                        Logger.Information($"Guest private-channel invite sent to {normalizedName} ({characterId}).");
                        DevTrace($"GUEST invite sent: {normalizedName}.");
                    }
                }
                catch (Exception ex)
                {
                    Reply(
                        target,
                        $"Guest channel {(kick ? "kick" : "invite")} failed: {ex.Message}");
                    Logger.Warning($"Guest private-channel action failed: {ex.Message}");
                    DevTrace($"GUEST {(kick ? "kick" : "invite")} error: {ex.Message}");
                }
            });
        }

        private bool TryResolveCharacterId(string characterName, out uint characterId)
        {
            characterId = 0;

            if (Client.Chat == null)
                return false;

            try
            {
                if (Client.Chat.NameToIdMap.TryGetValue(characterName, out characterId))
                    return true;

                Client.Chat.RequestCharacterId(characterName);
            }
            catch
            {
                return false;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(GuestLookupTimeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);

                try
                {
                    if (Client.Chat != null &&
                        Client.Chat.NameToIdMap.TryGetValue(characterName, out characterId))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private string NormalizeCharacterName(string characterName)
        {
            string value = (characterName ?? string.Empty).Trim();
            if (value.Length == 0)
                return string.Empty;

            if (value.Length == 1)
                return value.ToUpperInvariant();

            return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
        }

        private void SendPrivateGroupKick(uint characterId)
        {
            if (Client.Chat == null)
                return;

            // AO chat client packet 51 (0x0033): private-group owner kicks one player.
            // Header is big-endian packet id + payload length, followed by the uint32 character id.
            byte[] packet = new byte[8];
            packet[0] = 0x00;
            packet[1] = 0x33;
            packet[2] = 0x00;
            packet[3] = 0x04;
            packet[4] = (byte)(characterId >> 24);
            packet[5] = (byte)(characterId >> 16);
            packet[6] = (byte)(characterId >> 8);
            packet[7] = (byte)characterId;

            Client.Chat.Send(packet);
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

            SendGuestMessage("GUEST channel confirmed. Flushing buffered telemetry.");
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

            SendGuestMessage(text);
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

                SendGuestMessage(message);
            }
        }

        private void SendGuestMessage(string text)
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
                _lastObservedUtc = response.ObservedUtc ?? now;
                _observationSource = response.Cached ? "Flipper.Cache" : "Flipper.Probe";
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
                    response.Cached ? "flipper_cache" : "flipper_probe",
                    _observationSource,
                    response.Character,
                    null,
                    response.Message);

                SaveState();
            }
        }

        private void Tick(object sender, double e)
        {
            TryInviteDeveloper();
            TickMembership();
            TickRaidCoordinator();

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
            Guest
        }

        private class ReplyTarget
        {
            public ReplyKind Kind;
            public uint SenderId;
            public object ChannelId;
            public string ChannelName;

            public bool IsOrg => Kind == ReplyKind.Org;
            public bool IsGuest => Kind == ReplyKind.Guest;
            public bool RequiresPrefix => Kind != ReplyKind.Tell;

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

            public static ReplyTarget ForGuest(uint senderId, object channelId)
            {
                return new ReplyTarget
                {
                    Kind = ReplyKind.Guest,
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
            public List<string> Characters;
            public List<int> Indexes;
            public int? Count;
            public bool Cached;
            public DateTime? ObservedUtc;
            public bool ActionSent;
        }

        private class WorkerLinkStatus
        {
            public bool IsUsable;
            public string Detail;

            public string PublicText => IsUsable
                ? "linked/usable"
                : "not linked/unusable";

            public string DiagnosticText =>
                $"{PublicText} ({Detail ?? "no detail"})";

            public static WorkerLinkStatus Usable(string detail)
            {
                return new WorkerLinkStatus
                {
                    IsUsable = true,
                    Detail = detail
                };
            }

            public static WorkerLinkStatus Unusable(string detail)
            {
                return new WorkerLinkStatus
                {
                    IsUsable = false,
                    Detail = detail
                };
            }
        }
    }
}
