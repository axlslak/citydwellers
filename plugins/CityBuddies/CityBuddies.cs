using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityDwellers.Shared;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBuddies
{
    public partial class CityBuddies : ClientlessPluginEntry
    {
        private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan NavigatingSnapshotInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DirectivePollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan StandDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan HeadingSettleDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan FirstMovementPulseDuration = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan ProvenMovementPulseDuration = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan SettledPositionTimeout = TimeSpan.FromMilliseconds(2500);
        private static readonly TimeSpan MovementObservationQuietPeriod =
            TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan NavigationTraceFlushInterval =
            TimeSpan.FromSeconds(1);
        private static readonly TimeSpan NavigationStateSampleInterval =
            TimeSpan.FromSeconds(1);
        private static readonly JsonSerializerSettings NavigationTraceJsonSettings =
            new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            };

        private const int SerenityIslandsPlayfieldId = 6010;
        private const float HomeX = 996.004f;
        private const float HomeY = 5.010f;
        private const float HomeZ = 1248.512f;
        private const float HomeHeadingX = 0.000f;
        private const float HomeHeadingY = -0.997f;
        private const float HomeHeadingZ = 0.000f;
        private const float HomeHeadingW = 0.079f;
        private const float HomeReachedDistance = 0.25f;
        private const float NavmeshWaypointMinimumDistance = 0.75f;
        private const float FirstMovementPulseDistance = 0.75f;
        private const float ProvenMovementPulseDistance = 2.00f;
        private const float MinimumPulseProgress = 0.10f;
        private const float WrongWayTolerance = 0.15f;
        private const float PulseDisplacementSlack = 1.00f;
        private const int MaximumStopConfirmationRetries = 2;
        private const int MaximumStuckRecoveries = 5;
        private const string StaticDynelDataMutexName =
            @"Local\CityDwellers.StaticDynelData.1.0.16";

        private static readonly TimeSpan StaticDynelDataMutexTimeout =
            TimeSpan.FromSeconds(30);

        private readonly object _snapshotSync = new object();
        private readonly object _movementObservationSync = new object();
        private readonly object _navigationTraceSync = new object();
        private readonly Queue<string> _pendingNavigationTrace = new Queue<string>();
        private readonly Dictionary<int, NavmeshPathfinder> _navmeshPathfinders =
            new Dictionary<int, NavmeshPathfinder>();
        private readonly Dictionary<int, string> _navmeshLoadErrors =
            new Dictionary<int, string>();
        private string _readyPath;
        private string _snapshotPath;
        private string _homeDirectivePath;
        private string _navmeshDirectory;
        private string _pluginDirectory;
        private string _navigationTracePath;
        private bool _readyWritten;
        private bool _inPlay;
        private bool _dead;
        private DateTime _lastSnapshotUtc = DateTime.MinValue;
        private string _lastSnapshotError;
        private string _lastDirectiveError;
        private string _lastNavigationTraceError;
        private DateTime _nextNavigationTraceFlushUtc = DateTime.MinValue;
        private DateTime _nextNavigationStateSampleUtc = DateTime.MinValue;
        private long _navigationTraceSequence;
        private int? _lastRunSpeed;

        private BuddyHomeDirective _homeDirective;
        private string _homeState;
        private string _homeDetail;
        private float? _homeDistance;
        private DateTime? _homeUpdatedUtc;
        private DateTime _nextDirectivePollUtc = DateTime.MinValue;
        private DateTime _standReadyUtc = DateTime.MinValue;
        private DateTime _pulseDeadlineUtc = DateTime.MinValue;
        private Vector3 _pulseStart;
        private Vector3 _pulseTarget;
        private Vector3 _pulseEndpoint;
        private Quaternion _pulseHeading;
        private float _pulseDistance;
        private float _pulseStartTargetDistance;
        private long _settledObservationSerial;
        private long _settledObservationSerialAtStop;
        private Vector3 _lastSettledPosition;
        private MovementAction _lastMovementAction;
        private DateTime _lastMovementObservationUtc = DateTime.MinValue;
        private int _lastMovementObservationDeltaTime;
        private Vector3 _lastMovementCommandPosition;
        private MovementAction _lastMovementCommandAction;
        private DateTime _lastMovementCommandUtc = DateTime.MinValue;
        private long _movementCommandSerial;
        private MovementPulsePhase _pulsePhase;
        private int _stuckRecoveries;
        private int _stopConfirmationRetries;
        private int _successfulPulses;
        private bool _standRequested;

        private enum MovementPulsePhase
        {
            Idle,
            Orienting,
            Moving,
            Settling
        }

        public override void Init(string pluginDir)
        {
            PreloadStaticDynelData();

            _pluginDirectory = pluginDir;

            _readyPath = Path.Combine(
                pluginDir,
                $"citybuddies-ready-{Client.CharacterName}.ready");
            _snapshotPath = Path.Combine(
                pluginDir,
                $"citybuddies-position-{Client.CharacterName}.json");
            _homeDirectivePath = Path.Combine(
                pluginDir,
                $"citybuddies-home-{Client.CharacterName}.json");
            _navmeshDirectory = Path.Combine(pluginDir, "NavMeshes");

            DeleteSnapshot();

            Logger.Information("CityBuddies runtime helper initialized.");

            Client.Config.AutoReconnect = true;
            Client.MessageReceived += MessageReceived;
            Client.OnUpdate += Tick;
            Client.Died += Died;
            Client.Disconnected += Disconnected;

            Logger.Information(
                $"CityBuddies AutoReconnect={Client.Config.AutoReconnect}.");
        }

        private static void PreloadStaticDynelData()
        {
            bool mutexHeld = false;

            using (var mutex = new Mutex(false, StaticDynelDataMutexName))
            {
                try
                {
                    try
                    {
                        mutexHeld = mutex.WaitOne(StaticDynelDataMutexTimeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The previous owner exited while holding the mutex. The
                        // current caller owns it now and can safely retry the load.
                        mutexHeld = true;
                    }

                    if (!mutexHeld)
                    {
                        throw new TimeoutException(
                            "Timed out waiting to preload AOSharp static-dynel data.");
                    }

                    Type dataType = typeof(DynelManager).Assembly.GetType(
                        "AOSharp.Clientless.StaticDynelData",
                        true);
                    PropertyInfo staticDynels = dataType.GetProperty(
                        "StaticDynels",
                        BindingFlags.Static | BindingFlags.NonPublic);

                    if (staticDynels == null)
                    {
                        throw new MissingMemberException(
                            dataType.FullName,
                            "StaticDynels");
                    }

                    // AOSharp.Clientless 1.0.16 opens StaticDynelData.bin with
                    // FileShare.None. Each buddy runs in a separate AppDomain,
                    // so warm each private cache while one named mutex prevents
                    // parallel domains or Buddies processes from racing the file.
                    staticDynels.GetValue(null, null);
                    Logger.Information(
                        "CityBuddies AOSharp static-dynel data preload completed.");
                }
                catch (TargetInvocationException ex)
                {
                    throw new InvalidOperationException(
                        "AOSharp static-dynel data preload failed.",
                        ex.InnerException ?? ex);
                }
                finally
                {
                    if (mutexHeld)
                        mutex.ReleaseMutex();
                }
            }
        }

        public override void Teardown()
        {
            StopMovement();
            TraceNavigation("lifecycle", "CityBuddies plugin teardown.");
            FlushNavigationTrace(true);
            Client.MessageReceived -= MessageReceived;
            Client.OnUpdate -= Tick;
            Client.Died -= Died;
            Client.Disconnected -= Disconnected;
            DeleteSnapshot();
            Logger.Information("CityBuddies runtime helper teardown.");
        }

        private void MessageReceived(object sender, Message e)
        {
            try
            {
                if (e?.Body == null || e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3Message = (N3Message)e.Body;
                if (n3Message.N3MessageType == N3MessageType.CharDCMove)
                {
                    ObserveMovement((CharDCMoveMessage)e.Body);
                    return;
                }

                if (n3Message.N3MessageType != N3MessageType.CharInPlay)
                    return;

                var charInPlay = (CharInPlayMessage)e.Body;
                if (charInPlay.Identity.Instance != Client.LocalDynelId)
                    return;

                _inPlay = true;
                _dead = false;

                if (!_readyWritten)
                {
                    File.WriteAllText(
                        _readyPath,
                        $"{Client.CharacterName}|{DateTime.UtcNow:O}");

                    _readyWritten = true;
                    Logger.Information(
                        $"CityBuddies ready: {Client.CharacterName} reached InPlay.");
                }

                WriteSnapshot(true);
            }
            catch (Exception ex)
            {
                Logger.Error($"CityBuddies readiness signal failed: {ex}");
            }
        }

        private void Tick(object sender, double deltaTime)
        {
            DateTime now = DateTime.UtcNow;
            _inPlay = Client.InPlay;

            if (now >= _nextDirectivePollUtc)
            {
                _nextDirectivePollUtc = now.Add(DirectivePollInterval);
                ReadHomeDirective();
            }

            if (_homeDirective != null && _inPlay)
                ProcessHomeNavigation(now);

            if (_homeDirective != null &&
                !IsTerminalHomeState(_homeState) &&
                now >= _nextNavigationStateSampleUtc)
            {
                TraceNavigation(
                    "controller-sample",
                    _homeDetail ?? "Home navigation is active.");
                _nextNavigationStateSampleUtc =
                    now.Add(NavigationStateSampleInterval);
            }

            FlushNavigationTrace(false);

            TimeSpan interval = _homeDirective == null
                ? SnapshotInterval
                : NavigatingSnapshotInterval;

            if (now - _lastSnapshotUtc < interval)
                return;

            WriteSnapshot(_inPlay);
        }

        private void Died()
        {
            _dead = true;
            if (_homeDirective != null)
            {
                StopMovement();
                SetHomeState(
                    "route-unavailable",
                    "Character is dead; movement stopped.");
            }
            WriteSnapshot(_inPlay);
            Logger.Information($"CityBuddies observed {Client.CharacterName} die.");
        }

        private void Disconnected()
        {
            StopMovement();
            _inPlay = false;
            TraceNavigation("lifecycle", "AO clientless session disconnected.");
            FlushNavigationTrace(true);
            WriteSnapshot(false);
        }

        private void ReadHomeDirective()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_homeDirectivePath) ||
                    !File.Exists(_homeDirectivePath))
                {
                    return;
                }

                BuddyHomeDirective directive =
                    JsonConvert.DeserializeObject<BuddyHomeDirective>(
                        File.ReadAllText(_homeDirectivePath));

                if (!string.IsNullOrWhiteSpace(_lastDirectiveError))
                {
                    Logger.Information(
                        $"CityBuddies home directive reading recovered for " +
                        $"{Client.CharacterName}.");
                    _lastDirectiveError = null;
                }

                if (directive == null || string.IsNullOrWhiteSpace(directive.JobId))
                    return;

                if (_homeDirective != null &&
                    string.Equals(
                        _homeDirective.JobId,
                        directive.JobId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        _homeDirective.MovementMode,
                        directive.MovementMode,
                        StringComparison.OrdinalIgnoreCase) &&
                    _homeDirective.Cancel == directive.Cancel)
                {
                    return;
                }

                bool newJob =
                    _homeDirective == null ||
                    !string.Equals(
                        _homeDirective.JobId,
                        directive.JobId,
                        StringComparison.Ordinal);

                StopMovement();
                _homeDirective = directive;
                _standRequested = false;
                _standReadyUtc = DateTime.MinValue;
                ResetPulseState(true);
                ResetContinuousState(true);
                ResetGridCrossing();
                ResetIccEntry();

                if (directive.Cancel)
                {
                    SetHomeState("canceled", "Home navigation was canceled by Buddies.");
                    return;
                }

                if (newJob)
                    BeginNavigationTrace(directive);

                SetHomeState(
                    "starting",
                    $"Home navigation directive received; mode={GetMovementModeName()}.");
                Logger.Information(
                    $"CityBuddies home job {directive.JobId} started for " +
                    $"{Client.CharacterName}.");
            }
            catch (Exception ex)
            {
                string error = ex.GetType().Name + ": " + ex.Message;
                if (!string.Equals(error, _lastDirectiveError, StringComparison.Ordinal))
                {
                    Logger.Warning(
                        $"CityBuddies home directive read failed for " +
                        $"{Client.CharacterName}; it will retry: {error}");
                    _lastDirectiveError = error;
                }

                SetHomeState(
                    "waiting",
                    "Home directive read was temporarily unavailable; retrying.");
            }
        }

        private void ProcessHomeNavigation(DateTime now)
        {
            ProcessHomeRoute(now);
        }

        private void BeginMovementPulse(
            LocalPlayer localPlayer,
            Vector3 position,
            Vector3 target,
            float targetDistance,
            DateTime now)
        {
            Vector3 flatPosition = new Vector3(position.X, 0, position.Z);
            Vector3 flatTarget = new Vector3(target.X, 0, target.Z);
            Vector3 direction = (flatTarget - flatPosition).Normalize();
            float requestedPulseDistance = _successfulPulses == 0
                ? FirstMovementPulseDistance
                : ProvenMovementPulseDistance;
            float pulseDistance = Math.Min(requestedPulseDistance, targetDistance);

            _pulseStart = position;
            _pulseTarget = target;
            _pulseDistance = pulseDistance;
            _pulseEndpoint = new Vector3(
                position.X + (direction.X * pulseDistance),
                // Navigation owns the horizontal direction only. Preserve the
                // current elevation in the outbound endpoint and accept the
                // server's returned Y after water, beach, and terrain handling.
                position.Y,
                position.Z + (direction.Z * pulseDistance));
            _pulseHeading = Quaternion.FromTo(flatPosition, flatTarget);
            _pulseStartTargetDistance = targetDistance;
            _stopConfirmationRetries = 0;

            // First publish the new heading while explicitly stopped. Forward
            // motion is never left open-ended: every start below has a matching
            // endpoint packet after one short pulse.
            SendMovementCommand(
                localPlayer,
                position,
                _pulseHeading,
                MovementAction.FullStop,
                $"Bounded pulse orienting toward " +
                $"({_pulseTarget.X:F3},{_pulseTarget.Y:F3},{_pulseTarget.Z:F3}).");
            _pulsePhase = MovementPulsePhase.Orienting;
            _pulseDeadlineUtc = now.Add(HeadingSettleDelay);
        }

        private void ProcessMovementPulse(LocalPlayer localPlayer, DateTime now)
        {
            if (_pulsePhase == MovementPulsePhase.Settling)
            {
                ProcessSettledPulse(localPlayer, now);
                return;
            }

            if (now < _pulseDeadlineUtc)
                return;

            if (_pulsePhase == MovementPulsePhase.Orienting)
            {
                SendMovementCommand(
                    localPlayer,
                    _pulseStart,
                    _pulseHeading,
                    MovementAction.ForwardStart,
                    $"Bounded pulse start; requestedDistance={_pulseDistance:F3}m.");
                _pulsePhase = MovementPulsePhase.Moving;
                TimeSpan pulseDuration = _successfulPulses == 0
                    ? FirstMovementPulseDuration.Add(
                        TimeSpan.FromMilliseconds(_stuckRecoveries * 300))
                    : ProvenMovementPulseDuration;
                _pulseDeadlineUtc = now.Add(pulseDuration);
                return;
            }

            if (_pulsePhase == MovementPulsePhase.Moving)
            {
                lock (_movementObservationSync)
                    _settledObservationSerialAtStop = _settledObservationSerial;

                SendMovementCommand(
                    localPlayer,
                    _pulseEndpoint,
                    _pulseHeading,
                    MovementAction.FullStop,
                    "Bounded pulse endpoint stop.");
                _pulsePhase = MovementPulsePhase.Settling;
                _pulseDeadlineUtc = now.Add(SettledPositionTimeout);
                return;
            }

        }

        private void ProcessSettledPulse(LocalPlayer localPlayer, DateTime now)
        {
            Vector3 settledPosition;
            MovementAction observedAction;
            DateTime observedUtc;
            bool hasSettledPosition = TryGetPositionAfterStop(
                out settledPosition,
                out observedAction,
                out observedUtc);

            if (hasSettledPosition)
            {
                float observedDisplacement =
                    settledPosition.Distance2DFrom(_pulseStart);

                // A delayed copy of the pre-pulse stop can arrive first. Ignore
                // that zero-distance copy. A terminal movement packet can be
                // used immediately; swimming/update packets are accepted once
                // the stream has been quiet long enough to represent a stop.
                bool terminalAction =
                    observedAction == MovementAction.FullStop ||
                    observedAction == MovementAction.ForwardStop;
                bool observationSettled =
                    terminalAction ||
                    now - observedUtc >= MovementObservationQuietPeriod;

                if (observedDisplacement > 0.05f && observationSettled)
                {
                    EvaluateSettledPulse(localPlayer, settledPosition, now);
                    return;
                }
            }

            if (now < _pulseDeadlineUtc)
                return;

            if (hasSettledPosition &&
                settledPosition.Distance2DFrom(_pulseStart) > 0.05f)
            {
                EvaluateSettledPulse(localPlayer, settledPosition, now);
            }
            else
            {
                RetryUnconfirmedPulse(localPlayer, now);
            }
        }

        private void RetryUnconfirmedPulse(LocalPlayer localPlayer, DateTime now)
        {
            if (_stopConfirmationRetries < MaximumStopConfirmationRetries)
            {
                _stopConfirmationRetries++;

                lock (_movementObservationSync)
                    _settledObservationSerialAtStop = _settledObservationSerial;

                SendMovementCommand(
                    localPlayer,
                    _pulseEndpoint,
                    _pulseHeading,
                    MovementAction.FullStop,
                    $"Bounded pulse stop confirmation " +
                    $"{_stopConfirmationRetries}/{MaximumStopConfirmationRetries}.");
                _pulseDeadlineUtc = now.Add(SettledPositionTimeout);
                SetHomeState(
                    "confirming-position",
                    $"Waiting for a settled position after a slow or delayed pulse; " +
                    $"confirmation {_stopConfirmationRetries}/" +
                    $"{MaximumStopConfirmationRetries}.");
                return;
            }

            if (_stuckRecoveries >= MaximumStuckRecoveries)
            {
                StopMovement();
                ResetPulseState(false);
                SetHomeState(
                    "movement-unverified",
                    $"Gave up after {MaximumStuckRecoveries} slow-movement retries; " +
                    "the server never confirmed a changed position.");
                return;
            }

            _stuckRecoveries++;
            _successfulPulses = 0;
            ResetPulseState(false);
            _standReadyUtc = now.Add(HeadingSettleDelay);
            SetHomeState(
                "recovering",
                $"No changed position was confirmed; trying a slower movement pulse " +
                $"{_stuckRecoveries}/{MaximumStuckRecoveries}.");
        }

        private void EvaluateSettledPulse(
            LocalPlayer localPlayer,
            Vector3 settledPosition,
            DateTime now)
        {
            float displacement = settledPosition.Distance2DFrom(_pulseStart);
            float settledTargetDistance = settledPosition.Distance2DFrom(_pulseTarget);
            float progress = _pulseStartTargetDistance - settledTargetDistance;
            float crossTrack = DistanceFromSegment2D(
                settledPosition,
                _pulseStart,
                _pulseTarget);

            if (displacement > _pulseDistance + PulseDisplacementSlack ||
                progress < -WrongWayTolerance)
            {
                SendMovementCommand(
                    localPlayer,
                    settledPosition,
                    localPlayer.Transform.Heading,
                    MovementAction.FullStop,
                    $"Emergency bounded-pulse stop; moved={displacement:F3}m, " +
                    $"progress={progress:F3}m, crossTrack={crossTrack:F3}m.");
                ResetPulseState(false);
                SetHomeState(
                    "movement-diverged",
                    $"Emergency stop after one pulse: moved={displacement:F2}m, " +
                    $"target progress={progress:F2}m, lateral drift={crossTrack:F2}m.");
                return;
            }

            if (progress < MinimumPulseProgress)
            {
                if (_stuckRecoveries >= MaximumStuckRecoveries)
                {
                    StopMovement();
                    ResetPulseState(false);
                    SetHomeState(
                        "stuck",
                        $"Gave up after {MaximumStuckRecoveries} bounded pulses " +
                        "made no measurable progress.");
                    return;
                }

                _stuckRecoveries++;
                _successfulPulses = 0;
                ResetPulseState(false);
                _standReadyUtc = now.Add(HeadingSettleDelay);
                SetHomeState(
                    "recovering",
                    $"Pulse made no progress; reorienting " +
                    $"{_stuckRecoveries}/{MaximumStuckRecoveries}.");
                return;
            }

            _stuckRecoveries = 0;
            _successfulPulses++;
            ResetPulseState(false);
        }

        private void ObserveMovement(CharDCMoveMessage movement)
        {
            if (movement.Identity.Instance != Client.LocalDynelId)
                return;

            lock (_movementObservationSync)
            {
                _lastSettledPosition = movement.Position;
                _lastMovementAction = movement.MoveType;
                _lastMovementObservationUtc = DateTime.UtcNow;
                _lastMovementObservationDeltaTime = (int)movement.DeltaTime;
                _settledObservationSerial++;
            }

            TraceNavigation(
                "movement-echo",
                "Received local CharDCMove echo.",
                movement.MoveType,
                movement.Position,
                movement.Heading,
                (int)movement.DeltaTime);

            if (_continuousForwardActive &&
                (movement.MoveType == MovementAction.FullStop ||
                 movement.MoveType == MovementAction.ForwardStop))
            {
                // AO ended the stream. The update thread will adopt the server
                // position and issue an explicit ForwardStart rather than an
                // Update that assumes forward is still held.
                _continuousServerStopped = true;
            }
        }

        private bool TryGetPositionAfterStop(
            out Vector3 position,
            out MovementAction action,
            out DateTime observedUtc)
        {
            lock (_movementObservationSync)
            {
                position = _lastSettledPosition;
                action = _lastMovementAction;
                observedUtc = _lastMovementObservationUtc;
                return _settledObservationSerial > _settledObservationSerialAtStop;
            }
        }

        private static float DistanceFromSegment2D(
            Vector3 point,
            Vector3 start,
            Vector3 end)
        {
            float dx = end.X - start.X;
            float dz = end.Z - start.Z;
            float lengthSquared = (dx * dx) + (dz * dz);
            if (lengthSquared <= 0.0001f)
                return point.Distance2DFrom(start);

            float projection =
                (((point.X - start.X) * dx) + ((point.Z - start.Z) * dz)) /
                lengthSquared;
            projection = Math.Max(0.0f, Math.Min(1.0f, projection));
            var closest = new Vector3(
                start.X + (projection * dx),
                point.Y,
                start.Z + (projection * dz));
            return point.Distance2DFrom(closest);
        }

        private bool TrySelectNavmeshTarget(
            int playfieldId,
            Vector3 position,
            Vector3 destination,
            out Vector3 target,
            out string state,
            out string detail)
        {
            NavmeshPathfinder pathfinder;
            string loadError;
            if (!TryGetPathfinder(playfieldId, out pathfinder, out loadError))
            {
                target = new Vector3();
                state = null;
                detail = loadError;
                return false;
            }

            try
            {
                IReadOnlyList<Vector3> path =
                    pathfinder.FindStraightPath(position, destination);

                for (int i = 0; i < path.Count; i++)
                {
                    float waypointDistance = position.Distance2DFrom(path[i]);
                    if (waypointDistance < NavmeshWaypointMinimumDistance)
                        continue;

                    target = path[i];
                    state = "moving";
                    detail =
                        $"Playfield {playfieldId} navmesh path has {path.Count} points; next " +
                        $"waypoint=({target.X:F1},{target.Y:F1},{target.Z:F1}).";
                    return true;
                }

                target = destination;
                state = "moving";
                detail = $"Playfield {playfieldId} navmesh path ends at the destination.";
                return true;
            }
            catch (Exception ex)
            {
                target = new Vector3();
                state = null;
                detail =
                    $"Playfield {playfieldId} navmesh route unavailable from " +
                    $"({position.X:F1},{position.Y:F1},{position.Z:F1}): " +
                    ex.Message;
                return false;
            }
        }

        private bool TryGetPathfinder(
            int playfieldId,
            out NavmeshPathfinder pathfinder,
            out string error)
        {
            if (_navmeshPathfinders.TryGetValue(playfieldId, out pathfinder))
            {
                error = null;
                return true;
            }

            if (_navmeshLoadErrors.TryGetValue(playfieldId, out error))
                return false;

            string navmeshPath =
                Path.Combine(_navmeshDirectory, $"{playfieldId}.Navmesh");

            try
            {
                pathfinder = NavmeshPathfinder.Load(navmeshPath);
                _navmeshPathfinders.Add(playfieldId, pathfinder);
                error = null;
                Logger.Information(
                    $"CityBuddies loaded navmesh for playfield {playfieldId}.");
                return true;
            }
            catch (Exception ex)
            {
                pathfinder = null;
                error =
                    $"Unable to load navmesh for playfield {playfieldId}: " +
                    ex.Message;
                _navmeshLoadErrors[playfieldId] = error;
                Logger.Warning(error);
                return false;
            }
        }

        private static void PrepareMovementComponent(
            LocalPlayer localPlayer,
            Quaternion heading)
        {
            PrepareMovementComponent(
                localPlayer,
                localPlayer.Transform.Position,
                heading);
        }

        private static void PrepareMovementComponent(
            LocalPlayer localPlayer,
            Vector3 position,
            Quaternion heading)
        {
            localPlayer.MovementComponent.Position = position;
            localPlayer.MovementComponent.Heading = heading;
        }

        private void FaceHome(LocalPlayer localPlayer)
        {
            Quaternion heading = new Quaternion(
                HomeHeadingX,
                HomeHeadingY,
                HomeHeadingZ,
                HomeHeadingW);
            SendMovementCommand(
                localPlayer,
                localPlayer.Transform.Position,
                heading,
                MovementAction.FullStop,
                "Final City Controller position and heading.");
            ResetPulseState(false);
            ResetContinuousState(false);
        }

        private void StopMovement()
        {
            try
            {
                LocalPlayer localPlayer = DynelManager.LocalPlayer;
                if (localPlayer == null || localPlayer.Transform == null)
                    return;

                SendMovementCommand(
                    localPlayer,
                    localPlayer.Transform.Position,
                    localPlayer.Transform.Heading,
                    MovementAction.FullStop,
                    "StopMovement requested.");
            }
            catch
            {
            }
            finally
            {
                ResetPulseState(false);
                ResetContinuousState(false);
            }
        }

        private void ResetPulseState(bool resetRecoveries)
        {
            _pulsePhase = MovementPulsePhase.Idle;
            _pulseDeadlineUtc = DateTime.MinValue;
            _stopConfirmationRetries = 0;
            if (resetRecoveries)
            {
                _stuckRecoveries = 0;
                _successfulPulses = 0;
            }
        }

        private void BeginNavigationTrace(BuddyHomeDirective directive)
        {
            FlushNavigationTrace(true);

            lock (_navigationTraceSync)
            {
                _pendingNavigationTrace.Clear();
                _navigationTracePath = null;
                _navigationTraceSequence = 0;
                _nextNavigationTraceFlushUtc = DateTime.MinValue;
                _lastNavigationTraceError = null;
            }

            try
            {
                string traceDirectory = Path.Combine(
                    _pluginDirectory,
                    "NavigationTraces");
                Directory.CreateDirectory(traceDirectory);

                string fileName =
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-" +
                    $"{SafeFileToken(Client.CharacterName)}-" +
                    $"{SafeFileToken(directive.JobId)}.jsonl";
                string path = Path.Combine(traceDirectory, fileName);
                File.WriteAllText(path, string.Empty);

                lock (_navigationTraceSync)
                {
                    _navigationTracePath = path;
                }

                TraceNavigation(
                    "trace-start",
                    $"Home navigation trace started; requested={directive.RequestedUtc:O}; " +
                    $"mode={GetMovementModeName()}.");
                FlushNavigationTrace(true);
                Logger.Information(
                    $"CityBuddies navigation trace for {Client.CharacterName}: {path}");
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    $"CityBuddies could not start navigation trace for " +
                    $"{Client.CharacterName}: {ex.Message}");
            }
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');

            return token;
        }

        private void SendMovementCommand(
            LocalPlayer localPlayer,
            Vector3 assertedPosition,
            Quaternion assertedHeading,
            MovementAction action,
            string reason)
        {
            PrepareMovementComponent(
                localPlayer,
                assertedPosition,
                assertedHeading);

            lock (_movementObservationSync)
            {
                _lastMovementCommandPosition = assertedPosition;
                _lastMovementCommandAction = action;
                _lastMovementCommandUtc = DateTime.UtcNow;
                _movementCommandSerial++;
            }

            try
            {
                localPlayer.MovementComponent.ChangeMovement(action);
                TraceNavigation(
                    "movement-command",
                    reason,
                    action,
                    assertedPosition,
                    assertedHeading,
                    null);
            }
            catch (Exception ex)
            {
                TraceNavigation(
                    "movement-command-error",
                    $"{reason} Send failed: {ex.GetType().Name}: {ex.Message}",
                    action,
                    assertedPosition,
                    assertedHeading,
                    null);
                throw;
            }
        }

        private void TraceNavigation(
            string kind,
            string message,
            MovementAction? action = null,
            Vector3? assertedPosition = null,
            Quaternion? assertedHeading = null,
            int? packetDeltaTime = null)
        {
            string tracePath;
            lock (_navigationTraceSync)
                tracePath = _navigationTracePath;

            if (string.IsNullOrWhiteSpace(tracePath))
                return;

            try
            {
                DateTime now = DateTime.UtcNow;
                LocalPlayer localPlayer = DynelManager.LocalPlayer;
                bool observedAvailable =
                    localPlayer != null && localPlayer.Transform != null;
                Vector3 observedPosition = observedAvailable
                    ? localPlayer.Transform.Position
                    : new Vector3();
                Quaternion observedHeading = observedAvailable
                    ? localPlayer.Transform.Heading
                    : Quaternion.Identity;

                int? playfieldId = null;
                try
                {
                    if (_inPlay)
                        playfieldId = (int)Playfield.ModelId;
                }
                catch
                {
                }

                if (!_lastRunSpeed.HasValue && localPlayer != null)
                {
                    try
                    {
                        _lastRunSpeed = localPlayer.GetStat(Stat.RunSpeed);
                    }
                    catch
                    {
                    }
                }

                MovementAction lastCommandAction = default(MovementAction);
                Vector3 lastCommandPosition = new Vector3();
                DateTime lastCommandUtc = DateTime.MinValue;
                long movementCommandSerial;
                MovementAction lastObservationAction = default(MovementAction);
                Vector3 lastObservationPosition = new Vector3();
                DateTime lastObservationUtc = DateTime.MinValue;
                int lastObservationDeltaTime = 0;
                long movementObservationSerial;

                lock (_movementObservationSync)
                {
                    lastCommandAction = _lastMovementCommandAction;
                    lastCommandPosition = _lastMovementCommandPosition;
                    lastCommandUtc = _lastMovementCommandUtc;
                    movementCommandSerial = _movementCommandSerial;
                    lastObservationAction = _lastMovementAction;
                    lastObservationPosition = _lastSettledPosition;
                    lastObservationUtc = _lastMovementObservationUtc;
                    lastObservationDeltaTime = _lastMovementObservationDeltaTime;
                    movementObservationSerial = _settledObservationSerial;
                }

                var entry = new NavigationTraceEntry
                {
                    Format = "citydwellers-navigation-trace-v1",
                    Utc = now,
                    Character = Client.CharacterName,
                    JobId = _homeDirective?.JobId,
                    Kind = kind,
                    Message = message,
                    PlayfieldId = playfieldId,
                    InPlay = _inPlay,
                    Dead = _dead,
                    RunSpeed = _lastRunSpeed,
                    HomeMode = _homeDirective == null
                        ? null
                        : GetMovementModeName(),
                    HomeState = _homeState,
                    HomeDetail = _homeDetail,
                    Action = action?.ToString(),
                    PacketDeltaTime = packetDeltaTime,
                    ObservedPositionAvailable = observedAvailable,
                    ObservedX = observedAvailable ? (float?)observedPosition.X : null,
                    ObservedY = observedAvailable ? (float?)observedPosition.Y : null,
                    ObservedZ = observedAvailable ? (float?)observedPosition.Z : null,
                    ObservedHeadingX = observedAvailable ? (float?)observedHeading.X : null,
                    ObservedHeadingY = observedAvailable ? (float?)observedHeading.Y : null,
                    ObservedHeadingZ = observedAvailable ? (float?)observedHeading.Z : null,
                    ObservedHeadingW = observedAvailable ? (float?)observedHeading.W : null,
                    AssertedX = assertedPosition.HasValue
                        ? (float?)assertedPosition.Value.X
                        : null,
                    AssertedY = assertedPosition.HasValue
                        ? (float?)assertedPosition.Value.Y
                        : null,
                    AssertedZ = assertedPosition.HasValue
                        ? (float?)assertedPosition.Value.Z
                        : null,
                    AssertedHeadingX = assertedHeading.HasValue
                        ? (float?)assertedHeading.Value.X
                        : null,
                    AssertedHeadingY = assertedHeading.HasValue
                        ? (float?)assertedHeading.Value.Y
                        : null,
                    AssertedHeadingZ = assertedHeading.HasValue
                        ? (float?)assertedHeading.Value.Z
                        : null,
                    AssertedHeadingW = assertedHeading.HasValue
                        ? (float?)assertedHeading.Value.W
                        : null,
                    MovementCommandSerial = movementCommandSerial,
                    LastCommandAction = movementCommandSerial > 0
                        ? lastCommandAction.ToString()
                        : null,
                    LastCommandUtc = movementCommandSerial > 0
                        ? (DateTime?)lastCommandUtc
                        : null,
                    LastCommandX = movementCommandSerial > 0
                        ? (float?)lastCommandPosition.X
                        : null,
                    LastCommandY = movementCommandSerial > 0
                        ? (float?)lastCommandPosition.Y
                        : null,
                    LastCommandZ = movementCommandSerial > 0
                        ? (float?)lastCommandPosition.Z
                        : null,
                    MovementObservationSerial = movementObservationSerial,
                    LastObservationAction = movementObservationSerial > 0
                        ? lastObservationAction.ToString()
                        : null,
                    LastObservationUtc = movementObservationSerial > 0
                        ? (DateTime?)lastObservationUtc
                        : null,
                    LastObservationDeltaTime = movementObservationSerial > 0
                        ? (int?)lastObservationDeltaTime
                        : null,
                    LastObservationX = movementObservationSerial > 0
                        ? (float?)lastObservationPosition.X
                        : null,
                    LastObservationY = movementObservationSerial > 0
                        ? (float?)lastObservationPosition.Y
                        : null,
                    LastObservationZ = movementObservationSerial > 0
                        ? (float?)lastObservationPosition.Z
                        : null,
                    PulsePhase = _pulsePhase.ToString(),
                    StuckRecoveries = _stuckRecoveries,
                    ContinuousForwardActive = _continuousForwardActive,
                    ContinuousCommandAvailable =
                        _continuousCommandPositionAvailable,
                    ContinuousCommandX = _continuousCommandPositionAvailable
                        ? (float?)_continuousCommandPosition.X
                        : null,
                    ContinuousCommandY = _continuousCommandPositionAvailable
                        ? (float?)_continuousCommandPosition.Y
                        : null,
                    ContinuousCommandZ = _continuousCommandPositionAvailable
                        ? (float?)_continuousCommandPosition.Z
                        : null,
                    ContinuousWaypointIndex = _continuousWaypointIndex,
                    ContinuousPathCount = _continuousPath.Count,
                    GridExitPhase = _gridExitPhase.ToString(),
                    GridCrossingActive =
                        _gridExitPhase != GridExitPhase.Idle,
                    GridCrossingForwardActive =
                        _gridExitPhase == GridExitPhase.FinalApproach ||
                        _gridExitPhase == GridExitPhase.NearExitSample,
                    IccUseAttempts = _iccUseAttempts
                };

                lock (_navigationTraceSync)
                {
                    if (!string.Equals(
                            tracePath,
                            _navigationTracePath,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    entry.Sequence = Interlocked.Increment(
                        ref _navigationTraceSequence);
                    _pendingNavigationTrace.Enqueue(
                        JsonConvert.SerializeObject(
                            entry,
                            NavigationTraceJsonSettings));
                }
            }
            catch (Exception ex)
            {
                string error = ex.GetType().Name + ": " + ex.Message;
                if (!string.Equals(
                        error,
                        _lastNavigationTraceError,
                        StringComparison.Ordinal))
                {
                    Logger.Warning(
                        $"CityBuddies navigation trace event failed for " +
                        $"{Client.CharacterName}: {error}");
                    _lastNavigationTraceError = error;
                }
            }
        }

        private void FlushNavigationTrace(bool force)
        {
            lock (_navigationTraceSync)
            {
                if (string.IsNullOrWhiteSpace(_navigationTracePath) ||
                    _pendingNavigationTrace.Count == 0)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (!force && now < _nextNavigationTraceFlushUtc)
                    return;

                try
                {
                    File.AppendAllLines(
                        _navigationTracePath,
                        _pendingNavigationTrace);
                    _pendingNavigationTrace.Clear();
                    _nextNavigationTraceFlushUtc =
                        now.Add(NavigationTraceFlushInterval);

                    if (!string.IsNullOrWhiteSpace(_lastNavigationTraceError))
                    {
                        Logger.Information(
                            $"CityBuddies navigation trace writing recovered for " +
                            $"{Client.CharacterName}.");
                        _lastNavigationTraceError = null;
                    }
                }
                catch (Exception ex)
                {
                    string error = ex.GetType().Name + ": " + ex.Message;
                    if (!string.Equals(
                            error,
                            _lastNavigationTraceError,
                            StringComparison.Ordinal))
                    {
                        Logger.Warning(
                            $"CityBuddies navigation trace flush failed for " +
                            $"{Client.CharacterName}: {error}");
                        _lastNavigationTraceError = error;
                    }
                }
            }
        }

        private void SetHomeState(string state, string detail)
        {
            DateTime now = DateTime.UtcNow;
            bool stateChanged =
                !string.Equals(_homeState, state, StringComparison.Ordinal);
            bool detailChanged =
                !string.Equals(_homeDetail, detail, StringComparison.Ordinal);
            if (!stateChanged && !detailChanged)
            {
                return;
            }

            if (stateChanged || detailChanged)
            {
                _homeState = state;
                _homeDetail = detail;
                _homeUpdatedUtc = now;
            }

            bool terminal = IsTerminalHomeState(state);
            TraceNavigation(
                stateChanged ? "state-change" : "state-detail",
                detail);
            _nextNavigationStateSampleUtc =
                now.Add(NavigationStateSampleInterval);

            if (terminal)
                FlushNavigationTrace(true);
        }

        private static bool IsTerminalHomeState(string state)
        {
            return string.Equals(state, "home", StringComparison.Ordinal) ||
                   string.Equals(state, "route-unavailable", StringComparison.Ordinal) ||
                   string.Equals(state, "movement-unverified", StringComparison.Ordinal) ||
                   string.Equals(state, "movement-diverged", StringComparison.Ordinal) ||
                   string.Equals(state, "stuck", StringComparison.Ordinal) ||
                   string.Equals(state, "failed", StringComparison.Ordinal) ||
                   string.Equals(state, "canceled", StringComparison.Ordinal);
        }

        private void WriteSnapshot(bool inPlay)
        {
            lock (_snapshotSync)
            {
                DateTime now = DateTime.UtcNow;
                _lastSnapshotUtc = now;

                try
                {
                    var snapshot = new BuddyPositionSnapshot
                    {
                        Character = Client.CharacterName,
                        ObservedUtc = now,
                        InPlay = inPlay,
                        Dead = _dead,
                        HomeJobId = _homeDirective?.JobId,
                        HomeMovementMode = _homeDirective == null
                            ? null
                            : GetMovementModeName(),
                        HomeState = _homeState,
                        HomeDetail = _homeDetail,
                        HomeDistance = _homeDistance,
                        HomeUpdatedUtc = _homeUpdatedUtc,
                        NavigationTraceFile = string.IsNullOrWhiteSpace(_navigationTracePath)
                            ? null
                            : Path.GetFileName(_navigationTracePath),
                        NavigationTraceSequence = string.IsNullOrWhiteSpace(
                                _navigationTracePath)
                            ? (long?)null
                            : Interlocked.Read(ref _navigationTraceSequence)
                    };

                    if (inPlay)
                        PopulateWorldSnapshot(snapshot);

                    PopulateMovementSnapshot(snapshot);

                    WriteSnapshotAtomically(snapshot);

                    if (!string.IsNullOrWhiteSpace(_lastSnapshotError))
                    {
                        Logger.Information(
                            $"CityBuddies position telemetry recovered for {Client.CharacterName}.");
                        _lastSnapshotError = null;
                    }
                }
                catch (Exception ex)
                {
                    string error = ex.GetType().Name + ": " + ex.Message;
                    if (!string.Equals(error, _lastSnapshotError, StringComparison.Ordinal))
                    {
                        Logger.Warning(
                            $"CityBuddies position telemetry failed for " +
                            $"{Client.CharacterName}: {error}");
                        _lastSnapshotError = error;
                    }
                }
            }
        }

        private void PopulateWorldSnapshot(BuddyPositionSnapshot snapshot)
        {
            var localPlayer = DynelManager.LocalPlayer;
            if (localPlayer == null)
            {
                snapshot.Error = "Local player is unavailable.";
                return;
            }

            snapshot.PlayfieldId = (int)Playfield.ModelId;
            try
            {
                snapshot.PlayfieldName = Playfield.Name;
            }
            catch (Exception ex)
            {
                snapshot.PlayfieldName = snapshot.PlayfieldId.Value.ToString();
                snapshot.Error = "Unable to read playfield name: " + ex.Message;
            }

            try
            {
                snapshot.Health = localPlayer.GetStat(Stat.Health);
                snapshot.MaxHealth = localPlayer.GetStat(Stat.MaxHealth);
            }
            catch (Exception ex)
            {
                snapshot.Error = AppendError(
                    snapshot.Error,
                    "Unable to read health: " + ex.Message);
            }

            try
            {
                snapshot.RunSpeed = localPlayer.GetStat(Stat.RunSpeed);
                _lastRunSpeed = snapshot.RunSpeed;
            }
            catch (Exception ex)
            {
                snapshot.Error = AppendError(
                    snapshot.Error,
                    "Unable to read Run Speed: " + ex.Message);
            }

            if (localPlayer.Transform == null)
            {
                snapshot.Error = AppendError(snapshot.Error, "Transform is unavailable.");
                return;
            }

            object position = localPlayer.Transform.Position;
            float positionX = 0;
            float positionY = 0;
            float positionZ = 0;

            snapshot.PositionAvailable =
                TryReadComponent(position, "X", out positionX) &&
                TryReadComponent(position, "Y", out positionY) &&
                TryReadComponent(position, "Z", out positionZ);

            if (snapshot.PositionAvailable)
            {
                snapshot.PositionX = positionX;
                snapshot.PositionY = positionY;
                snapshot.PositionZ = positionZ;
            }
            else
            {
                snapshot.Error = AppendError(snapshot.Error, "Position components are unavailable.");
            }

            object heading = localPlayer.Transform.Heading;
            float headingX = 0;
            float headingY = 0;
            float headingZ = 0;
            float headingW = 0;

            snapshot.HeadingAvailable =
                TryReadComponent(heading, "X", out headingX) &&
                TryReadComponent(heading, "Y", out headingY) &&
                TryReadComponent(heading, "Z", out headingZ) &&
                TryReadComponent(heading, "W", out headingW);

            if (snapshot.HeadingAvailable)
            {
                snapshot.HeadingX = headingX;
                snapshot.HeadingY = headingY;
                snapshot.HeadingZ = headingZ;
                snapshot.HeadingW = headingW;
            }
            else
            {
                snapshot.Error = AppendError(snapshot.Error, "Heading components are unavailable.");
            }
        }

        private void PopulateMovementSnapshot(BuddyPositionSnapshot snapshot)
        {
            lock (_movementObservationSync)
            {
                if (_movementCommandSerial > 0)
                {
                    snapshot.LastMovementCommandAction =
                        _lastMovementCommandAction.ToString();
                    snapshot.LastMovementCommandUtc = _lastMovementCommandUtc;
                    snapshot.LastMovementCommandX = _lastMovementCommandPosition.X;
                    snapshot.LastMovementCommandY = _lastMovementCommandPosition.Y;
                    snapshot.LastMovementCommandZ = _lastMovementCommandPosition.Z;
                }

                if (_settledObservationSerial > 0)
                {
                    snapshot.LastMovementObservationAction =
                        _lastMovementAction.ToString();
                    snapshot.LastMovementObservationUtc =
                        _lastMovementObservationUtc;
                    snapshot.LastMovementObservationDeltaTime =
                        _lastMovementObservationDeltaTime;
                    snapshot.LastMovementObservationX = _lastSettledPosition.X;
                    snapshot.LastMovementObservationY = _lastSettledPosition.Y;
                    snapshot.LastMovementObservationZ = _lastSettledPosition.Z;
                }
            }
        }

        private static bool TryReadComponent(object value, string component, out float result)
        {
            result = 0;
            if (value == null)
                return false;

            Type type = value.GetType();
            object componentValue = null;

            PropertyInfo property = type.GetProperty(component, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
                componentValue = property.GetValue(value, null);
            else
            {
                FieldInfo field = type.GetField(component, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                    componentValue = field.GetValue(value);
            }

            if (componentValue == null)
                return false;

            try
            {
                result = Convert.ToSingle(componentValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string AppendError(string current, string next)
        {
            return string.IsNullOrWhiteSpace(current)
                ? next
                : current + " " + next;
        }

        private void WriteSnapshotAtomically(BuddyPositionSnapshot snapshot)
        {
            string tempPath = _snapshotPath + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(snapshot));

            if (File.Exists(_snapshotPath))
                File.Replace(tempPath, _snapshotPath, null);
            else
                File.Move(tempPath, _snapshotPath);
        }

        private void DeleteSnapshot()
        {
            if (string.IsNullOrWhiteSpace(_snapshotPath))
                return;

            try
            {
                if (File.Exists(_snapshotPath))
                    File.Delete(_snapshotPath);

                string tempPath = _snapshotPath + ".tmp";
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }

        private sealed class NavigationTraceEntry
        {
            public string Format { get; set; }
            public long Sequence { get; set; }
            public DateTime Utc { get; set; }
            public string Character { get; set; }
            public string JobId { get; set; }
            public string Kind { get; set; }
            public string Message { get; set; }
            public int? PlayfieldId { get; set; }
            public bool InPlay { get; set; }
            public bool Dead { get; set; }
            public int? RunSpeed { get; set; }
            public string HomeMode { get; set; }
            public string HomeState { get; set; }
            public string HomeDetail { get; set; }
            public string Action { get; set; }
            public int? PacketDeltaTime { get; set; }
            public bool ObservedPositionAvailable { get; set; }
            public float? ObservedX { get; set; }
            public float? ObservedY { get; set; }
            public float? ObservedZ { get; set; }
            public float? ObservedHeadingX { get; set; }
            public float? ObservedHeadingY { get; set; }
            public float? ObservedHeadingZ { get; set; }
            public float? ObservedHeadingW { get; set; }
            public float? AssertedX { get; set; }
            public float? AssertedY { get; set; }
            public float? AssertedZ { get; set; }
            public float? AssertedHeadingX { get; set; }
            public float? AssertedHeadingY { get; set; }
            public float? AssertedHeadingZ { get; set; }
            public float? AssertedHeadingW { get; set; }
            public long MovementCommandSerial { get; set; }
            public string LastCommandAction { get; set; }
            public DateTime? LastCommandUtc { get; set; }
            public float? LastCommandX { get; set; }
            public float? LastCommandY { get; set; }
            public float? LastCommandZ { get; set; }
            public long MovementObservationSerial { get; set; }
            public string LastObservationAction { get; set; }
            public DateTime? LastObservationUtc { get; set; }
            public int? LastObservationDeltaTime { get; set; }
            public float? LastObservationX { get; set; }
            public float? LastObservationY { get; set; }
            public float? LastObservationZ { get; set; }
            public string PulsePhase { get; set; }
            public int StuckRecoveries { get; set; }
            public bool ContinuousForwardActive { get; set; }
            public bool ContinuousCommandAvailable { get; set; }
            public float? ContinuousCommandX { get; set; }
            public float? ContinuousCommandY { get; set; }
            public float? ContinuousCommandZ { get; set; }
            public int ContinuousWaypointIndex { get; set; }
            public int ContinuousPathCount { get; set; }
            public string GridExitPhase { get; set; }
            public bool GridCrossingActive { get; set; }
            public bool GridCrossingForwardActive { get; set; }
            public int IccUseAttempts { get; set; }
        }
    }
}
