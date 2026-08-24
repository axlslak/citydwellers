using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;

namespace CityManager
{
    public partial class CityManager
    {
        private const int RaidConfigurationSeconds = 180;
        private const int RaidAdminVetoSeconds = 60;
        private const int RaidControllerFillSeconds = 60;
        private const int RaidCooldownSeconds = 600;
        private const int RaidTargetTimeoutSeconds = 300;
        private const int RaidWorkerGraceSeconds = 120;
        private const int GeneralBuddyStartOffsetSeconds = 1005;
        private const int BuddyLogoutOffsetSeconds = 1125;
        private const float MinimumRaidControllerCharge = 0.75f;

        // Nadybot's established AP city schedule, verified against the supplied
        // raid log. Values are cumulative seconds after CITY_ATTACKED.
        private static readonly int[] RaidMilestoneOffsets =
        {
            105,
            255,
            345,
            465,
            585,
            705,
            825,
            945,
            1065
        };

        private readonly object _raidSync = new object();
        private readonly Dictionary<string, DateTime> _raidCooldowns =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private RaidSession _raidSession;
        private DateTime _nextRaidTickUtc = DateTime.MinValue;
        private bool _raidCoordinatorShuttingDown;

        private void InitializeRaidCoordinator()
        {
            lock (_raidSync)
            {
                _raidCoordinatorShuttingDown = false;
                _raidSession = null;
                _raidCooldowns.Clear();
                _nextRaidTickUtc = DateTime.MinValue;
            }

            Logger.Information(
                "Raid coordinator initialized: 3m setup, 1m admin veto, 1m CT fill, 75% minimum charge.");
        }

        private void ShutdownRaidCoordinator()
        {
            lock (_raidSync)
            {
                _raidCoordinatorShuttingDown = true;
                _raidSession = null;
                _raidCooldowns.Clear();
            }
        }

