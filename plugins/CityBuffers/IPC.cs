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

namespace MalisBuffBots
{
    public class IPC : IPCChannelBase
    {
        public IPCBotCacheData BotCache = new IPCBotCacheData();

        private AutoResetInterval _updateInterval;

        protected override int _localDynelId => Client.LocalDynelId;

        public IPC(byte channelId, int pingPongUpdateMs) : base(channelId)
        {
           // _updateInterval = new AutoResetInterval(updateIntervalMs);
            _updateInterval = new AutoResetInterval(pingPongUpdateMs);

            RegisterCallback((int)IPCOpcode.CastRequest, OnCastRequestReceived);
            RegisterCallback((int)IPCOpcode.ReceiveQueueInfo, OnReceiveQueueInfoReceived);
            RegisterCallback((int)IPCOpcode.UpdateBotInfo, OnReceivedBotInfoMessage);
            RegisterCallback((int)IPCOpcode.UpdateTeamMember, OnReceivedTeamInfoMessage);
            RegisterCallback((int)IPCOpcode.RequestTeamInvite, OnRequestTeamInviteReceived);
            RegisterCallback((int)IPCOpcode.BanRequest, OnBanRequestReceived);
            RegisterCallback((int)IPCOpcode.BanRemove, OnBanRemoveReceived);
            RegisterCallback((int)IPCOpcode.RegisterTeamTracker, OnRegisterTeamTracker);
            RegisterCallback((int)IPCOpcode.Ping, OnPingMessageReceived);
            RegisterCallback((int)IPCOpcode.Pong, OnPongMessageReceived);
        }

        private void OnBanRemoveReceived(int arg1, IPCMessage msg)
        {
            BanRemoveMessage banMsg = (BanRemoveMessage)msg;
            Main.BanJson.TryRemove(banMsg.Name);
        }

        private void OnBanRequestReceived(int arg1, IPCMessage msg)
        {
            BanRequestMessage banMsg = (BanRequestMessage)msg;
            Main.BanJson.TryAdd(banMsg.Name);
        }

        public void OnUpdate(object _, double deltaTime)
        {
            if (!_updateInterval.Elapsed)
                return;

            Main.Ipc.Broadcast(new PingMessage { Requester = Client.LocalDynelId });
        }

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
            CityBufferBridge.SendPrivateMessage((uint)trackMsg.TeamTrackerId, ScriptTemplate.TeamInvite());
        }

        private void OnPongMessageReceived(int arg1, IPCMessage ipcMsg)
        {
            PongMessage pongMsg = (PongMessage)ipcMsg;

            if (pongMsg.Requester != Client.LocalDynelId)
                return;

            BotCache.PingPong(pongMsg.Receiver);
        }

        private void OnPingMessageReceived(int arg1, IPCMessage ipcMsg)
        {
            Main.Ipc.Broadcast(new PongMessage { Requester = ((PingMessage)ipcMsg).Requester, Receiver = (Profession)DynelManager.LocalPlayer.Profession });
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

        private void OnCastRequestReceived(int sender, IPCMessage msg)
        {
            CastRequestMessage cMsg = (CastRequestMessage)msg;

            if (DynelManager.LocalPlayer == null)
                return;

            if ((Profession)DynelManager.LocalPlayer.Profession != cMsg.Caster)
                return;

            var requester = DynelManager.Players.FirstOrDefault(x => x.Identity.Instance == cMsg.Requester);

            if (requester == null)
                return;

            Main.QueueProcessor.LocalEnqueue(requester, cMsg.Entries);
            BotCache.BroadcastQueueInfoMessage();
        }

        private void OnReceiveQueueInfoReceived(int sender, IPCMessage msg)
        {
            QueueInfoMessage qMsg = (QueueInfoMessage)msg;
            BotCache.UpdateQueueInfo(qMsg.Profession, qMsg.Entries);
        }

        private void OnReceivedBotInfoMessage(int sender, IPCMessage msg)
        {
            BotInfoMessage sMsg = (BotInfoMessage)msg;
            BotCache.UpdateBotInfo(sMsg.Profession, sMsg.Identity, sMsg.SpellData);
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
                _entries[profession].Identity = new Identity(
                    (IdentityType)snapshot.IdentityType, snapshot.IdentityInstance);
                _entries[profession].SpellData = snapshot.SpellData ?? new int[0];
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
            int[] spellList = DynelManager.LocalPlayer.SpellList;

            UpdateBotInfo(prof, identity, spellList);
            ManagerMemory.Current.PublishBufferBotInfo(new BufferBotInfo
            {
                Character = Client.CharacterName,
                Profession = (int)prof,
                IdentityType = (int)identity.Type,
                IdentityInstance = identity.Instance,
                SpellData = spellList
            });
        }

        public void BroadcastQueueInfoMessage()
        {
            Profession prof = (Profession)DynelManager.LocalPlayer.Profession;
            BuffEntry[] queue = Main.QueueProcessor.Queue.AllEntries.ToArray();

            UpdateQueueInfo(prof, queue);

            Main.Ipc.Broadcast(new QueueInfoMessage
            {
                Profession = prof,
                Entries  = queue
            });
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

        public void PingPong(Profession prof)
        {
            TryAddLocal(prof);
            _entries[prof].LastUpdateInTicks = DateTime.Now.Ticks;
        }

        private void TryAddLocal(Profession prof)
        {
            if (!_entries.ContainsKey(prof))
                _entries.Add(prof, new BotData
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
