using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using System;
using System.Collections.Generic;
using System.Linq;
using CityDwellers.Shared;

namespace MalisBuffBots
{
    public class Main : ClientlessPluginEntry
    {
        public static IPC Ipc;                              // Team IPC compatibility plus ManagerMemory buffer coordination
        public static SettingsJson SettingsJson;            // Behavior defaults plus citydwellers.json Buffers.Behavior overrides
        public static BuffsJson BuffsJson;                  // All bot nanos (configurable in JSON/BuffsDb.json)
        public static RebuffJson RebuffJson;                // Rebuff info (configurable in JSON/RebuffInfo.json)
        public static QueueProcessor QueueProcessor;        // Queue processing logic
        public static RebuffProcessor RebuffProcessor;      // Rebuff processing logic
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
                UserRank = new UserRank();
                QueueProcessor = new QueueProcessor();

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

        // Direct buffer tells/private-group commands are retired. Apcmanager owns the
        // public conversation surface; this plugin keeps only casting/team machinery.

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
