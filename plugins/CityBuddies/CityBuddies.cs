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

        private readonly object _snapshotSync = new object();
        private string _readyPath;
        private string _snapshotPath;
        private bool _readyWritten;
        private bool _inPlay;
        private bool _dead;
        private DateTime _lastSnapshotUtc = DateTime.MinValue;
        private string _lastSnapshotError;

        public override void Init(string pluginDir)
        {
            _readyPath = Path.Combine(
                pluginDir,
                $"citybuddies-ready-{Client.CharacterName}.ready");
            _snapshotPath = Path.Combine(
                pluginDir,
                $"citybuddies-position-{Client.CharacterName}.json");

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
            if (now - _lastSnapshotUtc < SnapshotInterval)
                return;

            _inPlay = Client.InPlay;
            WriteSnapshot(_inPlay);
        }

        private void Died()
        {
            _dead = true;
            WriteSnapshot(_inPlay);
            Logger.Information($"CityBuddies observed {Client.CharacterName} die.");
        }

        private void Disconnected()
        {
            _inPlay = false;
            WriteSnapshot(false);
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
                        Dead = _dead
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
