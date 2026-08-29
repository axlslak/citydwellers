using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;

namespace CityManager
{
    public partial class CityManager
    {
        private const int RaidConfigurationSeconds = 180;
        private const int RaidControllerFillSeconds = 60;
        private const int RaidCooldownSeconds = 600;
        private const int RaidTargetTimeoutSeconds = 300;
        private const int RaidWorkerGraceSeconds = 20;
        private const int RaidWorkerRetryDelaySeconds = 5;
        private const int CityTargetAfterCloakSeconds = 180;
        // Measured from the authoritative city-targeted system event: wave 8
        // arrives at +945s and the general physically lands at about +1125s.
        // General-only buddies enter thirty seconds after wave 8 arrives.
        private const int Wave8OffsetSeconds = 945;
        private const int GeneralBuddyStartOffsetSeconds = 975;
        private const int BuddyLogoutOffsetSeconds = 1125;
        private const int GeneralBuddySafetyLeaseSeconds =
            BuddyLogoutOffsetSeconds - GeneralBuddyStartOffsetSeconds;
        private const int AllWavesBuddySafetyLeaseSeconds =
            RaidControllerFillSeconds +
            CityTargetAfterCloakSeconds +
            BuddyLogoutOffsetSeconds;
        private const float MinimumRaidControllerCharge = 0.75f;
        private const int DefaultRaidLevel = 200;
        private static readonly int[] AvailableRaidLevels =
            { 25, 50, 75, 100, 125, 150, 175, 200 };

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
        private readonly object _raidPersistenceSync = new object();
        private readonly Dictionary<string, DateTime> _raidCooldowns =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private RaidSession _raidSession;
        private DateTime _nextRaidTickUtc = DateTime.MinValue;
        private bool _raidCoordinatorShuttingDown;
        private bool _raidResumeWorkersOnInPlay;

        private void InitializeRaidCoordinator()
        {
            lock (_raidSync)
            {
                _raidCoordinatorShuttingDown = false;
                _raidSession = null;
                _raidCooldowns.Clear();
                _nextRaidTickUtc = DateTime.MinValue;
                _raidResumeWorkersOnInPlay = false;
            }

            LoadRaidState();

            Logger.Information(
                "Raid coordinator initialized: 3m setup, immediate CT handling, 1m CT fill, 75% minimum charge; owner/admin cancel remains available throughout.");
        }

        private void ShutdownRaidCoordinator()
        {
            SaveRaidState();

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

            if (!target.IsOrg && !target.IsGuest && !isAdmin && !privateOwnerControl)
            {
                DevTrace(
                    $"RAID DENIED {target.Kind} {senderName}: raid requires org or guest chat.");
                Reply(target, "Use #raid in organization or guest chat.");
                return;
            }

            if (parts.Length == 1)
            {
                BeginOrReopenRaid(senderName, target, isAdmin);
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
                if (!int.TryParse(parts[2], out level) ||
                    !IsAvailableRaidLevel(level))
                {
                    Reply(target, "Available City Dwellers levels are 25, 50, 75, 100, 125, 150, 175, and 200.");
                    return;
                }

                UpdateRaidSelection(session, null, null, level);
                return;
            }

            Reply(target, Usage(target, "raid"));
        }

