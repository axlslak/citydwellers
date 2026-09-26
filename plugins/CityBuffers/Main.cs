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
                int[] configuredKnownNanos = ConfiguredKnownNanoOverrides();
                if (configuredKnownNanos.Length != 0)
                    Logger.Information("BUFFER configured known-nano override for " +
                        Client.CharacterName + ": [" + string.Join(",", configuredKnownNanos) + "].");
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

        // AOSharp.Clientless exposes FullCharacter.UploadedNanoIds as SpellList.
        // Some server-owned/social nanos are usable in game but absent from that array.
        internal static int[] ConfiguredKnownNanoOverrides()
        {
            if (SettingsJson?.Data?.KnownNanoOverrides == null ||
                string.IsNullOrWhiteSpace(Client.CharacterName))
                return new int[0];

            foreach (var pair in SettingsJson.Data.KnownNanoOverrides)
                if (string.Equals(pair.Key, Client.CharacterName, StringComparison.OrdinalIgnoreCase))
                    return (pair.Value ?? new int[0]).Where(id => id > 0).Distinct().ToArray();

            return new int[0];
        }

        internal static int[] EffectiveSpellList()
        {
            var known = new HashSet<int>(
                DynelManager.LocalPlayer?.SpellList ?? new int[0]);

            foreach (int id in ConfiguredKnownNanoOverrides())
                known.Add(id);

            return known.ToArray();
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

        internal static bool TryProcessManagerCastRequest(
            string[] nanoTags,
            PlayerChar requester,
            out string message)
        {
            message = null;
            if (requester == null)
            {
                message = "The requester is no longer visible to the buffer fleet.";
                return false;
            }
            if (nanoTags == null || nanoTags.Length == 0)
            {
                message = "Cast requires at least one buff tag.";
                return false;
            }
            if (!BuffsJson.FindByTags(nanoTags, out Dictionary<Profession, List<NanoEntry>> entries))
            {
                message = "No configured buff matches: " +
                    string.Join(" ", nanoTags);
                return false;
            }

            QueueProcessor.RequestBuffs(entries, requester);
            Logger.Information($"Received Manager cast request from '{requester.Name}'");
            return true;
        }

        internal static bool TryProcessManagerRebuffRequest(
            PlayerChar requester,
            out string message)
        {
            message = null;
            if (requester == null)
            {
                message = "The requester is no longer visible to the buffer fleet.";
                return false;
            }

            var requesterBuffs = requester.Buffs;
            if (requesterBuffs.Count == 0)
            {
                message = "No active NCU buffs were visible to rebuff.";
                return false;
            }

            if (!BuffsJson.FindByIds(
                    requesterBuffs.Select(x => x.Id),
                    out Dictionary<Profession, List<NanoEntry>> entries))
            {
                message = "No currently active NCU buffs match the configured buff catalogue.";
                return false;
            }

            QueueProcessor.RequestBuffs(entries, requester);
            Logger.Information($"Received Manager rebuff request from '{requester.Name}'");
            return true;
        }

        internal static bool TryBuildManagerBuffmacro(
            PlayerChar requester,
            out string[] tags,
            out string message)
        {
            tags = null;
            message = null;
            if (requester == null)
            {
                message = "The requester is no longer visible to the buffer fleet.";
                return false;
            }

            var requesterBuffs = requester.Buffs;
            if (requesterBuffs.Count == 0)
            {
                message = "No active NCU buffs were visible for a buff macro.";
                return false;
            }

            List<string> found;
            if (!BuffsJson.FindByIds(requesterBuffs.Select(x => x.Id), out found) ||
                found.Count == 0)
            {
                message = "No currently active NCU buffs match the configured buff catalogue.";
                return false;
            }

            tags = found.ToArray();
            Logger.Information($"Built Manager buffmacro request for '{requester.Name}'");
            return true;
        }

        public void ProcessCastRequest(string[] nanoTags, PlayerChar requester)
        {
            string ignored;
            TryProcessManagerCastRequest(nanoTags, requester, out ignored);
        }

        private void ProcessRebuffRequest(PlayerChar requester)
        {
            string ignored;
            TryProcessManagerRebuffRequest(requester, out ignored);
        }


        private void ProcessBuffmacroRequest(PlayerChar requester)
        {
            string[] tags;
            string ignored;
            TryBuildManagerBuffmacro(requester, out tags, out ignored);
        }

        private void ProcessHelpRequest(PlayerChar requester)
        {
            Logger.Information("Legacy buffer help request ignored; use Apcmanager bufflist.");
        }
    }
}
