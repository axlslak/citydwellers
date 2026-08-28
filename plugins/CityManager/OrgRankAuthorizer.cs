using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityManager
{
    internal static class OrgRankAuthorizer
    {
        private const int RankCacheSeconds = 300;
        private const int LookupTimeoutMs = 5000;

        private static readonly object Sync = new object();
        private static readonly Dictionary<uint, CachedRank> RankCache =
            new Dictionary<uint, CachedRank>();
        private static readonly Dictionary<uint, PendingLookup> PendingLookups =
            new Dictionary<uint, PendingLookup>();

        private static bool _initialized;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized)
                    return;

                Client.MessageReceived += MessageReceived;
                _initialized = true;
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (_initialized)
                    Client.MessageReceived -= MessageReceived;

                _initialized = false;
                RankCache.Clear();
                PendingLookups.Clear();
            }
        }

        public static void Authorize(
            uint senderId,
            string senderName,
            Action<OrgRankAuthorization> callback)
        {
            if (callback == null)
                return;

            CachedRank cached = null;

            lock (Sync)
            {
                CachedRank candidate;
                if (RankCache.TryGetValue(senderId, out candidate))
                {
                    if (candidate.ExpiresUtc > DateTime.UtcNow)
                        cached = candidate;
                    else
                        RankCache.Remove(senderId);
                }
            }

            if (cached != null)
            {
                callback(BuildResult(cached.Rank, true));
                return;
            }

            bool shouldRequest = false;

            lock (Sync)
            {
                PendingLookup pending;
                if (!PendingLookups.TryGetValue(senderId, out pending))
                {
                    pending = new PendingLookup
                    {
                        SenderId = senderId,
                        SenderName = senderName,
                        RequestedUtc = DateTime.UtcNow
                    };
                    PendingLookups[senderId] = pending;
                    shouldRequest = true;
                }

                pending.Callbacks.Add(callback);
            }

            if (!shouldRequest)
                return;

            try
            {
                Logger.Information(
                    $"Requesting org rank for {senderName} ({senderId}) for command authorization.");

                Client.InfoRequest(
                    new Identity(IdentityType.SimpleChar, unchecked((int)senderId)));
            }
            catch (Exception ex)
            {
                CompleteFailure(
                    senderId,
                    $"Org rank lookup failed: {ex.Message}");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(LookupTimeoutMs);
                Timeout(senderId);
            });
        }

        private static void MessageReceived(object sender, Message e)
        {
            try
            {
                if (e?.Body == null || e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3 = (N3Message)e.Body;
                if (n3.N3MessageType != N3MessageType.InfoPacket)
                    return;

                uint senderId = unchecked((uint)n3.Identity.Instance);

                lock (Sync)
                {
                    if (!PendingLookups.ContainsKey(senderId))
                        return;
                }

                var infoPacket = (InfoPacketMessage)e.Body;
                if (infoPacket.Type != InfoPacketType.CharacterOrg &&
                    infoPacket.Type != InfoPacketType.CharacterOrgSite &&
                    infoPacket.Type != InfoPacketType.CharacterOrgSiteTower)
                {
                    return;
                }

                var characterInfo = infoPacket.Info as CharacterInfoPacket;
                if (characterInfo == null ||
                    string.IsNullOrWhiteSpace(characterInfo.OrganizationRank))
                {
                    return;
                }

                string rank = characterInfo.OrganizationRank.Trim();

                lock (Sync)
                {
                    RankCache[senderId] = new CachedRank
                    {
                        Rank = rank,
                        ExpiresUtc = DateTime.UtcNow.AddSeconds(RankCacheSeconds)
                    };
                }

                Logger.Information(
                    $"Org rank resolved for {senderId}: {rank}.");

                Complete(senderId, BuildResult(rank, false));
            }
            catch (Exception ex)
            {
                Logger.Warning($"Org rank packet handling failed: {ex.Message}");
            }
        }

        private static OrgRankAuthorization BuildResult(string rank, bool fromCache)
        {
            return new OrgRankAuthorization
            {
                Allowed = IsSquadCommanderOrHigher(rank),
                Rank = rank,
                FromCache = fromCache
            };
        }

        internal static bool IsSquadCommanderOrHigher(string rank)
        {
            return string.Equals(rank, "President", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rank, "General", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rank, "Squad Commander", StringComparison.OrdinalIgnoreCase);
        }

        private static void Timeout(uint senderId)
        {
            PendingLookup pending;

            lock (Sync)
            {
                if (!PendingLookups.TryGetValue(senderId, out pending))
                    return;

                if ((DateTime.UtcNow - pending.RequestedUtc).TotalMilliseconds < LookupTimeoutMs)
                    return;
            }

            CompleteFailure(senderId, "Org rank lookup timed out.");
        }

        private static void CompleteFailure(uint senderId, string error)
        {
            Complete(
                senderId,
                new OrgRankAuthorization
                {
                    Allowed = false,
                    Error = error
                });
        }

        private static void Complete(uint senderId, OrgRankAuthorization result)
        {
            List<Action<OrgRankAuthorization>> callbacks = null;

            lock (Sync)
            {
                PendingLookup pending;
                if (!PendingLookups.TryGetValue(senderId, out pending))
                    return;

                PendingLookups.Remove(senderId);
                callbacks = new List<Action<OrgRankAuthorization>>(pending.Callbacks);
            }

            foreach (Action<OrgRankAuthorization> callback in callbacks)
            {
                try
                {
                    callback(result);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Org rank authorization callback failed: {ex.Message}");
                }
            }
        }

        private class CachedRank
        {
            public string Rank;
            public DateTime ExpiresUtc;
        }

        private class PendingLookup
        {
            public uint SenderId;
            public string SenderName;
            public DateTime RequestedUtc;
            public readonly List<Action<OrgRankAuthorization>> Callbacks =
                new List<Action<OrgRankAuthorization>>();
        }
    }

    internal class OrgRankAuthorization
    {
        public bool Allowed;
        public string Rank;
        public string Error;
        public bool FromCache;
    }

    internal static class CityRaidAutomation
    {
        private const string OrgChannelName = "Athen Paladins";
        private const string FlipperPipeName = "citydwellers-flipper";
        private const int FlipperConnectTimeoutMs = 1000;
        private const int RetryAfterFailureSeconds = 30;
        private const int DuplicateRaidWindowSeconds = 20;
        private const int PersistedStateTrustSeconds = 3600;
        private const int CloakDownSeconds = 3600;
        private const int CityTargetAfterCloakSeconds = 180;

        private static readonly object Sync = new object();

        private static bool _initialized;
        private static bool _groupSubscribed;
        private static bool _ensureInFlight;
        private static bool _raidRecoveryPending;
        private static bool _bootAssessmentStarted;
        private static bool _failureReported;
        private static DateTime _managerStartedUtc = DateTime.MinValue;
        private static DateTime _raidOccurredUtc = DateTime.MinValue;
        private static DateTime _lastRaidEventUtc = DateTime.MinValue;
        private static DateTime? _lastObservedUtc;
        private static DateTime? _canRaiseAtUtc;
        private static CloakStatus _knownStatus = CloakStatus.Unknown;
        private static string _observationSource = "Unknown";
        private static Action<CloakStatus, int?, DateTime?, bool, string>
            _observationCallback;
        private static Timer _retryTimer;

        public static void Initialize(
            CloakStatus status,
            DateTime? lastObservedUtc,
            DateTime? canRaiseAtUtc,
            string observationSource,
            Action<CloakStatus, int?, DateTime?, bool, string> observationCallback)
        {
            lock (Sync)
            {
                if (_initialized)
                    return;

                Client.MessageReceived += LifecycleMessageReceived;
                _managerStartedUtc = DateTime.UtcNow;
                _bootAssessmentStarted = false;
                _failureReported = false;
                _knownStatus = status;
                _lastObservedUtc = lastObservedUtc;
                _canRaiseAtUtc = canRaiseAtUtc;
                _observationSource = observationSource ?? "Unknown";
                _observationCallback = observationCallback;
                _initialized = true;
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (!_initialized)
                    return;

                Client.MessageReceived -= LifecycleMessageReceived;

                if (_groupSubscribed && Client.Chat != null)
                    Client.Chat.GroupMessageReceived -= HandleGroupMessage;

                _groupSubscribed = false;
                _initialized = false;
                _ensureInFlight = false;
                _raidRecoveryPending = false;
                _bootAssessmentStarted = false;
                _failureReported = false;
                _knownStatus = CloakStatus.Unknown;
                _lastObservedUtc = null;
                _canRaiseAtUtc = null;
                _observationSource = "Unknown";
                _observationCallback = null;

                if (_retryTimer != null)
                {
                    _retryTimer.Dispose();
                    _retryTimer = null;
                }
            }
        }

        private static void LifecycleMessageReceived(object sender, Message e)
        {
            try
            {
                if (e?.Body == null || e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3 = (N3Message)e.Body;
                if (n3.N3MessageType != N3MessageType.CharInPlay)
                    return;

                var charInPlay = (CharInPlayMessage)e.Body;
                if (charInPlay.Identity.Instance != Client.LocalDynelId)
                    return;

                SubscribeGroupMessages();
                StartBootCloakAssessment();
            }
            catch (Exception ex)
            {
                Logger.Warning($"City raid lifecycle handler failed: {ex.Message}");
            }
        }

        private static void SubscribeGroupMessages()
        {
            lock (Sync)
            {
                if (_groupSubscribed || Client.Chat == null)
                    return;

                Client.Chat.GroupMessageReceived += HandleGroupMessage;
                _groupSubscribed = true;
                Logger.Information("City raid automation is observing organization city events.");
            }
        }

        private static void StartBootCloakAssessment()
        {
            DateTime now = DateTime.UtcNow;
            DateTime assessmentUtc = now;
            DateTime? scheduledUtc = null;
            bool requestLiveAssessment = false;
            string decision;

            lock (Sync)
            {
                if (_bootAssessmentStarted)
                    return;

                _bootAssessmentStarted = true;
                CancelRetryLocked();

                bool recentPersistedObservation =
                    _knownStatus != CloakStatus.Unknown &&
                    _lastObservedUtc.HasValue &&
                    _lastObservedUtc.Value <= now.AddMinutes(1) &&
                    (now - _lastObservedUtc.Value).TotalSeconds <=
                        PersistedStateTrustSeconds;

                if (recentPersistedObservation &&
                    _knownStatus == CloakStatus.Enabled)
                {
                    _raidRecoveryPending = false;
                    decision =
                        $"trusted persisted Enabled observation from " +
                        $"{_lastObservedUtc.Value:O} ({_observationSource})";
                }
                else if (recentPersistedObservation &&
                         _knownStatus == CloakStatus.Disabled)
                {
                    _raidOccurredUtc = _lastObservedUtc.Value;
                    _raidRecoveryPending = true;
                    scheduledUtc = _canRaiseAtUtc ??
                        _lastObservedUtc.Value.AddSeconds(CloakDownSeconds);

                    if (scheduledUtc.Value <= now)
                    {
                        requestLiveAssessment = true;
                        assessmentUtc = _raidOccurredUtc;
                        decision = "persisted cloak deadline is already due";
                    }
                    else
                    {
                        decision =
                            $"trusted persisted Disabled observation; recovery due " +
                            $"{scheduledUtc.Value:O}";
                    }
                }
                else
                {
                    _raidOccurredUtc = assessmentUtc;
                    _raidRecoveryPending = true;
                    requestLiveAssessment = true;
                    decision = "persisted state is absent or older than one hour";
                }
            }

            Logger.Information(
                $"CLOAK BOOT ASSESSMENT: manager-start={_managerStartedUtc:O}; " +
                $"{decision}.");

            if (scheduledUtc.HasValue && scheduledUtc.Value > now)
            {
                ScheduleRetry(
                    Math.Max(1, (int)Math.Ceiling((scheduledUtc.Value - now).TotalSeconds)));
                return;
            }

            if (requestLiveAssessment)
            {
                // ensure-enabled is safe as an assessment: it can observe or raise,
                // but it can never lower the cloak. NotBeforeUtc prevents a stale
                // cache from being mistaken for a current boot observation.
                QueueEnsureEnabled(assessmentUtc, "MANAGER_BOOT");
            }
        }

        private static void HandleGroupMessage(object sender, GroupMsg msg)
        {
            try
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.Message))
                    return;

                if (!string.Equals(msg.ChannelName, OrgChannelName, StringComparison.OrdinalIgnoreCase))
                    return;

                string text =
                    CityExtendedMessageParser.DecodeOrOriginal(msg.Message).Trim();

                const string cloakOff = " turned the cloaking device in your city off.";
                const string cloakOn = " turned the cloaking device in your city on.";

                int offIndex = text.IndexOf(cloakOff, StringComparison.OrdinalIgnoreCase);
                if (offIndex > 0)
                {
                    ObserveCloakDisabled();
                    return;
                }

                int onIndex = text.IndexOf(cloakOn, StringComparison.OrdinalIgnoreCase);
                if (onIndex > 0)
                {
                    lock (Sync)
                    {
                        _knownStatus = CloakStatus.Enabled;
                        _lastObservedUtc = DateTime.UtcNow;
                        _canRaiseAtUtc = null;
                        _observationSource = "OrgChat.CloakAnnouncement";
                    }

                    CompleteRecoveryIfPending("cloak-enabled city event");
                    return;
                }

                if (text.IndexOf(
                        "Your radar station is picking up alien activity in the area surrounding your city.",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                if (text.StartsWith("Your city in ", StringComparison.OrdinalIgnoreCase) &&
                    text.EndsWith(
                        " has been targeted by hostile forces.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = " has been targeted by hostile forces.";
                    string location = text.Substring(
                        "Your city in ".Length,
                        text.Length - "Your city in ".Length - suffix.Length).Trim();

                    OnCityAttacked(location);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"City raid group handler failed: {ex.Message}");
            }
        }

        private static void ObserveCloakDisabled()
        {
            DateTime now = DateTime.UtcNow;

            lock (Sync)
            {
                _knownStatus = CloakStatus.Disabled;
                _lastObservedUtc = now;
                _canRaiseAtUtc = now.AddSeconds(CloakDownSeconds);
                _observationSource = "OrgChat.CloakAnnouncement";
                _raidOccurredUtc = now;
                _raidRecoveryPending = true;
                _failureReported = false;
                CancelRetryLocked();
            }

            // The org announcement is authoritative. Do not log Flipper back in
            // merely to rediscover the same state; arm the one-hour deadline.
            ScheduleRetry(CloakDownSeconds);
        }

        private static void OnCityAttacked(string location)
        {
            DateTime now = DateTime.UtcNow;
            DateTime recoveryUtc;

            lock (Sync)
            {
                if ((now - _lastRaidEventUtc).TotalSeconds < DuplicateRaidWindowSeconds)
                    return;

                _lastRaidEventUtc = now;

                if (_knownStatus == CloakStatus.Disabled &&
                    _canRaiseAtUtc.HasValue)
                {
                    // The earlier cloak-off announcement already supplied the
                    // authoritative timer. CITY_ATTACKED must not cause a probe.
                    return;
                }

                // If Manager came online between cloak-off and CITY_ATTACKED,
                // the targeted event is still enough to reconstruct a safe timer.
                _knownStatus = CloakStatus.Disabled;
                _lastObservedUtc = now;
                _observationSource = "OrgChat.CityTargeted";
                _raidOccurredUtc = now.AddSeconds(-CityTargetAfterCloakSeconds);
                _canRaiseAtUtc = _raidOccurredUtc.AddSeconds(CloakDownSeconds);
                recoveryUtc = _canRaiseAtUtc.Value;
                _raidRecoveryPending = true;
                _failureReported = false;
                CancelRetryLocked();
            }

            Logger.Information(
                $"CITY_ATTACKED at {location}; reconstructed cloak recovery " +
                $"deadline={recoveryUtc:O} without a Flipper probe.");
            ScheduleRetry(
                Math.Max(1, (int)Math.Ceiling((recoveryUtc - now).TotalSeconds)));
        }

        private static void QueueEnsureEnabled(DateTime raidOccurredUtc, string reason)
        {
            lock (Sync)
            {
                if (!_raidRecoveryPending || _ensureInFlight)
                    return;

                _ensureInFlight = true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string shortId = null;

                try
                {
                    var request = new FlipperRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = "ensure-enabled",
                        NotBeforeUtc = raidOccurredUtc
                    };

                    shortId = request.Id.Substring(0, 8);
                    if (!string.Equals(reason, "retry", StringComparison.OrdinalIgnoreCase))
                    {
                        SendDev(
                            $"CLOAK RECOVERY -> ensure-enabled [{shortId}] reason={reason}.");
                    }
                    else
                    {
                        Logger.Information(
                            $"CLOAK RECOVERY retry -> ensure-enabled [{shortId}].");
                    }

                    FlipperResponse response = SendFlipperRequest(request);
                    HandleEnsureResponse(shortId, response);
                }
                catch (Exception ex)
                {
                    ReportRecoveryWait(
                        $"CLOAK RECOVERY ERROR{(shortId == null ? string.Empty : " [" + shortId + "]")}: " +
                        ex.Message,
                        RetryAfterFailureSeconds);

                    ScheduleRetry(RetryAfterFailureSeconds);
                }
                finally
                {
                    lock (Sync)
                    {
                        _ensureInFlight = false;
                    }
                }
            });
        }

        private static void HandleEnsureResponse(
            string shortId,
            FlipperResponse response)
        {
            if (response != null &&
                response.Ok &&
                string.Equals(
                    response.CloakState,
                    "Enabled",
                    StringComparison.OrdinalIgnoreCase))
            {
                lock (Sync)
                {
                    _knownStatus = CloakStatus.Enabled;
                    _lastObservedUtc = response.ObservedUtc ?? DateTime.UtcNow;
                    _canRaiseAtUtc = null;
                    _observationSource = response.Cached
                        ? "Flipper.Cache"
                        : "Flipper.Probe";
                }

                SendDev($"CLOAK RECOVERY OK [{shortId}]: {response.Message}");
                NotifyObservation(response);
                CompleteRecoveryIfPending("Flipper confirmed Enabled");
                return;
            }

            int retrySeconds = RetryAfterFailureSeconds;

            if (response != null &&
                string.Equals(
                    response.CloakState,
                    "Disabled",
                    StringComparison.OrdinalIgnoreCase) &&
                response.ShieldTimerInSeconds.HasValue &&
                response.ShieldTimerInSeconds.Value > 0)
            {
                retrySeconds = Math.Max(
                    5,
                    Math.Min(3600, response.ShieldTimerInSeconds.Value + 2));

                lock (Sync)
                {
                    _knownStatus = CloakStatus.Disabled;
                    _lastObservedUtc = response.ObservedUtc ?? DateTime.UtcNow;
                    _canRaiseAtUtc = DateTime.UtcNow.AddSeconds(retrySeconds);
                    _observationSource = response.Cached
                        ? "Flipper.Cache"
                        : "Flipper.Probe";
                }

                NotifyObservation(response);
            }

            string message = response?.Message ?? "No response from Flipper.";
            ReportRecoveryWait(
                $"CLOAK RECOVERY WAIT [{shortId}]: {message}",
                retrySeconds);

            ScheduleRetry(retrySeconds);
        }

        private static void ReportRecoveryWait(string message, int retrySeconds)
        {
            bool reportToDev;

            lock (Sync)
            {
                reportToDev = !_failureReported;
                _failureReported = true;
            }

            string full = $"{message} Retrying in {retrySeconds}s.";
            if (reportToDev)
                SendDev(full);
            else
                Logger.Warning(full);
        }

        private static void NotifyObservation(FlipperResponse response)
        {
            Action<CloakStatus, int?, DateTime?, bool, string> callback;
            CloakStatus parsed;

            if (response == null ||
                !Enum.TryParse(response.CloakState, true, out parsed))
            {
                return;
            }

            lock (Sync)
                callback = _observationCallback;

            try
            {
                callback?.Invoke(
                    parsed,
                    response.ShieldTimerInSeconds,
                    response.ObservedUtc,
                    response.Cached,
                    response.Message);
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    $"Unable to apply cloak recovery observation: {ex.Message}");
            }
        }

        private static void ScheduleRetry(int seconds)
        {
            lock (Sync)
            {
                if (!_raidRecoveryPending)
                    return;

                CancelRetryLocked();

                _retryTimer = new Timer(
                    _ =>
                    {
                        DateTime raidUtc;
                        bool trustObservedEnabled;
                        lock (Sync)
                        {
                            if (!_raidRecoveryPending)
                                return;

                            trustObservedEnabled =
                                _knownStatus == CloakStatus.Enabled &&
                                (DateTime.UtcNow - _managerStartedUtc).TotalSeconds >=
                                    PersistedStateTrustSeconds;

                            raidUtc = _raidOccurredUtc;
                            CancelRetryLocked();
                        }

                        if (trustObservedEnabled)
                        {
                            CompleteRecoveryIfPending(
                                "Manager observed one hour without a contradictory city event");
                            return;
                        }

                        QueueEnsureEnabled(raidUtc, "retry");
                    },
                    null,
                    TimeSpan.FromSeconds(Math.Max(1, seconds)),
                    Timeout.InfiniteTimeSpan);
            }
        }

        private static void CompleteRecoveryIfPending(string reason)
        {
            bool changed = false;

            lock (Sync)
            {
                if (_raidRecoveryPending)
                {
                    _raidRecoveryPending = false;
                    _failureReported = false;
                    changed = true;
                }

                CancelRetryLocked();
            }

            if (changed)
                Logger.Information($"CLOAK RECOVERY COMPLETE: {reason}.");
        }

        public static string GetStatusText()
        {
            lock (Sync)
            {
                if (!_bootAssessmentStarted)
                    return "cloak recovery initializing";

                if (_ensureInFlight)
                    return "cloak recovery checking Flipper";

                if (_raidRecoveryPending)
                {
                    if (_canRaiseAtUtc.HasValue && _canRaiseAtUtc.Value > DateTime.UtcNow)
                    {
                        TimeSpan remaining = _canRaiseAtUtc.Value - DateTime.UtcNow;
                        return $"cloak recovery armed in {FormatStatusDuration(remaining)}";
                    }

                    return _failureReported
                        ? "cloak recovery waiting to retry"
                        : "cloak recovery pending";
                }

                return _knownStatus == CloakStatus.Enabled
                    ? "cloak recovery settled"
                    : "cloak recovery idle";
            }
        }

        public static bool IsFlipperBusy()
        {
            lock (Sync)
                return _ensureInFlight;
        }

        public static void ObserveConfirmedState(
            CloakStatus status,
            DateTime? observedUtc,
            DateTime? canRaiseAtUtc,
            string source)
        {
            DateTime now = DateTime.UtcNow;
            DateTime? scheduleUtc = null;
            bool complete = false;

            lock (Sync)
            {
                if (!_initialized)
                    return;

                _knownStatus = status;
                _lastObservedUtc = observedUtc ?? now;
                _canRaiseAtUtc = canRaiseAtUtc;
                _observationSource = source ?? "Manager";

                if (status == CloakStatus.Enabled)
                {
                    complete = true;
                }
                else if (status == CloakStatus.Disabled)
                {
                    _raidOccurredUtc = _lastObservedUtc.Value;
                    _raidRecoveryPending = true;
                    _failureReported = false;
                    scheduleUtc = _canRaiseAtUtc ??
                        _raidOccurredUtc.AddSeconds(CloakDownSeconds);
                    _canRaiseAtUtc = scheduleUtc;
                    CancelRetryLocked();
                }
            }

            if (complete)
            {
                CompleteRecoveryIfPending("Manager confirmed Enabled");
                return;
            }

            if (scheduleUtc.HasValue)
            {
                ScheduleRetry(
                    Math.Max(
                        1,
                        (int)Math.Ceiling((scheduleUtc.Value - now).TotalSeconds)));
            }
        }

        private static string FormatStatusDuration(TimeSpan value)
        {
            int totalSeconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
        }

        private static void CancelRetryLocked()
        {
            if (_retryTimer == null)
                return;

            _retryTimer.Dispose();
            _retryTimer = null;
        }

        private static FlipperResponse SendFlipperRequest(FlipperRequest request)
        {
            using (var pipe = new NamedPipeClientStream(
                ".",
                FlipperPipeName,
                PipeDirection.InOut,
                PipeOptions.None))
            {
                pipe.Connect(FlipperConnectTimeoutMs);

                var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };

                writer.WriteLine(JsonConvert.SerializeObject(request));
                string line = reader.ReadLine();

                if (string.IsNullOrWhiteSpace(line))
                    throw new IOException("Flipper closed without a response.");

                FlipperResponse response =
                    JsonConvert.DeserializeObject<FlipperResponse>(line);

                if (response == null)
                    throw new IOException("Flipper returned invalid JSON.");

                return response;
            }
        }

        private static void SendDev(string text)
        {
            Logger.Information(text);

            try
            {
                if (Client.Chat != null)
                    Client.Chat.SendPrivateGroupMessage(Client.Chat.CharId, text);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to send city raid telemetry to dev channel: {ex.Message}");
            }
        }

        private class FlipperRequest
        {
            public string Id;
            public string Command;
            public DateTime? NotBeforeUtc;
        }

        private class FlipperResponse
        {
            public string Id;
            public bool Ok;
            public string Message;
            public string CloakState;
            public int? ShieldTimerInSeconds;
            public float? ControllerCharge;
            public bool Cached;
            public DateTime? ObservedUtc;
        }
    }
}
