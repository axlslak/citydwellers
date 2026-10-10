using System;
using System.IO;
using System.Linq;
using AOSharp.Common.GameData;
using System.Collections.Generic;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.GameData;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;

namespace MalisBuffBots
{
    public class BufferCoordinator
    {
        public BufferBotCache BotCache = new BufferBotCache();

        private sealed class MemoryCastRequest
        {
            public int Caster;
            public int Requester;
            public NanoEntry[] Entries;
        }

        public bool SendCastRequest(Profession caster, int requester, IEnumerable<NanoEntry> entries)
        {
            var request = new MemoryCastRequest
            {
                Caster = (int)caster,
                Requester = requester,
                Entries = (entries ?? Enumerable.Empty<NanoEntry>()).ToArray()
            };
            return ManagerMemory.Current.EnqueueBufferSignalForProfession(
                (int)caster, "cast", JsonConvert.SerializeObject(request));
        }

        public void DrainMemorySignals()
        {
            if (!Client.InPlay || !CityBufferBridge.Ready || DynelManager.LocalPlayer == null || Main.QueueProcessor == null)
                return;

            foreach (BufferMemorySignal signal in
                ManagerMemory.Current.TakeBufferSignals(Client.CharacterName, 32))
            {
                if (Main.PaidPilot) continue;
                if (signal.Kind == "observer-refresh")
                {
                    ReceiveObservedRefresh(signal);
                    continue;
                }
                if (!string.Equals(signal.Kind, "cast", StringComparison.Ordinal))
                    continue;

                MemoryCastRequest request;
                try { request = JsonConvert.DeserializeObject<MemoryCastRequest>(signal.Payload ?? "null"); }
                catch (JsonException) { continue; }
                if (request == null ||
                    request.Caster != (int)DynelManager.LocalPlayer.Profession ||
                    request.Requester == 0 ||
                    request.Entries == null || request.Entries.Length == 0)
                    continue;

                if (!Main.QueueProcessor.DeferReceivedRequest((Profession)request.Caster,
                    request.Requester, request.Entries))
                {
                    if (signal.CreatedUtc >= DateTime.UtcNow.AddMinutes(-2) &&
                        ManagerMemory.Current.ReturnBufferSignal(Client.CharacterName, signal)) continue;
                    Logger.Warning("Buffer handoff queue expired/full for requester " + request.Requester + ".");
                    CityBufferBridge.Diagnostic(request.Requester, "Buffer queue remained full; please retry your buff request.");
                }
            }

            DrainPublicCommands();
        }

        private void DrainPublicCommands()
        {
            if (!CityBufferBridge.Ready)
                return;

            foreach (BufferPublicCommandRequest request in
                ManagerMemory.Current.PendingBufferPublicCommands(128))
            {
                if (request == null || request.SenderId == 0)
                    continue;
                if (Main.PaidPilot != !string.IsNullOrWhiteSpace(request.TargetCharacter)) continue;

                var requester = DynelManager.Players.FirstOrDefault(
                    x => x.Identity.Instance == request.SenderId);
                if (requester == null)
                    continue;

                if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession))
                {
                    int manager = ManagerMemory.Current.PaidFixerManager(Client.CharacterName);
                    if (!Team.IsInTeam || manager == 0 ||
                        !Team.Members.Any(m => m.Identity.Instance == manager) ||
                        !Team.Members.Any(m => m.Identity == requester.Identity)) continue;
                }

                if (!CanClaimPublicCommand(request))
                    continue;

                if (!ManagerMemory.Current.TryClaimBufferPublicCommand(
                        request.Id, Client.CharacterName))
                    continue;

                bool success = false;
                string message = null;
                string[] tags = null;

                try
                {
                    switch ((request.Command ?? string.Empty).ToLowerInvariant())
                    {
                        case "cast":
                            success = request.ObservedNanoId > 0
                                ? TryProcessObservedPaidRequest(request, requester, out message)
                                : Main.TryProcessManagerCastRequest(request.Arguments, requester, out message);
                            break;

                        case "rebuff":
                            success = Main.TryProcessManagerRebuffRequest(
                                requester, out message);
                            break;

                        case "buffmacro":
                            success = Main.TryBuildManagerBuffmacro(
                                requester, out tags, out message);
                            break;

                        default:
                            message = "Unsupported buffer command '" +
                                (request.Command ?? "<missing>") + "'.";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    message = ex.GetType().Name + ": " + ex.Message;
                }

                if (request.ObservedNanoId > 0 && !success)
                    CityBufferBridge.Diagnostic(requester.Identity.Instance,
                        "Automatic buff request: " + (message ?? "could not queue the buff."));

                ManagerMemory.Current.CompleteBufferPublicCommand(
                    new BufferPublicCommandOutcome
                    {
                        Id = request.Id,
                        Success = success,
                        Message = message,
                        Tags = tags,
                        ClaimedBy = Client.CharacterName,
                        CompletedUtc = DateTime.UtcNow
                    });

                Logger.Information(
                    $"Manager buffer command {request.Id} {request.Command} " +
                    $"for {request.SenderName ?? request.SenderId.ToString()} " +
                    $"claimed by {Client.CharacterName}; success={success}.");
            }
        }

