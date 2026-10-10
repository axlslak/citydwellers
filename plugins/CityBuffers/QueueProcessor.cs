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
        private sealed class CastWork
        {
            internal string AttemptKey, ObservationKey, LastRetryReason;
            internal int NanoId, InviteRequester;
            internal DateTime AttemptedUtc, PreviousExpiryUtc, DeadlineUtc, TeamDeadlineUtc;
            internal DateTime CompletionUtc, FeedbackUtc;
            internal bool MissingBefore;
            internal int[] TeamMembers = new int[0];
        }
        private readonly Dictionary<BuffEntry, CastWork> _castWork = new Dictionary<BuffEntry, CastWork>();
        private BuffEntry _feedbackEntry;

        private CastWork Work(BuffEntry entry)
        {
            CastWork work;
            if (!_castWork.TryGetValue(entry, out work))
                _castWork.Add(entry, work = new CastWork {
                    DeadlineUtc = entry.Requester == DynelManager.LocalPlayer.Identity
                        ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(2),
                    TeamDeadlineUtc = DateTime.UtcNow.AddSeconds(30) });
            return work;
        }

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
                Main.Coordination.BotCache.PublishQueueInfo();
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

                ProcessAwaitingObservations();
                if (PaidFixerPostCastHold) return;

                if (Team.IsInTeam)
                    ProcessLeaveTeam();

                if (!Main.PaidPilot && TeamTrackerId != 0 && Main.Coordination.BotCache.IsTeamQueueEmpty(TeamTrackerId) && !Queue.AllEntries.Any(x => x.Requester.Instance == TeamTrackerId && x.NanoEntry.Type == CastType.Team))
                    ProcessResetTeamTrackerId();

                if (DynelManager.LocalPlayer.IsCasting)
                    return;

                switch (Queue.Process(DynelManager.LocalPlayer.Identity))
                {
                    case QueueState.Current:
                        ProcessCurrentBuffEntry();
                        break;
                    case QueueState.Dequeue:
                        Work(Queue.Current);
                        TeamTimeout.Reset();
                        Main.Coordination.BotCache.PublishQueueInfo();
                        ProcessCurrentBuffEntry();
                        break;
                    case QueueState.Empty:
                        break;
                }
                YieldCurrentWork();
            }
            catch (Exception ex)
            {
                try
                {
                    RetryCurrentBuffEntry("cast processing exception: " + ex.Message);
                    if (Queue.Current != null)
                        Work(Queue.Current).FeedbackUtc = DateTime.UtcNow;
                }
                catch { /* Preserve the remaining queue even if reporting fails. */ }
                Logger.Error(ex.Message);
                Logger.Error("QueueProcessorOnUpdate");
                YieldCurrentWork();
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
            _castWork.Clear();
            _feedbackEntry = null;
            LeaveTeam();
            Queue.Clear();
            ProcessResetTeamTrackerId();
            Main.Coordination.BotCache.PublishQueueInfo();
            TeamTrackerId = 0;
        }

        private void ProcessResetTeamTrackerId()
        {
            Main.Coordination.BotCache.PublishTeamTracker((Profession)DynelManager.LocalPlayer.Profession, 0);
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
            Main.Coordination.BotCache.PublishQueueInfo();
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
                if (!Main.Coordination.BotCache.ContainsNanoEntry(pending.Profession, nano)) return false;
                return Main.Coordination.SendCastRequest(pending.Profession, pending.Requester, new[] { nano });
            }
            foreach (var caster in Main.Coordination.BotCache.OrderByQueueEntries()
                .OrderBy(c => (c.Value.Queue ?? new BuffEntry[0]).Length +
                    (_genericAssignments.ContainsKey(c.Key) ? _genericAssignments[c.Key] : 0)))
            {
                if (!DynelManager.Characters.Any(c => c.Identity == caster.Value.Identity) ||
                    !Main.Coordination.BotCache.ContainsNanoEntry(caster.Key, nano)) continue;
                if (caster.Value.Identity == DynelManager.LocalPlayer.Identity)
                {
                    if (!TryLocal(nano, requester)) continue;
                }
                else if (!Main.Coordination.SendCastRequest(caster.Key, pending.Requester, new[] { nano })) continue;
                _genericAssignments[caster.Key] = (_genericAssignments.ContainsKey(caster.Key)
                    ? _genericAssignments[caster.Key] : 0) + 1;
                return true;
            }
            return false;
        }

        private void YieldCurrentWork()
        {
            var entry = Queue.Current;
            if (entry == null) return;
            Queue.YieldCurrent(Work(entry).AttemptKey != null);
            Main.Coordination.BotCache.PublishQueueInfo();
        }

        private bool ObservationLanded(BuffEntry entry, CastWork work)
            => work.AttemptKey != null && ManagerMemory.Current.ObservedCastLanded(
                entry.Requester.Instance, work.NanoId, work.AttemptedUtc,
                work.PreviousExpiryUtc, work.MissingBefore);

        internal void ConfirmServerEvidence()
        {
            if (DynelManager.LocalPlayer == null) return;
            foreach (var pair in _castWork.ToArray())
            {
                var entry = pair.Key;
                var work = pair.Value;
                if (!_castWork.ContainsKey(entry) || work.AttemptKey == null) continue;
                if (ObservationLanded(entry, work))
                {
                    Logger.Information("Server/Manager confirmed '" + entry.NanoEntry.Name +
                        "' for requester " + entry.Requester.Instance + " nano=" + work.NanoId);
                    CompleteBuffEntry(entry, completed: true);
                }
                else if (work.CompletionUtc == default(DateTime))
                {
                    int target = PaidBufferCatalogue.FindEffect(work.NanoId)?.IsTeam == true
                        ? DynelManager.LocalPlayer.Identity.Instance : entry.Requester.Instance;
                    if (ManagerMemory.Current.ServerNanoCastExecuted(DynelManager.LocalPlayer.Identity.Instance,
                        target, work.NanoId, work.AttemptedUtc)) MarkCastCompleted(entry, work);
                }
            }
        }

        private void ProcessAwaitingObservations()
        {
            ConfirmServerEvidence();
            foreach (var entry in Queue.Awaiting)
            {
                var work = Work(entry);
                if (ObservationLanded(entry, work))
                {
                    Logger.Information("Manager NCU confirmed '" + entry.NanoEntry.Name +
                        "' for requester " + entry.Requester.Instance + " nano=" + work.NanoId);
                    CompleteBuffEntry(entry, completed: true);
                    continue;
                }
                if (DateTime.UtcNow >= work.DeadlineUtc)
                {
                    CityBufferBridge.Diagnostic(entry.Requester.Instance,
                        "Buff request expired: " + entry.NanoEntry.Name);
                    CompleteBuffEntry(entry);
                    continue;
                }
                if (DynelManager.LocalPlayer.IsCasting) continue;
                // A newly sampled snapshot may still contain old server data.
                // Completion is positive evidence: don't undo it because Manager's
                // remote client has not received the target update yet.
                if (work.CompletionUtc != default(DateTime)) continue;
                if (work.FeedbackUtc <= work.AttemptedUtc) continue;
                // The server responded without completion. Keep the original NCU
                // baseline, and put the request behind other admitted work.
                if (ReturnObservedFailure(entry, work)) continue;
                Queue.RetryAtTail(entry);
            }
        }

        private bool ReturnObservedFailure(BuffEntry entry, CastWork work)
        {
            if (entry.NanoEntry.ObserverAssignmentId == null) return false;
            if (!ManagerMemory.Current.BufferBotInfos().Any(b =>
                !string.Equals(b.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                !ManagerMemory.Current.IsPaidBuffer(b.Character) && b.Ready && b.InPlay &&
                b.ObservedUtc >= DateTime.UtcNow.AddSeconds(-5) &&
                (b.AdvertisedBuffs ?? new BufferAdvertisedBuff[0]).Any(n => n.Type == "Single" &&
                    (n.NanoIds ?? new int[0]).Contains(work.NanoId)))) return false;
            ManagerMemory.Current.ReleaseObservedBuffRefresh(Client.CharacterName,
                entry.Requester.Instance, entry.NanoEntry.LevelToId[0].Id, entry.NanoEntry.ObserverAssignmentId);
            Queue.Remove(entry);
            _castWork.Remove(entry);
            if (ReferenceEquals(_feedbackEntry, entry)) _feedbackEntry = null;
            CityBufferBridge.Diagnostic(entry.Requester.Instance,
                "Returning " + entry.NanoEntry.Name + " after game feedback for another capable buffer.");
            Main.Coordination.BotCache.PublishQueueInfo();
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
                _castWork.Remove(Queue.Current);
                Queue.ClearCurrent();
                Main.Coordination.BotCache.PublishQueueInfo();
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
            if (DateTime.UtcNow >= Work(Queue.Current).DeadlineUtc)
            {
                Logger.Warning("Buff request expired after two minutes of cast processing: " +
                    Queue.Current.NanoEntry.Name + "; requester=" + Queue.Current.Requester.Instance + ".");
                ReportCurrentDiagnostic("couldn't cast", "retry limit reached after two minutes" +
                    (string.IsNullOrEmpty(Work(Queue.Current).LastRetryReason) ? "." : ": " + Work(Queue.Current).LastRetryReason));
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

        private bool TryCompleteFromObservation()
        {
            if (Queue.Current == null || Work(Queue.Current).ObservationKey == null ||
                Work(Queue.Current).ObservationKey != CurrentCastAttemptKey()) return false;
            if (!ManagerMemory.Current.ObservedCastLanded(Queue.Current.Requester.Instance,
                Work(Queue.Current).NanoId, Work(Queue.Current).AttemptedUtc, Work(Queue.Current).PreviousExpiryUtc,
                Work(Queue.Current).MissingBefore)) return false;
            Logger.Information("Manager NCU confirmed '" + Queue.Current.NanoEntry.Name +
                "' for requester " + Queue.Current.Requester.Instance + " nano=" + Work(Queue.Current).NanoId +
                (Work(Queue.Current).MissingBefore ? "; learned buff appeared after cast attempt." :
                    "; duration renewed after cast attempt."));
            ResetCurrentBuffEntry(completed: true);
            return true;
        }

        // Feedback has no nano id; correlate it with this caster's last send,
        // never whichever recipient happens to occupy the queue's active slot.
        internal bool HasOutstandingCast => _feedbackEntry != null && _castWork.ContainsKey(_feedbackEntry);

        internal void RetryFeedback(string reason)
        {
            if (!HasOutstandingCast) return;
            Work(_feedbackEntry).FeedbackUtc = DateTime.UtcNow;
            ReportRetry(_feedbackEntry, reason);
        }

        internal void RecordFeedback(string reason)
        {
            if (HasOutstandingCast) ReportRetry(_feedbackEntry, reason);
        }

        internal void SuccessfulCastFeedback()
        {
            if (HasOutstandingCast) MarkCastCompleted(_feedbackEntry, Work(_feedbackEntry));
        }

        internal void FinishFeedback(LdbFeedback feedback)
        {
            if (HasOutstandingCast) CompleteBuffEntry(_feedbackEntry, feedback);
        }

        internal void RetryCurrentBuffEntry(string reason)
        {
            if (Queue.Current != null) ReportRetry(Queue.Current, reason);
        }

        private void ReportRetry(BuffEntry entry, string reason)
        {
            var work = Work(entry);
            if (work.LastRetryReason != reason)
                CityBufferBridge.Diagnostic(entry.Requester.Instance,
                    "Retaining " + entry.NanoEntry.Name + ": " + reason);
            work.LastRetryReason = reason;
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

        internal void FinishNanoCasting(int nanoId)
        {
            var match = _castWork.Where(p => p.Value.NanoId == nanoId &&
                p.Value.AttemptKey != null && p.Value.CompletionUtc == default(DateTime) &&
                p.Value.FeedbackUtc <= p.Value.AttemptedUtc)
                .OrderBy(p => p.Value.AttemptedUtc).FirstOrDefault();
            if (match.Key == null) return;
            MarkCastCompleted(match.Key, match.Value);
        }

        private void MarkCastCompleted(BuffEntry entry, CastWork work)
        {
            if (work.CompletionUtc != default(DateTime)) return;
            work.CompletionUtc = DateTime.UtcNow;
            Logger.Information("Finished casting '" + entry.NanoEntry.Name + "' for requester " + entry.Requester.Instance);
            bool teamRecipient = PaidBufferCatalogue.FindEffect(work.NanoId)?.IsTeam == true &&
                entry.Requester != DynelManager.LocalPlayer.Identity;
            if ((!teamRecipient && work.ObservationKey == null) || ObservationLanded(entry, work))
                CompleteBuffEntry(entry, completed: true);
            else
            {
                // A late completion can arrive after a failure was put at the tail.
                // Remove it from the ready queue as well as from the active slot.
                Queue.AwaitConfirmation(entry);
                Main.Coordination.BotCache.PublishQueueInfo();
            }
            // Otherwise this entry remains awaiting Manager, not eligible for an
            // immediate recast. The caster can serve the rest of the queue.
        }

        public void ResetCurrentBuffEntry(LdbFeedback? feedback = null, bool completed = false)
            => CompleteBuffEntry(Queue.Current, feedback, completed);

        private void CompleteBuffEntry(BuffEntry entry, LdbFeedback? feedback = null, bool completed = false)
        {
            if (entry == null) return;
            ManagerMemory.Current.ReportBufferCastFinished(unchecked((uint)entry.Requester.Instance),
                entry.NanoEntry.Tags.ToArray());
            if (feedback != null)
                CityBufferBridge.Diagnostic(entry.Requester.Instance, entry.NanoEntry.Name + ": " + FeedbackReason(feedback.Value));
            if (feedback != null)
                Logger.Warning("Buff feedback for requester " + entry.Requester.Instance +
                    ": " + feedback.Value);

            if (completed)
            {
                if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession) &&
                    (entry.NanoEntry.ContainsId(275043) || entry.NanoEntry.ContainsId(235291)))
                {
                    // Keep the team and session alive briefly for the team effect to land.
                    _paidFixerPostCastUntilUtc = DateTime.UtcNow.AddSeconds(1);
                }
                if (!Main.PaidPilot)
                    DynelManager.LocalPlayer.TryRemoveBuffs(entry.NanoEntry.RemoveNanoIdUponCast);
                if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession) &&
                    (entry.NanoEntry.ContainsId(275043) || entry.NanoEntry.ContainsId(235291)))
                {
                    int teamNano = entry.NanoEntry.ContainsId(275043) ? 275043 : 235291;
                    foreach (int recipient in Queue.RemoveQueuedTeamBuff(teamNano, Work(entry).TeamMembers))
                        CityBufferBridge.Diagnostic(recipient,
                            Client.CharacterName + " finished casting " + entry.NanoEntry.Name + " for the team.");
                }
            }
            Work(entry).TeamMembers = new int[0];
            if (entry.NanoEntry.ObserverAssignmentId != null)
                ManagerMemory.Current.ReleaseObservedBuffRefresh(Client.CharacterName,
                    entry.Requester.Instance, entry.NanoEntry.LevelToId[0].Id, entry.NanoEntry.ObserverAssignmentId);

            Logger.Information("RESET TRIGGERED");
            _castWork.Remove(entry);
            if (ReferenceEquals(_feedbackEntry, entry)) _feedbackEntry = null;
            Queue.Remove(entry);
            var remaining = Queue.AllEntries;
            foreach (var retired in _castWork.Keys.Where(e => !remaining.Contains(e)).ToArray())
                _castWork.Remove(retired);
            Main.Coordination.BotCache.PublishQueueInfo();
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

            // FinishNanoCasting carries a nano id, not a recipient. Keep at most
            // one unanswered send of this nano so two nearby players cannot
            // borrow each other's completion. Other nanos remain ready to serve.
            if (_castWork.Any(p => !ReferenceEquals(p.Key, Queue.Current) &&
                p.Value.NanoId == firstAvailableBuff.Id && p.Value.AttemptKey != null &&
                ((p.Value.CompletionUtc == default(DateTime) &&
                  p.Value.FeedbackUtc <= p.Value.AttemptedUtc) ||
                 (p.Value.TeamMembers.Contains(Queue.Current.Requester.Instance) &&
                  p.Value.CompletionUtc != default(DateTime))))) return;

            Logger.Information($"Attempting to cast '{Queue.Current.NanoEntry.Name}' on '{buffTarget.Name}'");

            if (Main.SettingsJson.Data.DanceOnCast)
            {
                Client.Send(new SocialActionCmdMessage
                {
                    Unknown5 = 0x3E,
                    Unknown = 1,
                    Action = (SocialAction)Main.SettingsJson.Data.SocialAction
                });
            }

            bool firstAttempt = Work(Queue.Current).AttemptKey != CurrentCastAttemptKey();
            Work(Queue.Current).AttemptKey = CurrentCastAttemptKey();
            if (firstAttempt)
            {
                Work(Queue.Current).ObservationKey = null;
                Work(Queue.Current).MissingBefore = false;
                Work(Queue.Current).NanoId = firstAvailableBuff.Id;
                Work(Queue.Current).AttemptedUtc = DateTime.UtcNow;
                if (Queue.Current.Requester != DynelManager.LocalPlayer.Identity)
                {
                    var before = ManagerMemory.Current.ReadNearbyPlayers().FirstOrDefault(p =>
                        p.CharacterId == Queue.Current.Requester.Instance);
                    var oldNano = before?.Nanos.FirstOrDefault(n => n.Id == firstAvailableBuff.Id ||
                        PaidBufferCatalogue.FindEffect(firstAvailableBuff.Id)?.MatchesEffect(n.Id) == true);
                    // Preserve the first baseline across retries; never chase the new expiry.
                    if (oldNano != null && oldNano.RemainingSeconds > 0)
                    {
                        Work(Queue.Current).ObservationKey = Work(Queue.Current).AttemptKey;
                        Work(Queue.Current).PreviousExpiryUtc = before.ObservedUtc.AddSeconds(oldNano.RemainingSeconds);
                    }
                    else if (before != null)
                    {
                        Work(Queue.Current).ObservationKey = Work(Queue.Current).AttemptKey;
                        Work(Queue.Current).MissingBefore = true;
                    }
                }
            }
            Work(Queue.Current).AttemptedUtc = DateTime.UtcNow;
            Work(Queue.Current).CompletionUtc = default(DateTime);
            Work(Queue.Current).FeedbackUtc = default(DateTime);
            _feedbackEntry = Queue.Current;
            // Team emitters are cast on the caster; the server applies their effects to teammates.
            PlayerChar castTarget = PaidBufferCatalogue.FindEffect(firstAvailableBuff.Id)?.IsTeam == true
                ? DynelManager.LocalPlayer : buffTarget;
            if (Main.PaidPilot && PaidBufferCatalogue.UsesManagerTeam(Main.PaidProfession) &&
                PaidBufferCatalogue.FindEffect(firstAvailableBuff.Id)?.IsTeam == true)
                Work(Queue.Current).TeamMembers = Team.Members.Select(m => m.Identity.Instance).ToArray();
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

            if (DateTime.UtcNow >= Work(Queue.Current).TeamDeadlineUtc &&
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
                else if (!Queue.Awaiting.Any(e => e.NanoEntry.Type == CastType.Team &&
                    Team.Members.Any(m => m.Identity == e.Requester))) LeaveTeam();
                return;
            }
            if (Work(Queue.Current).InviteRequester != Queue.Current.Requester.Instance)
            {
                Work(Queue.Current).InviteRequester = Queue.Current.Requester.Instance;
                ResetTeamTimer();
                Team.Invite(Queue.Current.Requester);
                Logger.Information("Direct team invitation sent to requester " + Work(Queue.Current).InviteRequester +
                    " for " + Queue.Current.NanoEntry.Name + ".");
                CityBufferBridge.RequestResult(Work(Queue.Current).InviteRequester,
                    "Accept " + Client.CharacterName + "'s team invitation for " + Queue.Current.NanoEntry.Name + ".");
            }
        }
    }
}
