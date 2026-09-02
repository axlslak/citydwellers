using System;
using System.Collections.Generic;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;

namespace CityBuddies
{
    public partial class CityBuddies
    {
        private static readonly TimeSpan ContinuousMovementUpdateInterval =
            TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan ContinuousStuckTimeout =
            TimeSpan.FromMilliseconds(3000);
        private static readonly TimeSpan GridZoneTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan GridFinalApproachDuration =
            TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan GridFinalTraversalUpdateInterval =
            TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan GridNearExitToStopDelay =
            TimeSpan.FromMilliseconds(35);
        private static readonly TimeSpan GridPostStopTurnRightDelay =
            TimeSpan.FromMilliseconds(83);
        private static readonly TimeSpan GridPostStopTurnLeftDelay =
            TimeSpan.FromMilliseconds(6);
        private static readonly TimeSpan GridPostStopTurnStopDelay =
            TimeSpan.FromMilliseconds(160);
        private static readonly TimeSpan IccDynelDiscoveryTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan IccUseRetryInterval = TimeSpan.FromSeconds(5);

        private const string ContinuousMovementMode = "continuous";
        private const string BoundedPulseMovementMode = "bounded-pulse";
        private const int IccHeadquartersPlayfieldId = 655;
        private const int GridPlayfieldId = 152;
        private const int EnterTheGridTemplateId = 95350;
        private const int MaximumIccUseAttempts = 3;
        private const float IccUseDistance = 12.0f;
        private const float ContinuousWaypointRadius = 0.60f;
        private const float ContinuousProgressDistance = 0.15f;
        private const float ContinuousMaximumCrossTrack = 4.0f;
        private const float ContinuousMaximumCommandLead = 2.50f;
        // Clientless has no local movement engine. Advance the position in
        // small conservative increments equivalent to the proven 2m/1.2s
        // pulse, but publish them as one uninterrupted movement stream.
        private const float ContinuousMovementSpeed = 1.6667f;
        private const float ContinuousTurnRate = 5.0f;
        private const float GridExitX = 211.6727f;
        private const float GridExitY = 3.775f;
        private const float GridExitZ = 186.7213f;
        private const float GridNearExitX = 211.9757f;
        private const float GridNearExitZ = 187.0108f;
        private const float GridStagingReachedDistance = 0.25f;
        private const float GridObservedArrivalX = 234.3062f;
        private const float GridObservedArrivalZ = 212.8138f;
        private const float GridStagingDistance = 2.0f;
        private const float GridStagingConfirmationDistance = 0.05f;
        private const int GridFinalTraversalUpdateCount = 5;

        private int _activeNavigationPlayfieldId = int.MinValue;

        private readonly List<Vector3> _continuousPath = new List<Vector3>();
        private int _continuousWaypointIndex;
        private int _continuousPlayfieldId = int.MinValue;
        private Vector3 _continuousDestination;
        private Vector3 _continuousRouteStart;
        private Vector3 _continuousProgressPosition;
        private Vector3 _continuousCommandPosition;
        private Quaternion _continuousHeading;
        private DateTime _continuousLastCommandUtc = DateTime.MinValue;
        private DateTime _continuousLastSteerUtc = DateTime.MinValue;
        private DateTime _continuousNextUpdateUtc = DateTime.MinValue;
        private DateTime _continuousProgressUtc = DateTime.MinValue;
        private bool _continuousDestinationAvailable;
        private bool _continuousCommandPositionAvailable;
        private bool _continuousHeadingAvailable;
        private bool _continuousForwardActive;
        private volatile bool _continuousServerStopped;
        private int _continuousRecoveries;

        private DateTime _gridZoneDeadlineUtc = DateTime.MinValue;
        private DateTime _gridExitPhaseDeadlineUtc = DateTime.MinValue;
        private Vector3 _gridExitStaging;
        private Quaternion _gridExitHeading;
        private GridExitPhase _gridExitPhase;
        private int _gridStagingStopRetries;
        private DateTime _gridFinalApproachStartedUtc = DateTime.MinValue;
        private DateTime _gridNextTraversalUpdateUtc = DateTime.MinValue;
        private int _gridFinalTraversalUpdatesSent;

        private DateTime _iccDiscoveryStartedUtc = DateTime.MinValue;
        private DateTime _nextIccUseUtc = DateTime.MinValue;
        private bool _iccNearbyDynelsLogged;
        private int _iccUseAttempts;

        private enum GridExitPhase
        {
            Idle,
            SettlingAtStaging,
            FinalApproach,
            NearExitSample,
            PostStopTurnRight,
            PostStopTurnLeft,
            PostStopTurnStop,
            WaitingForSerenity
        }