        private void ReceiveObservedRefresh(BufferMemorySignal signal)
        {
            var parts = (signal.Payload ?? "").Split(':');
            int requester, nanoId;
            if (parts.Length != 2 || !int.TryParse(parts[0], out requester) ||
                !int.TryParse(parts[1], out nanoId)) return;
            var memory = ManagerMemory.Current;
            if (!memory.OwnsObservedBuffRefresh(Client.CharacterName, requester, nanoId, signal.Id)) return;
            bool accepted = false;
            try
            {
                if (!memory.ObservedBuffNeedsRefresh(requester, nanoId)) return;
                var player = DynelManager.Players.FirstOrDefault(p => p.Identity.Instance == requester);
                if (player == null || !Main.EffectiveSpellList().Contains(nanoId)) return;
                var entry = Main.BuffsJson.Entries.Values.SelectMany(entries => entries)
                    .FirstOrDefault(e => e.Type == CastType.Single && e.ContainsId(nanoId));
                if (entry == null) return;
                // A manual request already owns this nano on this caster. Let it
                // finish; Manager will see delivery or reconsider on the next scan.
                if (Main.QueueProcessor.Queue.AllEntries.Any(e => e.Requester == player.Identity &&
                    e.NanoEntry.ContainsId(nanoId))) return;
                var exact = JsonConvert.DeserializeObject<NanoEntry>(JsonConvert.SerializeObject(entry));
                exact.LevelToId = exact.LevelToId.Where(n => n.Id == nanoId).ToArray();
                exact.ObserverRefreshUntilUtc = DateTime.UtcNow.AddMinutes(2);
                exact.ObserverAssignmentId = signal.Id;
                string error;
                accepted = Main.QueueProcessor.Queue.TryEnqueue(
                    new BuffEntry { Requester = player.Identity, NanoEntry = exact }, out error);
                Main.Coordination.BotCache.PublishQueueInfo();
                if (!accepted) CityBufferBridge.Diagnostic(requester,
                    "Observer assignment not admitted for nano=" + nanoId + ": " + error);
            }
            finally
            {
                if (!accepted) memory.ReleaseObservedBuffRefresh(Client.CharacterName, requester, nanoId, signal.Id);
            }
        }

        private static bool TryProcessObservedPaidRequest(
            BufferPublicCommandRequest request, PlayerChar requester, out string message)
        {
            message = null;
            if (!Main.PaidPilot || (int)DynelManager.LocalPlayer.Profession != Main.PaidProfession ||
                !PaidBufferCatalogue.ForProfession(Main.PaidProfession)
                .Any(n => n.MatchesEffect(request.ObservedNanoId)))
            { message = "This provider does not offer the observed nano."; return false; }
            if (!ManagerMemory.Current.ObservedBuffNeedsRefresh(requester.Identity.Instance, request.ObservedNanoId))
            { message = "The observed buff no longer needs refreshing."; return true; }
            int castId = PaidBufferCatalogue.FindEffect(request.ObservedNanoId).Id;
            var source = Main.BuffsJson.Entries.Values.SelectMany(entries => entries)
                .FirstOrDefault(entry => entry != null && entry.ContainsId(castId));
            if (source == null)
            { message = "Observed nano " + request.ObservedNanoId + " has no casting definition."; return false; }
            var exact = JsonConvert.DeserializeObject<NanoEntry>(JsonConvert.SerializeObject(source));
            exact.LevelToId = exact.LevelToId.Where(n => n.Id == castId).ToArray();
            exact.ObservedNanoId = request.ObservedNanoId;
            exact.ObserverRefreshUntilUtc = DateTime.UtcNow.AddMinutes(2);
            return Main.QueueProcessor.RequestPaidBuffs(new[] { exact }, requester, out message);
        }

        private static bool CanClaimPublicCommand(BufferPublicCommandRequest request)
        {
            if (Main.PaidPilot) return true; // Claim to return concrete upload/level errors as well.
            string command = (request.Command ?? string.Empty).ToLowerInvariant();
            if (command != "cast")
                return true;

            Dictionary<Profession, List<NanoEntry>> entries;
            if (request.Arguments == null ||
                !Main.BuffsJson.FindByTags(request.Arguments, out entries))
                return true;

            int[] knownNanos = Main.EffectiveSpellList();
            Profession localProfession = (Profession)DynelManager.LocalPlayer.Profession;
            return entries
                .Where(pair => pair.Key == Profession.Generic || pair.Key == localProfession)
                .SelectMany(pair => pair.Value ?? new List<NanoEntry>())
                .Any(entry => entry != null && knownNanos.Any(entry.ContainsId));
        }

