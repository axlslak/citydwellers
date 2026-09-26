using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MalisBuffBots
{
    public class QueueProcessor
    {
        internal AutoResetInterval TeamTimeout;
        private AutoResetInterval _gracePeriod;
        private AutoResetInterval _teamGracePeriod;
        public int TeamTrackerId;
        public BuffQueue Queue = new BuffQueue();
        public N3MessageProcessor N3MessageProcessor;

        public QueueProcessor(int graceTimeMs = 1000)
        {
            Queue = new BuffQueue();
            _gracePeriod = new AutoResetInterval(graceTimeMs);
            _teamGracePeriod = new AutoResetInterval(5000);
            TeamTimeout = new AutoResetInterval((int)Main.SettingsJson.Data.TeamTimeoutInSeconds * 1000);
            N3MessageProcessor = new N3MessageProcessor(this);

            Team.TeamMember += OnTeamMember;
        }

        private void OnTeamMember(object sender, TeamMemberEventsArgs e)
        {
            _teamGracePeriod.Reset();
        }

        public void OnUpdate(object _, double deltaTime)
        {
            try
            {
                if (!_gracePeriod.Elapsed)
                    return;

                if (Team.IsInTeam)
                    ProcessLeaveTeam();

                if (TeamTrackerId != 0 && Main.Ipc.BotCache.IsTeamQueueEmpty(TeamTrackerId) && !Queue.AllEntries.Any(x => x.Requester.Instance == TeamTrackerId && x.NanoEntry.Type == CastType.Team))
                    ProcessResetTeamTrackerId();

                if (DynelManager.LocalPlayer.IsCasting)
                    return;

                switch (Queue.Process())
                {
                    case QueueState.Current:
                        ProcessCurrentBuffEntry();
                        break;
                    case QueueState.Dequeue:
                        TeamTimeout.Reset();
                        Main.Ipc.BotCache.BroadcastQueueInfoMessage();
                        ProcessCurrentBuffEntry();
                        break;
                    case QueueState.Empty:
                        break;
                }
            }
            catch (Exception ex)
            {
                // One failed cast must not discard every other user's queue.
                var failed = Queue.Current;
                Queue.ClearCurrent();
                try
                {
                    if (failed != null)
                        Logger.Warning("Buff cast failed for requester " + failed.Requester.Instance +
                            "; remaining queued requests are retained.");
                    Main.Ipc.BotCache.BroadcastQueueInfoMessage();
                }
                catch { /* Preserve the remaining queue even if reporting fails. */ }
                Logger.Error(ex.Message);
                Logger.Error("QueueProcessorOnUpdate");
            }
        }

        private void ProcessLeaveTeam()
        {
            if (!Main.Ipc.BotCache.IsTeamQueueEmpty(TeamTrackerId))
                return;

            if (Team.Members.Any(x => Main.UserRank.MeetsRank(Rank.Warper, x.Name)))
                return;

            if (Queue.Current == null || Queue.Current.NanoEntry.Type != CastType.Team)
                LeaveTeam();
        }

        public void ResetTeamTimer() => _teamGracePeriod.Reset();

        public void ResetBotQueue()
        {
            Logger.Information("Clearing my queue due to an exception");
            LeaveTeam();
            Queue.Clear();
            ProcessResetTeamTrackerId();
            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
            TeamTrackerId = 0;
        }

        private void ProcessResetTeamTrackerId()
        {
            Main.Ipc.BotCache.BroadcastTeamTrackerMessage((Profession)DynelManager.LocalPlayer.Profession, 0);
            TeamTrackerId = 0;
        }

        private readonly Dictionary<int, DateTime> _queueNoticeAfter = new Dictionary<int, DateTime>();
        private void NotifyQueueLimit(Identity requester, string message)
        {
            DateTime now = DateTime.UtcNow, until;
            lock (_queueNoticeAfter)
            {
                foreach (int id in _queueNoticeAfter.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                    _queueNoticeAfter.Remove(id);
                if (_queueNoticeAfter.TryGetValue(requester.Instance, out until) || _queueNoticeAfter.Count >= 1024) return;
                _queueNoticeAfter[requester.Instance] = now.AddSeconds(10);
            }
            Logger.Warning("Buffer queue request from " + requester.Instance + " was not admitted: " + message);
        }

        public void LocalEnqueue(SimpleChar requester, IEnumerable<NanoEntry> entries)
        {
            string rejection = null;
            foreach (var entry in entries.Where(x => DynelManager.LocalPlayer.SpellList.Any(y => x.ContainsId(y))))
            {
                string error;
                if (!Queue.TryEnqueue(new BuffEntry { Requester = requester.Identity, NanoEntry = entry }, out error))
                    rejection = error; // One duplicate must not skip later distinct buffs.
            }
            if (rejection != null) NotifyQueueLimit(requester.Identity, rejection);
            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
        }

        public void RequestBuffs(Dictionary<Profession, List<NanoEntry>> entries, PlayerChar requester)
        {
            var teamEntries = entries.Where(x => x.Value.Any(y => y.Type == CastType.Team));

            if (teamEntries.Count() > 0 && !Main.Ipc.BotCache.Entries.Any(x => x.Value.TeamTrackerId == requester.Identity.Instance))
            {
                List<Profession> orderedEntries = Main.BuffsJson.Entries.OrderBy(kv => kv.Value.Count(entry => entry.Type == CastType.Team)).Select(x => x.Key).ToList();

                var queueData = Main.Ipc.BotCache.NonTeamTrackerBots()
                    .Where(x => DynelManager.Characters.Any(c => c.Identity == x.Value.Identity))
                    .OrderBy(kv => orderedEntries.IndexOf(kv.Key));

                foreach (var bla in DynelManager.Characters)
                    if (queueData.Count() == 0)
                    {
                    Logger.Warning("No team buffer is currently available for requester " + requester.Identity.Instance + ".");
                    return;
                }

                if (queueData.FirstOrDefault().Value.Identity == DynelManager.LocalPlayer.Identity)
                {
                    ResetTeamTimer();
                    Team.Invite(requester.Identity);

                    TeamTrackerId = requester.Identity.Instance;
                }

                Main.Ipc.BotCache.BroadcastTeamTrackerMessage(queueData.FirstOrDefault().Key, requester.Identity.Instance);
            }


            foreach (var entry in entries)
            {
                FinalizeBuffRequest(entry.Key, entry.Value, requester);
            }
        }

        public void FinalizeBuffRequest(Profession castProf, IEnumerable<NanoEntry> results, PlayerChar requester) => ProcessBuffRequest(castProf, results.ToList(), requester);

        public void FinalizeBuffRequest(Profession castProf, NanoEntry result, PlayerChar requester) => ProcessBuffRequest(castProf, new List<NanoEntry> { result }, requester);

        private void ProcessBuffRequest(Profession castProf, List<NanoEntry> results, PlayerChar requester)
        {
            if (castProf == Profession.Generic) // We handle generic buffs by distributing the results evenly amongst all buffers
            {
                EnqueueByBotQueuePriority(results, requester);
            }
            else if (castProf == (Profession)DynelManager.LocalPlayer.Profession) // If the caster is our local player, enqueue buffs
            {
                LocalEnqueue(requester, results);
            }
            else // If the caster is not our local player, broadcast to the required profession
            {
                if (!Main.Ipc.SendCastRequest(castProf, requester.Identity.Instance, results))
                    Logger.Warning($"No ready buffer accepted cast routing for {castProf}.");
            }
        }

        private void EnqueueByBotQueuePriority(IEnumerable<NanoEntry> results, PlayerChar requester)
        {
            Queue<NanoEntry> spells = new Queue<NanoEntry>(results);

            while (spells.Count() > 0)
            {
                int cachedSpellCount = spells.Count();

                foreach (var prof in Main.Ipc.BotCache.OrderByQueueEntries())
                {
                    if (spells.Count == 0)
                        break;

                    var nextSpellToCast = spells.Peek();

                    if (!Main.Ipc.BotCache.ContainsKey(prof.Key))
                        continue;

                    if (!DynelManager.Characters.Any(x => x.Identity == prof.Value.Identity))
                        continue;

                    if (!Main.Ipc.BotCache.ContainsNanoEntry(prof.Key, nextSpellToCast))
                        continue;

                    if (prof.Key == (Profession)DynelManager.LocalPlayer.Profession)
                    {
                        string error;
                        if (!Queue.TryEnqueue(new BuffEntry { Requester = requester.Identity, NanoEntry = nextSpellToCast }, out error))
                            NotifyQueueLimit(requester.Identity, error);
                        else
                            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
                    }
                    else
                    {
                        if (!Main.Ipc.SendCastRequest(
                                prof.Key,
                                requester.Identity.Instance,
                                new NanoEntry[1] { nextSpellToCast }))
                        {
                            Logger.Warning($"No ready buffer accepted generic cast routing for {prof.Key}.");
                            continue;
                        }
                    }

                    spells.Dequeue();
                }

                //Nobody can cast anything that is left
                if (cachedSpellCount == spells.Count())
                {
                    Logger.Warning($"No buffers could cast queued buffs.");
                    spells.Clear();
                }
            }
        }

        private void ProcessCurrentBuffEntry()
        {
            switch (Queue.Current.NanoEntry.Type)
            {
                case CastType.Single:
                    AttemptToBuffTarget();
                    break;
                case CastType.Team:
                    ProcessTeamEntry();
                    break;
            }
        }

        public void ResetCurrentBuffEntry(LdbFeedback? feedback = null)
        {
            if (feedback != null)
                Logger.Warning("Buff feedback for requester " + Queue.Current.Requester.Instance +
                    ": " + feedback.Value);

            DynelManager.LocalPlayer.TryRemoveBuffs(Queue.Current.NanoEntry.RemoveNanoIdUponCast);

            Logger.Information("RESET TRIGGERED");
            Queue.ClearCurrent();
            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
        }

        private void AttemptToBuffTarget()
        {
            var buffTarget = DynelManager.Players.FirstOrDefault(x => x.Identity == Queue.Current.Requester);

            if (buffTarget == null || Queue.Current.Requester == Identity.None)
            {
                Logger.Information($"Cast attempt on UNKNOWN character skipped.");
                ResetCurrentBuffEntry();
                return;
            }

            if (Main.SettingsJson.Data.PvpFlagCheck && buffTarget.IsPvpFlagged())
            {
                Logger.Warning("Skipping buff for flagged requester " + buffTarget.Name + ".");
                ResetCurrentBuffEntry();
                return;
            }

            Logger.Information($"Attempting to cast '{Queue.Current.NanoEntry.Name}' on '{buffTarget.Name}'");

            var firstAvailableBuff = Queue.Current.NanoEntry.LevelToId.FirstOrDefault(x => x.Level <= buffTarget.Level && DynelManager.LocalPlayer.SpellList.Contains(x.Id));

            if (firstAvailableBuff == null)
            {
                Logger.Warning("Skipping buff for requester " + buffTarget.Name + ": level is too low.");
                ResetCurrentBuffEntry();
                return;
            }

            if (Main.SettingsJson.Data.DanceOnCast)
            {
                Client.Send(new SocialActionCmdMessage
                {
                    Unknown5 = 0x3E,
                    Unknown = 1,
                    Action = (SocialAction)Main.SettingsJson.Data.SocialAction
                });
            }

            DynelManager.LocalPlayer.Cast(buffTarget, firstAvailableBuff.Id);
        }

        private void LeaveTeam()
        {
            if (!_teamGracePeriod.Elapsed)
                return;

            if (Team.Members.Count > 0 && Team.Members.Any(x => Main.UserRank.MeetsRank(Rank.Warper, x.Name)))
                return;

            Logger.Information("Leaving team...");
            Team.LeaveTeam();
        }

        private void ProcessTeamEntry()
        {
            if (Team.IsInTeam)
            {
                if (!Team.Members.Any(x => x.Identity == Queue.Current.Requester))
                {
                    if (TeamTrackerId != 0)
                    {
                        TeamTrackerId = 0;
                    }

                    LeaveTeam();
                    return;
                }

                AttemptToBuffTarget();
                return;
            }

            var botInTeam = Main.Ipc.BotCache.Entries.Values.FirstOrDefault(x => x.TeamMemberId == Queue.Current.Requester.Instance);

            if (botInTeam != null)
            {
                Main.Ipc.Broadcast(new RequestTeamInviteMessage
                {
                    IsTeamTracker = false,
                    Requester = DynelManager.LocalPlayer.Identity.Instance,
                    Bot = botInTeam.Identity.Instance
                });
            }

            if (TeamTimeout.Elapsed)
            {
                Logger.Warning("Team buff timed out for requester " + Queue.Current.Requester.Instance +
                    ": " + Queue.Current.NanoEntry.Name + ".");
                ResetCurrentBuffEntry();
            }
        }
    }
}