        private void ProcessHomeRoute(DateTime now)
        {
            if (_homeDirective == null || _homeDirective.Cancel)
                return;

            LocalPlayer localPlayer = DynelManager.LocalPlayer;
            if (localPlayer == null || localPlayer.Transform == null)
            {
                SetHomeState("waiting", "Waiting for the local player transform.");
                return;
            }

            int playfieldId = (int)Playfield.ModelId;
            if (playfieldId != _activeNavigationPlayfieldId)
                BeginNavigationPlayfield(playfieldId);

            if (IsTerminalHomeState(_homeState))
                return;

            if (_dead)
            {
                StopMovement();
                SetHomeState("route-unavailable", "Character is dead; movement stopped.");
                return;
            }

            if (!EnsureStanding(localPlayer, now))
                return;

            switch (playfieldId)
            {
                case IccHeadquartersPlayfieldId:
                    ProcessIccEntry(localPlayer, now);
                    return;

                case GridPlayfieldId:
                    ProcessGridRoute(localPlayer, now);
                    return;

                case SerenityIslandsPlayfieldId:
                    ProcessSerenityRoute(localPlayer, now);
                    return;

                default:
                    StopMovement();
                    SetHomeState(
                        "route-unavailable",
                        $"Playfield {playfieldId} is outside the mapped ICC-to-CT route " +
                        $"({IccHeadquartersPlayfieldId}, {GridPlayfieldId}, " +
                        $"{SerenityIslandsPlayfieldId}).");
                    return;
            }
        }

        private void BeginNavigationPlayfield(int playfieldId)
        {
            _activeNavigationPlayfieldId = playfieldId;
            _homeDistance = null;
            _standRequested = false;
            _standReadyUtc = DateTime.MinValue;
            ResetPulseState(true);
            ResetContinuousState(true);
            ResetGridCrossing();
            ResetIccEntry();
            SetHomeState(
                "zoning",
                $"Entered playfield {playfieldId}; preparing {GetMovementModeName()} navigation.");
            TraceNavigation(
                "playfield-change",
                $"Stable playfield model changed to {playfieldId}.");
            FlushNavigationTrace(true);
        }

        private bool EnsureStanding(LocalPlayer localPlayer, DateTime now)
        {
            if (!_standRequested)
            {
                SendMovementCommand(
                    localPlayer,
                    localPlayer.Transform.Position,
                    localPlayer.Transform.Heading,
                    MovementAction.LeaveSit,
                    "Standing before home navigation.");
                _standRequested = true;
                _standReadyUtc = now.Add(StandDelay);
                SetHomeState("standing", "Standing before movement or interaction.");
                return false;
            }

            return now >= _standReadyUtc;
        }

        private void ProcessSerenityRoute(LocalPlayer localPlayer, DateTime now)
        {
            Vector3 position = localPlayer.Transform.Position;
            Vector3 home = new Vector3(HomeX, HomeY, HomeZ);
            _homeDistance = position.Distance2DFrom(home);

            if (_homeDistance.Value <= HomeReachedDistance)
            {
                FaceHome(localPlayer);
                SetHomeState(
                    "home",
                    $"Home at ({HomeX:F3},{HomeY:F3},{HomeZ:F3}); " +
                    $"distance={_homeDistance.Value:F2}m; mode={GetMovementModeName()}.");
                Logger.Information(
                    $"CityBuddies home job {_homeDirective.JobId} completed for " +
                    $"{Client.CharacterName}; distance={_homeDistance.Value:F2}m; " +
                    $"mode={GetMovementModeName()}.");
                return;
            }

            ProcessMappedRoute(
                localPlayer,
                SerenityIslandsPlayfieldId,
                home,
                HomeReachedDistance,
                "moving-to-home",
                "City Controller",
                now);
        }

        private void ProcessGridRoute(LocalPlayer localPlayer, DateTime now)
        {
            _homeDistance = null;

            if (_gridExitPhase != GridExitPhase.Idle)
            {
                ProcessGridExitState(localPlayer, now);
                return;
            }

            Vector3 staging = GetGridExitStaging();
            float stagingDistance =
                localPlayer.Transform.Position.Distance2DFrom(staging);
            if (stagingDistance <= GridStagingReachedDistance)
            {
                BeginGridExitStaging(localPlayer, now);
                return;
            }

            ProcessMappedRoute(
                localPlayer,
                GridPlayfieldId,
                staging,
                GridStagingReachedDistance,
                "moving-to-city-exit-staging",
                "Grid city-exit staging point",
                now);
        }

        private void ProcessMappedRoute(
            LocalPlayer localPlayer,
            int playfieldId,
            Vector3 destination,
            float reachedDistance,
            string state,
            string label,
            DateTime now)
        {
            if (UseBoundedPulseMovement())
            {
                ProcessBoundedPulseRoute(
                    localPlayer,
                    playfieldId,
                    destination,
                    state,
                    label,
                    now);
                return;
            }

            ProcessContinuousRoute(
                localPlayer,
                playfieldId,
                destination,
                reachedDistance,
                state,
                label,
                now);
        }

        private void ProcessBoundedPulseRoute(
            LocalPlayer localPlayer,
            int playfieldId,
            Vector3 destination,
            string state,
            string label,
            DateTime now)
        {
            if (_pulsePhase != MovementPulsePhase.Idle)
            {
                ProcessMovementPulse(localPlayer, now);
                return;
            }

            Vector3 position = localPlayer.Transform.Position;
            Vector3 target;
            string routeState;
            string routeDetail;
            if (!TrySelectNavmeshTarget(
                    playfieldId,
                    position,
                    destination,
                    out target,
                    out routeState,
                    out routeDetail))
            {
                StopMovement();
                SetHomeState("route-unavailable", routeDetail);
                return;
            }

            float targetDistance = position.Distance2DFrom(target);
            BeginMovementPulse(localPlayer, position, target, targetDistance, now);
            SetHomeState(
                state,
                $"{routeDetail} Bounded pulse toward {label}; " +
                $"distance={targetDistance:F1}m.");
        }

