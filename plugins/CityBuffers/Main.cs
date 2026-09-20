using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MalisBuffBots
{
    public class Main : ClientlessPluginEntry
    {
        public static IPC Ipc;                              // Used to communicate between different bots
        public static SettingsJson SettingsJson;            // Behavior defaults plus citydwellers.json Buffers.Behavior overrides
        public static BuffsJson BuffsJson;                  // All bot nanos (configurable in JSON/BuffsDb.json)
        public static RebuffJson RebuffJson;                // Rebuff info (configurable in JSON/RebuffInfo.json)
        public static BanJson BanJson;                      // Ban list (use admin commands ban 'name' and unban 'name' to manipulate
        public static QueueProcessor QueueProcessor;        // Queue processing logic
        public static RebuffProcessor RebuffProcessor;      // Rebuff processing logic
        public static CommandProcessor _commandProcessor;   // Command processing logic
        public static UserRank UserRank;

        public override void Init(string pluginDir)
        {
            try
            {
                new StaticDynelDataPreloader().Init(pluginDir);
                Logger.Information("CityBuffers loading Mali buff engine.");

                //Client.SuppressDeserializationErrors();
                Path.Init(pluginDir);
                Logger.Information($"Plugin root dir set to '{Path.PLUGIN_DIR}'");

                SettingsJson = new SettingsJson(Path.SETTINGS_JSON);
                CityBufferBridge.Start();
                Ipc = new IPC(SettingsJson.Data.IPCChannelId, 5000);
                BuffsJson = new BuffsJson(Path.BUFF_JSON);
                RebuffJson = new RebuffJson(Path.REBUFF_JSON);
                BanJson = new BanJson(Path.BAN_JSON);
                UserRank = new UserRank(Path.USERRANK_JSON);
                _commandProcessor = new CommandProcessor();
                QueueProcessor = new QueueProcessor();

                Client.Chat.PrivateGroupInviteMessageReceived += (e, charId) => HandlePrivateGroupInviteMessageReceived(charId);
                Client.OnUpdate += OnUpdate;
            }

            catch (Exception ex)
            {
                CityBufferBridge.Stop();
                Logger.Error("CityBuffers initialization failed: " + ex);
                throw;
            }
        }

        public override void Teardown()
        {
            Client.OnUpdate -= OnUpdate;
            if (QueueProcessor != null) Client.OnUpdate -= QueueProcessor.OnUpdate;
            CityBufferBridge.Stop();
            (Ipc as IDisposable)?.Dispose();
        }

        private void HandlePrivateGroupInviteMessageReceived(PrivateGroupInviteArgs args)
        {
            if (Client.LocalDynelId != SettingsJson.Data.PrivateChannelListenerId)
                return;

            if (args.Requester != SettingsJson.Data.PrivateChannelId)
                return;

            Logger.Debug($"Accepted private group invite from: {args.Requester}");
            PrivateGroupChat.Join(args.Requester);
        }

        private void HandlePrivateGroupMessage(PrivateGroupMsg msg)
        {
            if (!CityBufferBridge.AdmitPublicRequest(msg.SenderId)) return;
            ProcessMessage(new PrivateMessage { SenderId = msg.SenderId, SenderName = msg.SenderName, Message = msg.Message });
        }

        private void HandlePrivateMessage(PrivateMessage msg)
        {
            if (!CityBufferBridge.AdmitPublicRequest(msg.SenderId)) return;
            if (SettingsJson.Data.PrivateChannelMode)
            {
                CityBufferBridge.SendPrivateMessage(msg.SenderId, SettingsJson.Data.PrivateChannelInfoMsg);
                return;
            }

            ProcessMessage(msg);
        }

        private void ProcessMessage(PrivateMessage msg)
        {
            if (!_commandProcessor.TryProcess(msg, out Command command, out string[] commandParts, out int requester))
            {
                return;
            }

            if (!DynelManager.Find(new Identity { Type = IdentityType.SimpleChar, Instance = requester }, out PlayerChar simpleChar))
            {
                Logger.Error($"Unable to locate requester.");
                return;
            }

            if (BanJson.Contains(simpleChar.Name.ToLower()) || simpleChar.OrgName != null && BanJson.Contains(simpleChar.OrgName.ToLower()))
            {
                Logger.Error($"Banned user '{msg.SenderName}' command rejected.");
                return;
            }

            if (SettingsJson.Data.InitConnectionDelay > 0)
            {
                CityBufferBridge.SendPrivateMessage((uint)requester, ScriptTemplate.RequestRejected());
                return;
            }

            // Command logic execution
            switch (command)
            {
                case Command.Cast:
                    ProcessCastRequest(commandParts, simpleChar);
                    break;
                case Command.Rebuff:
                    ProcessRebuffRequest(simpleChar);
                    break;
                case Command.Buffmacro:
                    ProcessBuffmacroRequest(simpleChar);
                    break;
                case Command.Stand:
                    DynelManager.LocalPlayer.MovementComponent.ChangeMovement(MovementAction.LeaveSit);
                    break;
                case Command.Sit:
                    DynelManager.LocalPlayer.MovementComponent.ChangeMovement(MovementAction.SwitchToSit);
                    break;
                case Command.Help:
                    ProcessHelpRequest(simpleChar);
                    break;
                case Command.Debug:
                    ProcessDebugRequest(requester);
                    break;
                case Command.Clear:
                    ProcessClearRequest(requester);
                    break;
                case Command.Ban:
                    ProcessBanRequest(requester, commandParts);
                    break;
                case Command.Unban:
                    ProcessUnbanRequest(requester, commandParts);
                    break;
            }
        }
        private void ProcessBanRequest(int requester, string[] name)
        {
            if (name == null)
            {
                CityBufferBridge.SendPrivateMessage((uint)requester, "Error processing name");
                return;
            }

            string formattedName = string.Join(" ", name).ToLower();

            if (!BanJson.TryAdd(formattedName))
            {
                CityBufferBridge.SendPrivateMessage((uint)requester, ScriptTemplate.AlreadyBanned(formattedName));
                return;
            }

            Ipc.Broadcast(new BanRequestMessage { Name = formattedName });
            CityBufferBridge.SendPrivateMessage((uint)requester, ScriptTemplate.AddToBanlist(formattedName));
        }

        private void ProcessUnbanRequest(int requester, string[] name)
        {
            if (name == null)
            {
                CityBufferBridge.SendPrivateMessage((uint)requester, "Error processing name");
                return;
            }

            string formattedName = string.Join(" ", name).ToLower();

            if (!BanJson.TryRemove(formattedName))
            {
                CityBufferBridge.SendPrivateMessage((uint)requester, ScriptTemplate.CannotRemoveFromBanlist(formattedName));
                return;
            }

            Ipc.Broadcast(new BanRemoveMessage { Name = formattedName });
            CityBufferBridge.SendPrivateMessage((uint)requester, ScriptTemplate.RemoveFromBanlist(formattedName));
        }

        private void ProcessClearRequest(int requester)
        {
            QueueProcessor.ResetBotQueue();
            CityBufferBridge.SendPrivateMessage((uint)requester, "Clearing my queue");
        }

        private void ProcessDebugRequest(int requester)
        {
            string currentQueue = "";

            if (QueueProcessor.Queue.Current != null)
                currentQueue = $"CurrQueue {QueueProcessor.Queue.Current.NanoEntry.Name}";

            CityBufferBridge.SendPrivateMessage((uint)requester, $"{DynelManager.LocalPlayer.Name}\n " +
                $"teamTrackerId: {QueueProcessor.TeamTrackerId}\n" +
                $"QueueData.Entries.Count: {Ipc.BotCache.Entries.Count}\n " +
                $"IPCBotCache.BotData.Count: {Ipc.BotCache.Entries.Count}\n" +
                $"QueueData.Entries.Values.All: {Ipc.BotCache.Entries.Values.All(x => x.Queue.Count() == 0)}\n" +
                $"Team.IsInTeam: {Team.IsInTeam}\n " +
                $"QueueData.IsTeamQueueEmpty(trackId): {Ipc.BotCache.IsTeamQueueEmpty(QueueProcessor.TeamTrackerId)}\n " +
                $"ldb:{QueueProcessor.N3MessageProcessor.LastLdbMessage}\n" +
                $"{currentQueue}\n ");
        }

        private void OnUpdate(object sender, double delta)
        {
            try
            {
                if (!Client.InPlay || DynelManager.LocalPlayer == null) return;
                if ((SettingsJson.Data.InitConnectionDelay -= delta) < 0)
                {
                    if (UserRank.MeetsRank(Rank.Warper, DynelManager.LocalPlayer.Name))
                        return;

                    DynelManager.LocalPlayer.MovementComponent.ChangeMovement(MovementAction.LeaveSit);
                    Ipc.Init();
                    RebuffProcessor = new RebuffProcessor(RebuffJson);
                    CityBufferBridge.Ready = true;
                    // Client.OnUpdate += Ipc.OnUpdate; TODO

                    if (SettingsJson.Data.PrivateChannelMode && SettingsJson.Data.PrivateChannelListenerId == Client.LocalDynelId)
                        Client.Chat.PrivateGroupMessageReceived += (e, msg) => HandlePrivateGroupMessage(msg);

                    Client.Chat.PrivateMessageReceived += (e, msg) => HandlePrivateMessage(msg);
                    Client.OnUpdate += QueueProcessor.OnUpdate;
                    Client.OnUpdate -= OnUpdate;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                Logger.Error("MainOnUpdate");
            }
        }

        public void ProcessCastRequest(string[] nanoTags, PlayerChar requester)
        {
            if (!BuffsJson.FindByTags(nanoTags, out Dictionary<Profession, List<NanoEntry>> entries))
                return;

            QueueProcessor.RequestBuffs(entries, requester);

            Logger.Information($"Received cast request from '{requester.Name}'");
        }

        private void ProcessRebuffRequest(PlayerChar requester)
        {
            var requesterBuffs = requester.Buffs;

            if (requesterBuffs.Count == 0)
                return;

            if (!BuffsJson.FindByIds(requesterBuffs.Select(x => x.Id), out Dictionary<Profession, List<NanoEntry>> entries))
                return;

            QueueProcessor.RequestBuffs(entries, requester);

            Logger.Information($"Received rebuff request from '{requester.Name}'");
        }


        private void ProcessBuffmacroRequest(PlayerChar requester)
        {
            var requesterBuffs = requester.Buffs;

            if (requesterBuffs.Count == 0)
                return;

            List<string> buffsByTag = new List<string>();

            if (!BuffsJson.FindByIds(requesterBuffs.Select(x=>x.Id), out List<string> tags))
                return;

            CityBufferBridge.SendPrivateMessage((uint)requester.Identity.Instance, ScriptTemplate.Buffmacro(DynelManager.LocalPlayer.Name, tags));
        }

        private void ProcessHelpRequest(PlayerChar requester)
        {
            CityBufferBridge.SendPrivateMessage((uint)requester.Identity.Instance, ScriptTemplate.RetrievingBuffs());
            CityBufferBridge.SendPrivateMessage((uint)requester.Identity.Instance, ScriptTemplate.HelpMenu(), false);
        }
    }
}
