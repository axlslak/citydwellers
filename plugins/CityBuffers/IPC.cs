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
        public Dictionary<Profession, BotData> Entries = new Dictionary<Profession, BotData>();

        public bool ContainsKey(Profession prof) => Entries.ContainsKey(prof);

        public bool ContainsIdentity(int identityInstance) => Entries.Count() > 0 && Entries.Values.Any(x => x.Identity.Instance == identityInstance);

        public void BroadcastBotInfoMessage()
        {
            Profession prof = (Profession)DynelManager.LocalPlayer.Profession;
            Identity identity = DynelManager.LocalPlayer.Identity;
            int[] spellList = DynelManager.LocalPlayer.SpellList;

            UpdateBotInfo(prof, identity, spellList);

            Main.Ipc.Broadcast(new BotInfoMessage
            {
                Profession = prof,
                Identity = identity,
                SpellData = spellList,
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
            TryAdd(prof);
            Entries[prof].TeamTrackerId = trackId;
        }

        public void PingPong(Profession prof)
        {
            TryAdd(prof);
            Entries[prof].LastUpdateInTicks = DateTime.Now.Ticks;
        }

        public void TryAdd(Profession prof)
        {
            if (!Entries.ContainsKey(prof))
                Entries.Add(prof, new BotData());
        }

        public bool ContainsNanoEntry(Profession prof, NanoEntry nanoEntry)
        {
            if (!Entries.TryGetValue(prof, out BotData botCache))
                return false;

            return botCache.SpellData.Any(s => nanoEntry.ContainsId(s));
        }

        public Dictionary<Profession, BotData> OutOfTeamBots() => Entries.Count == 0 ? new Dictionary<Profession, BotData>() : Entries.Where(x => x.Value.TeamMemberId == 0).ToDictionary(kv => kv.Key, kv => kv.Value);

        public Dictionary<Profession, BotData> NonTeamTrackerBots() => Entries.Count == 0 ? new Dictionary<Profession, BotData>() : Entries.Where(x => x.Value.TeamTrackerId == 0).ToDictionary(kv => kv.Key, kv => kv.Value);


        public bool IsTeamQueueEmpty(int charId)
        {
            try
            {
                return Entries.Values.SelectMany(x => x.Queue).Where(x => x != null && x.NanoEntry != null && x.NanoEntry.Type == CastType.Team && x.Requester.Instance == charId).ToList().Count == 0;
            }
            catch
            {
                return true;
            }
        }

        public IOrderedEnumerable<KeyValuePair<Profession, BotData>> OrderByQueueEntries() => Entries.OrderBy(x => x.Value.Queue.Count());

        internal void UpdateBotInfo(Profession profession, Identity identity, int[] spellData)
        {
            TryAdd(profession);
            Entries[profession].Identity = identity;
            Entries[profession].SpellData = spellData;
        }

        internal void UpdateTeamInfo(Profession profession, int teamMemberId)
        {
            TryAdd(profession);
            Entries[profession].TeamMemberId = teamMemberId;
        }

        internal void UpdateQueueInfo(Profession profession, BuffEntry[] entries)
        {
            TryAdd(profession);
            Entries[profession].Queue = entries;
        }
    }
}
