using System;
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
    public class CityBuddies : ClientlessPluginEntry
    {
        private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan NavigatingSnapshotInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DirectivePollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan MovementUpdateInterval = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan StandDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan StuckCheckInterval = TimeSpan.FromSeconds(8);

        private const int SerenityIslandsPlayfieldId = 6010;
        private const float HomeX = 996.004f;
        private const float HomeY = 5.010f;
        private const float HomeZ = 1248.512f;
        private const float HomeHeadingX = 0.000f;
        private const float HomeHeadingY = -0.997f;
        private const float HomeHeadingZ = 0.000f;
        private const float HomeHeadingW = 0.079f;
        private const float JunctionX = 994.0f;
        private const float JunctionY = 5.0f;
        private const float JunctionZ = 1403.2f;
        private const float HomeReachedDistance = 0.75f;
        private const float WaypointReachedDistance = 1.25f;
        private const float MeaningfulProgressDistance = 0.75f;
        private const int MaximumStuckRecoveries = 3;

        // These are the two unobstructed Serenity Islands street segments
        // supplied from the live city. Anything outside them is deliberately
        // reported as unmapped instead of attempting speculative movement.

        private readonly object _snapshotSync = new object();
        private string _readyPath;
        private string _snapshotPath;
        private string _homeDirectivePath;
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
        private DateTime _nextMovementUpdateUtc = DateTime.MinValue;
        private DateTime _standReadyUtc = DateTime.MinValue;
        private DateTime _lastProgressUtc = DateTime.MinValue;
        private float _bestTargetDistance = float.MaxValue;
        private int _stuckRecoveries;
        private bool _standRequested;
        private bool _moving;
        private bool _junctionReached;

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
                    "Character is dead; the ICC/grid recovery route is not mapped yet.");
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
                    _homeDirective.Cancel == directive.Cancel)
                {
                    return;
                }

                StopMovement();
                _homeDirective = directive;
                _standRequested = false;
                _standReadyUtc = DateTime.MinValue;
                _junctionReached = false;
                ResetProgress(DateTime.UtcNow);

                if (directive.Cancel)
                {
                    SetHomeState("canceled", "Home navigation was canceled by Buddies.");
                    return;
                }

                SetHomeState("starting", "Home navigation directive received.");
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
            if (_homeDirective == null || _homeDirective.Cancel)
                return;

            if (IsTerminalHomeState(_homeState))
                return;

            LocalPlayer localPlayer = DynelManager.LocalPlayer;
            if (localPlayer == null || localPlayer.Transform == null)
            {
                SetHomeState("waiting", "Waiting for the local player transform.");
                return;
            }

            if (_dead)
            {
                StopMovement();
                SetHomeState(
                    "route-unavailable",
                    "Character is dead; the ICC/grid recovery route is not mapped yet.");
                return;
            }

            int playfieldId = (int)Playfield.ModelId;
            if (playfieldId != SerenityIslandsPlayfieldId)
            {
                StopMovement();
                SetHomeState(
                    "route-unavailable",
                    $"Playfield {playfieldId} is not mapped; expected Serenity Islands " +
                    $"({SerenityIslandsPlayfieldId}).");
                return;
            }

            Vector3 position = localPlayer.Transform.Position;
            Vector3 home = new Vector3(HomeX, HomeY, HomeZ);
            _homeDistance = position.Distance2DFrom(home);

            if (!_standRequested)
            {
                PrepareMovementComponent(localPlayer, localPlayer.Transform.Heading);
                localPlayer.MovementComponent.ChangeMovement(MovementAction.LeaveSit);
                _standRequested = true;
                _standReadyUtc = now.Add(StandDelay);
                SetHomeState("standing", "Standing before movement.");
                return;
            }

            if (now < _standReadyUtc)
                return;

            if (_homeDistance.Value <= HomeReachedDistance)
            {
                FaceHome(localPlayer);
                SetHomeState(
                    "home",
                    $"Home at ({HomeX:F3},{HomeY:F3},{HomeZ:F3}); " +
                    $"distance={_homeDistance.Value:F2}m.");
                Logger.Information(
                    $"CityBuddies home job {_homeDirective.JobId} completed for " +
                    $"{Client.CharacterName}; distance={_homeDistance.Value:F2}m.");
                return;
            }

            Vector3 target;
            string routeState;
            string routeDetail;

            if (!TrySelectCityTarget(
                    position,
                    out target,
                    out routeState,
                    out routeDetail))
            {
                StopMovement();
                SetHomeState("route-unavailable", routeDetail);
                return;
            }

            float targetDistance = position.Distance2DFrom(target);

            if (string.Equals(routeState, "moving-to-junction", StringComparison.Ordinal) &&
                targetDistance <= WaypointReachedDistance)
            {
                StopMovement();
                _junctionReached = true;
                ResetProgress(now);
                SetHomeState(
                    "junction",
                    "Reached the T-junction; turning south toward the City Controller.");
                return;
            }

            if (_bestTargetDistance == float.MaxValue ||
                _bestTargetDistance - targetDistance >= MeaningfulProgressDistance)
            {
                _bestTargetDistance = targetDistance;
                _lastProgressUtc = now;
            }
            else if (now - _lastProgressUtc >= StuckCheckInterval)
            {
                if (_stuckRecoveries >= MaximumStuckRecoveries)
                {
                    StopMovement();
                    SetHomeState(
                        "stuck",
                        $"Stopped after {MaximumStuckRecoveries} movement recoveries; " +
                        $"target remained {targetDistance:F1}m away.");
                    return;
                }

                _stuckRecoveries++;
                StopMovement();
                PrepareMovementComponent(localPlayer, localPlayer.Transform.Heading);
                localPlayer.MovementComponent.ChangeMovement(MovementAction.LeaveSit);
                _standReadyUtc = now.Add(StandDelay);
                ResetProgress(now, false);
                SetHomeState(
                    "recovering",
                    $"Movement made no progress; recovery " +
                    $"{_stuckRecoveries}/{MaximumStuckRecoveries}.");
                return;
            }

            Quaternion heading = Quaternion.FromTo(position, target);
            PrepareMovementComponent(localPlayer, heading);

            if (!_moving)
            {
                localPlayer.MovementComponent.ChangeMovement(MovementAction.ForwardStart);
                _moving = true;
            }
            else if (now >= _nextMovementUpdateUtc)
            {
                localPlayer.MovementComponent.ChangeMovement(MovementAction.Update);
            }

            _nextMovementUpdateUtc = now.Add(MovementUpdateInterval);
            SetHomeState(
                routeState,
                routeDetail + $" Target distance={targetDistance:F1}m.");
        }

        private bool TrySelectCityTarget(
            Vector3 position,
            out Vector3 target,
            out string state,
            out string detail)
        {
            var home = new Vector3(HomeX, HomeY, HomeZ);
            var junction = new Vector3(JunctionX, JunctionY, JunctionZ);

            bool nearHome = position.Distance2DFrom(home) <= 8.0f;
            bool onSouthStreet =
                Math.Abs(position.X - JunctionX) <= 8.0f &&
                position.Z >= HomeZ - 8.0f &&
                position.Z <= JunctionZ + 8.0f;
            bool onEastStreet =
                position.X >= JunctionX - 8.0f &&
                position.X <= 1090.0f &&
                position.Z >= JunctionZ - 18.0f &&
                position.Z <= JunctionZ + 20.0f;

            if (nearHome || onSouthStreet || _junctionReached)
            {
                target = home;
                state = "moving-to-home";
                detail = "Following the mapped south street to the City Controller.";
                return true;
            }

            if (onEastStreet)
            {
                target = junction;
                state = "moving-to-junction";
                detail = "Following the mapped west street to the T-junction.";
                return true;
            }

            target = new Vector3();
            state = null;
            detail =
                $"Serenity Islands position ({position.X:F1},{position.Y:F1}," +
                $"{position.Z:F1}) is outside the two mapped safe corridors.";
            return false;
        }

        private static void PrepareMovementComponent(
            LocalPlayer localPlayer,
            Quaternion heading)
        {
            localPlayer.MovementComponent.Position = localPlayer.Transform.Position;
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
            _moving = false;
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
                _moving = false;
            }
        }

        private void ResetProgress(DateTime now, bool resetRecoveries = true)
        {
            _bestTargetDistance = float.MaxValue;
            _lastProgressUtc = now;
            _nextMovementUpdateUtc = DateTime.MinValue;
            if (resetRecoveries)
                _stuckRecoveries = 0;
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
