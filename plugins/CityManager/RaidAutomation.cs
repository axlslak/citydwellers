using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityManager
{
    internal static class RaidAutomation
    {
        private const string FlipperPipeName = "citydwellers-flipper";
        private const int WorkerConnectTimeoutMs = 1000;
        private const int RetryAfterErrorSeconds = 60;
        private const int RetryAfterReadyFailureSeconds = 30;
        private const int TimerSafetySeconds = 2;

        private const string RaidTargetPrefix = "Your city in ";
        private const string RaidTargetSuffix = " has been targeted by hostile forces.";
        private const string CloakOnSuffix = " turned the cloaking device in your city on.";

        private static readonly object Sync = new object();

        private static bool _initialized;
        private static bool _charInPlay;
        private static bool _ensureInFlight;
        private static bool _autoRecloakPending;
        private static DateTime? _raidDetectedUtc;
        private static DateTime _nextAttemptUtc = DateTime.MaxValue;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized)
                    return;

                _initialized = true;
                Client.MessageReceived += MessageReceived;
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (!_initialized)
                    return;

                Client.MessageReceived -= MessageReceived;

                if (_charInPlay && Client.Chat != null)
                    Client.Chat.GroupMessageReceived -= GroupMessageReceived;

                if (_charInPlay)
                    Client.OnUpdate -= Tick;

                _initialized = false;
                _charInPlay = false;
                _ensureInFlight = false;
            }
        }

        private static void MessageReceived(object sender, Message e)
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

                OnCharInPlay();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Raid automation message handler failed: {ex.Message}");
            }
        }

        private static void OnCharInPlay()
        {
            lock (Sync)
            {
                if (_charInPlay || Client.Chat == null)
                    return;

                _charInPlay = true;
                Client.Chat.GroupMessageReceived += GroupMessageReceived;
                Client.OnUpdate += Tick;
            }

            Logger.Information("Raid automation armed: city raid may trigger enable-only recloak.");
            DevTrace("RAID automation armed: Flipper may ENABLE cloak after a detected raid; it can never disable it.");
        }

        private static void GroupMessageReceived(object sender, GroupMsg msg)
        {
            try
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.Message))
                    return;

                string text = msg.Message.Trim();

                if (text.StartsWith(RaidTargetPrefix, StringComparison.OrdinalIgnoreCase) &&
                    text.EndsWith(RaidTargetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    ArmForRaid(text);
                    return;
                }

                if (text.EndsWith(CloakOnSuffix, StringComparison.OrdinalIgnoreCase))
                    ClearPendingBecauseEnabled(text);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Raid automation group handler failed: {ex.Message}");
            }
        }

        private static void ArmForRaid(string rawMessage)
        {
            DateTime now = DateTime.UtcNow;

            lock (Sync)
            {
                _raidDetectedUtc = now;
                _autoRecloakPending = true;
                _nextAttemptUtc = now;
            }

            Logger.Warning($"CITY RAID DETECTED at {now:O}: {rawMessage}");
            DevTrace("RAID DETECTED: auto-recloak armed. Requesting authoritative enable-only Flipper check.");

            QueueEnsureEnabled();
        }

        private static void ClearPendingBecauseEnabled(string rawMessage)
        {
            bool cleared;

            lock (Sync)
            {
                cleared = _autoRecloakPending;
                _autoRecloakPending = false;
                _nextAttemptUtc = DateTime.MaxValue;
            }

            if (!cleared)
                return;

            Logger.Information($"Auto-recloak cancelled because cloak-on was observed: {rawMessage}");
            DevTrace("RAID recloak complete: cloak-on observed; pending automation cleared.");
        }

        private static void Tick(object sender, double e)
        {
            bool due;

            lock (Sync)
            {
                due = _autoRecloakPending &&
                      !_ensureInFlight &&
                      DateTime.UtcNow >= _nextAttemptUtc;
            }

            if (due)
                QueueEnsureEnabled();
        }

        private static void QueueEnsureEnabled()
        {
            DateTime? raidDetectedUtc;

            lock (Sync)
            {
                if (!_autoRecloakPending || _ensureInFlight)
                    return;

                _ensureInFlight = true;
                raidDetectedUtc = _raidDetectedUtc;
            }

            ThreadPool.QueueUserWorkItem(_ => EnsureEnabledWorker(raidDetectedUtc));
        }

        private static void EnsureEnabledWorker(DateTime? raidDetectedUtc)
        {
            string requestId = Guid.NewGuid().ToString("N");
            string shortId = requestId.Substring(0, 8);

            try
            {
                var request = new WorkerRequest
                {
                    Id = requestId,
                    Command = "ensure-enabled",
                    NotBeforeUtc = raidDetectedUtc
                };

                DevTrace($"AUTO-CLOAK -> ensure-enabled [{shortId}]");

                WorkerResponse response = SendWorkerRequest(request);
                HandleEnsureResponse(shortId, response);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Automatic recloak Flipper IPC failed: {ex.Message}");
                DevTrace($"AUTO-CLOAK ERROR [{shortId}]: {ex.Message}; retry in {RetryAfterErrorSeconds}s.");

                lock (Sync)
                {
                    _nextAttemptUtc = DateTime.UtcNow.AddSeconds(RetryAfterErrorSeconds);
                }
            }
            finally
            {
                lock (Sync)
                {
                    _ensureInFlight = false;
                }
            }
        }

        private static void HandleEnsureResponse(string shortId, WorkerResponse response)
        {
            if (response == null)
            {
                ScheduleRetry(RetryAfterErrorSeconds);
                DevTrace($"AUTO-CLOAK FAIL [{shortId}]: empty Flipper response; retry scheduled.");
                return;
            }

            if (string.Equals(response.CloakState, "Enabled", StringComparison.OrdinalIgnoreCase))
            {
                lock (Sync)
                {
                    _autoRecloakPending = false;
                    _nextAttemptUtc = DateTime.MaxValue;
                }

                Logger.Warning($"Automatic recloak satisfied: {response.Message}");
                DevTrace($"AUTO-CLOAK OK [{shortId}]: ENABLED. {response.Message}");
                return;
            }

            if (string.Equals(response.CloakState, "Disabled", StringComparison.OrdinalIgnoreCase) &&
                response.ShieldTimerInSeconds.HasValue &&
                response.ShieldTimerInSeconds.Value > 0)
            {
                int waitSeconds = response.ShieldTimerInSeconds.Value + TimerSafetySeconds;
                ScheduleRetry(waitSeconds);

                Logger.Information(
                    $"Automatic recloak waiting on authoritative Flipper timer: {response.ShieldTimerInSeconds.Value}s.");
                DevTrace(
                    $"AUTO-CLOAK WAIT [{shortId}]: shield locked for {FormatDuration(response.ShieldTimerInSeconds.Value)}; retry scheduled.");
                return;
            }

            ScheduleRetry(RetryAfterReadyFailureSeconds);

            DevTrace(
                $"AUTO-CLOAK {(response.Ok ? "WAIT" : "FAIL")} [{shortId}]: " +
                $"{response.Message ?? "no detail"}; retry in {RetryAfterReadyFailureSeconds}s.");
        }

        private static void ScheduleRetry(int seconds)
        {
            lock (Sync)
            {
                if (_autoRecloakPending)
                    _nextAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
            }
        }

        private static WorkerResponse SendWorkerRequest(WorkerRequest request)
        {
            using (var pipe = new NamedPipeClientStream(
                ".",
                FlipperPipeName,
                PipeDirection.InOut,
                PipeOptions.None))
            {
                pipe.Connect(WorkerConnectTimeoutMs);

                var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };

                writer.WriteLine(JsonConvert.SerializeObject(request));
                string line = reader.ReadLine();

                if (string.IsNullOrWhiteSpace(line))
                    throw new IOException("Flipper closed without an automatic-recloak response.");

                WorkerResponse response = JsonConvert.DeserializeObject<WorkerResponse>(line);
                if (response == null)
                    throw new IOException("Flipper returned invalid automatic-recloak JSON.");

                return response;
            }
        }

        private static void DevTrace(string text)
        {
            try
            {
                if (Client.Chat == null || string.IsNullOrWhiteSpace(text))
                    return;

                Client.Chat.SendPrivateGroupMessage(Client.Chat.CharId, text);
            }
            catch
            {
            }
        }

        private static string FormatDuration(int totalSeconds)
        {
            totalSeconds = Math.Max(0, totalSeconds);
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;

            if (hours > 0)
                return $"{hours}h {minutes}m {seconds}s";
            if (minutes > 0)
                return $"{minutes}m {seconds}s";
            return $"{seconds}s";
        }

        private class WorkerRequest
        {
            public string Id;
            public string Command;
            public DateTime? NotBeforeUtc;
        }

        private class WorkerResponse
        {
            public string Id;
            public bool Ok;
            public string Message;
            public string Character;
            public string CloakState;
            public int? ShieldTimerInSeconds;
            public float? ControllerCharge;
            public bool Cached;
            public DateTime? ObservedUtc;
        }
    }
}
