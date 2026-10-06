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
        private string _observedAttemptKey;
        private int _attemptedNanoId;
        private DateTime _attemptedUtc;
        private DateTime _previousObservedExpiryUtc;
        private bool _attemptedMissingLearnedBuff;
        private DateTime _requestDeadlineUtc;
        private string _lastRetryReason;
        private int _directInviteRequester;
        private DateTime _directTeamDeadline;
        private int[] _fixerCastMembers = new int[0];
        private DateTime _paidFixerPostCastUntilUtc;
        internal bool PaidFixerPostCastHold => DateTime.UtcNow < _paidFixerPostCastUntilUtc;

        internal bool RequestPaidBuffs(NanoEntry[] entries, PlayerChar requester, out string message)
        {
            int[] known = Main.EffectiveSpellList();
            var allowed = PaidBufferCatalogue.ForProfession(Main.PaidProfession).Select(n => n.Id).ToArray();
            int? recipientLevel = null;
            try { recipientLevel = requester.Level; } catch { }
            var accepted = new List<NanoEntry>();
            var notices = new List<string>();
            foreach (var entry in entries.Distinct())
            {
                if (!entry.LevelToId.Any(n => allowed.Contains(n.Id)))
                    notices.Add("Can't cast " + entry.Name + ": not offered by " + Client.CharacterName + ".");
                else if (!entry.LevelToId.Any(n => allowed.Contains(n.Id) && known.Contains(n.Id)))
                    notices.Add("Can't cast " + entry.Name + ": " + Client.CharacterName + " has not uploaded it.");
                else if (recipientLevel.HasValue && entry.ObserverRefreshUntilUtc == default(DateTime) &&
                    !entry.LevelToId.Any(n => allowed.Contains(n.Id) && known.Contains(n.Id) && n.Level <= recipientLevel.Value))
                    notices.Add("Can't cast " + entry.Name + ": requires recipient level " + entry.LevelToId.Min(n => n.Level) + "+.");
                else accepted.Add(entry);
            }
            if (Main.PaidProfession == 12 && accepted.Count > 1)
            {
                // These composites replace each other. Deliver the best eligible
                // one instead of refusing the macro or downgrading it afterwards.
                var best = accepted.OrderByDescending(e => e.LevelToId.Where(n => known.Contains(n.Id) &&
                    (!recipientLevel.HasValue || n.Level <= recipientLevel.Value)).Select(n => n.Level).DefaultIfEmpty(0).Max()).First();
                notices.Add("Using " + best.Name + " for the requested MP composites; they replace each other.");
                accepted = new List<NanoEntry> { best };
            }
            var queued = new List<string>();
            foreach (var entry in accepted.OrderBy(e => e.ContainsId(275043) ? 0 : 1))
            {
                string error;
                if (Queue.TryEnqueuePaid(new[] { new BuffEntry {
                    Requester = requester.Identity, NanoEntry = entry
                } }, out error, PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession) ? 16 : 4))
                    queued.Add(entry.Name);
                else notices.Add("Can't queue " + entry.Name + ": " + error);
            }
            if (queued.Count != 0)
            {
                Main.Ipc.BotCache.BroadcastQueueInfoMessage();
                notices.Insert(0, "Queued " + string.Join(" and ", queued) + " on " + Client.CharacterName + ".");
            }
            if (notices.Count == 0) notices.Add("No matching buff is offered by " + Client.CharacterName + ".");
            message = string.Join(" ", notices);
            return queued.Count != 0;
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
                if (PaidFixerPostCastHold) return;
                RetryPendingRoutes();
                if (!_gracePeriod.Elapsed)
                    return;

                if (TryCompleteFromObservation()) return;

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
                        _lastRetryReason = null;
                        _requestDeadlineUtc = Queue.Current.Requester == DynelManager.LocalPlayer.Identity
                            ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(2);
                        TeamTimeout.Reset();
                        _directInviteRequester = 0;
                        _directTeamDeadline = DateTime.UtcNow.AddSeconds(30);
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
            // The paid team buffer stays with Manager until its complete admitted batch
            // is finished. The activity bridge leaves immediately before parking.
            if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession)) return;
            if (!Queue.AllEntries.Any(e => e.NanoEntry.Type == CastType.Team &&
                Team.Members.Any(m => m.Identity == e.Requester)))
                LeaveTeam();
        }

        public void ResetTeamTimer() => _teamGracePeriod.Reset();

        public void ResetBotQueue()
        {
            Logger.Information("Clearing my queue due to an exception");
            foreach (var queued in Queue.AllEntries)
                if (queued.NanoEntry.ObserverAssignmentId != null)
                    ManagerMemory.Current.ReleaseObservedBuffRefresh(Client.CharacterName,
                        queued.Requester.Instance, queued.NanoEntry.LevelToId[0].Id,
                        queued.NanoEntry.ObserverAssignmentId);
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
        private readonly Dictionary<Profession, int> _genericAssignments = new Dictionary<Profession, int>();

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
            bool accepted = false;
            foreach (var pair in entries)
                foreach (var entry in pair.Value.Distinct())
                {
                    bool existing = _pendingRoutes.Any(p => p.Requester == requester.Identity.Instance && p.Entry.Equals(entry));
                    if (!existing && (_pendingRoutes.Count >= 256 ||
                        _pendingRoutes.Count(p => p.Requester == requester.Identity.Instance) >= 32))
                    {
                        CityBufferBridge.Diagnostic(requester.Identity.Instance,
                            "Can't queue " + entry.Name + ": the buffer readiness queue is full.");
                        continue;
                    }
                    RetainRequest(pair.Key, requester.Identity.Instance, entry);
                    accepted = true;
                }
            return accepted;
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
            _genericAssignments.Clear();
            foreach (var pending in _pendingRoutes.ToArray())
            {
                if (DateTime.UtcNow >= pending.Expires)
                {
                    _pendingRoutes.Remove(pending);
                    ManagerMemory.Current.ReportBufferCastFinished(unchecked((uint)pending.Requester), pending.Entry.Tags.ToArray());
                    Logger.Warning("Buffer readiness request expired: " + pending.Entry.Name +
                        "; requester=" + pending.Requester + ".");
                    CityBufferBridge.Diagnostic(pending.Requester,
                        "Can't cast " + pending.Entry.Name + ": no ready nearby buffer with that nano could accept it within two minutes.");
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
                    return TryLocal(nano, requester);
                }
                if (!Main.Ipc.BotCache.ContainsNanoEntry(pending.Profession, nano)) return false;
                return Main.Ipc.SendCastRequest(pending.Profession, pending.Requester, new[] { nano });
            }
            foreach (var caster in Main.Ipc.BotCache.OrderByQueueEntries()
                .OrderBy(c => (c.Value.Queue ?? new BuffEntry[0]).Length +
                    (_genericAssignments.ContainsKey(c.Key) ? _genericAssignments[c.Key] : 0)))
            {
                if (!DynelManager.Characters.Any(c => c.Identity == caster.Value.Identity) ||
                    !Main.Ipc.BotCache.ContainsNanoEntry(caster.Key, nano)) continue;
                if (caster.Value.Identity == DynelManager.LocalPlayer.Identity)
                {
                    if (!TryLocal(nano, requester)) continue;
                }
                else if (!Main.Ipc.SendCastRequest(caster.Key, pending.Requester, new[] { nano })) continue;
                _genericAssignments[caster.Key] = (_genericAssignments.ContainsKey(caster.Key)
                    ? _genericAssignments[caster.Key] : 0) + 1;
                return true;
            }
            return false;
        }

        private void ReleaseObservedAssignment()
        {
            var entry = Queue.Current?.NanoEntry;
            if (entry?.ObserverAssignmentId == null) return;
            ManagerMemory.Current.ReleaseObservedBuffRefresh(Client.CharacterName,
                Queue.Current.Requester.Instance, entry.LevelToId[0].Id, entry.ObserverAssignmentId);
        }

        private bool TryReturnObservedAttempt()
        {
            var entry = Queue.Current.NanoEntry;
            if (entry.ObserverAssignmentId == null || !HasOutstandingCast) return false;
            // A subsequent Manager scan has not confirmed this attempt and the
            // caster is ready again. Return ownership so Manager can choose a peer.
            var player = ManagerMemory.Current.ReadNearbyPlayers().FirstOrDefault(p =>
                p.CharacterId == Queue.Current.Requester.Instance && p.ObservedUtc > _attemptedUtc);
            if (player == null) return false;
            if (!ManagerMemory.Current.BufferBotInfos().Any(b =>
                !string.Equals(b.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                !ManagerMemory.Current.IsPaidBuffer(b.Character) && b.Ready && b.InPlay &&
                b.ObservedUtc >= DateTime.UtcNow.AddSeconds(-5) &&
                (b.AdvertisedBuffs ?? new BufferAdvertisedBuff[0]).Any(n => n.Type == "Single" &&
                    (n.NanoIds ?? new int[0]).Contains(_attemptedNanoId)))) return false;
            CityBufferBridge.Diagnostic(Queue.Current.Requester.Instance,
                "Manager has not observed " + entry.Name + "; returning it for provider selection.");
            ReleaseObservedAssignment();
            ClearCastAttempt();
            Queue.ClearCurrent();
            Main.Ipc.BotCache.BroadcastQueueInfoMessage();
            return true;
        }

        private void ProcessCurrentBuffEntry()
        {
            var observedEntry = Queue.Current.NanoEntry;
            if (observedEntry.ObserverAssignmentId != null &&
                !ManagerMemory.Current.OwnsObservedBuffRefresh(Client.CharacterName,
                    Queue.Current.Requester.Instance, observedEntry.LevelToId[0].Id,
                    observedEntry.ObserverAssignmentId))
            {
                ClearCastAttempt();
                Queue.ClearCurrent();
                Main.Ipc.BotCache.BroadcastQueueInfoMessage();
                return;
            }
            if (observedEntry.ObserverRefreshUntilUtc != default(DateTime) &&
                (DateTime.UtcNow >= observedEntry.ObserverRefreshUntilUtc ||
                 !CityDwellers.Shared.ManagerMemory.Current.ObservedBuffNeedsRefresh(
                     Queue.Current.Requester.Instance, observedEntry.ObservedNanoId != 0
                         ? observedEntry.ObservedNanoId : observedEntry.LevelToId[0].Id)))
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
                ReportCurrentDiagnostic("couldn't cast", "retry limit reached after two minutes" +
                    (string.IsNullOrEmpty(_lastRetryReason) ? "." : ": " + _lastRetryReason));
                ResetCurrentBuffEntry();
                return;
            }
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
            _observedAttemptKey = null;
            _attemptedNanoId = 0;
            _attemptedUtc = DateTime.MinValue;
            _previousObservedExpiryUtc = DateTime.MinValue;
            _attemptedMissingLearnedBuff = false;
        }

        private bool TryCompleteFromObservation()
        {
            if (_observedAttemptKey == null ||
                _observedAttemptKey != CurrentCastAttemptKey()) return false;
            if (!ManagerMemory.Current.ObservedCastLanded(Queue.Current.Requester.Instance,
                _attemptedNanoId, _attemptedUtc, _previousObservedExpiryUtc,
                _attemptedMissingLearnedBuff)) return false;
            Logger.Information("Manager NCU confirmed '" + Queue.Current.NanoEntry.Name +
                "' for requester " + Queue.Current.Requester.Instance + " nano=" + _attemptedNanoId +
                (_attemptedMissingLearnedBuff ? "; learned buff appeared after cast attempt." :
                    "; duration renewed after cast attempt."));
            ResetCurrentBuffEntry(completed: true);
            return true;
        }

        // Feedback has no nano id. Only attach it while a cast is outstanding.
        internal bool HasOutstandingCast => _attemptedEntryKey != null &&
            _attemptedEntryKey == CurrentCastAttemptKey();

        internal bool IsOutstandingNano(int nanoId) => HasOutstandingCast && _attemptedNanoId == nanoId;

        internal void RetryCurrentBuffEntry(string reason)
        {
            if (Queue.Current == null) return;
            // Retain the request, not an artificial cooldown. The normal update
            // tick and the SDK casting state govern when another attempt is possible.
            if (_lastRetryReason != reason)
                CityBufferBridge.Diagnostic(Queue.Current.Requester.Instance,
                    "Retaining " + Queue.Current.NanoEntry.Name + ": " + reason);
            _lastRetryReason = reason;
        }

        private void ReportCurrentDiagnostic(string outcome, string reason)
        {
            var entry = Queue.Current;
            if (entry == null || entry.Requester == Identity.None ||
                entry.Requester == DynelManager.LocalPlayer.Identity) return;
            try
            {
                CityBufferBridge.Diagnostic(entry.Requester.Instance,
                    Client.CharacterName + " " + outcome + " " + entry.NanoEntry.Name + ": " + reason);
            }
            catch (Exception ex)
            {
                // Reporting must not change the cast outcome or discard retry state.
                Logger.Warning("Could not record buff diagnostic: " + ex.Message);
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

        internal void CastFinished()
        {
            // When Manager has a baseline, target NCU decides delivery, including
            // instant casts. Outside Manager's view retain Mali's completion path.
            if (_observedAttemptKey != null)
            {
                TryCompleteFromObservation();
                return;
            }
            ResetCurrentBuffEntry(completed: true);
        }

        public void ResetCurrentBuffEntry(LdbFeedback? feedback = null, bool completed = false)
        {
            if (Queue.Current == null) return;
            ManagerMemory.Current.ReportBufferCastFinished(unchecked((uint)Queue.Current.Requester.Instance),
                Queue.Current.NanoEntry.Tags.ToArray());
            if (feedback != null)
                ReportCurrentDiagnostic("couldn't cast", FeedbackReason(feedback.Value) + ".");
            if (feedback != null)
                Logger.Warning("Buff feedback for requester " + Queue.Current.Requester.Instance +
                    ": " + feedback.Value);

            if (completed)
            {
                if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession) &&
                    (Queue.Current.NanoEntry.ContainsId(275043) || Queue.Current.NanoEntry.ContainsId(235291)))
                {
                    // Keep the team and session alive briefly for the team effect to land.
                    _paidFixerPostCastUntilUtc = DateTime.UtcNow.AddSeconds(1);
                }
                if (!Main.PaidPilot)
                    DynelManager.LocalPlayer.TryRemoveBuffs(Queue.Current.NanoEntry.RemoveNanoIdUponCast);
                if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession) &&
                    (Queue.Current.NanoEntry.ContainsId(275043) || Queue.Current.NanoEntry.ContainsId(235291)))
                {
                    int teamNano = Queue.Current.NanoEntry.ContainsId(275043) ? 275043 : 235291;
                    foreach (int recipient in Queue.RemoveQueuedTeamBuff(teamNano, _fixerCastMembers))
                        CityBufferBridge.Diagnostic(recipient,
                            Client.CharacterName + " finished casting " + Queue.Current.NanoEntry.Name + " for the team.");
                }
            }
            _fixerCastMembers = new int[0];
            ReleaseObservedAssignment();

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
                ReportCurrentDiagnostic("couldn't cast", "your character is PvP flagged.");
                Logger.Warning("Skipping buff for flagged requester " + buffTarget.Name + ".");
                ResetCurrentBuffEntry();
                return;
            }

            if (TryCompleteFromObservation()) return;
            if (TryReturnObservedAttempt()) return;

            Logger.Information($"Attempting to cast '{Queue.Current.NanoEntry.Name}' on '{buffTarget.Name}'");

            var knownNanos = new HashSet<int>(Main.EffectiveSpellList());
            bool observedRefresh = Queue.Current.NanoEntry.ObserverRefreshUntilUtc != default(DateTime);
            var firstAvailableBuff = Queue.Current.NanoEntry.LevelToId.FirstOrDefault(x =>
                knownNanos.Contains(x.Id) && (observedRefresh || x.Level <= buffTarget.Level));

            if (firstAvailableBuff == null)
            {
                string reason = observedRefresh ? "the observed nano is no longer uploaded" :
                    "no uploaded version is available for your level";
                Logger.Warning("Skipping buff for requester " + buffTarget.Name + ": " + reason + ".");
                ReportCurrentDiagnostic("couldn't cast", reason + ".");
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

            bool firstAttempt = _attemptedEntryKey != CurrentCastAttemptKey();
            _attemptedEntryKey = CurrentCastAttemptKey();
            if (firstAttempt)
            {
                _observedAttemptKey = null;
                _attemptedMissingLearnedBuff = false;
                _attemptedNanoId = firstAvailableBuff.Id;
                _attemptedUtc = DateTime.UtcNow;
                if (Queue.Current.Requester != DynelManager.LocalPlayer.Identity)
                {
                    var before = ManagerMemory.Current.ReadNearbyPlayers().FirstOrDefault(p =>
                        p.CharacterId == Queue.Current.Requester.Instance);
                    var oldNano = before?.Nanos.FirstOrDefault(n => n.Id == firstAvailableBuff.Id ||
                        PaidBufferCatalogue.FindEffect(firstAvailableBuff.Id)?.MatchesEffect(n.Id) == true);
                    // Preserve the first baseline across retries; never chase the new expiry.
                    if (oldNano != null && oldNano.RemainingSeconds > 0)
                    {
                        _observedAttemptKey = _attemptedEntryKey;
                        _previousObservedExpiryUtc = before.ObservedUtc.AddSeconds(oldNano.RemainingSeconds);
                    }
                    else if (before != null)
                    {
                        _observedAttemptKey = _attemptedEntryKey;
                        _attemptedMissingLearnedBuff = true;
                    }
                }
            }
            // Team emitters are cast on the caster; the server applies their effects to teammates.
            PlayerChar castTarget = PaidBufferCatalogue.FindEffect(firstAvailableBuff.Id)?.IsTeam == true
                ? DynelManager.LocalPlayer : buffTarget;
            if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession) &&
                PaidBufferCatalogue.FindEffect(firstAvailableBuff.Id)?.IsTeam == true)
                _fixerCastMembers = Team.Members.Select(m => m.Identity.Instance).ToArray();
            Targeting.SetTarget(castTarget);
            DynelManager.LocalPlayer.Cast(castTarget, firstAvailableBuff.Id);
        }

        private void LeaveTeam()
        {
            if (!_teamGracePeriod.Elapsed)
                return;

            Logger.Information("Leaving team...");
            Team.LeaveTeam();
        }

        private void ProcessTeamEntry()
        {
            if (Queue.Current.Requester == DynelManager.LocalPlayer.Identity)
            {
                AttemptToBuffTarget();
                return;
            }
            if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession))
            {
                int manager = ManagerMemory.Current.PaidFixerManager(Client.CharacterName);
                if (manager != 0 && Team.IsInTeam &&
                    Team.Members.Any(m => m.Identity.Instance == manager) &&
                    Team.Members.Any(m => m.Identity == Queue.Current.Requester) &&
                    ManagerMemory.Current.PaidFixerMember(Client.CharacterName, Queue.Current.Requester.Instance))
                    AttemptToBuffTarget();
                // Manager alone invites and owns the team. The ordinary request
                // deadline still bounds a departed requester or lost Manager.
                return;
            }

            if (DateTime.UtcNow >= _directTeamDeadline &&
                (!Team.IsInTeam || !Team.Members.Any(m => m.Identity == Queue.Current.Requester)))
            {
                ReportCurrentDiagnostic("couldn't cast", "team invitation timed out. Leave your current team and retry.");
                ResetCurrentBuffEntry();
                return;
            }
            if (Team.IsInTeam)
            {
                if (Team.Members.Any(m => m.Identity == Queue.Current.Requester))
                    AttemptToBuffTarget();
                else LeaveTeam();
                return;
            }
            if (_directInviteRequester != Queue.Current.Requester.Instance)
            {
                _directInviteRequester = Queue.Current.Requester.Instance;
                ResetTeamTimer();
                Team.Invite(Queue.Current.Requester);
                Logger.Information("Direct team invitation sent to requester " + _directInviteRequester +
                    " for " + Queue.Current.NanoEntry.Name + ".");
                CityBufferBridge.RequestResult(_directInviteRequester,
                    "Accept " + Client.CharacterName + "'s team invitation for " + Queue.Current.NanoEntry.Name + ".");
            }
        }
    }
}