        private void BeginOrReopenRaid(
            string senderName,
            ReplyTarget target,
            bool isAdmin)
        {
            RaidSession existing;
            DateTime cooldownUntil;
            DateTime now = DateTime.UtcNow;

            lock (_raidSync)
            {
                RemoveExpiredCooldownsLocked(now);

                if (isAdmin)
                    _raidCooldowns.Remove(senderName ?? string.Empty);

                if (!isAdmin &&
                    _raidCooldowns.TryGetValue(senderName ?? string.Empty, out cooldownUntil) &&
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
                    Level = DefaultRaidLevel,
                    CurrentMilestone = -1,
                    CreatedUtc = now
                };

                existing = _raidSession;
            }

            Logger.Information(
                $"Raid setup opened by {senderName}; token={existing.Token}, origin={target.Kind}.");
            DevTrace(
                $"RAID SETUP owner={senderName} token={existing.Token} origin={target.Kind} deadline={existing.StageDeadlineUtc:O}.");
            SaveRaidState();
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

        private bool IsCurrentRaidOwnerCancellation(
            string senderName,
            uint senderId,
            string[] parts)
        {
            lock (_raidSync)
            {
                if (_raidSession == null ||
                    !IsRaidOwner(_raidSession, senderName, senderId) ||
                    parts == null ||
                    parts.Length == 0 ||
                    parts.Length > 2)
                {
                    return false;
                }

                return parts.Length == 1 ||
                       string.Equals(
                           parts[1],
                           _raidSession.Token,
                           StringComparison.Ordinal);
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
                SaveRaidState();
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

            if (BeginControllerFillStage(session))
                SendRaidAnnouncementToAdmins(session);
        }

        private void SendRaidAnnouncementToAdmins(RaidSession session)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (string admin in AdminListStore.Snapshot())
                {
                    try
                    {
                        if (!IsCurrentRaidSession(session))
                            return;

                        uint adminId;
                        if (!TryResolveCharacterId(admin, out adminId))
                        {
                            DevTrace($"RAID ADMIN NOTICE: unable to resolve {admin}.");
                            continue;
                        }

                        string message =
                            $"{session.OwnerName} started a {session.RaidType} city raid: " +
                            $"{session.RaiderCount.Value} level-{session.Level} raiders. " +
                            $"Owner/admin cancellation remains available while the raid is active. " +
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
            ReplyTarget commandTarget,
            bool isAdmin)
        {
            RaidSession session;
            string error = null;
            RaidStage canceledStage = RaidStage.Configuring;
            bool cancelFlipper = false;
            bool cleanupBuddies = false;
            bool cooldownApplied = false;
            bool unmanagedAssistCanceled = false;

            lock (_raidSync)
            {
                session = _raidSession;

                if (session == null)
                {
                    error = "There is no raid request to cancel.";
                }
                else if (!isAdmin &&
                         !IsRaidOwner(
                             session,
                             senderName,
                             commandTarget.SenderId))
                {
                    error = "Only the raid owner or an administrator may cancel this raid.";
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
                    canceledStage = session.Stage;
                    cancelFlipper =
                        session.ControllerProbeInFlight &&
                        !string.IsNullOrWhiteSpace(session.ControllerRequestId) &&
                        !session.CloakLowerConfirmedUtc.HasValue;
                    cleanupBuddies =
                        session.StartedBuddyIndexes.Count > 0 ||
                        session.BuddySpinupInFlight ||
                        session.BuddyCleanupInFlight;
                    bool shouldApplyCooldown =
                        !session.IsExternalAssist ||
                        session.Stage != RaidStage.AssistSelection;
                    unmanagedAssistCanceled = !shouldApplyCooldown;
                    _raidSession = null;
                    if (shouldApplyCooldown)
                    {
                        cooldownApplied = ApplyRaidCooldownLocked(
                            session.OwnerName,
                            DateTime.UtcNow);
                    }
                }
            }

            if (error != null)
            {
                Reply(commandTarget, error);
                return;
            }

            if (cancelFlipper)
                RequestFlipperRaidCancellation(session);

            string message =
                $"Raid canceled by {senderName}. " +
                (cleanupBuddies
                    ? "Raid buddy logout is underway. "
                    : string.Empty) +
                (cooldownApplied
                    ? $"{session.OwnerName} has a 10-minute raid cooldown."
                    : unmanagedAssistCanceled
                        ? "No City Dwellers assistance will be started for this unmanaged raid."
                        : IsAdministrator(session.OwnerName)
                            ? "Administrators do not receive raid cooldowns."
                            : string.Empty);

            Logger.Warning(message);
            DevTrace(
                $"RAID CANCELED actor={senderName} owner={session.OwnerName} " +
                $"stage={canceledStage} flipper-cancel={cancelFlipper} " +
                $"buddy-cleanup={cleanupBuddies} cooldown={cooldownApplied}.");
            SaveRaidState();
            Reply(session.Origin, message);

            if (commandTarget.Kind != session.Origin.Kind ||
                (commandTarget.Kind == ReplyKind.Tell &&
                 commandTarget.SenderId != session.Origin.SenderId))
            {
                Reply(commandTarget, "Raid canceled.");
            }

            QueueDetachedBuddyCleanup(session);
        }

        private void RequestFlipperRaidCancellation(RaidSession session)
        {
            string requestId = session?.ControllerRequestId;
            if (string.IsNullOrWhiteSpace(requestId))
                return;

            string path = Path.Combine(
                _settingsDir,
                "cityflipper-cancel.request");
            string tempPath = path + "." + requestId + ".tmp";

            try
            {
                File.WriteAllText(tempPath, requestId);

                if (File.Exists(path))
                    File.Delete(path);

                File.Move(tempPath, path);
                DevTrace(
                    $"RAID FLIPPER cancel requested [{ShortId(requestId)}] before cloak lower.");
            }
            catch (Exception ex)
            {
                DevTrace(
                    $"RAID FLIPPER cancel marker failed [{ShortId(requestId)}]: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        private void ProcessRaidRecovery(
            string adminName,
            string[] parts,
            ReplyTarget commandTarget)
        {
            if (parts.Length != 5)
            {
                Reply(
                    commandTarget,
                    Usage(
                        commandTarget,
                        "recoverraid [owner] [all|general] [level] [count]"));
                return;
            }

            string ownerName = NormalizeCharacterName(parts[1]);
            string raidType = parts[2].ToLowerInvariant();
            int level;
            int count;

            if (string.IsNullOrWhiteSpace(ownerName) ||
                (raidType != "all" && raidType != "general") ||
                !int.TryParse(parts[3], out level) ||
                !IsAvailableRaidLevel(level) ||
                !int.TryParse(parts[4], out count) ||
                count < 0 ||
                count > 12)
            {
                Reply(
                    commandTarget,
                    "Recovery requires an owner, all/general, level 25, 50, 75, 100, 125, 150, 175, or 200, and 0-12 raiders.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            DateTime cloakLowerUtc;
            RaidSession session;
            string error = null;

            lock (_raidSync)
            {
                if (_raidSession != null)
                {
                    error = "Raid in progress already.";
                    session = null;
                    cloakLowerUtc = default(DateTime);
                }
                else if (_status != CloakStatus.Disabled || !_lastChangedUtc.HasValue)
                {
                    error =
                        "Manager has no persisted recent cloak-off event from which to recover a raid.";
                    session = null;
                    cloakLowerUtc = default(DateTime);
                }
                else if ((now - _lastChangedUtc.Value).TotalHours > 2)
                {
                    error = "The persisted cloak-off event is too old for raid recovery.";
                    session = null;
                    cloakLowerUtc = default(DateTime);
                }
                else
                {
                    cloakLowerUtc = _lastChangedUtc.Value;
                    DateTime predictedTargetUtc =
                        cloakLowerUtc.AddSeconds(CityTargetAfterCloakSeconds);
                    bool targetShouldHaveOccurred = now >= predictedTargetUtc;

                    session = new RaidSession
                    {
                        Token = Guid.NewGuid().ToString("N").Substring(0, 10),
                        OwnerName = ownerName,
                        OwnerId = 0,
                        Origin = commandTarget,
                        Stage = targetShouldHaveOccurred
                            ? RaidStage.Active
                            : RaidStage.AwaitingCityTarget,
                        CreatedUtc = cloakLowerUtc,
                        StageDeadlineUtc = targetShouldHaveOccurred
                            ? DateTime.MaxValue
                            : predictedTargetUtc.AddSeconds(RaidTargetTimeoutSeconds),
                        CityTargetedUtc = targetShouldHaveOccurred
                            ? predictedTargetUtc
                            : default(DateTime),
                        CloakLowerConfirmedUtc = cloakLowerUtc,
                        CloakLowerConfirmedActor =
                            "persisted Manager cloak state",
                        RaidType = raidType,
                        Level = level,
                        RaiderCount = count,
                        CurrentMilestone = targetShouldHaveOccurred
                            ? GetRaidMilestoneIndex(now - predictedTargetUtc)
                            : -1,
                        FlipperDetail =
                            "Recovered without another cloak action from the persisted cloak-off timestamp."
                    };

                    _raidSession = session;
                }
            }

            if (error != null)
            {
                Reply(commandTarget, error);
                return;
            }

            Logger.Warning(
                $"Raid recovered by {adminName} for {ownerName}: type={raidType}, " +
                $"level={level}, count={count}, cloak-off={cloakLowerUtc:O}, " +
                $"stage={session.Stage}.");
            DevTrace(
                $"RAID MANUAL RECOVERY admin={adminName} owner={ownerName} " +
                $"type={raidType} level={level} count={count} " +
                $"cloak-off={cloakLowerUtc:O} " +
                $"predicted-target={cloakLowerUtc.AddSeconds(CityTargetAfterCloakSeconds):O}.");
            SaveRaidState();
            Reply(session.Origin, BuildRaidWindow(session));
        }

        private void ProcessRaidAssistCommand(
            string senderName,
            string[] parts,
            ReplyTarget commandTarget,
            bool isAdmin)
        {
            bool levelSelection =
                parts.Length == 4 &&
                string.Equals(parts[1], "level", StringComparison.OrdinalIgnoreCase);
            bool typeSelection =
                parts.Length == 4 &&
                string.Equals(parts[1], "type", StringComparison.OrdinalIgnoreCase);
            int count = 0;
            int level = 0;
            string raidType = null;
            string token;

            if (typeSelection)
            {
                raidType = parts[2].ToLowerInvariant();
                if (raidType != "all" && raidType != "general")
                {
                    Reply(commandTarget, "Raid assistance must be all remaining waves or general only.");
                    return;
                }

                token = parts[3];
            }
            else if (levelSelection)
            {
                if (!int.TryParse(parts[2], out level) ||
                    !IsAvailableRaidLevel(level))
                {
                    Reply(commandTarget, "Raid-assistance levels are 25, 50, 75, 100, 125, 150, 175, and 200.");
                    return;
                }

                token = parts[3];
            }
            else if (parts.Length == 3)
            {
                if (!int.TryParse(parts[1], out count) || count < 0 || count > 12)
                {
                    Reply(commandTarget, "Raid assistance must be between 0 and 12 City Dwellers.");
                    return;
                }

                token = parts[2];
            }
            else
            {
                Reply(
                    commandTarget,
                    Usage(
                        commandTarget,
                        "raidassist [count] [raid-token], raidassist type [all|general] [raid-token], or raidassist level [25|50|75|100|125|150|175|200] [raid-token]"));
                return;
            }

            if (!commandTarget.IsOrg && !isAdmin)
            {
                Reply(commandTarget, "Raid-assist selections must be made in organization chat.");
                return;
            }

            Action<string> applyAuthorized = authority =>
            {
                if (typeSelection)
                {
                    ApplyRaidAssistTypeSelection(
                        senderName,
                        commandTarget,
                        raidType,
                        token,
                        authority);
                    return;
                }

                if (levelSelection)
                {
                    ApplyRaidAssistLevelSelection(
                        senderName,
                        commandTarget,
                        level,
                        token,
                        authority);
                    return;
                }

                ApplyRaidAssistSelection(
                    senderName,
                    commandTarget,
                    count,
                    token,
                    authority);
            };

            if (isAdmin)
            {
                applyAuthorized("named administrator");
                return;
            }

            string cachedAuthority;
            string authorityCharacter;
            bool hasCachedAuthority = TryGetCachedOfficerAuthority(
                    senderName,
                    out cachedAuthority,
                    out authorityCharacter);
            bool directCachedAuthority = hasCachedAuthority &&
                string.Equals(
                    senderName,
                    authorityCharacter,
                    StringComparison.OrdinalIgnoreCase);
            bool reliableAltAuthority = hasCachedAuthority &&
                IsAltIdentityGroupReliable(senderName);
            if (directCachedAuthority || reliableAltAuthority)
            {
                DevTrace(
                    $"RAID ASSIST AUTH cached sender={senderName} " +
                    $"authority={cachedAuthority} via={authorityCharacter}.");
                applyAuthorized(
                    directCachedAuthority
                        ? cachedAuthority
                        : $"{cachedAuthority} via alt {authorityCharacter}");
                return;
            }

            if (HasCachedOfficialRanks())
            {
                if (IsAltIdentityGroupReliable(senderName))
                {
                    DevTrace(
                        $"RAID ASSIST DENIED {senderName}: fresh alt group " +
                        "contains no Squad Commander-or-higher XML rank.");
                    Reply(
                        commandTarget,
                        "Raid-assist controls require Squad Commander rank or higher on one character in your current alt group.");
                    return;
                }

                Reply(
                    commandTarget,
                    $"Checking {_altsBotName ?? "the configured alt bot"} for {senderName}'s current alt group.");
                DevTrace(
                    $"RAID ASSIST AUTH lookup sender={senderName}; " +
                    (hasCachedAuthority
                        ? $"cached authority via {authorityCharacter} is stale."
                        : "no reliable cached officer alt exists."));

                ResolveOfficerAltGroup(
                    senderName,
                    lookupSucceeded =>
                    {
                        string refreshedAuthority;
                        string refreshedCharacter;
                        if (lookupSucceeded &&
                            TryGetCachedOfficerAuthority(
                                senderName,
                                out refreshedAuthority,
                                out refreshedCharacter))
                        {
                            DevTrace(
                                $"RAID ASSIST AUTH refreshed sender={senderName} " +
                                $"authority={refreshedAuthority} via={refreshedCharacter}.");
                            applyAuthorized(
                                string.Equals(
                                    senderName,
                                    refreshedCharacter,
                                    StringComparison.OrdinalIgnoreCase)
                                    ? refreshedAuthority
                                    : $"{refreshedAuthority} via alt {refreshedCharacter}");
                            return;
                        }

                        DevTrace(
                            $"RAID ASSIST DENIED {senderName}: targeted alt lookup " +
                            $"completed={lookupSucceeded} without officer authority.");
                        Reply(
                            commandTarget,
                            lookupSucceeded
                                ? "Raid-assist controls require Squad Commander rank or higher on one character in your current alt group."
                                : $"Unable to verify {senderName}'s alt group through {_altsBotName ?? "the configured alt bot"} right now.");
                    });
                return;
            }

            OrgRankAuthorizer.Authorize(
                commandTarget.SenderId,
                senderName,
                authorization =>
                {
                    if (!authorization.Allowed)
                    {
                        string detail = !string.IsNullOrWhiteSpace(authorization.Error)
                            ? authorization.Error
                            : $"organization rank '{authorization.Rank ?? "unknown"}'";

                        DevTrace(
                            $"RAID ASSIST DENIED {senderName}: {detail}.");
                        Reply(
                            commandTarget,
                            "Raid-assist controls require Squad Commander rank or higher.");
                        return;
                    }

                    applyAuthorized(authorization.Rank);
                });
        }

        private void ApplyRaidAssistTypeSelection(
            string senderName,
            ReplyTarget commandTarget,
            string raidType,
            string token,
            string authority)
        {
            RaidSession session;
            string error = null;

            lock (_raidSync)
            {
                session = _raidSession;

                if (session == null ||
                    !session.IsExternalAssist ||
                    session.Stage != RaidStage.AssistSelection)
                {
                    error = "That raid-assist offer is no longer active.";
                }
                else if (!string.Equals(session.Token, token, StringComparison.Ordinal))
                {
                    error = "That raid-assist button belongs to an older raid.";
                }
                else if (DateTime.UtcNow >= session.StageDeadlineUtc)
                {
                    error = "The raid-assist selection window has closed.";
                }
                else
                {
                    session.RaidType = raidType;
                }
            }

            if (error != null)
            {
                Reply(commandTarget, error);
                return;
            }

            DevTrace(
                $"RAID ASSIST type selected by={senderName} type={raidType} " +
                $"authority={authority}.");
            SaveRaidState();
            Reply(session.Origin, BuildRaidWindow(session));
        }

        private void ApplyRaidAssistLevelSelection(
            string senderName,
            ReplyTarget commandTarget,
            int level,
            string token,
            string authority)
        {
            RaidSession session;
            string error = null;

            lock (_raidSync)
            {
                session = _raidSession;

                if (session == null ||
                    !session.IsExternalAssist ||
                    session.Stage != RaidStage.AssistSelection)
                {
                    error = "That raid-assist offer is no longer active.";
                }
                else if (!string.Equals(session.Token, token, StringComparison.Ordinal))
                {
                    error = "That raid-assist button belongs to an older raid.";
                }
                else if (DateTime.UtcNow >= session.StageDeadlineUtc)
                {
                    error = "The raid-assist selection window has closed.";
                }
                else
                {
                    session.Level = level;
                }
            }

            if (error != null)
            {
                Reply(commandTarget, error);
                return;
            }

            DevTrace(
                $"RAID ASSIST level selected by={senderName} level={level} " +
                $"authority={authority}.");
            SaveRaidState();
            Reply(session.Origin, BuildRaidWindow(session));
        }

        private void ApplyRaidAssistSelection(
            string senderName,
            ReplyTarget commandTarget,
            int count,
            string token,
            string authority)
        {
            RaidSession session;
            string error = null;
            bool declined = false;

            lock (_raidSync)
            {
                session = _raidSession;

                if (session == null ||
                    !session.IsExternalAssist ||
                    session.Stage != RaidStage.AssistSelection)
                {
                    error = "That raid-assist offer is no longer active.";
                }
                else if (!string.Equals(session.Token, token, StringComparison.Ordinal))
                {
                    error = "That raid-assist button belongs to an older raid.";
                }
                else if (DateTime.UtcNow >= session.StageDeadlineUtc)
                {
                    error = "The raid-assist selection window has closed.";
                }
                else if (count == 0)
                {
                    _raidSession = null;
                    declined = true;
                }
                else
                {
                    session.OwnerName = senderName;
                    session.OwnerId = commandTarget.SenderId;
                    session.RaiderCount = count;
                    session.Stage = RaidStage.Active;
                }
            }

            if (error != null)
            {
                Reply(commandTarget, error);
                return;
            }

            if (declined)
            {
                DevTrace(
                    $"RAID ASSIST declined by {senderName} authority={authority}.");
                SaveRaidState();
                Reply(commandTarget, $"{senderName} declined City Dwellers assistance for this raid.");
                return;
            }

            Logger.Warning(
                $"External raid assistance selected by {senderName}: " +
                $"type={session.RaidType}, level={session.Level}, count={count}, " +
                $"authority={authority}.");
            DevTrace(
                $"RAID ASSIST selected by={senderName} type={session.RaidType} " +
                $"level={session.Level} count={count} " +
                $"authority={authority} wave8={session.CityTargetedUtc.AddSeconds(Wave8OffsetSeconds):O}.");
            SaveRaidState();
            Reply(session.Origin, BuildRaidWindow(session));

            if (string.Equals(
                    session.RaidType,
                    "all",
                    StringComparison.OrdinalIgnoreCase))
            {
                BeginRaidBuddySpinup(session, "external all-remaining-waves selection");
            }
            else
            {
                BeginRaidBuddyPreparation(session, "external general-only selection");
            }
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

            if (stage == RaidStage.AssistSelection && now >= deadline)
            {
                bool closed = false;

                lock (_raidSync)
                {
                    if (ReferenceEquals(_raidSession, session) &&
                        session.Stage == RaidStage.AssistSelection)
                    {
                        _raidSession = null;
                        closed = true;
                    }
                }

                if (closed)
                {
                    DevTrace(
                        "RAID ASSIST offer expired at wave 8 without a selection.");
                    SaveRaidState();
                    Reply(
                        session.Origin,
                        "City Dwellers assistance was not requested before wave 8; the offer is closed.");
                }

                return;
            }

            if (stage == RaidStage.Configuring && now >= deadline)
            {
                FailRaidSession(
                    session,
                    "Raid setup expired before it was started.",
                    true);
                return;
            }

            if (stage == RaidStage.AdminVeto)
            {
                BeginControllerFillStage(session);
                return;
            }

            if (stage == RaidStage.ControllerFill)
            {
                bool workerInFlight;
                bool retryPending;
                lock (_raidSync)
                {
                    workerInFlight =
                        ReferenceEquals(_raidSession, session) &&
                        session.ControllerProbeInFlight;
                    retryPending =
                        ReferenceEquals(_raidSession, session) &&
                        session.ControllerRetryPending;
                }

                if (now >= deadline)
                {
                    if (workerInFlight && now < session.WorkerDeadlineUtc)
                        return;

                    if (retryPending && now < session.WorkerDeadlineUtc)
                    {
                        TryStartControllerWatch(session);
                        return;
                    }

                    FailRaidSession(
                        session,
                        BuildControllerStartFailure(session),
                        true);
                    return;
                }

                TryStartControllerWatch(session);
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

        private bool BeginControllerFillStage(RaidSession session)
        {
            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    (session.Stage != RaidStage.Configuring &&
                     session.Stage != RaidStage.AdminVeto))
                {
                    return false;
                }

                session.Stage = RaidStage.ControllerFill;
                session.StageDeadlineUtc =
                    DateTime.UtcNow.AddSeconds(RaidControllerFillSeconds);
                session.WorkerDeadlineUtc =
                    session.StageDeadlineUtc.AddSeconds(RaidWorkerGraceSeconds);
                session.ControllerProbeInFlight = false;
                session.ControllerRetryPending = false;
                session.ControllerRetryNotBeforeUtc = DateTime.MinValue;
            }

            Logger.Warning(
                $"Raid for {session.OwnerName} started. CT fill handling opened immediately.");
            DevTrace(
                $"RAID CT FILL owner={session.OwnerName} deadline={session.StageDeadlineUtc:O}; " +
                $"minimum={MinimumRaidControllerCharge * 100:F0}%.");
            SaveRaidState();
            Reply(session.Origin, BuildRaidWindow(session));

            if (string.Equals(session.RaidType, "all", StringComparison.OrdinalIgnoreCase))
                BeginRaidBuddySpinup(session, "all-mode start");
            else if (string.Equals(
                         session.RaidType,
                         "general",
                         StringComparison.OrdinalIgnoreCase))
                BeginRaidBuddyPreparation(session, "general-mode start");

            TryStartControllerWatch(session);
            return true;
        }

        private void TryStartControllerWatch(RaidSession session)
        {
            bool start = false;
            bool retryAttempt = false;
            DateTime now = DateTime.UtcNow;
            string controllerRequestId = null;

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.ControllerFill ||
                    session.ControllerProbeInFlight)
                {
                    return;
                }

                bool normalWindow = now < session.StageDeadlineUtc;
                bool retryWindow =
                    session.ControllerRetryPending &&
                    now < session.WorkerDeadlineUtc;

                if (!normalWindow && !retryWindow)
                    return;

                if (session.ControllerRetryPending &&
                    now < session.ControllerRetryNotBeforeUtc)
                {
                    return;
                }

                retryAttempt = session.ControllerRetryPending;
                session.ControllerRetryPending = false;
                session.ControllerProbeInFlight = true;
                session.ControllerRequestId = Guid.NewGuid().ToString("N");
                controllerRequestId = session.ControllerRequestId;
                start = true;
            }

            if (!start)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int secondsRemaining = retryAttempt
                        ? 1
                        : Math.Max(
                            1,
                            (int)Math.Ceiling(
                                (session.StageDeadlineUtc - DateTime.UtcNow).TotalSeconds));

                    var request = new WorkerRequest
                    {
                        Id = controllerRequestId,
                        Command = retryAttempt
                            ? "ensure-disabled-ready"
                            : "ensure-disabled-watch",
                        TimeoutSeconds = secondsRemaining
                    };

                    string shortId = ShortId(request.Id);
                    DevTrace(
                        $"RAID FLIPPER -> {(retryAttempt ? "retry ready check" : "watch CT")} and lower [{shortId}] " +
                        $"window={secondsRemaining}s minimum={MinimumRaidControllerCharge * 100:F0}%.");

                    WorkerResponse response = SendWorkerRequest(
                        FlipperPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    if (response.Ok)
                        ApplyFlipperObservation(response);

                    bool workerConfirmed =
                        response.Ok &&
                        string.Equals(
                            response.CloakState,
                            "Disabled",
                            StringComparison.OrdinalIgnoreCase);
                    bool cityEventConfirmed;
                    float? effectiveCharge = response.ControllerCharge;

                    lock (_raidSync)
                    {
                        if (ReferenceEquals(_raidSession, session))
                        {
                            cityEventConfirmed = session.CloakLowerConfirmedUtc.HasValue;
                            if (cityEventConfirmed && !effectiveCharge.HasValue)
                                effectiveCharge = session.LastControllerCharge;

                            session.LastControllerCharge = effectiveCharge;
                            session.FlipperDetail = response.Message;
                            session.ControllerProbeInFlight = false;
                            session.ControllerRequestId = null;
                        }
                        else
                        {
                            cityEventConfirmed = false;
                        }
                    }

                    DevTrace(
                        $"RAID FLIPPER {(response.Ok ? "OK" : "FAIL")} [{shortId}]: " +
                        $"charge={FormatCharge(effectiveCharge)}; {response.Message}");

                    bool started =
                        effectiveCharge.HasValue &&
                        effectiveCharge.Value >= MinimumRaidControllerCharge &&
                        (response.ActionSent || workerConfirmed || cityEventConfirmed);

                    if (started)
                    {
                        lock (_raidSync)
                        {
                            if (!ReferenceEquals(_raidSession, session) ||
                                session.Stage != RaidStage.ControllerFill)
                            {
                                return;
                            }

                            session.Stage = RaidStage.AwaitingCityTarget;
                            session.StageDeadlineUtc =
                                DateTime.UtcNow.AddSeconds(RaidTargetTimeoutSeconds);
                        }

                        Logger.Warning(
                            $"Raid cloak lowered for {session.OwnerName}; " +
                            $"charge={FormatCharge(effectiveCharge)}.");
                        DevTrace(
                            $"RAID STARTED owner={session.OwnerName} " +
                            $"charge={FormatCharge(effectiveCharge)}; " +
                            $"waiting for CITY_ATTACKED until {session.StageDeadlineUtc:O}.");
                        SaveRaidState();
                        Reply(session.Origin, BuildRaidWindow(session));
                        return;
                    }

                    bool retryableWorkerFailure =
                        !response.ActionSent &&
                        !effectiveCharge.HasValue &&
                        string.IsNullOrWhiteSpace(response.CloakState);

                    if (retryableWorkerFailure &&
                        ScheduleControllerWatchRetry(session, response.Message))
                    {
                        return;
                    }

                    string failureReason =
                        effectiveCharge.HasValue &&
                        effectiveCharge.Value < MinimumRaidControllerCharge
                            ? $"The City Controller did not reach 75% within the one-minute fill window. " +
                              response.Message
                            : $"Raid start was blocked. {response.Message}";

                    FailRaidSession(session, failureReason, true);
                }
                catch (Exception ex)
                {
                    lock (_raidSync)
                    {
                        if (ReferenceEquals(_raidSession, session))
                        {
                            session.ControllerProbeInFlight = false;
                            session.ControllerRequestId = null;
                            session.FlipperDetail = ex.Message;
                        }
                    }

                    Logger.Warning($"Raid CT watch failed: {ex.Message}");

                    if (TryRecoverRaidCloakLowerFromCityEvent(session, ex.Message))
                        return;

                    if (ScheduleControllerWatchRetry(session, ex.Message))
                        return;

                    FailRaidSession(
                        session,
                        $"The City Controller could not be verified within the one-minute fill window: {ex.Message}",
                        true);
                }
            });
        }

        private bool ScheduleControllerWatchRetry(
            RaidSession session,
            string detail)
        {
            DateTime retryAt;

            lock (_raidSync)
            {
                DateTime now = DateTime.UtcNow;

                if (!ReferenceEquals(_raidSession, session) ||
                    session.Stage != RaidStage.ControllerFill ||
                    now >= session.WorkerDeadlineUtc)
                {
                    return false;
                }

                retryAt = now.AddSeconds(RaidWorkerRetryDelaySeconds);
                if (retryAt > session.WorkerDeadlineUtc)
                    retryAt = session.WorkerDeadlineUtc;

                session.ControllerProbeInFlight = false;
                session.ControllerRequestId = null;
                session.ControllerRetryPending = true;
                session.ControllerRetryNotBeforeUtc = retryAt;
                session.FlipperDetail = detail;
            }

            DevTrace(
                $"RAID FLIPPER transient failure; retry scheduled at {retryAt:O}: {detail}");
            SaveRaidState();
            return true;
        }

        private string BuildControllerStartFailure(RaidSession session)
        {
            if (session.LastControllerCharge.HasValue &&
                session.LastControllerCharge.Value < MinimumRaidControllerCharge)
            {
                return
                    $"The City Controller remained {FormatCharge(session.LastControllerCharge)}; " +
                    "75% is required to start the raid.";
            }

            if (!string.IsNullOrWhiteSpace(session.FlipperDetail))
            {
                return
                    "Raid start was blocked while the City Controller was " +
                    $"{FormatCharge(session.LastControllerCharge)}. {session.FlipperDetail}";
            }

            return
                "Raid start was blocked because Flipper could not verify and lower the cloak.";
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
                SaveRaidState();
                return;
            }

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) || session.BuddySpinupInFlight)
                    return;

                session.BuddySpinupInFlight = true;
                session.BuddySpinupRequested = true;
            }