        private void ProcessContinuousRoute(
            LocalPlayer localPlayer,
            int playfieldId,
            Vector3 destination,
            float reachedDistance,
            string state,
            string label,
            DateTime now)
        {
            Vector3 position = localPlayer.Transform.Position;
            if (_continuousServerStopped)
            {
                _continuousServerStopped = false;
                _continuousCommandPosition = position;
                _continuousCommandPositionAvailable = true;
                _continuousForwardActive = false;
                _continuousNextUpdateUtc = DateTime.MinValue;
            }

            if (!ContinuousRouteMatches(playfieldId, destination))
            {
                string error;
                if (!TryBuildContinuousRoute(
                        playfieldId,
                        position,
                        destination,
                        now,
                        out error))
                {
                    StopMovement();
                    SetHomeState("route-unavailable", error);
                    return;
                }
            }

            Vector3 navigationPosition = _continuousCommandPositionAvailable
                ? _continuousCommandPosition
                : position;
            AdvanceContinuousWaypoint(
                navigationPosition,
                destination,
                reachedDistance);
            Vector3 target = _continuousWaypointIndex < _continuousPath.Count
                ? _continuousPath[_continuousWaypointIndex]
                : destination;

            float commandLead = position.Distance2DFrom(navigationPosition);
            if (_continuousForwardActive &&
                commandLead > ContinuousMaximumCommandLead)
            {
                RecoverContinuousMovement(
                    localPlayer,
                    now,
                    $"outbound position led the server by {commandLead:F2}m");
                return;
            }

            // If AO reports a position farther along this segment than the last
            // clientless command, accept the server's position as the new base.
            if (_continuousCommandPositionAvailable &&
                commandLead <= ContinuousMaximumCommandLead &&
                position.Distance2DFrom(target) + 0.10f <
                    navigationPosition.Distance2DFrom(target))
            {
                _continuousCommandPosition = position;
                navigationPosition = position;
                AdvanceContinuousWaypoint(
                    navigationPosition,
                    destination,
                    reachedDistance);
                target = _continuousWaypointIndex < _continuousPath.Count
                    ? _continuousPath[_continuousWaypointIndex]
                    : destination;
            }

            if (_continuousForwardActive &&
                navigationPosition.Distance2DFrom(destination) <= reachedDistance)
            {
                ConfirmContinuousEndpoint(localPlayer, now, label);
                return;
            }

            float crossTrack =
                DistanceFromContinuousSegment(navigationPosition, target);
            if (_continuousForwardActive && crossTrack > ContinuousMaximumCrossTrack)
            {
                RecoverContinuousMovement(
                    localPlayer,
                    now,
                    $"lateral drift={crossTrack:F2}m");
                return;
            }

            if (_continuousProgressUtc == DateTime.MinValue ||
                position.Distance2DFrom(_continuousProgressPosition) >=
                    ContinuousProgressDistance)
            {
                _continuousProgressPosition = position;
                _continuousProgressUtc = now;
                _continuousRecoveries = 0;
            }
            else if (_continuousForwardActive &&
                     now - _continuousProgressUtc >= ContinuousStuckTimeout)
            {
                RecoverContinuousMovement(
                    localPlayer,
                    now,
                    $"less than {ContinuousProgressDistance:F2}m progress in " +
                    $"{ContinuousStuckTimeout.TotalSeconds:F1}s");
                return;
            }

            Quaternion desiredHeading = HeadingTowards(navigationPosition, target);
            UpdateContinuousHeading(localPlayer.Transform.Heading, desiredHeading, now);

            if (now >= _continuousNextUpdateUtc)
            {
                if (!_continuousForwardActive)
                {
                    _continuousCommandPosition = position;
                    _continuousCommandPositionAvailable = true;
                    SendMovementCommand(
                        localPlayer,
                        position,
                        _continuousHeading,
                        MovementAction.ForwardStart,
                        $"Continuous start toward waypoint " +
                        $"({_continuousWaypointIndex + 1}/{_continuousPath.Count}) " +
                        $"at ({target.X:F3},{target.Y:F3},{target.Z:F3}).");
                }
                else
                {
                    DateTime previousCommandUtc = _continuousLastCommandUtc;
                    double elapsedSeconds = previousCommandUtc == DateTime.MinValue
                        ? ContinuousMovementUpdateInterval.TotalSeconds
                        : (now - previousCommandUtc).TotalSeconds;
                    elapsedSeconds = Math.Max(
                        0.05,
                        Math.Min(0.40, elapsedSeconds));
                    Vector3 outbound = AdvanceContinuousCommand(
                        _continuousCommandPosition,
                        position.Y,
                        target,
                        _continuousHeading,
                        (float)(ContinuousMovementSpeed * elapsedSeconds));
                    float outboundStep =
                        outbound.Distance2DFrom(_continuousCommandPosition);
                    _continuousCommandPosition = outbound;
                    SendMovementCommand(
                        localPlayer,
                        outbound,
                        _continuousHeading,
                        MovementAction.Update,
                        $"Continuous update toward waypoint " +
                        $"({_continuousWaypointIndex + 1}/{_continuousPath.Count}); " +
                        $"step={outboundStep:F3}m.");
                }

                _continuousForwardActive = true;
                _continuousLastCommandUtc = now;
                _continuousNextUpdateUtc = now.Add(ContinuousMovementUpdateInterval);
            }

            SetHomeState(
                state,
                $"Continuous steering toward {label}; path point " +
                $"{Math.Min(_continuousWaypointIndex + 1, _continuousPath.Count)}/" +
                $"{_continuousPath.Count}, waypoint=" +
                $"({target.X:F1},{target.Y:F1},{target.Z:F1}), " +
                $"distance={position.Distance2DFrom(target):F1}m.");
        }

