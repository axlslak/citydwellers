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
using AOSharp.Core.IPC;
using CityDwellers.Shared;
using Newtonsoft.Json;

namespace MalisBuffBots
{
    public class IPC : IPCChannelBase
    {
        public IPCBotCacheData BotCache = new IPCBotCacheData();

        protected override int _localDynelId => Client.LocalDynelId;

        public IPC(byte channelId, int pingPongUpdateMs) : base(channelId)
        {
            // Only team coordination remains on Mali IPC. Bot/capability presence,
            // queue state, cast routing and bans are now same-host ManagerMemory.
            RegisterCallback((int)IPCOpcode.UpdateTeamMember, OnReceivedTeamInfoMessage);
            RegisterCallback((int)IPCOpcode.RequestTeamInvite, OnRequestTeamInviteReceived);
            RegisterCallback((int)IPCOpcode.RegisterTeamTracker, OnRegisterTeamTracker);
        }

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
            if (!Client.InPlay || DynelManager.LocalPlayer == null || Main.QueueProcessor == null)
                return;

            foreach (BufferMemorySignal signal in
                ManagerMemory.Current.TakeBufferSignals(Client.CharacterName, 32))
            {
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

                var requester = DynelManager.Players.FirstOrDefault(
                    x => x.Identity.Instance == request.Requester);
                if (requester == null)
                    continue;

                Main.QueueProcessor.LocalEnqueue(requester, request.Entries);
            }

            DrainPublicCommands();
        }

        private void DrainPublicCommands()
        {
            if (!CityBufferBridge.Ready)
                return;

            foreach (BufferPublicCommandRequest request in
                ManagerMemory.Current.PendingBufferPublicCommands(16))
            {
                if (request == null || request.SenderId == 0)
                    continue;

                var requester = DynelManager.Players.FirstOrDefault(
                    x => x.Identity.Instance == request.SenderId);
                if (requester == null)
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
                            success = Main.TryProcessManagerCastRequest(
                                request.Arguments, requester, out message);
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

        // Retained for Mali's existing surface. Presence now comes from ManagerMemory;
        // no peer Ping/Pong traffic is generated.
        public void OnUpdate(object _, double deltaTime) { }

        public void Init()
        {
            BotCache.BroadcastBotInfoMessage();
            BotCache.BroadcastTeamInfoMessage();
            BotCache.BroadcastQueueInfoMessage();
        }

        private void OnRegisterTeamTracker(int arg1, IPCMessage msg)
        {
            TeamTrackerMessage trackMsg = (TeamTrackerMessage)msg;

            BotCache.TeamTracker(trackMsg.Profession, trackMsg.TeamTrackerId);

            if (trackMsg.Profession != (Profession)DynelManager.LocalPlayer.Profession)
                return;

            Main.QueueProcessor.ResetTeamTimer();
            Main.QueueProcessor.TeamTrackerId = trackMsg.TeamTrackerId;
            Team.Invite(new Identity(IdentityType.SimpleChar, trackMsg.TeamTrackerId));
        }

        private void OnRequestTeamInviteReceived(int arg1, IPCMessage ipcMsg)
        {
            RequestTeamInviteMessage teamInviteMsg = (RequestTeamInviteMessage)ipcMsg;

            if (DynelManager.LocalPlayer.Identity.Instance != teamInviteMsg.Bot)
                return;

            if (teamInviteMsg.IsTeamTracker && Main.QueueProcessor.TeamTrackerId == 0)
            {
                Main.QueueProcessor.TeamTrackerId = teamInviteMsg.Requester;
            }

            Team.Invite(new Identity(IdentityType.SimpleChar, teamInviteMsg.Requester));
        }

        private void OnReceivedTeamInfoMessage(int arg1, IPCMessage msg)
        {
            TeamInfoMessage sMsg = (TeamInfoMessage)msg;
            BotCache.UpdateTeamInfo(sMsg.Profession, sMsg.TeamMemberId);
        }
    }

    public class IPCBotCacheData
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

        public void BroadcastBotInfoMessage()
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
                    .SelectMany(entry => (entry.Tags ?? new string[0])
                        .Where(tag => !string.IsNullOrWhiteSpace(tag))
                        .DefaultIfEmpty(string.Empty)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(tag => new BufferAdvertisedBuff
                    {
                        Profession = (int)pair.Key,
                        Name = entry.Name ?? string.Empty,
                        Description = entry.Description ?? string.Empty,
                        Tag = tag,
                        Type = entry.Type.ToString(),
                        Ncu = AdvertisedNcu(entry)
                    })))
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

        public void BroadcastQueueInfoMessage()
        {
            Profession prof = (Profession)DynelManager.LocalPlayer.Profession;
            BuffEntry[] queue = Main.QueueProcessor.Queue.AllEntries.ToArray();

            UpdateQueueInfo(prof, queue);
            ManagerMemory.Current.PublishBufferQueue(
                Client.CharacterName,
                (int)prof,
                JsonConvert.SerializeObject(queue));
        }

        public void BroadcastTeamTrackerMessage(Profession prof, int requester)
        {
            TeamTracker(prof, requester);

            Main.Ipc.Broadcast(new TeamTrackerMessage
            {
                Profession = prof,
                TeamTrackerId = requester,
            });
        }

        public void BroadcastTeamInfoMessage() => BroadcastTeamInfoMessage(Identity.None);

        public void BroadcastTeamInfoMessage(Identity target)
        {
            if (target != Identity.None && Main.Ipc.BotCache.Entries.Any(x => x.Value.Identity == target))
                return;

            Profession prof = (Profession)DynelManager.LocalPlayer.Profession;
            UpdateTeamInfo(prof, target.Instance);

            Main.Ipc.Broadcast(new TeamInfoMessage
            {
                Profession = (Profession)DynelManager.LocalPlayer.Profession,
                TeamMemberId = target.Instance
            });
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
