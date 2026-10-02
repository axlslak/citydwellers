using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using CityDwellers.Shared;

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
        private string _attemptedEntryKey;
        private DateTime _attemptedEntryExpiresUtc;
        private const int CastCompletionTimeoutSeconds = 8;
        private DateTime _requestDeadlineUtc;
        private DateTime _retryAfterUtc;
        private int _retryCount;
        private int? _lastAttemptFeedback;
        private bool _retryNoticeSent;
        private string _lastRetryReason;
        private int _paidInviteRequester;
        private DateTime _paidTeamDeadline;
        private bool _paidTeamMemberConfirmed;

        internal bool RequestPaidBuffs(NanoEntry[] entries, PlayerChar requester, out string message)
        {
            message = null;
            int[] known = Main.EffectiveSpellList();
            var allowed = PaidBufferCatalogue.ForProfession(Main.PaidProfession).Select(n => n.Id).ToArray();
            if (entries.Length == 0 || entries.Any(e => e.LevelToId.Any(n => !allowed.Contains(n.Id))))
            { message = "That buff is not offered by this paid buffer's configured profession."; return false; }
            if (Main.PaidProfession == 12 && entries.Length > 1)
            { message = "Choose one MP composite; these buffs replace each other."; return false; }
            foreach (var entry in entries)
            {
                if (!entry.LevelToId.Any(n => known.Contains(n.Id)))
                { message = Client.CharacterName + " does not report " + entry.Name + " as uploaded."; return false; }
                if (!entry.LevelToId.Any(n => known.Contains(n.Id) && n.Level <= requester.Level))
                { message = entry.Name + " requires recipient level " + entry.LevelToId.Min(n => n.Level) + "+."; return false; }
            }
            if (!Queue.TryEnqueuePaid(entries.Select(e => new BuffEntry {
                Requester = requester.Identity, NanoEntry = e
            }).ToArray(), out message)) return false;
            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
            message = "Queued " + string.Join(" and ", entries.Select(e => e.Name)) + " on " + Client.CharacterName + ".";
            return true;
        }

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
                if (!Client.InPlay || DynelManager.LocalPlayer == null) return;
                RetryPendingRoutes();
                if (!_gracePeriod.Elapsed)
                    return;

                if (Team.IsInTeam)
                    ProcessLeaveTeam();

                if (!Main.PaidPilot && TeamTrackerId != 0 && Main.Ipc.BotCache.IsTeamQueueEmpty(TeamTrackerId) && !Queue.AllEntries.Any(x => x.Requester.Instance == TeamTrackerId && x.NanoEntry.Type == CastType.Team))
                    ProcessResetTeamTrackerId();

                if (DynelManager.LocalPlayer.IsCasting)
                    return;

                switch (Queue.Process(DynelManager.LocalPlayer.Identity))
                {
                    case QueueState.Current:
                        ProcessCurrentBuffEntry();
                        break;
                    case QueueState.Dequeue:
                        ClearCastAttempt();
                        _retryCount = 0;
                        _retryNoticeSent = false;
                        _lastRetryReason = null;
                        _retryAfterUtc = DateTime.MinValue;
                        _requestDeadlineUtc = Queue.Current.Requester == DynelManager.LocalPlayer.Identity
                            ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(2);
                        TeamTimeout.Reset();
                        _paidInviteRequester = 0;
                        _paidTeamMemberConfirmed = false;
                        _paidTeamDeadline = DateTime.UtcNow.AddSeconds(30);
                        Main.Ipc.BotCache.BroadcastQueueInfoMessage();
                        ProcessCurrentBuffEntry();
                        break;
                    case QueueState.Empty:
                        break;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    RetryCurrentBuffEntry("cast processing exception: " + ex.Message);
                }
                catch { /* Preserve the remaining queue even if reporting fails. */ }
                Logger.Error(ex.Message);
                Logger.Error("QueueProcessorOnUpdate");
            }
        }

        private void ProcessLeaveTeam()
        {
            if (Main.PaidPilot)
            {
                if (!Queue.AllEntries.Any(e => e.NanoEntry.Type == CastType.Team)) LeaveTeam();
                return;
            }
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
            ClearCastAttempt();
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
            DeferReceivedRequest((Profession)DynelManager.LocalPlayer.Profession,
                requester.Identity.Instance, entries);
        }

        private sealed class PendingRoute
        {
            public Profession Profession;
            public int Requester;
            public NanoEntry Entry;
            public DateTime Expires;
            public bool LocalOnly;
            public bool WaitingLogged;
        }
        private readonly List<PendingRoute> _pendingRoutes = new List<PendingRoute>();
        private DateTime _nextRouteCheck;

        private void RetainRequest(Profession profession, int requester, NanoEntry entry, bool localOnly = false)
        {
            if (_pendingRoutes.Any(p => p.Requester == requester && p.Entry.Equals(entry))) return;
            if (_pendingRoutes.Count >= 256 || _pendingRoutes.Count(p => p.Requester == requester) >= 32)
            {
                Logger.Warning("Buffer readiness queue full for requester " + requester + "; request not admitted.");
                return;
            }
            _pendingRoutes.Add(new PendingRoute { Profession = profession, Requester = requester,
                Entry = entry, LocalOnly = localOnly,
                Expires = requester == DynelManager.LocalPlayer.Identity.Instance
                    ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(2) });
        }

        // Signals transfer routing ownership once; receiver retains requests even
        // when its local player view or casting queue is not ready yet.
        internal bool DeferReceivedRequest(Profession profession, int requester, IEnumerable<NanoEntry> entries)
        {
            var requested = entries.Distinct().ToArray();
            int additional = requested.Count(e => !_pendingRoutes.Any(p => p.Requester == requester && p.Entry.Equals(e)));
            if (_pendingRoutes.Count + additional > 256 ||
                _pendingRoutes.Count(p => p.Requester == requester) + additional > 32) return false;
            foreach (var entry in requested) RetainRequest(profession, requester, entry, true);
            return true;
        }

        public bool RequestBuffs(Dictionary<Profession, List<NanoEntry>> entries, PlayerChar requester)
        {
            var additional = entries.SelectMany(p => p.Value).Distinct()
                .Count(e => !_pendingRoutes.Any(p => p.Requester == requester.Identity.Instance && p.Entry.Equals(e)));
            if (_pendingRoutes.Count + additional > 256 ||
                _pendingRoutes.Count(p => p.Requester == requester.Identity.Instance) + additional > 32)
                return false;
            foreach (var pair in entries)
                FinalizeBuffRequest(pair.Key, pair.Value, requester);
            return true;
        }

        public void FinalizeBuffRequest(Profession profession, IEnumerable<NanoEntry> entries, PlayerChar requester)
        {
            foreach (var entry in entries) RetainRequest(profession, requester.Identity.Instance, entry);
        }

        public void FinalizeBuffRequest(Profession profession, NanoEntry entry, PlayerChar requester) =>
            RetainRequest(profession, requester.Identity.Instance, entry);

        private void RetryPendingRoutes()
        {
            if (!CityBufferBridge.Ready || DateTime.UtcNow < _nextRouteCheck) return;
            _nextRouteCheck = DateTime.UtcNow.AddSeconds(1);
            foreach (var pending in _pendingRoutes.ToArray())
            {
                if (DateTime.UtcNow >= pending.Expires)
                {
                    _pendingRoutes.Remove(pending);
                    Logger.Warning("Buffer readiness request expired: " + pending.Entry.Name +
                        "; requester=" + pending.Requester + ".");
                    CityBufferBridge.PaidResult(pending.Requester,
                        pending.Entry.Name + " could not be queued within two minutes; stay near the buffers and retry.");
                    continue;
                }
                var requester = pending.Requester == DynelManager.LocalPlayer.Identity.Instance
                    ? DynelManager.LocalPlayer
                    : DynelManager.Players.FirstOrDefault(p => p.Identity.Instance == pending.Requester);
                bool routed = false;
                try { routed = requester != null && TryRoute(pending, requester); }
                catch (Exception ex)
                {
                    if (!pending.WaitingLogged)
                        Logger.Warning("Buffer routing deferred: " + ex.Message);
                }
                if (routed)
                {
                    _pendingRoutes.Remove(pending);
                    if (pending.WaitingLogged)
                        Logger.Information("Deferred buff routed: " + pending.Entry.Name +
                            "; requester=" + pending.Requester + ".");
                }
                else if (!pending.WaitingLogged)
                {
                    pending.WaitingLogged = true;
                    Logger.Information("Waiting for buffer readiness/queue or nearby requester: " +
                        pending.Entry.Name + "; requester=" + pending.Requester + ". Request retained.");
                }
            }
        }

        private bool TryLocal(NanoEntry nano, PlayerChar requester)
        {
            if (!Main.EffectiveSpellList().Any(nano.ContainsId)) return false;
            var entry = new BuffEntry { Requester = requester.Identity, NanoEntry = nano };
            if (Queue.AllEntries.Any(e => e.Equals(entry))) return true;
            string error;
            if (!Queue.TryEnqueue(entry, out error)) return false;
            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
            return true;
        }

        private bool PrepareTeam(PlayerChar requester)
        {
            if (Main.Ipc.BotCache.Entries.Any(x => x.Value.TeamTrackerId == requester.Identity.Instance))
                return true;
            var ordered = Main.BuffsJson.Entries
                .OrderBy(p => p.Value.Count(e => e.Type == CastType.Team)).Select(p => p.Key).ToList();
            var candidates = Main.Ipc.BotCache.NonTeamTrackerBots()
                .Where(p => DynelManager.Characters.Any(c => c.Identity == p.Value.Identity))
                .OrderBy(p => ordered.IndexOf(p.Key)).ToArray();
            if (candidates.Length == 0) return false;
            var tracker = candidates[0];
            if (tracker.Value.Identity == DynelManager.LocalPlayer.Identity)
            {
                ResetTeamTimer();
                Team.Invite(requester.Identity);
                TeamTrackerId = requester.Identity.Instance;
            }
            Main.Ipc.BotCache.BroadcastTeamTrackerMessage(tracker.Key, requester.Identity.Instance);
            return true;
        }

        private bool TryRoute(PendingRoute pending, PlayerChar requester)
        {
            var nano = pending.Entry;
            if (pending.LocalOnly) return TryLocal(nano, requester);
            if (pending.Profession != Profession.Generic)
            {
                // Do not invite repeatedly while the actual caster is still starting.
                if (pending.Profession == (Profession)DynelManager.LocalPlayer.Profession)
                {
                    if (!Main.EffectiveSpellList().Any(nano.ContainsId)) return false;
                    if (nano.Type == CastType.Team && !PrepareTeam(requester)) return false;
                    return TryLocal(nano, requester);
                }
                if (!Main.Ipc.BotCache.ContainsNanoEntry(pending.Profession, nano)) return false;
                if (nano.Type == CastType.Team && !PrepareTeam(requester)) return false;
                return Main.Ipc.SendCastRequest(pending.Profession, pending.Requester, new[] { nano });
            }
            foreach (var caster in Main.Ipc.BotCache.OrderByQueueEntries())
            {
                if (!DynelManager.Characters.Any(c => c.Identity == caster.Value.Identity) ||
                    !Main.Ipc.BotCache.ContainsNanoEntry(caster.Key, nano)) continue;
                if (nano.Type == CastType.Team && !PrepareTeam(requester)) return false;
                if (caster.Value.Identity == DynelManager.LocalPlayer.Identity)
                {
                    if (TryLocal(nano, requester)) return true;
                }
                else if (Main.Ipc.SendCastRequest(caster.Key, pending.Requester, new[] { nano })) return true;
            }
            return false;
        }

        private void ProcessCurrentBuffEntry()
        {
            var observedEntry = Queue.Current.NanoEntry;
            if (observedEntry.ObserverRefreshUntilUtc != default(DateTime) &&
                !string.Equals(_attemptedEntryKey, CurrentCastAttemptKey(), StringComparison.Ordinal) &&
                (DateTime.UtcNow >= observedEntry.ObserverRefreshUntilUtc ||
                 !CityDwellers.Shared.ManagerMemory.Current.ObservedBuffNeedsRefresh(
                     Queue.Current.Requester.Instance, observedEntry.LevelToId[0].Id)))
            {
                Logger.Information("OBSERVER refresh no longer needed/visible or expired; requester=" +
                    Queue.Current.Requester.Instance + " nano=" + observedEntry.LevelToId[0].Id);
                ResetCurrentBuffEntry();
                return;
            }
            if (DateTime.UtcNow >= _requestDeadlineUtc)
            {
                Logger.Warning("Buff request expired after two minutes of cast processing: " +
                    Queue.Current.NanoEntry.Name + "; requester=" + Queue.Current.Requester.Instance + ".");
                NotifyCurrentRequester("couldn't cast", "retry limit reached after two minutes" +
                    (string.IsNullOrEmpty(_lastRetryReason) ? "." : ": " + _lastRetryReason));
                ResetCurrentBuffEntry();
                return;
            }
            if (DateTime.UtcNow < _retryAfterUtc) return;
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

        private void ClearCastAttempt()
        {
            _attemptedEntryKey = null;
            _attemptedEntryExpiresUtc = DateTime.MinValue;
            _lastAttemptFeedback = null;
        }

        // Feedback has no nano id. Only attach it while a cast is outstanding.
        internal bool HasOutstandingCast => _attemptedEntryKey != null &&
            _attemptedEntryKey == CurrentCastAttemptKey();

        internal void RecordCastFeedback(int messageId)
        {
            if (HasOutstandingCast)
                _lastAttemptFeedback = messageId;
        }

        internal void RetryCurrentBuffEntry(string reason)
        {
            if (Queue.Current == null || DateTime.UtcNow < _retryAfterUtc) return;
            ClearCastAttempt();
            _retryCount = Math.Min(_retryCount + 1, 6);
            int seconds = _retryCount * 5;
            _retryAfterUtc = DateTime.UtcNow.AddSeconds(seconds);
            _lastRetryReason = reason;
            Logger.Warning("Retaining '" + Queue.Current.NanoEntry.Name + "' for requester " +
                Queue.Current.Requester.Instance + ": " + reason + "; retry in " + seconds + "s.");
            if (!_retryNoticeSent)
            {
                NotifyCurrentRequester("couldn't cast yet", reason + ". Your request is retained; retrying.");
                _retryNoticeSent = true;
            }
        }

        private void NotifyCurrentRequester(string outcome, string reason)
        {
            var entry = Queue.Current;
            if (entry == null || entry.Requester == Identity.None ||
                entry.Requester == DynelManager.LocalPlayer.Identity) return;
            try
            {
                CityBufferBridge.RequestResult(entry.Requester.Instance,
                    Client.CharacterName + " " + outcome + " " + entry.NanoEntry.Name + ": " + reason);
            }
            catch (Exception ex)
            {
                // Reporting must not change the cast outcome or discard retry state.
                Logger.Warning("Could not enqueue buff result tell: " + ex.Message);
            }
        }

        internal static string FeedbackReason(LdbFeedback feedback)
        {
            switch (feedback)
            {
                case LdbFeedback.BetterNanoInNcu: return "an equal or stronger buff is already running";
                case LdbFeedback.NotEnoughNcu: return "you don't have enough free NCU";
                case LdbFeedback.NotInLineOfSight: return "you are not in line of sight";
                case LdbFeedback.OutOfRange: return "you are out of range";
                case LdbFeedback.UnableToUseNano: return "the game rejected the nano's casting requirements";
                case LdbFeedback.WaitForNanoToFinish: return "the caster is busy with another nano";
                case LdbFeedback.NotEnoughNano: return "the caster doesn't have enough nano energy";
                default: return feedback.ToString();
            }
        }

        private string CurrentCastAttemptKey()
        {
            if (Queue.Current == null || Queue.Current.NanoEntry == null)
                return null;

            string nanoIds = Queue.Current.NanoEntry.LevelToId == null
                ? string.Empty
                : string.Join(",", Queue.Current.NanoEntry.LevelToId
                    .Select(level => level.Id)
                    .OrderBy(id => id));

            return Queue.Current.Requester.Instance + "|" + Queue.Current.NanoEntry.Name + "|" + nanoIds;
        }

        private bool CurrentCastAttemptPending()
        {
            string currentKey = CurrentCastAttemptKey();
            if (string.IsNullOrWhiteSpace(_attemptedEntryKey) ||
                string.IsNullOrWhiteSpace(currentKey) ||
                !string.Equals(_attemptedEntryKey, currentKey, StringComparison.Ordinal))
                return false;

            if (DateTime.UtcNow < _attemptedEntryExpiresUtc)
                return true;

            RetryCurrentBuffEntry("no cast completion after " +
                (Main.PaidPilot ? 20 : CastCompletionTimeoutSeconds) + "s; last feedback=" +
                (_lastAttemptFeedback.HasValue ? _lastAttemptFeedback.Value.ToString() : "none"));
            return true;
        }

        public void ResetCurrentBuffEntry(LdbFeedback? feedback = null, bool completed = false)
        {
            if (Queue.Current == null) return;
            if (feedback != null)
                NotifyCurrentRequester("couldn't cast", FeedbackReason(feedback.Value) + ".");
            if (feedback != null)
                Logger.Warning("Buff feedback for requester " + Queue.Current.Requester.Instance +
                    ": " + feedback.Value);

            if (completed)
                DynelManager.LocalPlayer.TryRemoveBuffs(Queue.Current.NanoEntry.RemoveNanoIdUponCast);

            Logger.Information("RESET TRIGGERED");
            ClearCastAttempt();
            Queue.ClearCurrent();
            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
        }

        private void AttemptToBuffTarget()
        {
            var buffTarget = Queue.Current.Requester == DynelManager.LocalPlayer.Identity
                ? DynelManager.LocalPlayer
                : DynelManager.Players.FirstOrDefault(x => x.Identity == Queue.Current.Requester);

            if (buffTarget == null || Queue.Current.Requester == Identity.None)
            {
                RetryCurrentBuffEntry("requester is not visible yet");
                return;
            }

            if (Main.SettingsJson.Data.PvpFlagCheck && buffTarget.IsPvpFlagged())
            {
                NotifyCurrentRequester("couldn't cast", "your character is PvP flagged.");
                Logger.Warning("Skipping buff for flagged requester " + buffTarget.Name + ".");
                ResetCurrentBuffEntry();
                return;
            }

            if (CurrentCastAttemptPending())
                return;

            Logger.Information($"Attempting to cast '{Queue.Current.NanoEntry.Name}' on '{buffTarget.Name}'");

            var knownNanos = new HashSet<int>(Main.EffectiveSpellList());
            var firstAvailableBuff = Queue.Current.NanoEntry.LevelToId.FirstOrDefault(x => x.Level <= buffTarget.Level && knownNanos.Contains(x.Id));

            if (firstAvailableBuff == null)
            {
                Logger.Warning("Skipping buff for requester " + buffTarget.Name + ": level is too low.");
                NotifyCurrentRequester("couldn't cast", "no uploaded version is available for your level.");
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

            _attemptedEntryKey = CurrentCastAttemptKey();
            _lastAttemptFeedback = null;
            _attemptedEntryExpiresUtc = DateTime.UtcNow.AddSeconds(Main.PaidPilot ? 20 : CastCompletionTimeoutSeconds);
            // FSC is a self-cast whose effect is applied to the team, not a 1m targeted buff.
            PlayerChar castTarget = firstAvailableBuff.Id == 275043 ? DynelManager.LocalPlayer : buffTarget;
            Targeting.SetTarget(castTarget);
            DynelManager.LocalPlayer.Cast(castTarget, firstAvailableBuff.Id);
        }

        private void LeaveTeam()
        {
            if (!_teamGracePeriod.Elapsed)
                return;

            if (!Main.PaidPilot && Team.Members.Count > 0 && Team.Members.Any(x => Main.UserRank.MeetsRank(Rank.Warper, x.Name)))
                return;

            Logger.Information("Leaving team...");
            Team.LeaveTeam();
        }

        private void ProcessTeamEntry()
        {
            if (Main.PaidPilot)
            {
                if (!_paidTeamMemberConfirmed && DateTime.UtcNow >= _paidTeamDeadline)
                {
                    Logger.Warning("Paid team buff timed out for requester " + Queue.Current.Requester.Instance +
                        "; membershipConfirmed=" + _paidTeamMemberConfirmed + ".");
                    NotifyCurrentRequester("couldn't cast", "team invitation timed out. Leave your current team before requesting ncu.");
                    ResetCurrentBuffEntry();
                    return;
                }
                if (Team.IsInTeam)
                {
                    if (Team.Members.Any(m => m.Identity == Queue.Current.Requester))
                    {
                        if (!_paidTeamMemberConfirmed)
                        {
                            _paidTeamMemberConfirmed = true;
                            Logger.Information("Paid team membership confirmed for requester " +
                                Queue.Current.Requester.Instance + "; Firewalled Sync Compressor will be cast on self for the team.");
                        }
                        AttemptToBuffTarget();
                    }
                    else LeaveTeam();
                    return;
                }
                if (_paidInviteRequester != Queue.Current.Requester.Instance)
                {
                    _paidInviteRequester = Queue.Current.Requester.Instance;
                    ResetTeamTimer();
                    Team.Invite(Queue.Current.Requester);
                    Logger.Information("Paid team invitation sent to requester " + _paidInviteRequester +
                        "; waiting for team membership (30-second request deadline).");
                    CityBufferBridge.PaidResult(_paidInviteRequester,
                        "Accept " + Client.CharacterName + "'s team invitation for Firewalled Sync Compressor.");
                }
                return;
            }
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
                NotifyCurrentRequester("couldn't cast", "team membership was not established before the request timed out.");
                ResetCurrentBuffEntry();
            }
        }
    }
}