        private bool TryBuildContinuousRoute(
            int playfieldId,
            Vector3 position,
            Vector3 destination,
            DateTime now,
            out string error)
        {
            NavmeshPathfinder pathfinder;
            if (!TryGetPathfinder(playfieldId, out pathfinder, out error))
                return false;

            try
            {
                IReadOnlyList<Vector3> path =
                    pathfinder.FindStraightPath(position, destination);

                _continuousPath.Clear();
                for (int i = 0; i < path.Count; i++)
                    _continuousPath.Add(path[i]);

                if (_continuousPath.Count == 0)
                    _continuousPath.Add(destination);

                _continuousWaypointIndex = 0;
                _continuousPlayfieldId = playfieldId;
                _continuousDestination = destination;
                _continuousDestinationAvailable = true;
                _continuousRouteStart = position;
                _continuousProgressPosition = position;
                _continuousCommandPosition = position;
                _continuousCommandPositionAvailable = true;
                _continuousProgressUtc = now;
                _continuousHeadingAvailable = false;
                _continuousLastSteerUtc = now;
                _continuousNextUpdateUtc = DateTime.MinValue;
                _continuousLastCommandUtc = DateTime.MinValue;
                TraceNavigation(
                    "route-built",
                    $"Playfield {playfieldId} path from " +
                    $"({position.X:F3},{position.Y:F3},{position.Z:F3}) to " +
                    $"({destination.X:F3},{destination.Y:F3},{destination.Z:F3}); " +
                    $"points={_continuousPath.Count}: " +
                    string.Join(
                        " -> ",
                        _continuousPath.Select(x =>
                            $"({x.X:F3},{x.Y:F3},{x.Z:F3})")));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    $"Playfield {playfieldId} continuous navmesh route unavailable from " +
                    $"({position.X:F1},{position.Y:F1},{position.Z:F1}): {ex.Message}";
                return false;
            }
        }

        private void AdvanceContinuousWaypoint(
            Vector3 position,
            Vector3 destination,
            float reachedDistance)
        {
            while (_continuousWaypointIndex < _continuousPath.Count)
            {
                bool finalPoint =
                    _continuousWaypointIndex == _continuousPath.Count - 1;
                float radius = finalPoint
                    ? reachedDistance
                    : ContinuousWaypointRadius;
                Vector3 waypoint = _continuousPath[_continuousWaypointIndex];
                if (position.Distance2DFrom(waypoint) > radius)
                    break;

                _continuousRouteStart = waypoint;
                _continuousWaypointIndex++;
            }

            if (_continuousWaypointIndex >= _continuousPath.Count &&
                position.Distance2DFrom(destination) > reachedDistance)
            {
                _continuousPath.Add(destination);
            }
        }

        private float DistanceFromContinuousSegment(Vector3 position, Vector3 target)
        {
            Vector3 start = _continuousWaypointIndex > 0
                ? _continuousPath[_continuousWaypointIndex - 1]
                : _continuousRouteStart;
            return DistanceFromSegment2D(position, start, target);
        }

        private static Vector3 AdvanceContinuousCommand(
            Vector3 position,
            float observedY,
            Vector3 target,
            Quaternion heading,
            float maximumDistance)
        {
            Vector3 forward = heading.Forward;
            forward = new Vector3(forward.X, 0, forward.Z);
            if (forward.LengthSquared() <= 0.0001f)
            {
                forward = new Vector3(
                    target.X - position.X,
                    0,
                    target.Z - position.Z);
            }

            forward = forward.Normalize();
            float distance = position.Distance2DFrom(target);
            float step = Math.Min(maximumDistance, distance);
            return new Vector3(
                position.X + (forward.X * step),
                observedY,
                position.Z + (forward.Z * step));
        }

        private void ConfirmContinuousEndpoint(
            LocalPlayer localPlayer,
            DateTime now,
            string label)
        {
            SendMovementCommand(
                localPlayer,
                _continuousCommandPosition,
                _continuousHeading,
                MovementAction.FullStop,
                $"Continuous endpoint stop at {label}.");
            ResetContinuousRoute(false);
            _continuousForwardActive = false;
            _standReadyUtc = now.Add(MovementObservationQuietPeriod);
            SetHomeState(
                "confirming-position",
                $"Stopped the continuous stream at {label}; waiting for the " +
                "server position before completing or replanning.");
        }