            SaveRaidState();

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
                        Index = count,
                        Purpose = "raid",
                        Home = true,
                        LogoutAfterHome = false,
                        LeaseSeconds = string.Equals(
                                session.RaidType,
                                "all",
                                StringComparison.OrdinalIgnoreCase)
                            ? AllWavesBuddySafetyLeaseSeconds
                            : GeneralBuddySafetyLeaseSeconds
                    };

                    string shortId = ShortId(request.Id);
                    DevTrace(
                        $"RAID BUDDIES -> spinup level={session.Level} count={count} " +
                        $"safety-lease={request.LeaseSeconds}s [{shortId}] reason={reason}.");

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
                            // Buddy availability is assistance, never authority
                            // over the city raid. A partial pool remains visible
                            // in BuddyDetail so an administrator can supplement
                            // it, but cannot block or cancel the cloak action.
                            session.BuddySpinupFatal = false;
                        }
                    }

                    DevTrace(
                        $"RAID BUDDIES {(response.Ok ? "OK" : "FAIL")} [{shortId}]: {response.Message}");

                    if (!stillCurrent)
                    {
                        QueueDetachedBuddyCleanup(session);
                        return;
                    }

                    SaveRaidState();
                }
                catch (Exception ex)
                {
                    lock (_raidSync)
                    {
                        if (ReferenceEquals(_raidSession, session))
                        {
                            session.BuddySpinupInFlight = false;
                            session.BuddyDetail = ex.Message;
                            session.BuddySpinupFatal = false;
                        }
                    }

                    DevTrace($"RAID BUDDIES spinup error: {ex.Message}");

                    if (IsCurrentRaidSession(session))
                        SaveRaidState();
                }

                if (IsCurrentRaidSession(session) &&
                    session.Stage == RaidStage.ControllerFill)
                {
                    TryStartControllerWatch(session);
                }
            });
        }

        private void BeginRaidBuddyPreparation(RaidSession session, string reason)
        {
            int count = session.RaiderCount ?? 0;
            if (count == 0)
                return;

            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    session.BuddyPreparationInFlight)
                {
                    return;
                }

                session.BuddyPreparationInFlight = true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = new WorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = "spinup",
                        Level = session.Level,
                        Index = count,
                        Purpose = "raid-preflight",
                        Home = true,
                        LogoutAfterHome = true
                    };

                    string shortId = ShortId(request.Id);
                    DevTrace(
                        $"RAID BUDDIES -> preflight level={session.Level} " +
                        $"count={count} [{shortId}] reason={reason}.");

                    WorkerResponse response = SendWorkerRequest(
                        BuddiesPipeName,
                        request,
                        WorkerConnectTimeoutMs);

                    lock (_raidSync)
                    {
                        session.BuddyPreparationInFlight = false;
                        session.BuddyPreparationDetail = response.Message;
                    }

                    DevTrace(
                        $"RAID BUDDIES PREFLIGHT " +
                        $"{(response.Ok ? "OK" : "PARTIAL")} [{shortId}]: " +
                        response.Message);
                    SaveRaidState();
                }
                catch (Exception ex)
                {
                    lock (_raidSync)
                    {
                        session.BuddyPreparationInFlight = false;
                        session.BuddyPreparationDetail = ex.Message;
                    }

                    DevTrace($"RAID BUDDIES preflight error: {ex.Message}");
                }
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

        private bool TryRecoverRaidCloakLowerFromCityEvent(
            RaidSession session,
            string workerError)
        {
            lock (_raidSync)
            {
                if (!ReferenceEquals(_raidSession, session) ||
                    (session.Stage != RaidStage.ControllerFill &&
                     session.Stage != RaidStage.LoweringCloak) ||
                    !session.CloakLowerConfirmedUtc.HasValue ||
                    ((!session.LastControllerCharge.HasValue ||
                      session.LastControllerCharge.Value < MinimumRaidControllerCharge) &&
                     !string.Equals(
                         session.CloakLowerConfirmedActor,
                         "Apcflipper",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                session.FlipperDetail =
                    $"Org city event confirmed the cloak was lowered by " +
                    $"{session.CloakLowerConfirmedActor}; worker error: {workerError}";
                session.ControllerProbeInFlight = false;
                session.Stage = RaidStage.AwaitingCityTarget;
                session.StageDeadlineUtc =
                    DateTime.UtcNow.AddSeconds(RaidTargetTimeoutSeconds);
            }

            Logger.Warning(
                $"Raid cloak lower recovered from org event for {session.OwnerName}; " +
                $"charge={FormatCharge(session.LastControllerCharge)}.");
            DevTrace(
                $"RAID STARTED owner={session.OwnerName} " +
                $"charge={FormatCharge(session.LastControllerCharge)}; " +
                $"confirmation=OrgChat.CLOAK_DISABLED after worker error; " +
                $"waiting for CITY_ATTACKED until {session.StageDeadlineUtc:O}.");
            SaveRaidState();
            Reply(session.Origin, BuildRaidWindow(session));
            return true;
        }

        private void ObserveRaidCloakLowered(string actor)
        {
            RaidSession session;

            lock (_raidSync)
            {
                session = _raidSession;
                if (session == null ||
                    (session.Stage != RaidStage.ControllerFill &&
                     session.Stage != RaidStage.LoweringCloak &&
                     session.Stage != RaidStage.AwaitingCityTarget))
                {
                    return;
                }

                session.CloakLowerConfirmedUtc = DateTime.UtcNow;
                session.CloakLowerConfirmedActor = actor;
            }

            DevTrace(
                $"RAID CLOAK CONFIRMED owner={session.OwnerName} actor={actor} " +
                $"source=OrgChat.CLOAK_DISABLED.");
            SaveRaidState();
        }

        private void ObserveRaidCityMessage(string message, object channelId)
        {
            string location;
            if (!TryGetCityTargetLocation(message, out location))
                return;

            RaidSession session;
            DateTime now = DateTime.UtcNow;
            bool externalAssistOffer = false;

            lock (_raidSync)
            {
                session = _raidSession;

                if (session == null)
                {
                    session = new RaidSession
                    {
                        Token = Guid.NewGuid().ToString("N").Substring(0, 10),
                        OwnerName = "Unmanaged city raid",
                        OwnerId = 0,
                        Origin = ReplyTarget.ForOrg(
                            0,
                            channelId,
                            OrgChannelName),
                        Stage = RaidStage.AssistSelection,
                        CreatedUtc = now,
                        StageDeadlineUtc =
                            now.AddSeconds(Wave8OffsetSeconds),
                        CityTargetedUtc = now,
                        RaidType = "general",
                        Level = DefaultRaidLevel,
                        CurrentMilestone = -1,
                        IsExternalAssist = true
                    };

                    _raidSession = session;
                    externalAssistOffer = true;
                }
                else if (session.Stage == RaidStage.AwaitingCityTarget)
                {
                    session.Stage = RaidStage.Active;
                    session.CityTargetedUtc = now;
                    session.CurrentMilestone = -1;
                    session.StageDeadlineUtc = DateTime.MaxValue;
                }
                else
                {
                    return;
                }
            }

            if (externalAssistOffer)
            {
                Logger.Warning(
                    $"Unmanaged city raid detected at {now:O}; location={location}. " +
                    "Offering officer-controlled raid assistance in org chat.");
                DevTrace(
                    $"RAID ASSIST OFFER location={location} anchor={now:O} " +
                    $"selection-deadline={session.StageDeadlineUtc:O}.");
                SaveRaidState();
                Reply(session.Origin, BuildRaidWindow(session));
                return;
            }

            Logger.Warning(
                $"Raid city-targeted event accepted for {session.OwnerName} at {now:O}; location={location}.");
            DevTrace(
                $"RAID TIMER START owner={session.OwnerName} location={location} anchor={now:O}; " +
                $"wave8=+945s buddy-spinup=+975s general=+1065s cleanup=+1125s.");
            SaveRaidState();
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
                Logger.Debug(
                    $"RAID TIMER owner={session.OwnerName} elapsed={elapsed}s milestone={milestoneText}.");
                SaveRaidState();
            }

            if (elapsed >= BuddyLogoutOffsetSeconds)
            {
                if (session.BuddySpinupInFlight)
                    return;

                BeginSuccessfulRaidCleanup(session);
                return;
            }

            if (string.Equals(session.RaidType, "general", StringComparison.OrdinalIgnoreCase) &&
                elapsed >= GeneralBuddyStartOffsetSeconds &&
                !session.BuddySpinupRequested)
            {
                BeginRaidBuddySpinup(session, "30 seconds after wave 8 arrival");
            }
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

            SaveRaidState();
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
                SaveRaidState();
                Reply(session.Origin, message);
            });
        }

        private void FailRaidSession(
            RaidSession session,
            string reason,
            bool applyCooldown)
        {
            bool removed = false;
            bool cooldownApplied = false;

            lock (_raidSync)
            {
                if (ReferenceEquals(_raidSession, session))
                {
                    _raidSession = null;
                    if (applyCooldown)
                    {
                        cooldownApplied = ApplyRaidCooldownLocked(
                            session.OwnerName,
                            DateTime.UtcNow);
                    }
                    removed = true;
                }
            }

            if (!removed)
                return;

            string cooldownText = cooldownApplied
                ? " A 10-minute raid cooldown now applies."
                : string.Empty;

            Logger.Warning($"Raid failed for {session.OwnerName}: {reason}");
            DevTrace($"RAID FAILED owner={session.OwnerName}: {reason}");
            SaveRaidState();
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
                DevTrace($"RAID DETACHED CLEANUP owner={session.OwnerName}: {result}");
            });
        }

        private string SleepRaidBuddies(RaidSession session)
        {
            List<int> indexes;

            lock (_raidSync)
                indexes = new List<int>(session.StartedBuddyIndexes);

            if (indexes.Count == 0)
                return "No raid buddies needed logout.";

            try
            {
                var request = new WorkerRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Command = "sleepmany",
                    Indexes = indexes,
                    Purpose = "raid-cleanup"
                };

                WorkerResponse response = SendWorkerRequest(
                    BuddiesPipeName,
                    request,
                    WorkerConnectTimeoutMs);

                int slept = response.Count ?? 0;
                string text = $"Logged out {slept}/{indexes.Count} raid buddies.";
                if (!response.Ok && !string.IsNullOrWhiteSpace(response.Message))
                    text += $" {response.Message}";

                return text;
            }
            catch (Exception ex)
            {
                return $"Logged out 0/{indexes.Count} raid buddies. Failure: {ex.Message}";
            }
        }

        private bool IsCurrentRaidSession(RaidSession session)
        {
            lock (_raidSync)
                return ReferenceEquals(_raidSession, session);
        }

        private bool ApplyRaidCooldownLocked(string ownerName, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(ownerName) ||
                IsAdministrator(ownerName))
            {
                return false;
            }

            _raidCooldowns[ownerName] = now.AddSeconds(RaidCooldownSeconds);
            return true;
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

        private string RaidStatePath =>
            Path.Combine(_settingsDir, "citymanager-raid-state.json");

        private void SaveRaidState()
        {
            lock (_raidPersistenceSync)
            {
                try
                {
                    PersistedRaidCoordinatorState state;
                    DateTime now = DateTime.UtcNow;

                    lock (_raidSync)
                    {
                        state = new PersistedRaidCoordinatorState
                        {
                            Version = 1,
                            Session = ToPersistedRaidSession(_raidSession)
                        };

                        foreach (KeyValuePair<string, DateTime> item in _raidCooldowns)
                        {
                            if (item.Value > now)
                                state.Cooldowns[item.Key] = item.Value;
                        }
                    }

                    if (state.Session == null && state.Cooldowns.Count == 0)
                    {
                        DeleteRaidStateFile();
                        return;
                    }

                    string path = RaidStatePath;
                    string tempPath = path + ".tmp";
                    string json = JsonConvert.SerializeObject(state, Formatting.Indented);

                    File.WriteAllText(tempPath, json);

                    if (File.Exists(path))
                        File.Delete(path);

                    File.Move(tempPath, path);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Unable to save raid coordinator state: {ex.Message}");
                    DevTrace($"RAID STATE SAVE ERROR: {ex.Message}");
                }
            }
        }

        private void LoadRaidState()
        {
            string path = RaidStatePath;
            if (!File.Exists(path))
                return;

            try
            {
                PersistedRaidCoordinatorState state =
                    JsonConvert.DeserializeObject<PersistedRaidCoordinatorState>(
                        File.ReadAllText(path));

                if (state == null || state.Version != 1)
                    throw new InvalidDataException("Unsupported raid-state file.");

                DateTime now = DateTime.UtcNow;
                RaidSession restored = FromPersistedRaidSession(state.Session);
                bool predictedTargetRecovery = false;
                bool resumeControllerWorkers = false;

                lock (_raidSync)
                {
                    _raidCooldowns.Clear();
                    if (state.Cooldowns != null)
                    {
                        foreach (KeyValuePair<string, DateTime> item in state.Cooldowns)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Key) && item.Value > now)
                                _raidCooldowns[item.Key] = item.Value;
                        }
                    }

                    if (restored != null)
                    {
                        restored.ControllerProbeInFlight = false;
                        restored.BuddySpinupInFlight = false;
                        restored.BuddyCleanupInFlight = false;
                        restored.WorkerWaitAnnounced = false;

                        if (restored.Stage == RaidStage.LoweringCloak &&
                            !restored.CloakLowerConfirmedUtc.HasValue &&
                            _status == CloakStatus.Disabled)
                        {
                            restored.CloakLowerConfirmedUtc =
                                _lastChangedUtc ?? now;
                            restored.CloakLowerConfirmedActor =
                                "persisted Manager cloak state";
                        }

                        if (restored.Stage == RaidStage.LoweringCloak)
                        {
                            if (restored.CloakLowerConfirmedUtc.HasValue)
                            {
                                restored.Stage = RaidStage.AwaitingCityTarget;
                                restored.StageDeadlineUtc =
                                    restored.CloakLowerConfirmedUtc.Value
                                        .AddSeconds(
                                            CityTargetAfterCloakSeconds +
                                            RaidTargetTimeoutSeconds);
                            }
                            else
                            {
                                restored.Stage = RaidStage.ControllerFill;
                                restored.StageDeadlineUtc = now;
                                restored.WorkerDeadlineUtc =
                                    now.AddSeconds(RaidWorkerGraceSeconds);
                                restored.ControllerRetryPending = true;
                                restored.ControllerRetryNotBeforeUtc = now;
                                resumeControllerWorkers = true;
                            }
                        }

                        if (restored.Stage == RaidStage.ControllerFill)
                        {
                            restored.ControllerProbeInFlight = false;
                            if (now >= restored.StageDeadlineUtc &&
                                now < restored.WorkerDeadlineUtc)
                            {
                                restored.ControllerRetryPending = true;
                                restored.ControllerRetryNotBeforeUtc = now;
                            }
                            restored.BuddySpinupRequested = false;
                            restored.BuddySpinupFatal = false;
                            resumeControllerWorkers = true;
                        }

                        if (restored.Stage == RaidStage.AwaitingCityTarget &&
                            restored.CloakLowerConfirmedUtc.HasValue)
                        {
                            DateTime predictedTargetUtc =
                                restored.CloakLowerConfirmedUtc.Value
                                    .AddSeconds(CityTargetAfterCloakSeconds);

                            if (now >= predictedTargetUtc)
                            {
                                restored.Stage = RaidStage.Active;
                                restored.CityTargetedUtc = predictedTargetUtc;
                                restored.StageDeadlineUtc = DateTime.MaxValue;
                                restored.CurrentMilestone =
                                    GetRaidMilestoneIndex(now - predictedTargetUtc);
                                predictedTargetRecovery = true;
                            }
                        }

                        if (restored.Stage == RaidStage.Active &&
                            string.Equals(
                                restored.RaidType,
                                "general",
                                StringComparison.OrdinalIgnoreCase) &&
                            restored.StartedBuddyIndexes.Count == 0)
                        {
                            restored.BuddySpinupRequested = false;
                        }

                        if (restored.Stage == RaidStage.CleaningUp)
                        {
                            restored.Stage = RaidStage.Active;
                            if (restored.CityTargetedUtc == default(DateTime))
                            {
                                restored.CityTargetedUtc =
                                    now.AddSeconds(-BuddyLogoutOffsetSeconds);
                            }
                        }
                    }

                    _raidSession = restored;
                }

                if (restored == null)
                {
                    SaveRaidState();
                    return;
                }

                Logger.Warning(
                    $"Restored raid state for {restored.OwnerName}: " +
                    $"stage={restored.Stage}, type={restored.RaidType}, " +
                    $"level={restored.Level}, count={restored.RaiderCount}.");
                DevTrace(
                    $"RAID RESTORED owner={restored.OwnerName} stage={restored.Stage} " +
                    $"anchor={(restored.CityTargetedUtc == default(DateTime) ? "unset" : restored.CityTargetedUtc.ToString("O"))}.");

                if (predictedTargetRecovery)
                {
                    DevTrace(
                        $"RAID TIMER RECOVERED from persisted CLOAK_DISABLED: " +
                        $"predicted CITY_ATTACKED={restored.CityTargetedUtc:O}.");
                }

                SaveRaidState();

                if (resumeControllerWorkers)
                    _raidResumeWorkersOnInPlay = true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to restore raid coordinator state: {ex.Message}");
                DevTrace($"RAID STATE LOAD ERROR: {ex.Message}");
            }
        }

        private void ResumeRaidCoordinatorAfterInPlay()
        {
            RaidSession session;
            bool resumeWorkers;

            lock (_raidSync)
            {
                session = _raidSession;
                resumeWorkers = _raidResumeWorkersOnInPlay;
                _raidResumeWorkersOnInPlay = false;
            }

            if (session == null)
                return;

            Reply(session.Origin, BuildRaidWindow(session));

            if (!resumeWorkers || session.Stage != RaidStage.ControllerFill)
                return;

            if (string.Equals(
                    session.RaidType,
                    "all",
                    StringComparison.OrdinalIgnoreCase))
            {
                BeginRaidBuddySpinup(session, "restart recovery");
            }

            TryStartControllerWatch(session);
        }

        private PersistedRaidSession ToPersistedRaidSession(RaidSession session)
        {
            if (session == null)
                return null;

            return new PersistedRaidSession
            {
                Token = session.Token,
                OwnerName = session.OwnerName,
                OwnerId = session.OwnerId,
                OriginKind = session.Origin != null
                    ? session.Origin.Kind.ToString()
                    : ReplyKind.Tell.ToString(),
                OriginSenderId = session.Origin != null
                    ? session.Origin.SenderId
                    : session.OwnerId,
                OriginChannelName = session.Origin != null
                    ? session.Origin.ChannelName
                    : null,
                Stage = session.Stage.ToString(),
                CreatedUtc = session.CreatedUtc,
                StageDeadlineUtc = session.StageDeadlineUtc,
                WorkerDeadlineUtc = session.WorkerDeadlineUtc,
                CityTargetedUtc = session.CityTargetedUtc,
                CloakLowerConfirmedUtc = session.CloakLowerConfirmedUtc,
                CloakLowerConfirmedActor = session.CloakLowerConfirmedActor,
                RaidType = session.RaidType,
                Level = session.Level,
                RaiderCount = session.RaiderCount,
                LastControllerCharge = session.LastControllerCharge,
                FlipperDetail = session.FlipperDetail,
                BuddyDetail = session.BuddyDetail,
                BuddySpinupRequested = session.BuddySpinupRequested,
                BuddySpinupFatal = session.BuddySpinupFatal,
                IsExternalAssist = session.IsExternalAssist,
                CurrentMilestone = session.CurrentMilestone,
                ControllerRetryPending = session.ControllerRetryPending,
                ControllerRetryNotBeforeUtc = session.ControllerRetryNotBeforeUtc,
                StartedBuddyIndexes =
                    new List<int>(session.StartedBuddyIndexes)
            };
        }

        private RaidSession FromPersistedRaidSession(PersistedRaidSession saved)
        {
            if (saved == null)
                return null;

            RaidStage stage;
            ReplyKind originKind;

            if (string.IsNullOrWhiteSpace(saved.Token) ||
                string.IsNullOrWhiteSpace(saved.OwnerName) ||
                !Enum.TryParse(saved.Stage, true, out stage) ||
                !Enum.TryParse(saved.OriginKind, true, out originKind))
            {
                throw new InvalidDataException("Raid-state session is incomplete.");
            }

            ReplyTarget origin;
            switch (originKind)
            {
                case ReplyKind.Org:
                    origin = ReplyTarget.ForOrg(
                        saved.OriginSenderId,
                        null,
                        saved.OriginChannelName ?? OrgChannelName);
                    break;

                case ReplyKind.Guest:
                    origin = ReplyTarget.ForGuest(saved.OriginSenderId, null);
                    break;

                default:
                    origin = ReplyTarget.ForTell(saved.OriginSenderId);
                    break;
            }

            var session = new RaidSession
            {
                Token = saved.Token,
                OwnerName = saved.OwnerName,
                OwnerId = saved.OwnerId,
                Origin = origin,
                Stage = stage,
                CreatedUtc = saved.CreatedUtc,
                StageDeadlineUtc = saved.StageDeadlineUtc,
                WorkerDeadlineUtc = saved.WorkerDeadlineUtc,
                CityTargetedUtc = saved.CityTargetedUtc,
                CloakLowerConfirmedUtc = saved.CloakLowerConfirmedUtc,
                CloakLowerConfirmedActor = saved.CloakLowerConfirmedActor,
                RaidType = saved.RaidType,
                Level = saved.Level,
                RaiderCount = saved.RaiderCount,
                LastControllerCharge = saved.LastControllerCharge,
                FlipperDetail = saved.FlipperDetail,
                BuddyDetail = saved.BuddyDetail,
                BuddySpinupRequested = saved.BuddySpinupRequested,
                BuddySpinupFatal = saved.BuddySpinupFatal,
                IsExternalAssist = saved.IsExternalAssist,
                CurrentMilestone = saved.CurrentMilestone,
                ControllerRetryPending = saved.ControllerRetryPending,
                ControllerRetryNotBeforeUtc = saved.ControllerRetryNotBeforeUtc
            };

            if (saved.StartedBuddyIndexes != null)
                session.StartedBuddyIndexes.AddRange(saved.StartedBuddyIndexes);

            return session;
        }

        private int GetRaidMilestoneIndex(TimeSpan elapsed)
        {
            int seconds = Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds));
            int milestone = -1;

            for (int index = 0; index < RaidMilestoneOffsets.Length; index++)
            {
                if (seconds < RaidMilestoneOffsets[index])
                    break;

                milestone = index;
            }

            return milestone;
        }

        private string BuildRaidStatusSummary()
        {
            lock (_raidSync)
            {
                if (_raidSession == null)
                    return "Raid = idle";

                RaidSession session = _raidSession;
                string owner = string.IsNullOrWhiteSpace(session.OwnerName)
                    ? "unknown"
                    : session.OwnerName;
                string selection =
                    $"{session.RaidType ?? "unset"}, " +
                    $"{(session.RaiderCount.HasValue ? session.RaiderCount.Value.ToString() : "unset")}x{session.Level}";

                switch (session.Stage)
                {
                    case RaidStage.Configuring:
                        return
                            $"Raid = setup for {owner} ({selection}), " +
                            $"{FormatDuration(session.StageDeadlineUtc - DateTime.UtcNow)} left";

                    case RaidStage.AdminVeto:
                        return $"Raid = resuming CT handling for {owner} ({selection})";

                    case RaidStage.ControllerFill:
                        return
                            $"Raid = CT fill for {owner} ({selection}), " +
                            $"charge {FormatCharge(session.LastControllerCharge)}, " +
                            $"{FormatDuration(session.StageDeadlineUtc - DateTime.UtcNow)} left";

                    case RaidStage.LoweringCloak:
                        return $"Raid = lowering cloak for {owner} ({selection})";

                    case RaidStage.AwaitingCityTarget:
                        return $"Raid = cloak lowered for {owner}; waiting for city target";

                    case RaidStage.AssistSelection:
                        return
                            $"Raid = unmanaged; assistance selection closes in " +
                            FormatDuration(session.StageDeadlineUtc - DateTime.UtcNow);

                    case RaidStage.CleaningUp:
                        return $"Raid = general reached; buddy cleanup for {owner}";

                    case RaidStage.Active:
                    {
                        int elapsed = Math.Max(
                            0,
                            (int)Math.Floor(
                                (DateTime.UtcNow - session.CityTargetedUtc).TotalSeconds));
                        var details = new List<string>
                        {
                            RaidMilestoneText(session.CurrentMilestone)
                        };

                        if (string.Equals(
                                session.RaidType,
                                "general",
                                StringComparison.OrdinalIgnoreCase) &&
                            !session.BuddySpinupRequested &&
                            elapsed < GeneralBuddyStartOffsetSeconds)
                        {
                            details.Add(
                                "buddy login in " +
                                FormatDuration(
                                    TimeSpan.FromSeconds(
                                        GeneralBuddyStartOffsetSeconds - elapsed)));
                        }

                        if (elapsed < BuddyLogoutOffsetSeconds)
                        {
                            details.Add(
                                "buddy logout in " +
                                FormatDuration(
                                    TimeSpan.FromSeconds(
                                        BuddyLogoutOffsetSeconds - elapsed)));
                        }

                        return
                            $"Raid = active for {owner} ({selection}); " +
                            string.Join(", ", details);
                    }

                    default:
                        return $"Raid = {session.Stage} for {owner}";
                }
            }
        }

        private bool IsRaidFlipperBusy()
        {
            lock (_raidSync)
            {
                return _raidSession != null &&
                       (_raidSession.Stage == RaidStage.ControllerFill ||
                        _raidSession.Stage == RaidStage.LoweringCloak);
            }
        }

        private bool TryReplyRaidFlipperReservation(ReplyTarget target)
        {
            string reply = null;

            lock (_raidSync)
            {
                if (_raidSession != null &&
                    (_raidSession.Stage == RaidStage.ControllerFill ||
                     _raidSession.Stage == RaidStage.LoweringCloak))
                {
                    reply =
                        "City Cloak: raid start is using Flipper. " +
                        $"CT charge = {FormatCharge(_raidSession.LastControllerCharge)}.";
                }
            }

            if (reply == null)
                return false;

            Reply(target, reply);
            return true;
        }

        private void ApplyRaidControllerObservation(WorkerResponse response)
        {
            if (response == null || !response.ControllerCharge.HasValue)
                return;

            bool changed = false;

            lock (_raidSync)
            {
                if (_raidSession == null ||
                    (_raidSession.Stage != RaidStage.ControllerFill &&
                     _raidSession.Stage != RaidStage.LoweringCloak))
                {
                    return;
                }

                _raidSession.LastControllerCharge = response.ControllerCharge;
                changed = true;
            }

            if (changed)
                SaveRaidState();
        }

        private void DeleteRaidStateFile()
        {
            lock (_raidPersistenceSync)
            {
                try
                {
                    string path = RaidStatePath;
                    string tempPath = path + ".tmp";

                    if (File.Exists(path))
                        File.Delete(path);
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Unable to delete completed raid-state file: {ex.Message}");
                }
            }
        }

        private string BuildRaidWindow(RaidSession session)
        {
            var body = new StringBuilder();

            if (session.Stage == RaidStage.AssistSelection)
            {
                body.Append("<font color='#89D2E8'>City Dwellers Raid Assistance</font>\n\n");
                body.Append("<font color='#00DE42'>A city raid is in progress.</font>\n");
                body.Append(
                    "Squad Commanders and higher: do you need " +
                    "City Dwellers for the remaining raid?\n\n");
                body.Append("Select assistance type:\n");
                body.Append(
                    RaidAssistTypeButton(
                        session,
                        "all",
                        "All remaining waves"));
                body.Append("  ");
                body.Append(
                    RaidAssistTypeButton(
                        session,
                        "general",
                        "General only"));
                body.Append("\n\n");
                body.Append("Select level:\n");

                foreach (int availableLevel in AvailableRaidLevels)
                {
                    body.Append(
                        RaidAssistLevelButton(
                            session,
                            availableLevel,
                            availableLevel.ToString()));
                    body.Append("  ");
                }

                body.Append("\n\n");
                body.Append("Select number of City Dwellers:\n");

                for (int count = 0; count <= 12; count++)
                {
                    if (count > 0)
                        body.Append("  ");

                    string label = count == 0 ? "No" : count.ToString();
                    body.Append(RaidAssistButton(session, count, label));
                }

                body.Append("\n\n");
                body.Append(
                    $"Selection closes at wave 8 in: <font color='#FFFF00'>" +
                    $"{FormatDuration(session.StageDeadlineUtc - DateTime.UtcNow)}</font>\n");

                return
                    $"<a href=\"text://{body}\">Click here to open window</a>";
            }

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
                    body.Append("<font color='#F79410'>Resuming CT handling from an older saved raid request.</font>\n");
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
                    if (session.BuddyPreparationInFlight)
                        body.Append(
                            "Buddies: checking general-only raiders and returning " +
                            "misplaced toons home...\n");
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

            if (!string.IsNullOrWhiteSpace(session.BuddyPreparationDetail))
            {
                body.Append(
                    $"Preparation: " +
                    $"{SafeRaidText(session.BuddyPreparationDetail)}\n");
            }

            if (!string.IsNullOrWhiteSpace(session.FlipperDetail))
                body.Append($"Flipper: {SafeRaidText(session.FlipperDetail)}\n");

            body.Append(
                $"\n{RaidCancelButton(session)} " +
                "(raid owner or administrator)\n");

            return
                $"<a href=\"text://{body}\">Click here to open window</a>";
        }

        private string RaidAssistButton(
            RaidSession session,
            int count,
            string label)
        {
            string command =
                $"chatcmd:///o #raidassist {count} {session.Token}";

            return
                $"<a href='{command}'><font color='#00BFFF'>" +
                $"[{SafeRaidText(label)}]</font></a>";
        }

        private string RaidCancelButton(RaidSession session)
        {
            string command;

            if (session.Origin.Kind == ReplyKind.Org)
                command = $"chatcmd:///o #cancel {session.Token}";
            else if (session.Origin.Kind == ReplyKind.Guest)
                command = $"chatcmd:///g Apcmanager #cancel {session.Token}";
            else
                command = $"chatcmd:///tell Apcmanager #cancel {session.Token}";

            return
                $"<a href='{command}'><font color='#F79410'>" +
                "[CANCEL RAID]</font></a>";
        }

        private string RaidAssistLevelButton(
            RaidSession session,
            int level,
            string label)
        {
            string command =
                $"chatcmd:///o #raidassist level {level} {session.Token}";
            string color = session.Level == level ? "#00DE42" : "#00BFFF";

            return
                $"<a href='{command}'><font color='{color}'>" +
                $"[{SafeRaidText(label)}]</font></a>";
        }

        private string RaidAssistTypeButton(
            RaidSession session,
            string raidType,
            string label)
        {
            string command =
                $"chatcmd:///o #raidassist type {raidType} {session.Token}";
            string color = string.Equals(
                session.RaidType,
                raidType,
                StringComparison.OrdinalIgnoreCase)
                    ? "#00DE42"
                    : "#00BFFF";

            return
                $"<a href='{command}'><font color='{color}'>" +
                $"[{SafeRaidText(label)}]</font></a>";
        }

        private void AppendConfigurationControls(StringBuilder body, RaidSession session)
        {
            body.Append("Select raid type:\n");
            body.Append(RaidButton(session, "type all", "All waves", session.RaidType == "all"));
            body.Append("  ");
            body.Append(RaidButton(session, "type general", "General only", session.RaidType == "general"));
            body.Append("\n\nSelect level:\n");

            foreach (int availableLevel in AvailableRaidLevels)
            {
                body.Append(
                    RaidButton(
                        session,
                        $"level {availableLevel}",
                        availableLevel.ToString(),
                        session.Level == availableLevel));
                body.Append("  ");
            }

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

        private bool IsAvailableRaidLevel(int level)
        {
            foreach (int availableLevel in AvailableRaidLevels)
            {
                if (level == availableLevel)
                    return true;
            }

            return false;
        }

        private enum RaidStage
        {
            AssistSelection,
            Configuring,
            AdminVeto,
            ControllerFill,
            LoweringCloak,
            AwaitingCityTarget,
            Active,
            CleaningUp
        }

        private sealed class PersistedRaidCoordinatorState
        {
            public int Version;
            public PersistedRaidSession Session;
            public Dictionary<string, DateTime> Cooldowns =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedRaidSession
        {
            public string Token;
            public string OwnerName;
            public uint OwnerId;
            public string OriginKind;
            public uint OriginSenderId;
            public string OriginChannelName;
            public string Stage;
            public DateTime CreatedUtc;
            public DateTime StageDeadlineUtc;
            public DateTime WorkerDeadlineUtc;
            public DateTime CityTargetedUtc;
            public DateTime? CloakLowerConfirmedUtc;
            public string CloakLowerConfirmedActor;
            public string RaidType;
            public int Level;
            public int? RaiderCount;
            public float? LastControllerCharge;
            public string FlipperDetail;
            public string BuddyDetail;
            public bool BuddySpinupRequested;
            public bool BuddySpinupFatal;
            public bool IsExternalAssist;
            public int CurrentMilestone;
            public bool ControllerRetryPending;
            public DateTime ControllerRetryNotBeforeUtc;
            public List<int> StartedBuddyIndexes = new List<int>();
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
            public DateTime? CloakLowerConfirmedUtc;

            public string RaidType;
            public int Level;
            public int? RaiderCount;

            public float? LastControllerCharge;
            public string FlipperDetail;
            public string BuddyDetail;
            public string CloakLowerConfirmedActor;

            public bool ControllerProbeInFlight;
            public string ControllerRequestId;
            public bool ControllerRetryPending;
            public DateTime ControllerRetryNotBeforeUtc;
            public bool BuddySpinupRequested;
            public bool BuddySpinupInFlight;
            public bool BuddySpinupFatal;
            public bool BuddyCleanupInFlight;
            public bool BuddyPreparationInFlight;
            public string BuddyPreparationDetail;
            public bool WorkerWaitAnnounced;
            public bool IsExternalAssist;
            public int CurrentMilestone;

            public readonly List<int> StartedBuddyIndexes = new List<int>();
        }
    }
}