        private bool HasTellRaidCommandShape(string[] parts)
        {
            if (parts == null || parts.Length == 0)
                return false;

            if (parts.Length == 1)
                return true;

            if (parts.Length == 3 &&
                (string.Equals(parts[1], "start", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parts[1], "refresh", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return parts.Length == 4 &&
                   (string.Equals(parts[1], "type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parts[1], "count", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parts[1], "level", StringComparison.OrdinalIgnoreCase));
        }

        private void ProcessRaidCommand(
            string senderName,
            string[] parts,
            ReplyTarget target,
            bool isAdmin)
        {
            bool privateOwnerControl =
                target.Kind == ReplyKind.Tell &&
                parts.Length > 1 &&
                IsCurrentRaidOwnerCommand(senderName, target.SenderId, parts);

            if (!target.IsOrg && !isAdmin && !privateOwnerControl)
            {
                DevTrace(
                    $"RAID DENIED {target.Kind} {senderName}: public raid commands begin in org chat.");
                Reply(target, "Use #raid in organization chat.");
                return;
            }

            if (parts.Length == 1)
            {
                BeginOrReopenRaid(senderName, target);
                return;
            }

            RaidSession session;
            if (!TryGetOwnedRaidSession(senderName, target.SenderId, parts, out session))
            {
                Reply(target, "That raid interface is no longer active, or it belongs to someone else.");
                return;
            }

            string action = parts[1].ToLowerInvariant();

            if (action == "refresh" && parts.Length == 3)
            {
                Reply(session.Origin, BuildRaidWindow(session));
                return;
            }

            if (action == "start" && parts.Length == 3)
            {
                StartRaidApproval(session);
                return;
            }

            if (parts.Length != 4)
            {
                Reply(target, Usage(target, "raid"));
                return;
            }

            if (action == "type")
            {
                string raidType = parts[2].ToLowerInvariant();
                if (raidType != "all" && raidType != "general")
                {
                    Reply(target, "Raid type must be all or general.");
                    return;
                }

                UpdateRaidSelection(session, raidType, null, null);
                return;
            }

            if (action == "count")
            {
                int count;
                if (!int.TryParse(parts[2], out count) || count < 0 || count > 12)
                {
                    Reply(target, "Raiders must be between 0 and 12.");
                    return;
                }

                UpdateRaidSelection(session, null, count, null);
                return;
            }

            if (action == "level")
            {
                int level;
                if (!int.TryParse(parts[2], out level) || level != 200)
                {
                    Reply(target, "Only the level 200 City Dwellers bracket is available right now.");
                    return;
                }

                UpdateRaidSelection(session, null, null, level);
                return;
            }

            Reply(target, Usage(target, "raid"));
        }

        private void BeginOrReopenRaid(string senderName, ReplyTarget target)
        {
            RaidSession existing;
            DateTime cooldownUntil;
            DateTime now = DateTime.UtcNow;

            lock (_raidSync)
            {
                RemoveExpiredCooldownsLocked(now);

                if (_raidCooldowns.TryGetValue(senderName ?? string.Empty, out cooldownUntil) &&
                    cooldownUntil > now)
                {
                    Reply(
                        target,
                        $"You may request another raid in {FormatDuration(cooldownUntil - now)}.");
                    return;
                }

                existing = _raidSession;
                if (existing != null)
                {
                    if (IsRaidOwner(existing, senderName, target.SenderId))
                        Reply(existing.Origin, BuildRaidWindow(existing));
                    else
                        Reply(target, "Raid in progress already.");

                    return;
                }

                _raidSession = new RaidSession
                {
                    Token = Guid.NewGuid().ToString("N").Substring(0, 10),
                    OwnerName = senderName,
                    OwnerId = target.SenderId,
                    Origin = target,
                    Stage = RaidStage.Configuring,
                    StageDeadlineUtc = now.AddSeconds(RaidConfigurationSeconds),
                    Level = 200,
                    CurrentMilestone = -1,
                    CreatedUtc = now
                };

                existing = _raidSession;
            }

            Logger.Information(
                $"Raid setup opened by {senderName}; token={existing.Token}, origin={target.Kind}.");
            DevTrace(
                $"RAID SETUP owner={senderName} token={existing.Token} origin={target.Kind} deadline={existing.StageDeadlineUtc:O}.");
            Reply(existing.Origin, BuildRaidWindow(existing));
        }

        private bool IsCurrentRaidOwnerCommand(
            string senderName,
            uint senderId,
            string[] parts)
        {
            string token = parts[parts.Length - 1];

            lock (_raidSync)
            {
                return _raidSession != null &&
                       IsRaidOwner(_raidSession, senderName, senderId) &&
                       string.Equals(_raidSession.Token, token, StringComparison.Ordinal);
            }
        }

        private bool TryGetOwnedRaidSession(
            string senderName,
            uint senderId,
            string[] parts,
            out RaidSession session)
        {
            session = null;
            string token = parts[parts.Length - 1];

            lock (_raidSync)
            {
                if (_raidSession == null ||
                    !IsRaidOwner(_raidSession, senderName, senderId) ||
                    !string.Equals(_raidSession.Token, token, StringComparison.Ordinal))
                {
                    return false;
                }

                session = _raidSession;
                return true;
            }
        }

        private bool IsRaidOwner(RaidSession session, string senderName, uint senderId)
        {
            if (session == null)
                return false;

            if (session.OwnerId != 0 && senderId != 0)
                return session.OwnerId == senderId;

            return string.Equals(
                session.OwnerName,
                senderName,
                StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateRaidSelection(
            RaidSession session,
            string raidType,
            int? count,
            int? level)
        {
            bool updated = false;

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.Configuring ||
                    DateTime.UtcNow >= session.StageDeadlineUtc)
                {
                    return;
                }

                if (raidType != null)
                    session.RaidType = raidType;
                if (count.HasValue)
                    session.RaiderCount = count;
                if (level.HasValue)
                    session.Level = level.Value;

                updated = true;
            }

            if (updated)
            {
                DevTrace(
                    $"RAID SELECT owner={session.OwnerName} type={session.RaidType ?? "unset"} " +
                    $"level={session.Level} count={(session.RaiderCount.HasValue ? session.RaiderCount.Value.ToString() : "unset")}.");
                Reply(session.Origin, BuildRaidWindow(session));
            }
        }

        private void StartRaidApproval(RaidSession session)
        {
            string error = null;
            bool expired = false;

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.Configuring)
                {
                    error = "That raid is no longer in its setup stage.";
                }
                else if (DateTime.UtcNow >= session.StageDeadlineUtc)
                {
                    expired = true;
                }
                else if (string.IsNullOrWhiteSpace(session.RaidType) ||
                         !session.RaiderCount.HasValue)
                {
                    error = "Select raid type and number of raiders before pressing Raid.";
                }
                else
                {
                    session.Stage = RaidStage.AdminVeto;
                    session.StageDeadlineUtc =
                        DateTime.UtcNow.AddSeconds(RaidAdminVetoSeconds);
                }
            }

            if (expired)
            {
                FailRaidSession(
                    session,
                    "Raid setup expired before it was started.",
                    true);
                return;
            }

            if (error != null)
            {
                Reply(session.Origin, error);
                return;
            }

            Logger.Warning(
                $"Raid requested by {session.OwnerName}: type={session.RaidType}, " +
                $"level={session.Level}, count={session.RaiderCount.Value}. Admin veto window opened.");
            DevTrace(
                $"RAID VETO owner={session.OwnerName} token={session.Token} deadline={session.StageDeadlineUtc:O}.");

            Reply(session.Origin, BuildRaidWindow(session));
            SendRaidAnnouncementToAdmins(session);
        }

        private void SendRaidAnnouncementToAdmins(RaidSession session)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (string admin in AdminCommandSenders)
                {
                    try
                    {
                        uint adminId;
                        if (!TryResolveCharacterId(admin, out adminId))
                        {
                            DevTrace($"RAID ADMIN NOTICE: unable to resolve {admin}.");
                            continue;
                        }

                        string message =
                            $"{session.OwnerName} requested a {session.RaidType} city raid: " +
                            $"{session.RaiderCount.Value} level-{session.Level} raiders. " +
                            $"One minute to cancel. " +
                            $"<a href='chatcmd:///tell Apcmanager #cancel {session.Token}'>Cancel raid</a>";

                        Client.SendPrivateMessage(adminId, message);
                    }
                    catch (Exception ex)
                    {
                        DevTrace($"RAID ADMIN NOTICE error for {admin}: {ex.Message}");
                    }
                }
            });
        }