        private void RecoverContinuousMovement(
            LocalPlayer localPlayer,
            DateTime now,
            string reason)
        {
            SendMovementCommand(
                localPlayer,
                localPlayer.Transform.Position,
                localPlayer.Transform.Heading,
                MovementAction.FullStop,
                $"Continuous recovery stop: {reason}");

            if (_continuousRecoveries >= MaximumStuckRecoveries)
            {
                ResetContinuousState(false);
                SetHomeState(
                    "stuck",
                    $"Continuous movement stopped after {MaximumStuckRecoveries} " +
                    $"recoveries; {reason}.");
                return;
            }

            _continuousRecoveries++;
            ResetContinuousRoute(false);
            _continuousForwardActive = false;
            _standReadyUtc = now.Add(HeadingSettleDelay);
            SetHomeState(
                "recovering",
                $"Continuous movement recovery {_continuousRecoveries}/" +
                $"{MaximumStuckRecoveries}; {reason}.");
        }

        private bool ContinuousRouteMatches(int playfieldId, Vector3 destination)
        {
            return _continuousDestinationAvailable &&
                   _continuousPlayfieldId == playfieldId &&
                   _continuousDestination.Distance2DFrom(destination) <= 0.01f;
        }

        private void UpdateContinuousHeading(
            Quaternion observedHeading,
            Quaternion desiredHeading,
            DateTime now)
        {
            if (!_continuousHeadingAvailable)
            {
                _continuousHeading = desiredHeading;
                _continuousHeadingAvailable = true;
                _continuousLastSteerUtc = now;
                return;
            }

            Quaternion start = _continuousHeadingAvailable
                ? _continuousHeading
                : observedHeading;
            double elapsedSeconds = _continuousLastSteerUtc == DateTime.MinValue
                ? 0.0
                : (now - _continuousLastSteerUtc).TotalSeconds;
            float amount = (float)Math.Max(
                0.0,
                Math.Min(1.0, elapsedSeconds * ContinuousTurnRate));

            _continuousHeading = Slerp(start, desiredHeading, amount);
            _continuousHeadingAvailable = true;
            _continuousLastSteerUtc = now;
        }

        private static Quaternion HeadingTowards(Vector3 position, Vector3 target)
        {
            Vector3 flatPosition = new Vector3(position.X, 0, position.Z);
            Vector3 flatTarget = new Vector3(target.X, 0, target.Z);
            return Quaternion.FromTo(flatPosition, flatTarget);
        }

        private static Quaternion Slerp(Quaternion from, Quaternion to, float amount)
        {
            float dot =
                (from.X * to.X) +
                (from.Y * to.Y) +
                (from.Z * to.Z) +
                (from.W * to.W);

            if (dot < 0.0f)
            {
                dot = -dot;
                to = new Quaternion(-to.X, -to.Y, -to.Z, -to.W);
            }

            dot = Math.Max(-1.0f, Math.Min(1.0f, dot));
            if (dot > 0.9995f)
            {
                return NormalizeQuaternion(new Quaternion(
                    from.X + ((to.X - from.X) * amount),
                    from.Y + ((to.Y - from.Y) * amount),
                    from.Z + ((to.Z - from.Z) * amount),
                    from.W + ((to.W - from.W) * amount)));
            }

            double angle = Math.Acos(dot);
            double sinAngle = Math.Sin(angle);
            if (Math.Abs(sinAngle) < 0.000001)
                return to;

            float fromScale = (float)(Math.Sin((1.0 - amount) * angle) / sinAngle);
            float toScale = (float)(Math.Sin(amount * angle) / sinAngle);
            return NormalizeQuaternion(new Quaternion(
                (from.X * fromScale) + (to.X * toScale),
                (from.Y * fromScale) + (to.Y * toScale),
                (from.Z * fromScale) + (to.Z * toScale),
                (from.W * fromScale) + (to.W * toScale)));
        }

        private static Quaternion NormalizeQuaternion(Quaternion value)
        {
            double length = Math.Sqrt(
                (value.X * value.X) +
                (value.Y * value.Y) +
                (value.Z * value.Z) +
                (value.W * value.W));
            if (length < 0.000001)
                return Quaternion.Identity;

            return new Quaternion(
                value.X / length,
                value.Y / length,
                value.Z / length,
                value.W / length);
        }

        private static Vector3 GetGridExitStaging()
        {
            Vector3 exit = new Vector3(GridExitX, GridExitY, GridExitZ);
            Vector3 direction = new Vector3(
                GridExitX - GridObservedArrivalX,
                0,
                GridExitZ - GridObservedArrivalZ).Normalize();
            return new Vector3(
                exit.X - (direction.X * GridStagingDistance),
                exit.Y,
                exit.Z - (direction.Z * GridStagingDistance));
        }

        private void BeginGridExitStaging(LocalPlayer localPlayer, DateTime now)
        {
            _gridExitStaging = GetGridExitStaging();
            Vector3 exit = new Vector3(GridExitX, GridExitY, GridExitZ);
            _gridExitHeading = HeadingTowards(_gridExitStaging, exit);

            ResetPulseState(false);
            ResetContinuousState(false);

            lock (_movementObservationSync)
                _settledObservationSerialAtStop = _settledObservationSerial;

            SendMovementCommand(
                localPlayer,
                _gridExitStaging,
                _gridExitHeading,
                MovementAction.FullStop,
                $"Grid exit staging stop at " +
                $"({_gridExitStaging.X:F3},{_gridExitStaging.Y:F3}," +
                $"{_gridExitStaging.Z:F3}).");

            _gridExitPhase = GridExitPhase.SettlingAtStaging;
            _gridExitPhaseDeadlineUtc = now.Add(SettledPositionTimeout);
            _gridStagingStopRetries = 0;
            SetHomeState(
                "staging-city-exit",
                $"Stopped at the fixed Grid-side staging point " +
                $"{GridStagingDistance:F1}m before the captured exit; " +
                "waiting for its exact movement echo.");
        }

