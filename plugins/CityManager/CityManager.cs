using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.ChatMessages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager : ClientlessPluginEntry
    {
        private const int ProvisionalCloakDownSeconds = 3600;
        private const string FlipperPipeName = "citydwellers-flipper";
        private const string BuddiesPipeName = "citydwellers-buddies";
        private const int WorkerConnectTimeoutMs = 1000;
        private const int BuddySnapshotFreshSeconds = 15;
        private const int GuestLookupTimeoutMs = 5000;
        private const string OrgChannelName = "Athen Paladins";
        private const string CommandPrefix = "#";
        private const string DeveloperCharacter = "Kavem";
        private const int DiagnosticHistoryLimit = 500;
        private const long DiagnosticLogRotateBytes = 2L * 1024L * 1024L;

        private static readonly HashSet<string> PublicCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "help",
                "changelog",
                "online",
                "items",
                "i",
                "itemid",
                "bankid",
                "cloak",
                "status",
                "buffers",
                "stock",
                "symb",
                "symbs",
                "spirit",
                "spirits",
                "dyna",
                "nano",
                "nanos",
                "phat",
                "phatz",
                "donor",
                "pickups",
                "takers",
                "lost",
                "found",
                "withdraw",
                "get",
                "cru",
                "leave",
                "join",
                "alts",
                "raid",
                "raidassist",
                "cancel"
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
                "positions",
                "dynel",
                "home",
                "recoverraid",
                "adminlist",
                "admin",
                "memberlist",
                "member",
                "ban",
                "unban",
                "dump",
                "restart",
                "inventory",
                "inv"
            };

        private readonly object _stateSync = new object();
        private readonly object _devSync = new object();
        private readonly Queue<string> _diagnosticHistory = new Queue<string>();
        private readonly Stopwatch _managerUptime = Stopwatch.StartNew();
        private readonly DateTime _managerStartedUtc = DateTime.UtcNow;

        private string _settingsDir;
        private string _dataDir;
        private string _statePath;
        private string _eventsPath;
        private string _diagnosticLogPath;
        private bool _charInPlay;

        private readonly object _orgOutputSync = new object();
        private object _lastOrgChannelId;
        private string _lastOrgChannelName;
        private DateTime? _lastOrgChannelObservedUtc;
        private bool _orgOutboundDegraded;
        private readonly List<PendingOrgEcho> _pendingOrgEchoes = new List<PendingOrgEcho>();
        private sealed class PendingOrgEcho
        {
            public string Text, Route;
            public long Stamp;
            public int Length, ChannelId;
            public uint SenderId;
            public OrgReplyRetryPlan RetryPlan;
            public bool IsRetry;
            public int RetryAttempt;
            public int BudgetAtSend;
            public bool GrowthProbe;
        }

        private string _orgBlobStatePath;
        private int _orgBlobCurrentPageSize = OrgBlobPageSize;
        private int _orgLastConfirmedBytes;
        private int _orgLastFailedBytes;
        private readonly List<QueuedOrgRetry> _orgRetryQueue = new List<QueuedOrgRetry>();
        private DateTime _nextOrgRetrySendUtc = DateTime.MinValue;

        private sealed class QueuedOrgRetry
        {
            public ReplyTarget Target;
            public string RawText;
            public int Attempt;
            public DateTime DueUtc;
        }

        private sealed class OrgBlobBudgetState
        {
            public int Version;
            public int DefaultPageSize;
            public int CurrentPageSize;
            public int LastConfirmedBytes;
            public int LastFailedBytes;
            public DateTime UpdatedUtc;
        }

        private string _orgOutboundDetail = "not tested since startup";
        private DateTime? _lastOrgOutboundAttemptUtc;

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
            CityDwellers.Shared.BuildIdentity.Register();
            Logger.Information("BUILD " + CityDwellers.Shared.BuildIdentity.Label +
                " | revision=" + CityDwellers.Shared.BuildIdentity.Revision);
            string settingsError;
            if (!SettingsPaths.TryEnsureDirectories(
                    out _settingsDir,
                    out _dataDir,
                    out settingsError))
            {
                Logger.Error(settingsError);
                return;
            }

            _statePath = Path.Combine(_dataDir, "citymanager-cloak-state.json");
            _eventsPath = Path.Combine(_dataDir, "citymanager-cloak-events.jsonl");
            _diagnosticLogPath = Path.Combine(_dataDir, "citymanager-diagnostics.log");
            _orgBlobStatePath = Path.Combine(_dataDir, "citymanager-org-size.json");
            LoadOrgBlobBudgetState();
            SaveOrgBlobBudgetState();

            Logger.Information($"CityManager settings: {_settingsDir}");
            Logger.Information($"CityManager data: {_dataDir}");
            try { InitializeEventReporting(); }
            catch (Exception ex)
            {
                ShutdownEventReporting();
                Logger.Warning("Manager syslog reporting disabled: " + ex.Message);
            }
            ItemCatalog.StartLoading();
            AdminListStore.Initialize(_dataDir);
            BanListStore.Initialize(_dataDir);
            DevTrace(
                $"ADMIN LIST initialized file=adminlist.json " +
                $"count={AdminListStore.Snapshot().Count}.");
            if (!File.Exists(CityBankers.Shared.SettingsPaths.BankTerminalPath(_settingsDir)))
                CityBankers.Shared.SettingsPaths.SaveBankTerminal(_settingsDir,
                    CityBankers.Shared.SettingsPaths.InitialBankTerminalInstance, "initial");
            InitializeMembership();
            InitializeAlts();
            InitializeTellQueue();
            LoadState();
            InitializeRaidCoordinator();
            OrgRankAuthorizer.Initialize();
            CityRaidAutomation.Initialize(
                _status,
                _lastObservedUtc,
                _canRaiseAtUtc,
                _observationSource,
                ApplyCloakRecoveryObservation);
            Client.MessageReceived += MessageReceived;
        }

        public override void Teardown()
        {
            try
            {
                ShutdownEventReporting();
                Client.MessageReceived -= MessageReceived;
                CityRaidAutomation.Shutdown();
                OrgRankAuthorizer.Shutdown();
                ShutdownRaidCoordinator();
                ShutdownAlts();
                ShutdownMembership();
                ShutdownTellQueue();

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
            BeginAltsAfterInPlay();

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

                if (TryHandleAltsBotTell(msg))
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
                    ReplyTarget.ForTell(msg.SenderId, msg.SenderName));
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

                if (!IsOrganizationChannel(msg.ChannelId, msg.ChannelName))
                    return;

                RememberOrganizationChannel(msg.ChannelId, msg.ChannelName);
                // Our own reply coming back is the only proof it was delivered.
                ObserveOrgEcho(msg);
                ObserveAltPresenceAnnouncement(msg.SenderName, msg.Message);
                string cityMessage;
                bool nativeCityEvent = CityExtendedMessageParser.TryDecodeNative(msg, out cityMessage);
                if (nativeCityEvent)
                {
                    DevTrace($"CITY DECODED: {cityMessage}");
                    if (TryHandleCloakAnnouncement(msg, cityMessage))
                        return;
                    ObserveRaidCityMessage(cityMessage, msg.ChannelId);
                }
                else
                    cityMessage = msg.Message;

                ObserveOrganizationMembershipMessage(cityMessage);

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
                    ReplyTarget.ForOrg(
                        msg.SenderId,
                        msg.ChannelId,
                        msg.ChannelName,
                        msg.SenderName));
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
                ObserveGuestOnline(msg.SenderId, msg.SenderName);

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

            text = CityBankers.StockCommandEngine.NormalizeSymbiantCommand(text);
            string[] parts = text.Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return false;

            string command = parts[0].ToLowerInvariant();
            bool hasCommandShape =
                ((command == "cru" ||
                  command == "changelog" ||
                  command == "online" ||
                  command == "cloak" ||
                  command == "status" ||
                  command == "buffers" ||
                  command == "leave" ||
                  command == "join" ||
                  command == "adminlist" ||
                  command == "memberlist" ||
                  command == "positions" ||
                  command == "dump" ||
                  command == "restart") && parts.Length == 1) ||
                ((command == "inventory" || command == "inv") && parts.Length <= 2) ||
                (command == "help" && parts.Length <= 3) ||
                (command == "donor" && parts.Length <= 2) ||
                command == "pickups" || command == "takers" ||
                command == "lost" || command == "found" ||
                ((command == "withdraw" || command == "get") && parts.Length == 2) ||
                command == "items" || command == "i" || command == "itemid" ||
                command == "stock" ||
                command == "symb" || command == "symbs" ||
                command == "spirit" || command == "spirits" ||
                command == "dyna" || command == "nano" || command == "nanos" ||
                command == "phat" || command == "phatz" ||
                (command == "home" &&
                 (parts.Length == 1 || parts.Length == 2)) ||
                (command == "alts" && HasTellAltsCommandShape(parts)) ||
                (command == "raid" && HasTellRaidCommandShape(parts)) ||
                (command == "raidassist" &&
                 (parts.Length == 3 ||
                  (parts.Length == 4 &&
                   string.Equals(parts[1], "level", StringComparison.OrdinalIgnoreCase)))) ||
                (command == "cancel" && (parts.Length == 1 || parts.Length == 2)) ||
                (command == "recoverraid" && parts.Length == 5) ||
                (command == "admin" && parts.Length == 3 &&
                 (string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase) ||
                  IsRemoveVerb(parts[1]))) ||
                (command == "member" && parts.Length == 3 &&
                 (string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase) ||
                  IsRemoveVerb(parts[1]))) ||
                ((command == "ban" || command == "unban") && parts.Length == 2) ||
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
            rawCommand = CityBankers.StockCommandEngine.NormalizeSymbiantCommand(rawCommand);
            if (string.IsNullOrWhiteSpace(rawCommand))
                return;

            string[] parts = rawCommand.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string command = parts[0].ToLowerInvariant();
            DevTrace($"COMMAND {replyTarget.Kind} {senderName}: {rawCommand}");

            bool isAdmin = IsAdministrator(senderName);

            if (!isAdmin &&
                !string.Equals(command, "leave", StringComparison.OrdinalIgnoreCase) &&
                IsBanned(senderName))
            {
                DevTrace(
                    $"COMMAND DENIED {replyTarget.Kind} {senderName}: banned.");
                Reply(replyTarget, "You are banned from this bot.");
                return;
            }

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

            if ((string.Equals(command, "stock", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "symb", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "symbs", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "spirit", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "spirits", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "dyna", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "nano", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "nanos", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "phat", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "phatz", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "donor", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "pickups", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "takers", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "lost", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "found", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "withdraw", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "get", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "cru", StringComparison.OrdinalIgnoreCase)) &&
                !isAdmin &&
                !replyTarget.IsOrg &&
                !IsTellMember(senderName))
            {
                DevTrace(
                    $"COMMAND DENIED {replyTarget.Kind} {senderName}: " +
                    $"{command} requires AP membership.");
                Reply(
                    replyTarget,
                    "This command is available to Athen Paladins members.");
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
                case "items":
                case "i":
                case "itemid":
                    ProcessItemsCommand(parts, replyTarget);
                    break;

                case "online":
                    ProcessOnlineCommand(parts, replyTarget);
                    break;

                case "changelog":
                    ProcessChangelogCommand(parts, replyTarget);
                    break;

                case "help":
                    ProcessHelpCommand(parts, replyTarget, isAdmin);
                    break;

                case "cloak":
                    BeginFlipperProbe(replyTarget);
                    break;

                case "bankid":
                    ProcessBankIdCommand(senderName, parts, replyTarget, isAdmin);
                    break;

                case "dynel":
                    ProcessCentralDynelCommand(senderName, replyTarget);
                    break;

                case "buffers":
                    BeginBufferStatus(replyTarget);
                    break;

                case "status":
                    BeginServiceStatus(replyTarget);
                    break;

                case "stock":
                case "symb":
                case "symbs":
                case "spirit":
                case "spirits":
                case "dyna":
                case "nano":
                case "nanos":
                case "phat":
                case "phatz":
                    ProcessBankerStockCommand(senderName, rawCommand, replyTarget, isAdmin);
                    break;

                case "lost":
                case "found":
                    ProcessLostFoundCommand(parts, replyTarget);
                    break;

                case "pickups":
                case "takers":
                    ProcessBankerPickupsCommand(parts, replyTarget);
                    break;

                case "donor":
                    ProcessBankerDonorCommand(parts, replyTarget);
                    break;

                case "cru":
                    ProcessCruCommand(senderName, parts, replyTarget);
                    break;

                case "withdraw":
                case "get":
                    ProcessBankerWithdrawalCommand(senderName, parts, replyTarget);
                    break;

                case "inventory":
                case "inv":
                    ProcessBankerInventoryCommand(parts, replyTarget);
                    break;

                case "alts":
                    ProcessAltsCommand(senderName, parts, replyTarget, isAdmin);
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

                case "positions":
                    if (parts.Length != 1)
                    {
                        Reply(replyTarget, Usage(replyTarget, "positions"));
                        break;
                    }

                    BeginBuddyPositions(replyTarget);
                    break;

                case "home":
                {
                    if (parts.Length > 2)
                    {
                        Reply(replyTarget, Usage(replyTarget, "home [level|all|status]"));
                        break;
                    }

                    if (parts.Length == 2 &&
                        string.Equals(parts[1], "status", StringComparison.OrdinalIgnoreCase))
                    {
                        BeginHomeCommand(replyTarget, null, true);
                        break;
                    }

                    int? homeLevel = null;
                    if (parts.Length == 2 &&
                        !string.Equals(parts[1], "all", StringComparison.OrdinalIgnoreCase))
                    {
                        int parsedLevel;
                        if (!int.TryParse(parts[1], out parsedLevel))
                        {
                            Reply(replyTarget, Usage(replyTarget, "home [level|all|status]"));
                            break;
                        }

                        homeLevel = parsedLevel;
                    }

                    BeginHomeCommand(replyTarget, homeLevel, false);
                    break;
                }

                case "cancel":
                    ProcessRaidCancel(senderName, parts, replyTarget, isAdmin);
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

                case "ban":
                    ProcessBanCommand(senderName, parts, replyTarget, false);
                    break;

                case "unban":
                    ProcessBanCommand(senderName, parts, replyTarget, true);
                    break;

                case "dump":
                    BeginDiagnosticDump(senderName, parts, replyTarget);
                    break;

                case "restart":
                    BeginManagerRestart(senderName, parts, replyTarget);
                    break;
            }
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
            if (TryReplyRaidFlipperReservation(target))
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // A cloak query can be queued just before raid-start CT
                    // handling begins. Recheck here so that it cannot take the
                    // one Flipper login away from the raid-start operation.
                    if (TryReplyRaidFlipperReservation(target))
                        return;

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

                        ReplyWithCloakHistory(target, CloakPresentation.Unavailable());

                        return;
                    }

                    if (response.ObservedUtc.HasValue &&
                        UtcTimestamp.IsFuture(
                            response.ObservedUtc.Value,
                            DateTime.UtcNow))
                    {
                        string invalidTime =
                            UtcTimestamp.Normalize(response.ObservedUtc.Value).ToString("O");
                        Logger.Warning(
                            $"IPC <- Flipper {request.Id}: rejected future " +
                            $"observation {invalidTime}.");
                        DevTrace(
                            $"FLIPPER FAIL [{shortId}]: rejected future-dated " +
                            $"cache observation {invalidTime}.");
                        ReplyWithCloakHistory(target, CloakPresentation.Unavailable());
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

                    ReplyWithCloakHistory(target, reply);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Flipper IPC failed: {ex.Message}");
                    DevTrace($"FLIPPER ERROR: {ex.Message}");

                    ReplyWithCloakHistory(target, CloakPresentation.Unavailable());
                }
            });
        }

        private string BuildCloakStatusSummary()
        {
            lock (_stateSync)
            {
                string source = string.IsNullOrWhiteSpace(_observationSource)
                    ? "Unknown"
                    : _observationSource;
                string observed = _lastObservedUtc.HasValue
                    ? $", observed {FormatDuration(DateTime.UtcNow - _lastObservedUtc.Value)} ago"
                    : string.Empty;

                if (_status == CloakStatus.Disabled && _canRaiseAtUtc.HasValue)
                {
                    string due = _canRaiseAtUtc.Value > DateTime.UtcNow
                        ? $", enable due in {FormatDuration(_canRaiseAtUtc.Value - DateTime.UtcNow)}"
                        : ", enable is due";

                    return $"Cloak = Disabled via {source}{observed}{due}";
                }

                return $"Cloak = {_status} via {source}{observed}";
            }
        }

        private WorkerLinkStatus PingWorker(string workerName, string pipeName)
        {
            var request = new WorkerRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = "ping"
            };

            try
            {
                Logger.Information($"IPC -> {workerName} {request.Id}: ping");

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
                        Index = index,
                        Purpose =
                            string.Equals(command, "wakeup", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(command, "spinup", StringComparison.OrdinalIgnoreCase)
                                ? "demo"
                                : null,
                        LeaseSeconds =
                            string.Equals(command, "wakeup", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(command, "spinup", StringComparison.OrdinalIgnoreCase)
                                ? GeneralBuddySafetyLeaseSeconds
                                : (int?)null
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
                            ? $"BUDDIES -> {command} level={level.Value} {quantity} " +
                              $"purpose={request.Purpose ?? "manual"} " +
                              $"lease={request.LeaseSeconds?.ToString() ?? "none"}s [{shortId}]"
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

        private void BeginBuddyPositions(ReplyTarget target)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var request = new WorkerRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Command = "positions"
                };

                string shortId = ShortId(request.Id);

                try
                {
                    DevTrace($"BUDDY POSITIONS -> snapshot [{shortId}]");

                    WorkerResponse response = SendWorkerRequest(
                        BuddiesPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                    {
                        throw new IOException(
                            $"Buddies response id mismatch ({response.Id ?? "missing"}).");
                    }

                    if (!response.Ok)
                    {
                        DevTrace(
                            $"BUDDY POSITIONS FAIL [{shortId}]: {response.Message}");
                        Reply(
                            target,
                            $"Buddies position check failed: {response.Message ?? "unknown error"}");
                        return;
                    }

                    List<BuddyPositionSnapshot> positions =
                        response.Positions == null
                            ? new List<BuddyPositionSnapshot>()
                            : response.Positions
                                .Where(position => position != null)
                                .ToList();
                    DateTime now = DateTime.UtcNow;
                    int reporting = positions.Count(position => HasPositionReport(position));
                    int fresh = positions.Count(position => IsFreshPositionReport(position, now));
                    int inPlay = positions.Count(position => position.InPlay);
                    int dead = positions.Count(position => position.Dead);

                    DevTrace(
                        $"BUDDY POSITIONS OK [{shortId}]: active={positions.Count} " +
                        $"reporting={reporting} fresh={fresh} inplay={inPlay} dead={dead}.");
                    RecordBuddyPositionsForDump(positions, now);

                    string window = BuildBuddyPositionWindow(positions, now);
                    Reply(
                        target,
                        $"Buddy positions: {positions.Count} active, {reporting} reporting, " +
                        $"{inPlay} in play, {dead} dead. {window}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Buddies position IPC failed: {ex.Message}");
                    DevTrace($"BUDDY POSITIONS ERROR [{shortId}]: {ex.Message}");
                    Reply(target, $"Buddies position check unavailable: {ex.Message}");
                }
            });
        }

        private void BeginHomeCommand(
            ReplyTarget target,
            int? level,
            bool statusOnly)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var request = new WorkerRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Command = statusOnly ? "homestatus" : "home",
                    Level = level
                };

                string shortId = ShortId(request.Id);

                try
                {
                    DevTrace(
                        statusOnly
                            ? $"BUDDY HOME -> status [{shortId}]"
                            : $"BUDDY HOME -> level=" +
                              $"{(level.HasValue ? level.Value.ToString() : "all")} " +
                              $"[{shortId}]");

                    WorkerResponse response = SendWorkerRequest(
                        BuddiesPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    DevTrace(
                        $"BUDDY HOME {(response.Ok ? "OK" : "FAIL")} " +
                        $"[{shortId}]: {response.Message}");
                    Reply(
                        target,
                        response.Ok
                            ? $"Buddies: {response.Message}"
                            : $"Buddies failed: {response.Message}");

                    if (!statusOnly &&
                        response.Ok &&
                        !string.IsNullOrWhiteSpace(response.HomeJobId))
                    {
                        string homeJobId = response.HomeJobId;
                        ThreadPool.QueueUserWorkItem(
                            __ => MonitorHomeCompletion(target, homeJobId));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Buddies home IPC failed: {ex.Message}");
                    DevTrace($"BUDDY HOME ERROR [{shortId}]: {ex.Message}");
                    Reply(target, $"Buddies home service unavailable: {ex.Message}");
                }
            });
        }

        private void MonitorHomeCompletion(ReplyTarget target, string homeJobId)
        {
            var timeout = Stopwatch.StartNew();
            int consecutiveFailures = 0;

            while (timeout.Elapsed < TimeSpan.FromHours(2))
            {
                Thread.Sleep(5000);

                var request = new WorkerRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Command = "homestatus"
                };

                try
                {
                    WorkerResponse response = SendWorkerRequest(
                        BuddiesPipeName,
                        request,
                        WorkerConnectTimeoutMs);
                    consecutiveFailures = 0;

                    if (!response.Ok)
                        continue;

                    if (!string.Equals(
                            response.HomeJobId,
                            homeJobId,
                            StringComparison.Ordinal))
                    {
                        Reply(
                            target,
                            "Buddies home reporting changed to another job; " +
                            "use #home status for the current result.");
                        return;
                    }

                    if (response.HomeRunning)
                        continue;

                    DevTrace(
                        $"BUDDY HOME COMPLETE [{ShortId(homeJobId)}]: " +
                        response.Message);
                    Reply(target, "Buddies: " + response.Message);
                    return;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures < 3)
                        continue;

                    DevTrace(
                        $"BUDDY HOME MONITOR ERROR [{ShortId(homeJobId)}]: " +
                        ex.Message);
                    Reply(
                        target,
                        "Buddies home completion reporting became unavailable; " +
                        "use #home status to check it later.");
                    return;
                }
            }

            Reply(
                target,
                "Buddies home completion reporting timed out; " +
                "use #home status for the retained result.");
        }

        private string BuildBuddyPositionWindow(
            IList<BuddyPositionSnapshot> positions,
            DateTime now)
        {
            var body = new StringBuilder();
            body.Append("<font color='#89D2E8'>City Dwellers Positions</font>\n\n");

            if (positions.Count == 0)
            {
                body.Append("No City Dwellers-owned buddies are active.\n");
            }
            else
            {
                foreach (BuddyPositionSnapshot position in positions)
                {
                    bool reported = HasPositionReport(position);
                    bool fresh = IsFreshPositionReport(position, now);
                    string color = position.Dead
                        ? "#FF5050"
                        : reported && fresh && position.InPlay
                            ? "#00DE42"
                            : "#F79410";
                    string age = reported
                        ? FormatSnapshotAge(position, now)
                        : "never";

                    body.Append(
                        $"<font color='{color}'>{SafeRaidText(position.Character ?? "unknown")}</font> " +
                        $"(level {position.Level?.ToString() ?? "?"}, index {position.Index?.ToString() ?? "?"})\n");
                    body.Append(
                        $"  state: {(position.InPlay ? "in play" : "not in play")}, " +
                        $"dead: {(position.Dead ? "yes" : "no")}, observed: {age}\n");
                    body.Append(
                        $"  playfield: {position.PlayfieldId?.ToString() ?? "?"} " +
                        $"{SafeRaidText(position.PlayfieldName ?? "unknown")}\n");
                    body.Append($"  position: {FormatPosition(position)}\n");
                    body.Append($"  heading: {FormatHeading(position)}\n");
                    body.Append(
                        $"  health: {position.Health?.ToString() ?? "?"}/" +
                        $"{position.MaxHealth?.ToString() ?? "?"}\n");
                    body.Append(
                        $"  run speed: {position.RunSpeed?.ToString() ?? "?"}\n");

                    if (!string.IsNullOrWhiteSpace(position.NavigationTraceFile))
                    {
                        body.Append(
                            $"  navigation trace: " +
                            $"{SafeRaidText(position.NavigationTraceFile)} " +
                            $"(event {position.NavigationTraceSequence?.ToString() ?? "?"})\n");
                    }

                    string lastMovementCommand = SafeRaidText(
                        FormatMovementRecord(
                            position.LastMovementCommandAction,
                            position.LastMovementCommandUtc,
                            position.LastMovementCommandX,
                            position.LastMovementCommandY,
                            position.LastMovementCommandZ,
                            null,
                            now));
                    string lastMovementEcho = SafeRaidText(
                        FormatMovementRecord(
                            position.LastMovementObservationAction,
                            position.LastMovementObservationUtc,
                            position.LastMovementObservationX,
                            position.LastMovementObservationY,
                            position.LastMovementObservationZ,
                            position.LastMovementObservationDeltaTime,
                            now));
                    body.Append(
                        $"  last movement command: {lastMovementCommand}\n");
                    body.Append(
                        $"  last movement echo: {lastMovementEcho}\n");

                    if (!string.IsNullOrWhiteSpace(position.HomeState))
                    {
                        body.Append(
                            $"  home: {SafeRaidText(position.HomeState)}" +
                            (position.HomeDistance.HasValue
                                ? $", distance: {position.HomeDistance.Value:F2}m"
                                : string.Empty) +
                            "\n");

                        if (!string.IsNullOrWhiteSpace(position.HomeDetail))
                            body.Append(
                                $"  home detail: {SafeRaidText(position.HomeDetail)}\n");
                    }

                    if (!string.IsNullOrWhiteSpace(position.Error))
                        body.Append($"  error: {SafeRaidText(position.Error)}\n");

                    body.Append("\n");
                }
            }

            body.Append(
                "Position reports are observational unless an administrator or " +
                "raid preparation started a home job.");
            return $"<a href=\"text://{body}\">Click here to open window</a>";
        }

        private void RecordBuddyPositionsForDump(
            IList<BuddyPositionSnapshot> positions,
            DateTime now)
        {
            for (int index = 0; index < positions.Count; index++)
            {
                RecordDiagnostic(
                    "BUDDY POSITION SNAPSHOT: " +
                    BuildBuddyPositionTelemetry(positions[index], now));
            }
        }

        private string BuildBuddyPositionTelemetry(
            BuddyPositionSnapshot position,
            DateTime now)
        {
            string age = HasPositionReport(position)
                ? FormatSnapshotAgeSeconds(position, now)
                : "unknown";
            string error = string.IsNullOrWhiteSpace(position.Error)
                ? "none"
                : position.Error.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
            string homeDetail = string.IsNullOrWhiteSpace(position.HomeDetail)
                ? "none"
                : position.HomeDetail.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
            string lastMovementCommand = FormatMovementRecord(
                position.LastMovementCommandAction,
                position.LastMovementCommandUtc,
                position.LastMovementCommandX,
                position.LastMovementCommandY,
                position.LastMovementCommandZ,
                null,
                now);
            string lastMovementEcho = FormatMovementRecord(
                position.LastMovementObservationAction,
                position.LastMovementObservationUtc,
                position.LastMovementObservationX,
                position.LastMovementObservationY,
                position.LastMovementObservationZ,
                position.LastMovementObservationDeltaTime,
                now);

            return
                $"{position.Character ?? "unknown"} level={position.Level?.ToString() ?? "?"} " +
                $"index={position.Index?.ToString() ?? "?"} inplay={position.InPlay} " +
                $"dead={position.Dead} pf={position.PlayfieldId?.ToString() ?? "?"} " +
                $"name='{position.PlayfieldName ?? "unknown"}' pos={FormatPosition(position)} " +
                $"heading={FormatHeading(position)} hp={position.Health?.ToString() ?? "?"}/" +
                $"{position.MaxHealth?.ToString() ?? "?"} " +
                $"runSpeed={position.RunSpeed?.ToString() ?? "?"} age={age} " +
                $"home={position.HomeState ?? "none"} " +
                $"homeDistance={(position.HomeDistance.HasValue ? position.HomeDistance.Value.ToString("0.00", CultureInfo.InvariantCulture) : "?")} " +
                $"trace='{position.NavigationTraceFile ?? "none"}' " +
                $"traceSeq={position.NavigationTraceSequence?.ToString() ?? "?"} " +
                $"cmd='{lastMovementCommand}' " +
                $"echo='{lastMovementEcho}' " +
                $"homeDetail='{homeDetail}' error='{error}'";
        }

        private static string FormatMovementRecord(
            string action,
            DateTime? utc,
            float? x,
            float? y,
            float? z,
            int? packetDeltaTime,
            DateTime now)
        {
            if (string.IsNullOrWhiteSpace(action))
                return "none";

            string age = utc.HasValue
                ? FormatObservedAgeMilliseconds(utc.Value, now)
                : "?";
            string delta = packetDeltaTime.HasValue
                ? $" dt={packetDeltaTime.Value}"
                : string.Empty;
            return
                $"{action}@{FormatVector3(x, y, z)} age={age}{delta}";
        }

        private static string FormatVector3(float? x, float? y, float? z)
        {
            return x.HasValue && y.HasValue && z.HasValue
                ? $"({FormatCoordinate(x)},{FormatCoordinate(y)},{FormatCoordinate(z)})"
                : "unknown";
        }

        private static bool HasPositionReport(BuddyPositionSnapshot position)
        {
            return position != null && position.ObservedUtc != default(DateTime);
        }

        private static bool IsFreshPositionReport(
            BuddyPositionSnapshot position,
            DateTime now)
        {
            TimeSpan age;
            return HasPositionReport(position) &&
                   UtcTimestamp.TryGetAge(
                       position.ObservedUtc,
                       now,
                       out age) &&
                   age <= TimeSpan.FromSeconds(BuddySnapshotFreshSeconds);
        }

        private string FormatSnapshotAge(
            BuddyPositionSnapshot position,
            DateTime now)
        {
            TimeSpan age;
            return UtcTimestamp.TryGetAge(
                       position.ObservedUtc,
                       now,
                       out age)
                ? FormatDuration(age) + " ago"
                : "invalid future timestamp";
        }

        private static string FormatSnapshotAgeSeconds(
            BuddyPositionSnapshot position,
            DateTime now)
        {
            TimeSpan age;
            if (!UtcTimestamp.TryGetAge(
                    position.ObservedUtc,
                    now,
                    out age))
            {
                return "invalid-future";
            }

            return ((int)Math.Min(
                int.MaxValue,
                age.TotalSeconds)).ToString() + "s";
        }

        private static string FormatObservedAgeMilliseconds(
            DateTime observedUtc,
            DateTime nowUtc)
        {
            TimeSpan age;
            if (!UtcTimestamp.TryGetAge(observedUtc, nowUtc, out age))
                return "invalid-future";

            return ((int)Math.Min(
                int.MaxValue,
                age.TotalMilliseconds)).ToString() + "ms";
        }

        private static string FormatPosition(BuddyPositionSnapshot position)
        {
            return position != null &&
                   position.PositionAvailable &&
                   position.PositionX.HasValue &&
                   position.PositionY.HasValue &&
                   position.PositionZ.HasValue
                ? $"({FormatCoordinate(position.PositionX)}," +
                  $"{FormatCoordinate(position.PositionY)}," +
                  $"{FormatCoordinate(position.PositionZ)})"
                : "unknown";
        }

        private static string FormatHeading(BuddyPositionSnapshot position)
        {
            return position != null &&
                   position.HeadingAvailable &&
                   position.HeadingX.HasValue &&
                   position.HeadingY.HasValue &&
                   position.HeadingZ.HasValue &&
                   position.HeadingW.HasValue
                ? $"({FormatCoordinate(position.HeadingX)}," +
                  $"{FormatCoordinate(position.HeadingY)}," +
                  $"{FormatCoordinate(position.HeadingZ)}," +
                  $"{FormatCoordinate(position.HeadingW)})"
                : "unknown";
        }

        private static string FormatCoordinate(float? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.000", CultureInfo.InvariantCulture)
                : "?";
        }

        private WorkerResponse SendWorkerRequest(string pipeName, WorkerRequest request, int connectTimeoutMs)
        {
            return CityDwellers.Shared.LocalIpc.Request<WorkerRequest, WorkerResponse>(
                pipeName, request, connectTimeoutMs);
        }

        // Each text:// page is a separate transport message, including queued tells.
        private void Reply(ReplyTarget target, IEnumerable<string> messages)
        {
            foreach (string message in messages)
                Reply(target, message);
        }

        private void Reply(ReplyTarget target, string text)
        {
            OrgReplyRetryPlan orgRetryPlan =
                target != null && target.IsOrg
                    ? CaptureOrgReplyRetryPlan(target, text)
                    : null;
            text = CityBankers.Shared.CityBankersChatPalette.StyleMarkup(text);
            try
            {
                if (target.IsGuest)
                {
                    SendGuestMessage(text);
                    return;
                }

                if (target.IsOrg)
                {
                    if (TrySendOrgMessage(target, text, orgRetryPlan, false, 0))
                        return;

                    string warning =
                        "<font color='#F79410'>Organization output is degraded.</font> " +
                        "This reply was delivered privately. " + text;
                    Logger.Warning(
                        "Unable to send command reply in the originating org channel; " +
                        "falling back to the command issuer's tell.");
                    DevTrace(
                        "ORG SEND FALLBACK -> tell sender=" + target.SenderId +
                        " channel=" + (target.ChannelName ?? "unknown") + ".");

                    if (target.SenderId != 0)
                        QueueTell(target, warning);
                    return;
                }

                QueueTell(target, text);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed sending command reply: {ex}");
                DevTrace($"ERROR reply: {ex.Message}");
            }
        }

        private void RememberOrganizationChannel(int channelId, string channelName)
        {
            lock (_orgOutputSync)
            {
                _lastOrgChannelId = channelId;
                _lastOrgChannelName = channelName;
                _lastOrgChannelObservedUtc = DateTime.UtcNow;
            }
        }

        private bool TrySendOrgMessage(
            ReplyTarget target,
            string text,
            OrgReplyRetryPlan retryPlan,
            bool isRetry,
            int retryAttempt)
        {
            object channelId = target != null ? target.ChannelId : null;
            string channelName = target != null ? target.ChannelName : null;

            lock (_orgOutputSync)
            {
                if (channelId == null)
                    channelId = _lastOrgChannelId;
                if (string.IsNullOrWhiteSpace(channelName))
                    channelName = _lastOrgChannelName;
                _lastOrgOutboundAttemptUtc = DateTime.UtcNow;
            }

            // Expire old attempts without overwriting other pages still in flight.
            ObserveOrgEcho(null);

            // [CORRECTION] Organization chat is SENT on the game connection and
            // only received on the chat connection. Disassembly of
            // AOSharp.Clientless 1.0.16 shows Client.SendOrgMessage reads
            // Stat.Clan (5) from LocalPlayer, builds a GroupMsgMessage with
            // MessageType 3 and ChannelId set to that stat, then calls
            // Client.Send. GroupMessageType.Org is 3, so the raw route below
            // emits the identical packet. Neither route is "the wrong
            // connection"; what differs is only where the channel id comes from.
            //
            // The real defect here was gating the SDK call on Client.OrgId,
            // which is populated by OnOrgInfoPacket and is a different source
            // from the Stat.Clan value SendOrgMessage actually reads. A zero
            // Client.OrgId therefore skipped the SDK path for the wrong reason.
            int clanStat = 0;
            string statDetail;
            try
            {
                LocalPlayer localPlayer = DynelManager.LocalPlayer;
                clanStat = localPlayer == null ? 0 : Convert.ToInt32(localPlayer.GetStat(Stat.Clan));
                statDetail = "Stat.Clan=" + clanStat;
            }
            catch (Exception ex)
            {
                statDetail = "Stat.Clan unreadable (" + ex.Message + ")";
            }

            // #stock reaches org chat while #help, #cloak and #status do not,
            // through the identical Reply -> TrySendOrgMessage path with the
            // same target. The transport therefore works and the payload is
            // what differs, so every attempt records its own length.
            string detail = statDetail + "; Client.OrgId=" + Client.OrgId +
                "; observedChannel=" + (channelId == null ? "none" : channelId.ToString()) +
                "; orgName=" + (Client.OrgName ?? "none") +
                "; bytes=" + Encoding.UTF8.GetByteCount(text ?? string.Empty);

            if (clanStat > 0)
            {
                PendingOrgEcho pending = null;
                try
                {
                    // Register before writing: an immediate echo must find its attempt.
                    pending = NoteOrgEchoPending(
                        "Client.SendOrgMessage",
                        text,
                        clanStat,
                        retryPlan,
                        isRetry,
                        retryAttempt);
                    Client.SendOrgMessage(text, false);
                    SetOrgOutboundHealth(false, "Client.SendOrgMessage (" + detail + ")");
                    Logger.Information(
                        "Org reply submitted through AOSharp.Clientless.Client.SendOrgMessage; " +
                        "awaiting echo confirmation. " + detail);
                    return true;
                }
                catch (Exception ex)
                {
                    ForgetOrgEcho(pending);
                    detail += "; SendOrgMessage threw " + ex.Message;
                }
            }
            else
            {
                // The game server never reported this character's organization
                // stat. That is the condition first recorded on 2026-09-06, not
                // a chat-proxy or transport fault: inbound org chat is healthy.
                detail += "; LocalPlayer Stat.Clan is absent, so the game connection " +
                    "does not know this character's organization";
            }

            string directDetail;
            if (TrySendDirectGroupMessage(
                    channelId,
                    text,
                    out directDetail,
                    retryPlan,
                    isRetry,
                    retryAttempt))
            {
                // Both routes send on the game connection. Delivery remains
                // unverified until the corresponding chat echo arrives.
                SetOrgOutboundHealth(
                    true,
                    "unverified raw org-channel attempt via " +
                    (channelName ?? "remembered organization channel") +
                    " (" + directDetail + "; " + detail + ")");
                Logger.Warning(
                    "Organization reply submitted on the raw game-connection route; " +
                    "awaiting echo: " + directDetail + "; " + detail);
                DevTrace("ORG SEND UNVERIFIED: " + directDetail + "; " + detail);
                return true;
            }

            detail += "; " + directDetail;
            SetOrgOutboundHealth(true, detail);
            Logger.Warning("Organization reply unavailable: " + detail);
            DevTrace("ORG SEND DEGRADED: " + detail);
            return false;
        }

        private PendingOrgEcho NoteOrgEchoPending(
            string route,
            string text,
            int channelId,
            OrgReplyRetryPlan retryPlan,
            bool isRetry,
            int retryAttempt)
        {
            int length = Encoding.UTF8.GetByteCount(text ?? string.Empty);
            int budget = CurrentOrgBlobPageSize();
            var pending = new PendingOrgEcho
            {
                Text = text ?? string.Empty,
                Route = route,
                Stamp = Stopwatch.GetTimestamp(),
                Length = length,
                ChannelId = channelId,
                SenderId = Client.Chat == null ? 0 : Client.Chat.CharId,
                RetryPlan = retryPlan,
                IsRetry = isRetry,
                RetryAttempt = retryAttempt,
                BudgetAtSend = budget,
                GrowthProbe = retryPlan != null &&
                    retryPlan.IsBlob &&
                    !isRetry &&
                    length >= Math.Max(OrgBlobMinPageSize, budget - 512)
            };
            lock (_orgOutputSync) _pendingOrgEchoes.Add(pending);
            return pending;
        }

        private void ForgetOrgEcho(PendingOrgEcho pending)
        {
            if (pending == null) return;
            lock (_orgOutputSync) _pendingOrgEchoes.Remove(pending);
        }

        // Match exactly one pending attempt by sender, channel and complete text.
        // Shared markup prefixes cannot identify a page. Unmatched/decorated text
        // is not delivery evidence.
        private void ObserveOrgEcho(GroupMsg observed)
        {
            PendingOrgEcho confirmed = null;
            List<PendingOrgEcho> expired;
            lock (_orgOutputSync)
            {
                if (observed != null)
                {
                    confirmed = _pendingOrgEchoes.FirstOrDefault(p => p.SenderId != 0 &&
                        p.SenderId == observed.SenderId && p.ChannelId == observed.ChannelId &&
                        string.Equals(p.Text, observed.Message, StringComparison.Ordinal));
                    if (confirmed != null) _pendingOrgEchoes.Remove(confirmed);
                }
                long now = Stopwatch.GetTimestamp();
                expired = _pendingOrgEchoes.Where(p => now - p.Stamp > Stopwatch.Frequency * 15).ToList();
                foreach (var pending in expired) _pendingOrgEchoes.Remove(pending);
            }
            foreach (var pending in expired)
            {
                SetOrgOutboundHealth(true, "no observed echo within 15s via " + pending.Route);
                Logger.Warning("ORG DELIVERY UNCONFIRMED: no echo observed within 15s via " +
                    pending.Route + "; bytes=" + pending.Length + ".");
                DevTrace("ORG ECHO MISSING via " + pending.Route + " bytes=" + pending.Length);
                RecordOrgBlobDelivery(pending, false);
                QueueOrgRetry(pending);
            }
            if (confirmed != null)
            {
                SetOrgOutboundHealth(false, "delivery confirmed by observed echo via " + confirmed.Route);
                Logger.Information("ORG DELIVERY CONFIRMED via " + confirmed.Route +
                    "; bytes=" + confirmed.Length + ".");
                DevTrace("ORG ECHO CONFIRMED via " + confirmed.Route + " bytes=" + confirmed.Length);
                RecordOrgBlobDelivery(confirmed, true);
            }
        }

        private bool TrySendDirectGroupMessage(
            object channelId,
            string text,
            out string detail,
            OrgReplyRetryPlan retryPlan,
            bool isRetry,
            int retryAttempt)
        {
            detail = "no observed channel id";
            if (channelId == null)
                return false;

            PendingOrgEcho pending = null;
            try
            {
                int observedChannelId = Convert.ToInt32(
                    channelId,
                    CultureInfo.InvariantCulture);
                if (observedChannelId <= 0)
                {
                    detail = "observed channel id is invalid";
                    return false;
                }

                pending = NoteOrgEchoPending(
                    "raw game-connection GroupMsgMessage",
                    text,
                    observedChannelId,
                    retryPlan,
                    isRetry,
                    retryAttempt);
                Client.Send(
                    new GroupMsgMessage
                    {
                        MessageType = GroupMessageType.Org,
                        ChannelId = observedChannelId,
                        Text = text
                    });

                detail =
                    "Client.Send(GroupMsgMessage Org, channel " +
                    observedChannelId + ")";
                return true;
            }
            catch (Exception ex)
            {
                ForgetOrgEcho(pending);
                detail = "raw org-channel send failed: " + ex.Message;
                return false;
            }
        }

        private int CurrentOrgBlobPageSize()
        {
            lock (_orgOutputSync)
                return _orgBlobCurrentPageSize;
        }

        private void LoadOrgBlobBudgetState()
        {
            lock (_orgOutputSync)
            {
                _orgBlobCurrentPageSize = OrgBlobPageSize;
                _orgLastConfirmedBytes = 0;
                _orgLastFailedBytes = 0;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_orgBlobStatePath) ||
                    !File.Exists(_orgBlobStatePath))
                    return;

                OrgBlobBudgetState saved = JsonConvert.DeserializeObject<OrgBlobBudgetState>(
                    File.ReadAllText(_orgBlobStatePath));
                if (saved == null ||
                    saved.Version != 3 ||
                    saved.DefaultPageSize != OrgBlobPageSize ||
                    saved.CurrentPageSize < OrgBlobMinPageSize ||
                    saved.CurrentPageSize > OrgBlobMaxPageSize)
                    return;

                lock (_orgOutputSync)
                {
                    _orgBlobCurrentPageSize = saved.CurrentPageSize;
                    _orgLastConfirmedBytes = Math.Max(0, saved.LastConfirmedBytes);
                    _orgLastFailedBytes = Math.Max(0, saved.LastFailedBytes);
                }

                Logger.Information(
                    "ORG BLOB BUDGET restored: current=" + saved.CurrentPageSize +
                    "; default=" + OrgBlobPageSize +
                    "; lastConfirmed=" + Math.Max(0, saved.LastConfirmedBytes) +
                    "; lastFailed=" + Math.Max(0, saved.LastFailedBytes) + ".");
            }
            catch (Exception ex)
            {
                Logger.Warning("Org blob budget state could not be restored: " + ex.Message);
            }
        }

        private void SaveOrgBlobBudgetState()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_orgBlobStatePath))
                    return;

                OrgBlobBudgetState snapshot;
                lock (_orgOutputSync)
                {
                    snapshot = new OrgBlobBudgetState
                    {
                        Version = 3,
                        DefaultPageSize = OrgBlobPageSize,
                        CurrentPageSize = _orgBlobCurrentPageSize,
                        LastConfirmedBytes = _orgLastConfirmedBytes,
                        LastFailedBytes = _orgLastFailedBytes,
                        UpdatedUtc = DateTime.UtcNow
                    };
                }

                FileSnapshot.WriteText(
                    _orgBlobStatePath,
                    JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Warning("Org blob budget state could not be saved: " + ex.Message);
            }
        }

        private void RecordOrgBlobDelivery(PendingOrgEcho pending, bool delivered)
        {
            if (pending == null || pending.Length <= 0)
                return;

            int before;
            int after;
            bool changed = false;
            lock (_orgOutputSync)
            {
                before = _orgBlobCurrentPageSize;
                if (delivered)
                {
                    _orgLastConfirmedBytes = pending.Length;
                    if (pending.GrowthProbe && !pending.IsRetry)
                    {
                        _orgBlobCurrentPageSize = Math.Min(
                            OrgBlobMaxPageSize,
                            _orgBlobCurrentPageSize + OrgBlobPageStep);
                    }
                }
                else
                {
                    _orgLastFailedBytes = pending.Length;
                    _orgBlobCurrentPageSize = Math.Max(
                        OrgBlobMinPageSize,
                        _orgBlobCurrentPageSize - OrgBlobPageStep);
                }

                after = _orgBlobCurrentPageSize;
                changed = before != after;
            }

            if (changed)
            {
                Logger.Information(
                    "ORG BLOB BUDGET " +
                    (delivered ? "UP" : "DOWN") +
                    ": " + before + " -> " + after +
                    " (default " + OrgBlobPageSize +
                    "); evidence=" + pending.Length + " bytes" +
                    (pending.IsRetry ? "; retry=" + pending.RetryAttempt : string.Empty) + ".");
            }

            SaveOrgBlobBudgetState();
        }

        private void QueueOrgRetry(PendingOrgEcho pending)
        {
            if (pending == null || pending.RetryPlan == null)
                return;

            List<string> retryMessages;
            try
            {
                retryMessages = RebuildOrgReplyRetry(pending.RetryPlan);
            }
            catch (Exception ex)
            {
                Logger.Warning("ORG RETRY rebuild failed: " + ex.Message);
                return;
            }

            if (retryMessages == null || retryMessages.Count == 0)
                return;

            DateTime now = DateTime.UtcNow;
            int queueCount;
            DateTime firstDue;
            lock (_orgOutputSync)
            {
                DateTime due = now.AddSeconds(3);
                if (_nextOrgRetrySendUtc > due)
                    due = _nextOrgRetrySendUtc;
                if (_orgRetryQueue.Count > 0)
                {
                    DateTime tail = _orgRetryQueue[_orgRetryQueue.Count - 1].DueUtc.AddSeconds(3);
                    if (tail > due)
                        due = tail;
                }

                firstDue = due;
                foreach (string raw in retryMessages)
                {
                    _orgRetryQueue.Add(new QueuedOrgRetry
                    {
                        Target = pending.RetryPlan.Target,
                        RawText = raw ?? string.Empty,
                        Attempt = pending.RetryAttempt + 1,
                        DueUtc = due
                    });
                    due = due.AddSeconds(3);
                }

                queueCount = _orgRetryQueue.Count;
            }

            Logger.Warning(
                "ORG RETRY queued: attempt=" + (pending.RetryAttempt + 1) +
                "; pages=" + retryMessages.Count +
                "; first retry in " +
                Math.Max(0, (int)Math.Ceiling((firstDue - now).TotalSeconds)) +
                "s; queue=" + queueCount +
                "; current blob budget=" + CurrentOrgBlobPageSize() +
                " (default " + OrgBlobPageSize + ").");
        }

        private void TickOrgRetryQueue()
        {
            QueuedOrgRetry retry = null;
            DateTime now = DateTime.UtcNow;
            lock (_orgOutputSync)
            {
                if (_orgRetryQueue.Count == 0 ||
                    now < _nextOrgRetrySendUtc ||
                    now < _orgRetryQueue[0].DueUtc)
                    return;

                retry = _orgRetryQueue[0];
                _orgRetryQueue.RemoveAt(0);
                _nextOrgRetrySendUtc = now.AddSeconds(3);
            }

            OrgReplyRetryPlan plan = CaptureOrgReplyRetryPlan(retry.Target, retry.RawText);
            string styled = CityBankers.Shared.CityBankersChatPalette.StyleMarkup(retry.RawText);
            Logger.Information(
                "ORG RETRY send: attempt=" + retry.Attempt +
                "; bytes=" + Encoding.UTF8.GetByteCount(styled) +
                "; current blob budget=" + CurrentOrgBlobPageSize() +
                " (default " + OrgBlobPageSize + ").");

            if (TrySendOrgMessage(retry.Target, styled, plan, true, retry.Attempt))
                return;

            lock (_orgOutputSync)
            {
                DateTime due = DateTime.UtcNow.AddSeconds(3);
                if (_nextOrgRetrySendUtc > due)
                    due = _nextOrgRetrySendUtc;
                if (_orgRetryQueue.Count > 0)
                {
                    DateTime tail = _orgRetryQueue[_orgRetryQueue.Count - 1].DueUtc.AddSeconds(3);
                    if (tail > due)
                        due = tail;
                }

                retry.DueUtc = due;
                _orgRetryQueue.Add(retry);
            }

            Logger.Warning(
                "ORG RETRY send path unavailable; retry remains queued for another attempt.");
        }

        private void SetOrgOutboundHealth(bool degraded, string detail)
        {
            lock (_orgOutputSync)
            {
                _orgOutboundDegraded = degraded;
                _orgOutboundDetail = string.IsNullOrWhiteSpace(detail)
                    ? "no detail"
                    : detail;
            }
        }

        private string BuildOrgOutboundStatusSummary()
        {
            lock (_orgOutputSync)
            {
                string observed = _lastOrgChannelObservedUtc.HasValue
                    ? ", channel observed " +
                      FormatDuration(DateTime.UtcNow - _lastOrgChannelObservedUtc.Value) +
                      " ago"
                    : ", no org channel observed since startup";
                string attempted = _lastOrgOutboundAttemptUtc.HasValue
                    ? ", last send attempt " +
                      FormatDuration(DateTime.UtcNow - _lastOrgOutboundAttemptUtc.Value) +
                      " ago"
                    : ", no send attempted since startup";

                string confirmed = _orgLastConfirmedBytes > 0
                    ? _orgLastConfirmedBytes + "B"
                    : "none";
                string failed = _orgLastFailedBytes > 0
                    ? _orgLastFailedBytes + "B"
                    : "none";
                string adaptive =
                    ", blob " + _orgBlobCurrentPageSize + "(" + OrgBlobPageSize + " default)" +
                    ", last confirmed " + confirmed +
                    ", last failed " + failed +
                    ", retries queued " + _orgRetryQueue.Count;

                return (_orgOutboundDegraded ? "degraded" : "ready") +
                       " - " + _orgOutboundDetail + adaptive + observed + attempted;
            }
        }

        private bool IsOrgOutboundDegraded()
        {
            lock (_orgOutputSync)
                return _orgOutboundDegraded;
        }

        private void BeginManagerRestart(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "restart"));
                return;
            }

            string restartRequestPath = Path.Combine(
                _dataDir,
                "citydwellers-manager-restart.request");
            string temporaryPath = restartRequestPath + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    senderName + "|" + DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                Reply(target, "Manager restart is unavailable: " + ex.Message);
                DevTrace("RESTART PREPARE ERROR actor=" + senderName + ": " + ex.Message);
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                }
                return;
            }

            Reply(
                target,
                "<font color='#F79410'>Manager restart accepted.</font> " +
                "Apcmanager will disconnect and return in a few seconds.");
            RecordDiagnostic(
                "RESTART requested by " + senderName +
                "; unified host will recycle Manager only.");
            SaveState();

            try
            {
                if (File.Exists(restartRequestPath))
                    File.Delete(restartRequestPath);
                File.Move(temporaryPath, restartRequestPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Manager restart handoff failed: " + ex);
                DevTrace("RESTART HANDOFF ERROR actor=" + senderName + ": " + ex.Message);
                Reply(target, "Manager restart handoff failed: " + ex.Message);
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                }
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

            var timeout = Stopwatch.StartNew();

            while (timeout.ElapsedMilliseconds < GuestLookupTimeoutMs)
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
            lock (_altsSync)
                _observedOnlineGuests.Remove(characterId);
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
            bool newlyConfirmed = false;

            lock (_devSync)
            {
                if (!_devChannelConfirmed)
                {
                    _devChannelConfirmed = true;
                    newlyConfirmed = true;
                }
            }

            if (newlyConfirmed)
            {
                SendGuestMessage(
                    "<font color='#89D2E8'>[Manager]</font> " +
                    "<font color='#00DE42'>Live diagnostics connected.</font> " +
                    "Earlier events were kept on disk; use <font color='#F79410'>#dump</font> " +
                    "instead of receiving a backlog.");
            }
        }

        private void DevTrace(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            RecordDiagnostic(text);

            bool confirmed;
            lock (_devSync)
                confirmed = _devChannelConfirmed;

            if (confirmed)
                SendGuestMessage(FormatLiveDiagnostic(text));
        }

        private void RecordDiagnostic(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                          " | " + BuildIdentity.Label + " | " + SanitizeDiagnosticText(text);

            lock (_devSync)
            {
                while (_diagnosticHistory.Count >= DiagnosticHistoryLimit)
                    _diagnosticHistory.Dequeue();

                _diagnosticHistory.Enqueue(line);
                AppendDiagnosticLineLocked(line);
            }

            Logger.Information("DIAGNOSTIC " + SanitizeDiagnosticText(text));
        }

        private void AppendDiagnosticLineLocked(string line)
        {
            if (string.IsNullOrWhiteSpace(_diagnosticLogPath))
                return;

            try
            {
                if (File.Exists(_diagnosticLogPath) &&
                    new FileInfo(_diagnosticLogPath).Length >= DiagnosticLogRotateBytes)
                {
                    string previous = _diagnosticLogPath + ".previous";
                    if (File.Exists(previous))
                        File.Delete(previous);
                    File.Move(_diagnosticLogPath, previous);
                }

                File.AppendAllText(_diagnosticLogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Warning("Unable to append Manager diagnostic log: " + ex.Message);
            }
        }

        private static string SanitizeDiagnosticText(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        private string FormatLiveDiagnostic(string text)
        {
            return CityBankers.Shared.CityBankersChatPalette.StyleMarkup(
                "<font color='#89D2E8'>[Manager]</font> " +
                EscapeBlobText(SanitizeDiagnosticText(text)));
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
            _observationSource = "OrgChat.NativeCityEvent";
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
            // CT charge belongs to Manager's shared raid state, regardless of
            // which Manager command requested the Flipper observation.
            ApplyRaidControllerObservation(response);

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
                _lastObservedUtc = UtcTimestamp.Normalize(response.ObservedUtc) ?? now;
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

            CityRaidAutomation.ObserveConfirmedState(
                _status,
                _lastObservedUtc,
                _canRaiseAtUtc,
                _observationSource);
        }

        private void ApplyCloakRecoveryObservation(
            CloakStatus status,
            int? shieldTimerInSeconds,
            DateTime? observedUtc,
            bool cached,
            string message)
        {
            ApplyFlipperObservation(
                new WorkerResponse
                {
                    Ok = true,
                    CloakState = status.ToString(),
                    ShieldTimerInSeconds = shieldTimerInSeconds,
                    ObservedUtc = observedUtc,
                    Cached = cached,
                    Message = message,
                    Character = "Apcflipper"
                });
        }

        private void Tick(object sender, double e)
        {
            ObserveOrgEcho(null);
            TickOrgRetryQueue();
            TickTellQueue();
            TryInviteDeveloper();
            TickMembership();
            TickAlts();
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
                PersistedCloakState state = null;
                if (File.Exists(_statePath))
                {
                    try
                    {
                        state = JsonConvert.DeserializeObject<PersistedCloakState>(
                            File.ReadAllText(_statePath));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(
                            $"Persisted cloak snapshot is unreadable; " +
                            $"falling back to the event log: {ex.Message}");
                    }
                }

                if (state != null)
                {
                    _status = state.Status;
                    _shieldTimerInSeconds = state.ShieldTimerInSeconds;
                    _lastObservedUtc = UtcTimestamp.Normalize(state.LastObservedUtc);
                    _lastChangedUtc = UtcTimestamp.Normalize(state.LastChangedUtc);
                    _canRaiseAtUtc = UtcTimestamp.Normalize(state.CanRaiseAtUtc);
                    _raiseDueLogged = state.RaiseDueLogged;
                    _raiseTimeIsProvisional = state.RaiseTimeIsProvisional;
                    _observationSource = state.ObservationSource ?? "Unknown";

                    if (_lastObservedUtc.HasValue &&
                        UtcTimestamp.IsFuture(
                            _lastObservedUtc.Value,
                            DateTime.UtcNow))
                    {
                        Logger.Warning(
                            $"Discarding future-dated persisted cloak state " +
                            $"observation {_lastObservedUtc.Value:O}.");
                        _status = CloakStatus.Unknown;
                        _shieldTimerInSeconds = 0;
                        _lastObservedUtc = null;
                        _lastChangedUtc = null;
                        _canRaiseAtUtc = null;
                        _raiseDueLogged = false;
                        _raiseTimeIsProvisional = false;
                        _observationSource = "InvalidFutureTimestamp";
                    }
                }

                CloakEventRecord latestEvent = LoadLatestCloakEvent();
                if (latestEvent != null)
                    latestEvent.OccurredUtc =
                        UtcTimestamp.Normalize(latestEvent.OccurredUtc);

                if (latestEvent != null &&
                    UtcTimestamp.IsFuture(
                        latestEvent.OccurredUtc,
                        DateTime.UtcNow))
                {
                    Logger.Warning(
                        $"Ignoring future-dated cloak event " +
                        $"{latestEvent.OccurredUtc:O} while restoring state.");
                    latestEvent = null;
                }

                if (latestEvent != null &&
                    (!_lastObservedUtc.HasValue ||
                     latestEvent.OccurredUtc > _lastObservedUtc.Value))
                {
                    _status = latestEvent.NewStatus;
                    _shieldTimerInSeconds = latestEvent.ShieldTimerInSeconds ?? 0;
                    _lastObservedUtc = latestEvent.OccurredUtc;
                    _lastChangedUtc = latestEvent.OccurredUtc;
                    _canRaiseAtUtc = latestEvent.CanRaiseAtUtc;
                    _raiseDueLogged = false;
                    _raiseTimeIsProvisional =
                        string.Equals(
                            latestEvent.EventType,
                            "cloak_off_announcement",
                            StringComparison.OrdinalIgnoreCase);
                    _observationSource = latestEvent.Source ?? "PersistedEventLog";
                }

                if (state == null && latestEvent == null)
                {
                    Logger.Information("No persisted cloak state found; starting Unknown.");
                    return;
                }

                if (string.Equals(_observationSource, "OrgChat.CloakAnnouncement", StringComparison.Ordinal))
                {
                    // Old chat observations may describe a relayed second city.
                    // Retain history, but obtain fresh live evidence for state.
                    _status = CloakStatus.Unknown;
                    _lastObservedUtc = null;
                    _lastChangedUtc = null;
                    _canRaiseAtUtc = null;
                    _shieldTimerInSeconds = 0;
                    _raiseTimeIsProvisional = false;
                    _observationSource = "LegacyChatNeedsVerification";
                }

                Logger.Information(
                    $"Restored cloak state: {_status}, lastObserved={_lastObservedUtc:O}, " +
                    $"canRaiseAt={_canRaiseAtUtc:O}, provisional={_raiseTimeIsProvisional}, source={_observationSource}.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed loading persisted cloak state: {ex}");
            }
        }

        private CloakEventRecord LoadLatestCloakEvent()
        {
            if (!File.Exists(_eventsPath))
                return null;

            CloakEventRecord latest = null;

            foreach (string line in File.ReadLines(_eventsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    CloakEventRecord candidate =
                        JsonConvert.DeserializeObject<CloakEventRecord>(line);

                    if (candidate != null &&
                        (latest == null || candidate.OccurredUtc > latest.OccurredUtc))
                    {
                        latest = candidate;
                    }
                }
                catch
                {
                    // Keep scanning: a partially written final line must not hide
                    // earlier authoritative events.
                }
            }

            return latest;
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
                CityDwellers.Shared.ServiceEvents.Report("cloak.changed", "info", "Cloak observation recorded.", record);
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
            public string SenderName;
            public object ChannelId;
            public string ChannelName;

            public bool IsOrg => Kind == ReplyKind.Org;
            public bool IsGuest => Kind == ReplyKind.Guest;
            public bool RequiresPrefix => Kind != ReplyKind.Tell;

            public static ReplyTarget ForTell(uint senderId, string senderName = null)
            {
                return new ReplyTarget
                {
                    Kind = ReplyKind.Tell,
                    SenderId = senderId,
                    SenderName = senderName
                };
            }

            public static ReplyTarget ForOrg(
                uint senderId,
                object channelId,
                string channelName,
                string senderName = null)
            {
                return new ReplyTarget
                {
                    Kind = ReplyKind.Org,
                    SenderId = senderId,
                    SenderName = senderName,
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
            public int? TimeoutSeconds;
            public int? Level;
            public int? Index;
            public List<int> Indexes;
            public string Purpose;
            public int? LeaseSeconds;
            public bool Home;
            public bool LogoutAfterHome;
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
            public List<BuddyPositionSnapshot> Positions;
            public string HomeJobId;
            public bool HomeRunning;
            public int HomeAttempted;
            public int HomeStarted;
            public int HomeTerminal;
            public int HomeReached;
            public int HomeStopped;
            public List<string> HomeFailures;
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