        public void Init()
        {
            BotCache.PublishBotInfo();
            BotCache.PublishTeamInfo();
            BotCache.PublishQueueInfo();
        }

    }

    public class BufferBotCache
    {
        private readonly Dictionary<Profession, BotData> _entries = new Dictionary<Profession, BotData>();
        public Dictionary<Profession, BotData> Entries
        {
            get
            {
                RefreshBotInfoFromMemory();
                return _entries;
            }
        }

        private void RefreshBotInfoFromMemory()
        {
            List<BufferBotInfo> snapshots;
            try { snapshots = ManagerMemory.Current.BufferBotInfos(); }
            catch { return; }

            var present = new HashSet<Profession>();
            foreach (BufferBotInfo snapshot in snapshots)
            {
                if (ManagerMemory.Current.IsPaidBuffer(snapshot.Character)) continue;
                Profession profession = (Profession)snapshot.Profession;
                present.Add(profession);
                TryAddLocal(profession);
                bool fresh = snapshot.InPlay && snapshot.Ready &&
                    snapshot.ObservedUtc >= DateTime.UtcNow.AddSeconds(-5);
                _entries[profession].Identity = fresh
                    ? new Identity((IdentityType)snapshot.IdentityType, snapshot.IdentityInstance)
                    : Identity.None;
                _entries[profession].SpellData = fresh ? snapshot.SpellData ?? new int[0] : new int[0];
                _entries[profession].LastUpdateInTicks = fresh ? snapshot.ObservedUtc.Ticks : 0;
                _entries[profession].TeamMemberId = fresh ? snapshot.TeamMemberId : 0;
                _entries[profession].TeamTrackerId = fresh ? snapshot.TeamTrackerId : 0;
                if (fresh && !string.IsNullOrWhiteSpace(snapshot.QueueJson))
                {
                    try
                    {
                        _entries[profession].Queue =
                            JsonConvert.DeserializeObject<BuffEntry[]>(snapshot.QueueJson) ??
                            new BuffEntry[0];
                    }
                    catch (JsonException)
                    {
                        _entries[profession].Queue = new BuffEntry[0];
                    }
                }
                else
                {
                    _entries[profession].Queue = new BuffEntry[0];
                }
            }

            foreach (Profession profession in _entries.Keys.Where(x => !present.Contains(x)).ToArray())
            {
                _entries[profession].Identity = Identity.None;
                _entries[profession].SpellData = new int[0];
            }
        }

        public bool ContainsKey(Profession prof)
        {
            RefreshBotInfoFromMemory();
            return _entries.ContainsKey(prof) && _entries[prof].Identity != Identity.None;
        }

        public bool ContainsIdentity(int identityInstance)
        {
            RefreshBotInfoFromMemory();
            return _entries.Values.Any(x => x.Identity.Instance == identityInstance);
        }

        public void PublishBotInfo()
        {
            Profession prof = (Profession)DynelManager.LocalPlayer.Profession;
            Identity identity = DynelManager.LocalPlayer.Identity;
            int[] spellList = Main.EffectiveSpellList();

            UpdateBotInfo(prof, identity, spellList);
            ManagerMemory.Current.PublishBufferBotInfo(new BufferBotInfo
            {
                Character = Client.CharacterName,
                Profession = (int)prof,
                IdentityType = (int)identity.Type,
                IdentityInstance = identity.Instance,
                SpellData = spellList,
                ObservedUtc = DateTime.UtcNow,
                InPlay = Client.InPlay,
                Ready = CityBufferBridge.Ready,
                AdvertisedBuffs = BuildAdvertisedBuffs(spellList)
            });
        }

        internal static BufferAdvertisedBuff[] BuildAdvertisedBuffs(int[] spellList)
        {
            var known = new HashSet<int>(spellList ?? new int[0]);
            return Main.BuffsJson.Entries
                .SelectMany(pair => (pair.Value ?? new List<NanoEntry>())
                    .Where(entry => entry != null && entry.LevelToId != null &&
                        entry.LevelToId.Any(level => known.Contains(level.Id)))
                    .Select(entry => new BufferAdvertisedBuff
                    {
                        Profession = (int)pair.Key,
                        IsGeneric = pair.Key == Profession.Generic,
                        Name = entry.Name ?? string.Empty,
                        Description = entry.Description ?? string.Empty,
                        Tag = (entry.Tags ?? new string[0]).FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag)) ?? string.Empty,
                        Type = entry.Type.ToString(),
                        NanoIds = entry.LevelToId.Where(level => known.Contains(level.Id))
                            .Select(level => level.Id).Distinct().ToArray(),
                        Ncu = AdvertisedNcu(entry)
                    }))
                .OrderBy(entry => entry.Profession)
                .ThenBy(entry => entry.Tag, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int AdvertisedNcu(NanoEntry entry)
        {
            int nanoId = 0;
            if (entry.RemoveNanoIdUponCast != null && entry.RemoveNanoIdUponCast.Length > 0 &&
                entry.RemoveNanoIdUponCast[0] != 0)
                nanoId = entry.RemoveNanoIdUponCast[0];
            else if (entry.LevelToId != null && entry.LevelToId.Length > 0)
                nanoId = entry.LevelToId[0].Id;

            NanoItem nano;
            return nanoId != 0 && ItemData.Find(nanoId, out nano) ? nano.NCU : 0;
        }

        public void PublishQueueInfo()
        {
            Profession prof = (Profession)DynelManager.LocalPlayer.Profession;
            BuffEntry[] queue = Main.QueueProcessor.Queue.AllEntries.ToArray();

            UpdateQueueInfo(prof, queue);
            ManagerMemory.Current.PublishBufferQueue(
                Client.CharacterName,
                (int)prof,
                JsonConvert.SerializeObject(queue));
        }

        public void PublishTeamTracker(Profession prof, int requester)
        {
            if (Main.PaidPilot) return;
            TeamTracker(prof, requester);

            ManagerMemory.Current.PublishBufferTeamTracker(Client.CharacterName, requester);
        }

        public void PublishTeamInfo() => PublishTeamInfo(Identity.None);

        public void PublishTeamInfo(Identity target)
        {
            if (Main.PaidPilot) return;
            if (target != Identity.None && Main.Coordination.BotCache.Entries.Any(x => x.Value.Identity == target))
                return;

            Profession prof = (Profession)DynelManager.LocalPlayer.Profession;
            UpdateTeamInfo(prof, target.Instance);

            ManagerMemory.Current.PublishBufferTeamMember(Client.CharacterName, target.Instance);
        }

        public void TeamTracker(Profession prof, int trackId)
        {
            TryAddLocal(prof);
            _entries[prof].TeamTrackerId = trackId;
        }

        private void TryAddLocal(Profession prof)
        {
            if (!_entries.ContainsKey(prof))
                _entries.Add(prof, new BotData()
                {
                    Identity = Identity.None,
                    SpellData = new int[0],
                    Queue = new BuffEntry[0]
                });
        }

        public void TryAdd(Profession prof)
        {
            RefreshBotInfoFromMemory();
            TryAddLocal(prof);
        }

        public bool ContainsNanoEntry(Profession prof, NanoEntry nanoEntry)
        {
            RefreshBotInfoFromMemory();
            if (!_entries.TryGetValue(prof, out BotData botCache))
                return false;

            return botCache.SpellData.Any(s => nanoEntry.ContainsId(s));
        }

        public Dictionary<Profession, BotData> OutOfTeamBots() { RefreshBotInfoFromMemory(); return _entries.Count == 0 ? new Dictionary<Profession, BotData>() : _entries.Where(x => x.Value.TeamMemberId == 0).ToDictionary(kv => kv.Key, kv => kv.Value); }

        public Dictionary<Profession, BotData> NonTeamTrackerBots() { RefreshBotInfoFromMemory(); return _entries.Count == 0 ? new Dictionary<Profession, BotData>() : _entries.Where(x => x.Value.TeamTrackerId == 0).ToDictionary(kv => kv.Key, kv => kv.Value); }


        public bool IsTeamQueueEmpty(int charId)
        {
            try
            {
                return Entries.Values.SelectMany(x => x.Queue ?? new BuffEntry[0]).Where(x => x != null && x.NanoEntry != null && x.NanoEntry.Type == CastType.Team && x.Requester.Instance == charId).ToList().Count == 0;
            }
            catch
            {
                return true;
            }
        }

        public IOrderedEnumerable<KeyValuePair<Profession, BotData>> OrderByQueueEntries() => Entries.OrderBy(x => (x.Value.Queue ?? new BuffEntry[0]).Count());

        internal void UpdateBotInfo(Profession profession, Identity identity, int[] spellData)
        {
            TryAddLocal(profession);
            _entries[profession].Identity = identity;
            _entries[profession].SpellData = spellData ?? new int[0];
        }

        internal void UpdateTeamInfo(Profession profession, int teamMemberId)
        {
            TryAddLocal(profession);
            _entries[profession].TeamMemberId = teamMemberId;
        }

        internal void UpdateQueueInfo(Profession profession, BuffEntry[] entries)
        {
            TryAddLocal(profession);
            _entries[profession].Queue = entries ?? new BuffEntry[0];
        }
    }
}