        private void ProcessGridExitState(LocalPlayer localPlayer, DateTime now)
        {
            if (_gridExitPhase == GridExitPhase.SettlingAtStaging)
            {
                ProcessGridExitStaging(localPlayer, now);
                return;
            }

            if (_gridExitPhase == GridExitPhase.FinalApproach)
            {
                SendDueGridTraversalUpdate(localPlayer, now);
                if (now < _gridExitPhaseDeadlineUtc)
                    return;

                Vector3 nearExit =
                    new Vector3(GridNearExitX, GridExitY, GridNearExitZ);
                SendMovementCommand(
                    localPlayer,
                    nearExit,
                    new Quaternion(0, -0.919104f, 0, 0.3940149f),
                    MovementAction.TurnLeftMouse,
                    "Grid captured near-exit TurnLeftMouse sample.");
                _gridExitPhase = GridExitPhase.NearExitSample;
                _gridExitPhaseDeadlineUtc = now.Add(GridNearExitToStopDelay);
                SetHomeState(
                    "crossing-city-exit",
                    $"Sent the captured near-exit TurnLeftMouse sample at " +
                    $"({GridNearExitX:F3},{GridExitY:F3},{GridNearExitZ:F3}); " +
                    "finishing the full-client movement tail.");
                return;
            }

            if (_gridExitPhase == GridExitPhase.NearExitSample)
            {
                if (now < _gridExitPhaseDeadlineUtc)
                    return;

                Vector3 exit = new Vector3(GridExitX, GridExitY, GridExitZ);
                SendMovementCommand(
                    localPlayer,
                    exit,
                    new Quaternion(0, -0.9210626f, 0, 0.3894144f),
                    MovementAction.ForwardStop,
                    "Grid captured ForwardStop at the exact exit.");
                _gridExitPhase = GridExitPhase.PostStopTurnRight;
                _gridExitPhaseDeadlineUtc =
                    now.Add(GridPostStopTurnRightDelay);
                return;
            }

            if (_gridExitPhase == GridExitPhase.PostStopTurnRight)
            {
                if (now < _gridExitPhaseDeadlineUtc)
                    return;

                SendGridExitTurn(
                    localPlayer,
                    MovementAction.TurnRightMouse,
                    "Grid captured post-stop TurnRightMouse.");
                _gridExitPhase = GridExitPhase.PostStopTurnLeft;
                _gridExitPhaseDeadlineUtc =
                    now.Add(GridPostStopTurnLeftDelay);
                return;
            }

            if (_gridExitPhase == GridExitPhase.PostStopTurnLeft)
            {
                if (now < _gridExitPhaseDeadlineUtc)
                    return;

                SendGridExitTurn(
                    localPlayer,
                    MovementAction.TurnLeftMouse,
                    "Grid captured post-stop TurnLeftMouse.");
                _gridExitPhase = GridExitPhase.PostStopTurnStop;
                _gridExitPhaseDeadlineUtc =
                    now.Add(GridPostStopTurnStopDelay);
                return;
            }

            if (_gridExitPhase == GridExitPhase.PostStopTurnStop)
            {
                if (now < _gridExitPhaseDeadlineUtc)
                    return;

                Vector3 exit = new Vector3(GridExitX, GridExitY, GridExitZ);
                SendMovementCommand(
                    localPlayer,
                    exit,
                    new Quaternion(0, -0.9268001f, 0, 0.3755549f),
                    MovementAction.TurnLeftStop,
                    "Grid captured post-stop TurnLeftStop.");
                _gridExitPhase = GridExitPhase.WaitingForSerenity;
                _gridZoneDeadlineUtc = now.Add(GridZoneTimeout);
                SetHomeState(
                    "waiting-for-serenity",
                    $"Replayed the captured Grid exit movement tail at " +
                    $"({GridExitX:F3},{GridExitY:F3},{GridExitZ:F3}); " +
                    "waiting for Serenity.");
                return;
            }

            if (_gridExitPhase == GridExitPhase.WaitingForSerenity &&
                now >= _gridZoneDeadlineUtc)
            {
                StopMovement();
                ResetGridCrossing();
                SetHomeState(
                    "route-unavailable",
                    $"Grid did not change to Serenity within " +
                    $"{GridZoneTimeout.TotalSeconds:F0}s after replaying the " +
                    "captured exit movement tail.");
            }
        }

        private void SendGridExitTurn(
            LocalPlayer localPlayer,
            MovementAction action,
            string reason)
        {
            Vector3 exit = new Vector3(GridExitX, GridExitY, GridExitZ);
            SendMovementCommand(
                localPlayer,
                exit,
                new Quaternion(0, -0.9249107f, 0, 0.3801842f),
                action,
                reason);
        }

