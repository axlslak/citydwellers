using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

        private readonly object _snapshotSync = new object();
        private readonly object _movementObservationSync = new object();
        private readonly Dictionary<int, NavmeshPathfinder> _navmeshPathfinders =
            new Dictionary<int, NavmeshPathfinder>();
        private readonly Dictionary<int, string> _navmeshLoadErrors =
            new Dictionary<int, string>();
        private string _readyPath;
        private string _snapshotPath;
        private string _homeDirectivePath;
        private string _navmeshDirectory;
        private bool _readyWritten;
        private bool _inPlay;
        private bool _dead;
        private DateTime _lastSnapshotUtc = DateTime.MinValue;
        private string _lastSnapshotError;
        private string _lastDirectiveError;

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

        public override void Teardown()
        {
            StopMovement();
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

                if (n3Message.N3MessageType == N3MessageType.PlayfieldAnarchyF)
                {
                    ObservePlayfield((PlayfieldAnarchyFMessage)e.Body);
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

                StopMovement();
                _homeDirective = directive;
                _standRequested = false;
                _standReadyUtc = DateTime.MinValue;
                ResetPulseState(true);
                ResetContinuousState(true);

                if (directive.Cancel)
                {
                    SetHomeState("canceled", "Home navigation was canceled by Buddies.");
                    return;
                }

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
            PrepareMovementComponent(localPlayer, position, _pulseHeading);
            localPlayer.MovementComponent.ChangeMovement(MovementAction.FullStop);
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
                PrepareMovementComponent(localPlayer, _pulseStart, _pulseHeading);
                localPlayer.MovementComponent.ChangeMovement(MovementAction.ForwardStart);
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

                PrepareMovementComponent(localPlayer, _pulseEndpoint, _pulseHeading);
                localPlayer.MovementComponent.ChangeMovement(MovementAction.FullStop);
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

                PrepareMovementComponent(localPlayer, _pulseEndpoint, _pulseHeading);
                localPlayer.MovementComponent.ChangeMovement(MovementAction.FullStop);
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
                PrepareMovementComponent(
                    localPlayer,
                    settledPosition,
                    localPlayer.Transform.Heading);
                localPlayer.MovementComponent.ChangeMovement(MovementAction.FullStop);
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
                _settledObservationSerial++;
            }

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
            PrepareMovementComponent(localPlayer, heading);
            localPlayer.MovementComponent.ChangeMovement(MovementAction.FullStop);
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

                PrepareMovementComponent(localPlayer, localPlayer.Transform.Heading);
                localPlayer.MovementComponent.ChangeMovement(MovementAction.FullStop);
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

        private void SetHomeState(string state, string detail)
        {
            if (string.Equals(_homeState, state, StringComparison.Ordinal) &&
                string.Equals(_homeDetail, detail, StringComparison.Ordinal))
            {
                return;
            }

            _homeState = state;
            _homeDetail = detail;
            _homeUpdatedUtc = DateTime.UtcNow;
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
                        HomeUpdatedUtc = _homeUpdatedUtc
                    };

                    if (inPlay)
                        PopulateWorldSnapshot(snapshot);

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

        private static void PopulateWorldSnapshot(BuddyPositionSnapshot snapshot)
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
    }
}