        private void ProcessRaidCancel(
            string senderName,
            string[] parts,
            ReplyTarget commandTarget)
        {
            RaidSession session;
            string error = null;

            lock (_raidSync)
            {
                session = _raidSession;

                if (session == null)
                {
                    error = "There is no raid request to cancel.";
                }
                else if (session.Stage != RaidStage.AdminVeto)
                {
                    error = "The admin cancellation window is not open.";
                }
                else if (DateTime.UtcNow >= session.StageDeadlineUtc)
                {
                    error = "The admin cancellation window has closed.";
                }
                else if (parts.Length > 2)
                {
                    error = Usage(commandTarget, "cancel [raid-token]");
                }
                else if (parts.Length == 2 &&
                         !string.Equals(parts[1], session.Token, StringComparison.Ordinal))
                {
                    error = "That raid cancellation link is no longer active.";
                }
                else
                {
                    _raidSession = null;
                    ApplyRaidCooldownLocked(session.OwnerName, DateTime.UtcNow);
                }
            }

            if (error != null)
            {
                Reply(commandTarget, error);
                return;
            }

            string message =
                $"Raid canceled by {senderName}. {session.OwnerName} has a 10-minute raid cooldown.";

            Logger.Warning(message);
            DevTrace($"RAID CANCELED admin={senderName} owner={session.OwnerName}.");
            Reply(session.Origin, message);

            if (commandTarget.Kind != session.Origin.Kind)
                Reply(commandTarget, "Raid canceled.");
        }

        private void TickRaidCoordinator()
        {
            DateTime now = DateTime.UtcNow;

            if (now < _nextRaidTickUtc)
                return;

            _nextRaidTickUtc = now.AddSeconds(1);

            RaidSession session;
            RaidStage stage;
            DateTime deadline;

            lock (_raidSync)
            {
                if (_raidCoordinatorShuttingDown || _raidSession == null)
                    return;

                session = _raidSession;
                stage = session.Stage;
                deadline = session.StageDeadlineUtc;
            }

            if (stage == RaidStage.Configuring && now >= deadline)
            {
                FailRaidSession(
                    session,
                    "Raid setup expired before it was started.",
                    true);
                return;
            }

            if (stage == RaidStage.AdminVeto && now >= deadline)
            {
                BeginControllerFillStage(session);
                return;
            }

            if (stage == RaidStage.ControllerFill && now >= deadline)
            {
                TryBeginRaidLaunch(session, now);
                return;
            }

            if (stage == RaidStage.AwaitingCityTarget && now >= deadline)
            {
                FailRaidSession(
                    session,
                    "The city-targeted event did not arrive after the cloak was lowered.",
                    true);
                return;
            }

            if (stage == RaidStage.Active)
                TickActiveRaid(session, now);
        }

        private void BeginControllerFillStage(RaidSession session)
        {
            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.AdminVeto)
                {
                    return;
                }

                session.Stage = RaidStage.ControllerFill;
                session.StageDeadlineUtc =
                    DateTime.UtcNow.AddSeconds(RaidControllerFillSeconds);
                session.WorkerDeadlineUtc =
                    session.StageDeadlineUtc.AddSeconds(RaidWorkerGraceSeconds);
                session.ControllerProbeInFlight = true;
            }

