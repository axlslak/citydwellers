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
        public static BufferCoordinator Coordination;       // Same-process ManagerMemory coordination
        public static SettingsJson SettingsJson;            // Behavior defaults plus citydwellers.json Buffers.Behavior overrides
        public static BuffsJson BuffsJson;                  // Audited capabilities plus compatibility aliases
        internal static string[] PreparationBuffs = new string[0]; // This character's Buffers entry
        public static QueueProcessor QueueProcessor;        // Queue processing logic
        public static RebuffProcessor RebuffProcessor;      // Rebuff processing logic
        public static UserRank UserRank;
        internal static bool PaidPilot;
        internal static int PaidProfession;

        public override void Init(string pluginDir)
        {
            try
            {
                CityDwellers.Shared.BuildIdentity.Register();
                Logger.Information("BUILD " + CityDwellers.Shared.BuildIdentity.Label +
                    " | revision=" + CityDwellers.Shared.BuildIdentity.Revision);
                new StaticDynelDataPreloader().Init(pluginDir);
                Logger.Information("CityBuffers loading Mali buff engine.");

                //Client.SuppressDeserializationErrors();
                Path.Init(pluginDir);
                Logger.Information($"Plugin root dir set to '{Path.PLUGIN_DIR}'");

                SettingsJson = new SettingsJson(Path.SETTINGS_JSON);
                PaidPilot = ManagerMemory.Current.IsPaidBuffer(Client.CharacterName);
                PaidProfession = ManagerMemory.Current.ReadPaidBuffer(Client.CharacterName)?.Profession ?? 0;
                var accounts = BufferSettings.Read();
                var account = accounts.Froobs.Concat(accounts.Paid).FirstOrDefault(a => a != null &&
                    string.Equals(a.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase));
                PreparationBuffs = (account?.Buffs ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToLowerInvariant())
                    .Distinct().ToArray();
                int[] configuredKnownNanos = ConfiguredKnownNanoOverrides();
                if (configuredKnownNanos.Length != 0)
                    Logger.Information("BUFFER configured known-nano override for " +
                        Client.CharacterName + ": [" + string.Join(",", configuredKnownNanos) + "].");
                CityBufferBridge.Start();
                Coordination = new BufferCoordinator();
                BuffsJson = new BuffsJson(Path.BUFF_JSON);
                NanoCatalogue.RestoreDefinitions();
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
            RebuffProcessor?.Stop();
            CityBufferBridge.Stop();
            Coordination = null;
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
            => CityBufferBridge.CatalogueSpellList ?? UploadedSpellList();

        internal static int[] UploadedSpellList()
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
                // A paid session must not burn thirty seconds of its shared-account
                // turn on a warmup timer. InPlay and catalogue readiness are checked
                // directly; a missing spell list is retried by InitializeCatalogue.
                if (PaidPilot || (SettingsJson.Data.InitConnectionDelay -= delta) < 0)
                {
                    if (!PaidPilot && UserRank.MeetsRank(Rank.Warper, DynelManager.LocalPlayer.Name))
                        return;

                    DynelManager.LocalPlayer.MovementComponent.ChangeMovement(MovementAction.LeaveSit);
                    if (!CityBufferBridge.InitializeCatalogue()) return;
                    Coordination.Init();
                    if (PreparationBuffs.Length != 0) RebuffProcessor = new RebuffProcessor(PreparationBuffs);
                    CityBufferBridge.Ready = true;

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
            var unknown = nanoTags.Where(tag => !BuffsJson.FindByTags(new[] { tag }, out var ignored)).ToArray();
            string unknownMessage = unknown.Length == 0 ? null :
                "Unknown buff tag(s): " + string.Join(", ", unknown) + ".";
            if (!BuffsJson.FindByTags(nanoTags, out Dictionary<Profession, List<NanoEntry>> entries))
            {
                message = "No configured buff matches: " +
                    string.Join(" ", nanoTags);
                return false;
            }

            // Numeric selections request the exact nano rather than another tier
            // sharing the same Mali tag. Ordinary tag macros keep their old selection.
            int[] requestedIds = nanoTags.Select(tag => int.TryParse(tag, out int id) ? id : 0)
                .Where(id => id > 0).ToArray();
            entries = entries.ToDictionary(pair => pair.Key, pair => pair.Value.Select(entry =>
            {
                if (!requestedIds.Any(entry.ContainsId) || nanoTags.Any(tag => entry.Tags.Contains(tag))) return entry;
                var exact = Newtonsoft.Json.JsonConvert.DeserializeObject<NanoEntry>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(entry));
                exact.LevelToId = exact.LevelToId.Where(n => requestedIds.Contains(n.Id)).ToArray();
                return exact;
            }).Where(entry => entry.LevelToId.Length != 0).ToList());

            if (PaidPilot)
            {
                if ((int)DynelManager.LocalPlayer.Profession != PaidProfession)
                {
                    message = "The paid buffer's profession does not match its configured Profession. Check Buffers.Paid.";
                    return false;
                }
                var offered = PaidBufferCatalogue.ForProfession(PaidProfession)
                    .Where(n => ManagerMemory.Current.PaidBufferOffers(Client.CharacterName, n.Id)).Select(n => n.Id).ToArray();
                // Mali's ncu tag covers both the froob NCU line and paid FSC.
                // A paid worker admits only the nanos in its own advertised profile.
                return QueueProcessor.RequestPaidBuffs(entries.SelectMany(p => p.Value)
                    .Where(e => e.LevelToId.Any(n => offered.Contains(n.Id)))
                    .Distinct().ToArray(), requester, out message);
            }
            // Paid-only definitions share some Mali tags with free alternatives.
            // Manager has already routed paid work; a free worker must not also
            // retain an impossible paid cast for the same tag.
            var freeKnown = new HashSet<int>(Coordination.BotCache.Entries.Values.SelectMany(b => b.SpellData ?? new int[0]));
            entries = entries.ToDictionary(pair => pair.Key, pair => pair.Value
                .Where(e => e.LevelToId.Any(n => freeKnown.Contains(n.Id))).ToList());
            entries = entries.Where(pair => pair.Value.Count != 0).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (entries.Count == 0)
            { message = "No available buffer offers " + string.Join(", ", nanoTags) + "."; return false; }
            if (!QueueProcessor.RequestBuffs(entries, requester))
            { message = "The buffer readiness queue is full; please retry shortly."; return false; }
            message = unknownMessage;
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
                    requesterBuffs.Select(x => x.Id).Where(id => !BufferRefreshPolicy.IsRequestOnlyNano(id)),
                    out Dictionary<Profession, List<NanoEntry>> entries))
            {
                message = "No currently active NCU buffs match the configured buff catalogue.";
                return false;
            }

            if (!QueueProcessor.RequestBuffs(entries, requester))
            { message = "The buffer readiness queue is full; please retry shortly."; return false; }
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
            if (!BuffsJson.FindByIds(requesterBuffs.Select(x => x.Id).Where(id => !BufferRefreshPolicy.IsRequestOnlyNano(id)), out found) ||
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
            if (!string.IsNullOrWhiteSpace(ignored))
                CityBufferBridge.RequestResult(requester.Identity.Instance, ignored);
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