        private void ProcessGridExitStaging(LocalPlayer localPlayer, DateTime now)
        {
            Vector3 observedPosition;
            MovementAction observedAction;
            bool hasObservation = TryGetPositionAfterStop(
                out observedPosition,
                out observedAction,
                out _);
            bool exactStagingStop =
                hasObservation &&
                observedAction == MovementAction.FullStop &&
                observedPosition.Distance2DFrom(_gridExitStaging) <=
                    GridStagingConfirmationDistance;

            if (exactStagingStop)
            {
                BeginGridFinalApproach(localPlayer, now);
                return;
            }

            if (now < _gridExitPhaseDeadlineUtc)
                return;

            if (_gridStagingStopRetries < MaximumStopConfirmationRetries)
            {
                _gridStagingStopRetries++;
                lock (_movementObservationSync)
                    _settledObservationSerialAtStop = _settledObservationSerial;

                SendMovementCommand(
                    localPlayer,
                    _gridExitStaging,
                    _gridExitHeading,
                    MovementAction.FullStop,
                    $"Grid staging stop confirmation " +
                    $"{_gridStagingStopRetries}/" +
                    $"{MaximumStopConfirmationRetries}.");
                _gridExitPhaseDeadlineUtc = now.Add(SettledPositionTimeout);
                SetHomeState(
                    "staging-city-exit",
                    $"Waiting for the exact Grid staging FullStop echo; " +
                    $"confirmation {_gridStagingStopRetries}/" +
                    $"{MaximumStopConfirmationRetries}.");
                return;
            }

            StopMovement();
            ResetGridCrossing();
            SetHomeState(
                "route-unavailable",
                "Grid did not confirm the exact city-exit staging stop.");
        }

        private void BeginGridFinalApproach(LocalPlayer localPlayer, DateTime now)
        {
            SendMovementCommand(
                localPlayer,
                _gridExitStaging,
                _gridExitHeading,
                MovementAction.ForwardStart,
                $"Grid final approach start from " +
                $"({_gridExitStaging.X:F3},{_gridExitStaging.Y:F3}," +
                $"{_gridExitStaging.Z:F3}) toward the captured exit.");

            _gridExitPhase = GridExitPhase.FinalApproach;
            _gridFinalApproachStartedUtc = now;
            _gridNextTraversalUpdateUtc =
                now.Add(GridFinalTraversalUpdateInterval);
            _gridFinalTraversalUpdatesSent = 0;
            _gridExitPhaseDeadlineUtc = now.Add(GridFinalApproachDuration);
            SetHomeState(
                "crossing-city-exit",
                $"Started a bounded {GridStagingDistance:F1}m final approach " +
                $"with {GridFinalTraversalUpdateCount} intermediate traversal " +
                "assertions before reproducing the captured near-exit tail.");
        }

        private void SendDueGridTraversalUpdate(
            LocalPlayer localPlayer,
            DateTime now)
        {
            if (_gridFinalTraversalUpdatesSent >=
                    GridFinalTraversalUpdateCount ||
                now < _gridNextTraversalUpdateUtc)
            {
                return;
            }

            _gridFinalTraversalUpdatesSent++;
            float fraction =
                (float)_gridFinalTraversalUpdatesSent /
                (GridFinalTraversalUpdateCount + 1);
            Vector3 nearExit =
                new Vector3(GridNearExitX, GridExitY, GridNearExitZ);
            Vector3 traversalPosition = new Vector3(
                _gridExitStaging.X +
                    ((nearExit.X - _gridExitStaging.X) * fraction),
                GridExitY,
                _gridExitStaging.Z +
                    ((nearExit.Z - _gridExitStaging.Z) * fraction));

            SendMovementCommand(
                localPlayer,
                traversalPosition,
                _gridExitHeading,
                MovementAction.Update,
                $"Grid final traversal update " +
                $"{_gridFinalTraversalUpdatesSent}/" +
                $"{GridFinalTraversalUpdateCount} at " +
                $"({traversalPosition.X:F3},{traversalPosition.Y:F3}," +
                $"{traversalPosition.Z:F3}).");

            _gridNextTraversalUpdateUtc = _gridFinalApproachStartedUtc.Add(
                TimeSpan.FromTicks(
                    GridFinalTraversalUpdateInterval.Ticks *
                    (_gridFinalTraversalUpdatesSent + 1)));
        }