            Logger.Warning(
                $"Raid for {session.OwnerName} passed admin veto. CT fill window opened.");
            DevTrace(
                $"RAID CT FILL owner={session.OwnerName} deadline={session.StageDeadlineUtc:O}; " +
                $"minimum={MinimumRaidControllerCharge * 100:F0}%.");
            Reply(session.Origin, BuildRaidWindow(session));

            BeginFreshControllerProbe(session);

            if (string.Equals(session.RaidType, "all", StringComparison.OrdinalIgnoreCase))
                BeginRaidBuddySpinup(session, "all-mode start");
        }

        private void BeginFreshControllerProbe(RaidSession session)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = new WorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = "probe"
                    };

                    string shortId = ShortId(request.Id);
                    DevTrace($"RAID FLIPPER -> fresh CT probe [{shortId}].");

                    WorkerResponse response = SendWorkerRequest(
                        FlipperPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    if (response.Ok)
                        ApplyFlipperObservation(response);

                    lock (_raidSync)
                    {
                        if (ReferenceEquals(_raidSession, session))
                        {
                            session.LastControllerCharge = response.ControllerCharge;
                            session.FlipperDetail = response.Message;
                            session.ControllerProbeInFlight = false;
                        }
                    }

                    DevTrace(
                        $"RAID FLIPPER {(response.Ok ? "OK" : "FAIL")} [{shortId}]: " +
                        $"charge={FormatCharge(response.ControllerCharge)}; {response.Message}");

                    if (IsCurrentRaidSession(session))
                        Reply(session.Origin, BuildRaidWindow(session));
                }
                catch (Exception ex)
                {
                    lock (_raidSync)
                    {
                        if (ReferenceEquals(_raidSession, session))
                        {
                            session.ControllerProbeInFlight = false;
                            session.FlipperDetail = ex.Message;
                        }
                    }

                    DevTrace($"RAID FLIPPER probe error: {ex.Message}");
                    if (IsCurrentRaidSession(session))
                        Reply(session.Origin, BuildRaidWindow(session));
                }
            });
        }

        private void BeginRaidBuddySpinup(RaidSession session, string reason)
        {
            int count = session.RaiderCount ?? 0;

            if (count == 0)
            {
                lock (_raidSync)
                {
                    session.BuddySpinupRequested = true;
                    session.BuddyDetail = "No City Dwellers requested.";
                }
                return;
            }

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) || session.BuddySpinupInFlight)
                    return;

                session.BuddySpinupInFlight = true;
                session.BuddySpinupRequested = true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                WorkerResponse response = null;

                try
                {
                    var request = new WorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = "spinup",
                        Level = session.Level,
                        Index = count
                    };

                    string shortId = ShortId(request.Id);
                    DevTrace(
                        $"RAID BUDDIES -> spinup level={session.Level} count={count} [{shortId}] reason={reason}.");

                    response = SendWorkerRequest(
                        BuddiesPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    bool stillCurrent;

                    lock (_raidSync)
                    {
                        MergeStartedBuddyIndexes(session, response.Indexes);
                        stillCurrent = ReferenceEquals(_raidSession, session);

                        if (stillCurrent)
                        {
                            session.BuddySpinupInFlight = false;
                            session.BuddyDetail = response.Message;
                            session.BuddySpinupFatal =
                                string.Equals(session.RaidType, "all", StringComparison.OrdinalIgnoreCase) &&
                                count > 0 &&
                                session.StartedBuddyIndexes.Count < count;
                        }
                    }

                    DevTrace(
                        $"RAID BUDDIES {(response.Ok ? "OK" : "FAIL")} [{shortId}]: {response.Message}");

                    if (!stillCurrent)
                    {
                        QueueDetachedBuddyCleanup(session);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lock (_raidSync)
                    {
                        if (ReferenceEquals(_raidSession, session))
                        {
                            session.BuddySpinupInFlight = false;
                            session.BuddyDetail = ex.Message;
                            session.BuddySpinupFatal =
                                string.Equals(session.RaidType, "all", StringComparison.OrdinalIgnoreCase) &&
                                count > 0;
                        }
                    }

                    DevTrace($"RAID BUDDIES spinup error: {ex.Message}");
                }

                if (IsCurrentRaidSession(session))
                    Reply(session.Origin, BuildRaidWindow(session));
            });
        }

        private void MergeStartedBuddyIndexes(
            RaidSession session,
            List<int> indexes)
        {
            if (indexes == null)
                return;

            foreach (int index in indexes)
            {
                if (!session.StartedBuddyIndexes.Contains(index))
                    session.StartedBuddyIndexes.Add(index);
            }
        }

        private void TryBeginRaidLaunch(RaidSession session, DateTime now)
        {
            bool waiting;
            bool announceWait = false;
            bool fatal;

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.ControllerFill)
                {
                    return;
                }

                waiting = session.ControllerProbeInFlight || session.BuddySpinupInFlight;
                fatal = session.BuddySpinupFatal;

                if (waiting && !session.WorkerWaitAnnounced)
                {
                    session.WorkerWaitAnnounced = true;
                    announceWait = true;
                }
            }

            if (waiting)
            {
                if (announceWait)
                    Reply(session.Origin, BuildRaidWindow(session));

                if (now >= session.WorkerDeadlineUtc)
                {
                    FailRaidSession(
                        session,
                        "A raid worker did not finish in time.",
                        true);
                }

                return;
            }

            if (fatal)
            {
                FailRaidSession(
                    session,
                    $"Only {session.StartedBuddyIndexes.Count}/{session.RaiderCount.Value} " +
                    "requested all-mode City Dwellers could log in.",
                    true);
                return;
            }

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.ControllerFill)
                {
                    return;
                }

                session.Stage = RaidStage.LoweringCloak;
            }

            Reply(session.Origin, BuildRaidWindow(session));
            BeginSafeRaidCloakLower(session);
        }

        private void BeginSafeRaidCloakLower(RaidSession session)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = new WorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = "ensure-disabled-ready"
                    };

                    string shortId = ShortId(request.Id);
                    DevTrace(
                        $"RAID FLIPPER -> lower-if-ready [{shortId}] minimum={MinimumRaidControllerCharge * 100:F0}%.");

                    WorkerResponse response = SendWorkerRequest(
                        FlipperPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    if (response.Ok)
                        ApplyFlipperObservation(response);

                    bool started =
                        response.Ok &&
                        string.Equals(
                            response.CloakState,
                            "Disabled",
                            StringComparison.OrdinalIgnoreCase) &&
                        response.ControllerCharge.HasValue &&
                        response.ControllerCharge.Value >= MinimumRaidControllerCharge;

                    if (!started)
                    {
                        string reason =
                            $"Raid did not start. CT charge was {FormatCharge(response.ControllerCharge)}; " +
                            $"75% is required. {response.Message}";

                        DevTrace($"RAID FLIPPER FAIL [{shortId}]: {reason}");
                        FailRaidSession(session, reason, true);
                        return;
                    }

                    lock (_raidSync)
                    {
                        if (!ReferenceEquals(_raidSession, session) ||
                            session.Stage != RaidStage.LoweringCloak)
                        {
                            return;
                        }

                        session.LastControllerCharge = response.ControllerCharge;
                        session.FlipperDetail = response.Message;
                        session.Stage = RaidStage.AwaitingCityTarget;
                        session.StageDeadlineUtc =
                            DateTime.UtcNow.AddSeconds(RaidTargetTimeoutSeconds);
                    }

                    Logger.Warning(
                        $"Raid cloak lowered for {session.OwnerName}; charge={FormatCharge(response.ControllerCharge)}.");
                    DevTrace(
                        $"RAID STARTED owner={session.OwnerName} charge={FormatCharge(response.ControllerCharge)}; " +
                        $"waiting for CITY_ATTACKED until {session.StageDeadlineUtc:O}.");
                    Reply(session.Origin, BuildRaidWindow(session));
                }
                catch (Exception ex)
                {
                    DevTrace($"RAID FLIPPER lower error: {ex.Message}");
                    FailRaidSession(
                        session,
                        $"Raid could not verify and lower the cloak: {ex.Message}",
                        true);
                }
            });
        }

        private void ObserveRaidCityMessage(string message)
        {
            string location;
            if (!TryGetCityTargetLocation(message, out location))
                return;

            RaidSession session;
            DateTime now = DateTime.UtcNow;

            lock (_raidSync)
            {
                session = _raidSession;
                if (session == null || session.Stage != RaidStage.AwaitingCityTarget)
                    return;

                session.Stage = RaidStage.Active;
                session.CityTargetedUtc = now;
                session.CurrentMilestone = -1;
                session.StageDeadlineUtc = DateTime.MaxValue;
            }

            Logger.Warning(
                $"Raid city-targeted event accepted for {session.OwnerName} at {now:O}; location={location}.");
            DevTrace(
                $"RAID TIMER START owner={session.OwnerName} location={location} anchor={now:O}; " +
                $"wave8=+945s general=+1065s cleanup=+1125s.");
            Reply(session.Origin, BuildRaidWindow(session));
        }

        private bool TryGetCityTargetLocation(string message, out string location)
        {
            location = null;
            const string prefix = "Your city in ";
            const string suffix = " has been targeted by hostile forces.";

            if (string.IsNullOrWhiteSpace(message) ||
                !message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !message.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                message.Length <= prefix.Length + suffix.Length)
            {
                return false;
            }

            location = message.Substring(
                prefix.Length,
                message.Length - prefix.Length - suffix.Length).Trim();
            return location.Length > 0;
        }

        private void TickActiveRaid(RaidSession session, DateTime now)
        {
            int elapsed = Math.Max(
                0,
                (int)Math.Floor((now - session.CityTargetedUtc).TotalSeconds));

            int milestone = -1;
            for (int index = 0; index < RaidMilestoneOffsets.Length; index++)
            {
                if (elapsed >= RaidMilestoneOffsets[index])
                    milestone = index;
                else
                    break;
            }

            bool milestoneChanged = false;

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.Active)
                {
                    return;
                }

                if (milestone > session.CurrentMilestone)
                {
                    session.CurrentMilestone = milestone;
                    milestoneChanged = true;
                }
            }

            if (milestoneChanged)
            {
                string milestoneText = RaidMilestoneText(milestone);
                DevTrace(
                    $"RAID TIMER owner={session.OwnerName} elapsed={elapsed}s milestone={milestoneText}.");
                Reply(session.Origin, BuildRaidWindow(session));
            }

            if (string.Equals(session.RaidType, "general", StringComparison.OrdinalIgnoreCase) &&
                elapsed >= GeneralBuddyStartOffsetSeconds &&
                !session.BuddySpinupRequested)
            {
                BeginRaidBuddySpinup(session, "one minute after wave 8");
                Reply(session.Origin, BuildRaidWindow(session));
            }

            if (elapsed < BuddyLogoutOffsetSeconds)
                return;

            if (session.BuddySpinupInFlight)
                return;

            BeginSuccessfulRaidCleanup(session);
        }

        private string RaidMilestoneText(int milestone)
        {
            if (milestone < 0)
                return "waiting for wave 1";

            if (milestone >= 8)
                return "general incoming";

            return $"wave {milestone + 1} incoming";
        }

        private void BeginSuccessfulRaidCleanup(RaidSession session)
        {
            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.Active ||
                    session.BuddyCleanupInFlight)
                {
                    return;
                }

                session.BuddyCleanupInFlight = true;
                session.Stage = RaidStage.CleaningUp;
            }

            Reply(session.Origin, BuildRaidWindow(session));

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string cleanup = SleepRaidBuddies(session);
                bool complete = false;

                lock (_raidSync)
                {
                    if (ReferenceEquals(_raidSession, session))
                    {
                        _raidSession = null;
                        complete = true;
                    }
                }

                if (!complete)
                    return;

                string message =
                    $"City Dwellers raid support for {session.OwnerName} is complete. {cleanup}";

                Logger.Information(message);
                DevTrace($"RAID COMPLETE owner={session.OwnerName}. {cleanup}");
                Reply(session.Origin, message);
            });
        }

        private void FailRaidSession(
            RaidSession session,
            string reason,
            bool applyCooldown)
        {
            bool removed = false;

            lock (_raidSync)
            {
                if (ReferenceEquals(_raidSession, session))
                {
                    _raidSession = null;
                    if (applyCooldown)
                        ApplyRaidCooldownLocked(session.OwnerName, DateTime.UtcNow);
                    removed = true;
                }
            }

            if (!removed)
                return;

            string cooldownText = applyCooldown
                ? " A 10-minute raid cooldown now applies."
                : string.Empty;

            Logger.Warning($"Raid failed for {session.OwnerName}: {reason}");
            DevTrace($"RAID FAILED owner={session.OwnerName}: {reason}");
            Reply(session.Origin, reason + cooldownText);

            QueueDetachedBuddyCleanup(session);
        }

        private void QueueDetachedBuddyCleanup(RaidSession session)
        {
            lock (_raidSync)
            {
                if (session.StartedBuddyIndexes.Count == 0 ||
                    session.BuddyCleanupInFlight)
                {
                    return;
                }

                session.BuddyCleanupInFlight = true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result = SleepRaidBuddies(session);
                DevTrace($"RAID FAILURE CLEANUP owner={session.OwnerName}: {result}");
            });
        }

        private string SleepRaidBuddies(RaidSession session)
        {
            List<int> indexes;

            lock (_raidSync)
                indexes = new List<int>(session.StartedBuddyIndexes);

            if (indexes.Count == 0)
                return "No raid buddies needed logout.";

            int slept = 0;
            var failures = new List<string>();

            foreach (int index in indexes)
            {
                try
                {
                    var request = new WorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = "sleep",
                        Index = index
                    };

                    WorkerResponse response = SendWorkerRequest(
                        BuddiesPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    if (response.Ok)
                        slept++;
                    else
                        failures.Add($"{index}:{response.Message}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{index}:{ex.Message}");
                }
            }

            string text = $"Logged out {slept}/{indexes.Count} raid buddies.";
            if (failures.Count > 0)
                text += $" Failures: {string.Join("; ", failures)}";

            return text;
        }

        private bool IsCurrentRaidSession(RaidSession session)
        {
            lock (_raidSync)
                return ReferenceEquals(_raidSession, session);
        }

        private void ApplyRaidCooldownLocked(string ownerName, DateTime now)
        {
            if (!string.IsNullOrWhiteSpace(ownerName))
                _raidCooldowns[ownerName] = now.AddSeconds(RaidCooldownSeconds);
        }

        private void RemoveExpiredCooldownsLocked(DateTime now)
        {
            var expired = new List<string>();

            foreach (KeyValuePair<string, DateTime> item in _raidCooldowns)
            {
                if (item.Value <= now)
                    expired.Add(item.Key);
            }

            foreach (string key in expired)
                _raidCooldowns.Remove(key);
        }

        private string BuildRaidWindow(RaidSession session)
        {
            var body = new StringBuilder();

            body.Append("<font color='#89D2E8'>City Dwellers Raid</font>\n\n");
            body.Append($"Raider: <font color='#00BFFF'>{SafeRaidText(session.OwnerName)}</font>\n");
            body.Append($"Type: {RaidSelectionText(session.RaidType, "Not selected")}\n");
            body.Append($"Level: <font color='#FFFF00'>{session.Level}</font>\n");
            body.Append(
                $"Raiders: {RaidSelectionText(session.RaiderCount.HasValue ? session.RaiderCount.Value.ToString() : null, "Not selected")}\n\n");

            switch (session.Stage)
            {
                case RaidStage.Configuring:
                    AppendConfigurationControls(body, session);
                    break;

                case RaidStage.AdminVeto:
                    body.Append("<font color='#F79410'>Waiting for admin cancellation window.</font>\n");
                    body.Append(
                        $"Time remaining: <font color='#FFFF00'>{FormatDuration(session.StageDeadlineUtc - DateTime.UtcNow)}</font>\n");
                    body.Append("Admins may use #cancel during this stage.\n");
                    break;

                case RaidStage.ControllerFill:
                    body.Append("<font color='#F79410'>Fill the City Controller now.</font>\n");
                    body.Append("The raid requires at least <font color='#FFFF00'>75%</font> charge.\n");
                    body.Append(
                        $"Time remaining: <font color='#FFFF00'>{FormatDuration(session.StageDeadlineUtc - DateTime.UtcNow)}</font>\n");
                    body.Append($"Last fresh charge: <font color='#FFFF00'>{FormatCharge(session.LastControllerCharge)}</font>\n");

                    if (session.ControllerProbeInFlight)
                        body.Append("Flipper: obtaining a fresh reading...\n");
                    if (session.BuddySpinupInFlight)
                        body.Append("Buddies: logging in requested all-mode raiders...\n");
                    if (session.WorkerWaitAnnounced)
                        body.Append("CT window complete; waiting for raid workers to finish safely.\n");
                    break;

                case RaidStage.LoweringCloak:
                    body.Append("<font color='#F79410'>Flipper is verifying CT charge and lowering the cloak.</font>\n");
                    break;

                case RaidStage.AwaitingCityTarget:
                    body.Append("<font color='#00DE42'>Cloak lowered.</font> Waiting for the city-targeted event.\n");
                    body.Append($"CT charge at start: <font color='#FFFF00'>{FormatCharge(session.LastControllerCharge)}</font>\n");
                    break;

                case RaidStage.Active:
                    int elapsed = Math.Max(
                        0,
                        (int)Math.Floor((DateTime.UtcNow - session.CityTargetedUtc).TotalSeconds));
                    body.Append("<font color='#00DE42'>Raid active.</font>\n");
                    body.Append(
                        $"Timer: <font color='#FFFF00'>{FormatDuration(TimeSpan.FromSeconds(elapsed))}</font>\n");
                    body.Append(
                        $"Stage: <font color='#F79410'>{RaidMilestoneText(session.CurrentMilestone)}</font>\n");

                    if (string.Equals(session.RaidType, "general", StringComparison.OrdinalIgnoreCase))
                    {
                        int untilGeneralBuddies = GeneralBuddyStartOffsetSeconds - elapsed;
                        if (!session.BuddySpinupRequested && untilGeneralBuddies > 0)
                        {
                            body.Append(
                                $"General-only buddies in: <font color='#FFFF00'>{FormatDuration(TimeSpan.FromSeconds(untilGeneralBuddies))}</font>\n");
                        }
                    }

                    int untilLogout = BuddyLogoutOffsetSeconds - elapsed;
                    if (untilLogout > 0)
                    {
                        body.Append(
                            $"Buddy logout in: <font color='#FFFF00'>{FormatDuration(TimeSpan.FromSeconds(untilLogout))}</font>\n");
                    }
                    break;

                case RaidStage.CleaningUp:
                    body.Append("<font color='#F79410'>General phase reached. Logging out raid buddies.</font>\n");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(session.BuddyDetail))
                body.Append($"\nBuddies: {SafeRaidText(session.BuddyDetail)}\n");

            if (!string.IsNullOrWhiteSpace(session.FlipperDetail))
                body.Append($"Flipper: {SafeRaidText(session.FlipperDetail)}\n");

            return
                $"<a href=\"text://{body}\">Click here to open window</a>";
        }

        private void AppendConfigurationControls(StringBuilder body, RaidSession session)
        {
            body.Append("Select raid type:\n");
            body.Append(RaidButton(session, "type all", "All waves", session.RaidType == "all"));
            body.Append("  ");
            body.Append(RaidButton(session, "type general", "General only", session.RaidType == "general"));
            body.Append("\n\nSelect level:\n");

            int[] brackets = { 25, 50, 75, 100, 125, 150, 175 };
            foreach (int bracket in brackets)
                body.Append($"<font color='#777777'>{bracket}</font>  ");

            body.Append(RaidButton(session, "level 200", "200", session.Level == 200));
            body.Append("\n\nSelect City Dwellers:\n");

            for (int count = 0; count <= 12; count++)
            {
                if (count > 0)
                    body.Append("  ");

                body.Append(
                    RaidButton(
                        session,
                        $"count {count}",
                        count.ToString(),
                        session.RaiderCount == count));
            }

            body.Append("\n\n");
            body.Append(
                $"Setup time remaining: <font color='#FFFF00'>{FormatDuration(session.StageDeadlineUtc - DateTime.UtcNow)}</font>\n");

            if (!string.IsNullOrWhiteSpace(session.RaidType) && session.RaiderCount.HasValue)
                body.Append(RaidButton(session, "start", "START RAID", false));
            else
                body.Append("<font color='#777777'>Select type and raider count to enable START RAID.</font>");
        }

        private string RaidButton(
            RaidSession session,
            string action,
            string label,
            bool selected)
        {
            if (selected)
                return $"<font color='#00DE42'>[{SafeRaidText(label)}]</font>";

            string command;

            if (session.Origin.Kind == ReplyKind.Org)
                command = $"chatcmd:///o #raid {action} {session.Token}";
            else if (session.Origin.Kind == ReplyKind.Guest)
                command = $"chatcmd:///g Apcmanager #raid {action} {session.Token}";
            else
                command = $"chatcmd:///tell Apcmanager #raid {action} {session.Token}";

            return
                $"<a href='{command}'><font color='#00BFFF'>[{SafeRaidText(label)}]</font></a>";
        }

        private string RaidSelectionText(string value, string fallback)
        {
            return !string.IsNullOrWhiteSpace(value)
                ? $"<font color='#00DE42'>{SafeRaidText(value)}</font>"
                : $"<font color='#F79410'>{fallback}</font>";
        }

        private string SafeRaidText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\"", "'")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }

        private string FormatCharge(float? charge)
        {
            return charge.HasValue
                ? $"{charge.Value * 100:F1}%"
                : "unknown";
        }

        private enum RaidStage
        {
            Configuring,
            AdminVeto,
            ControllerFill,
            LoweringCloak,
            AwaitingCityTarget,
            Active,
            CleaningUp
        }

        private sealed class RaidSession
        {
            public string Token;
            public string OwnerName;
            public uint OwnerId;
            public ReplyTarget Origin;
            public RaidStage Stage;
            public DateTime CreatedUtc;
            public DateTime StageDeadlineUtc;
            public DateTime WorkerDeadlineUtc;
            public DateTime CityTargetedUtc;

            public string RaidType;
            public int Level;
            public int? RaiderCount;

            public float? LastControllerCharge;
            public string FlipperDetail;
            public string BuddyDetail;

            public bool ControllerProbeInFlight;
            public bool BuddySpinupRequested;
            public bool BuddySpinupInFlight;
            public bool BuddySpinupFatal;
            public bool BuddyCleanupInFlight;
            public bool WorkerWaitAnnounced;
            public int CurrentMilestone;

            public readonly List<int> StartedBuddyIndexes = new List<int>();
        }
    }
}