        private void ProcessIccEntry(LocalPlayer localPlayer, DateTime now)
        {
            _homeDistance = null;
            if (_iccDiscoveryStartedUtc == DateTime.MinValue)
                _iccDiscoveryStartedUtc = now;

            StaticDynel enterTheGrid = FindEnterTheGridDynel(localPlayer.Transform.Position);
            LogNearbyIccDynels(localPlayer.Transform.Position);

            if (enterTheGrid == null || enterTheGrid.Transform == null)
            {
                if (now - _iccDiscoveryStartedUtc >= IccDynelDiscoveryTimeout)
                {
                    SetHomeState(
                        "route-unavailable",
                        "ICC did not expose the nearby static Enter The Grid dynel.");
                }
                else
                {
                    SetHomeState(
                        "finding-grid-entry",
                        "Listing nearby ICC dynels and waiting for Enter The Grid.");
                }
                return;
            }

            float distance =
                localPlayer.Transform.Position.Distance2DFrom(
                    enterTheGrid.Transform.Position);
            if (distance > IccUseDistance)
            {
                SetHomeState(
                    "route-unavailable",
                    $"Enter The Grid is {distance:F1}m away; park the buddy within " +
                    $"{IccUseDistance:F0}m before starting #home.");
                return;
            }

            if (now < _nextIccUseUtc)
                return;

            if (_iccUseAttempts >= MaximumIccUseAttempts)
            {
                SetHomeState(
                    "route-unavailable",
                    $"Enter The Grid remained in ICC after {MaximumIccUseAttempts} " +
                    "static-identity use attempts.");
                return;
            }

            StopMovement();
            if (_iccUseAttempts == 0)
            {
                Logger.Information(
                    $"CityBuddies using AOSharp static Enter The Grid identity " +
                    $"{enterTheGrid.Identity} template={enterTheGrid.TemplateId}.");
            }

            enterTheGrid.Use();
            _iccUseAttempts++;
            _nextIccUseUtc = now.Add(IccUseRetryInterval);
            TraceNavigation(
                "interaction",
                $"Used Enter The Grid identity {enterTheGrid.Identity}; " +
                $"template={enterTheGrid.TemplateId}; attempt={_iccUseAttempts}/" +
                $"{MaximumIccUseAttempts}; distance={distance:F3}m.");
            SetHomeState(
                "entering-grid",
                $"Used static Enter The Grid identity {enterTheGrid.Identity}; attempt " +
                $"{_iccUseAttempts}/{MaximumIccUseAttempts}, waiting for Grid.");
        }

        private StaticDynel FindEnterTheGridDynel(Vector3 position)
        {
            List<StaticDynel> exact = DynelManager.AllDynels
                .OfType<StaticDynel>()
                .Where(x =>
                    string.Equals(
                        x.Name,
                        "Enter The Grid",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(x =>
                    x.Transform == null
                        ? float.MaxValue
                        : position.Distance2DFrom(x.Transform.Position))
                .ToList();
            if (exact.Count > 0)
                return exact[0];

            // Template 95350 is the terminal at the supplied ICC coordinate.
            // AOSharp's static-dynel model is expressly designed to use the
            // stored identity for these world objects.
            return DynelManager.AllDynels
                .OfType<StaticDynel>()
                .Where(x => x.TemplateId == EnterTheGridTemplateId)
                .OrderBy(x =>
                    x.Transform == null
                        ? float.MaxValue
                        : position.Distance2DFrom(x.Transform.Position))
                .FirstOrDefault();
        }

        private void LogNearbyIccDynels(Vector3 position)
        {
            if (_iccNearbyDynelsLogged)
                return;

            _iccNearbyDynelsLogged = true;
            List<Dynel> nearby = DynelManager.AllDynels
                .Where(x =>
                    x.Transform != null &&
                    position.Distance2DFrom(x.Transform.Position) <= 20.0f)
                .OrderBy(x => position.Distance2DFrom(x.Transform.Position))
                .Take(30)
                .ToList();

            if (nearby.Count == 0)
            {
                _iccNearbyDynelsLogged = false;
                return;
            }

            Logger.Information(
                $"CityBuddies nearby ICC dynels for {Client.CharacterName}: " +
                (nearby.Count == 0
                    ? "none"
                    : string.Join(
                        "; ",
                        nearby.Select(x =>
                            $"'{x.Name}' {x.Identity} " +
                            $"d={position.Distance2DFrom(x.Transform.Position):F1}m"))));

        }

        private bool UseBoundedPulseMovement()
        {
            string mode = _homeDirective?.MovementMode;
            return string.Equals(
                       mode,
                       BoundedPulseMovementMode,
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "bounded", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "pulse", StringComparison.OrdinalIgnoreCase);
        }

        private string GetMovementModeName()
        {
            return UseBoundedPulseMovement()
                ? BoundedPulseMovementMode
                : ContinuousMovementMode;
        }

        private void ResetContinuousState(bool resetRecoveries)
        {
            ResetContinuousRoute(resetRecoveries);
            _continuousForwardActive = false;
            _continuousServerStopped = false;
        }

        private void ResetContinuousRoute(bool resetRecoveries)
        {
            _continuousPath.Clear();
            _continuousWaypointIndex = 0;
            _continuousPlayfieldId = int.MinValue;
            _continuousDestinationAvailable = false;
            _continuousHeadingAvailable = false;
            _continuousLastSteerUtc = DateTime.MinValue;
            _continuousNextUpdateUtc = DateTime.MinValue;
            _continuousLastCommandUtc = DateTime.MinValue;
            _continuousProgressUtc = DateTime.MinValue;
            _continuousCommandPositionAvailable = false;
            if (resetRecoveries)
                _continuousRecoveries = 0;
        }

        private void ResetGridCrossing()
        {
            _gridExitPhase = GridExitPhase.Idle;
            _gridExitPhaseDeadlineUtc = DateTime.MinValue;
            _gridZoneDeadlineUtc = DateTime.MinValue;
            _gridStagingStopRetries = 0;
            _gridFinalApproachStartedUtc = DateTime.MinValue;
            _gridNextTraversalUpdateUtc = DateTime.MinValue;
            _gridFinalTraversalUpdatesSent = 0;
        }

        private void ResetIccEntry()
        {
            _iccDiscoveryStartedUtc = DateTime.MinValue;
            _nextIccUseUtc = DateTime.MinValue;
            _iccNearbyDynelsLogged = false;
            _iccUseAttempts = 0;
        }
    }
}
