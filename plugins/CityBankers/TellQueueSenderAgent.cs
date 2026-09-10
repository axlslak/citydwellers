using System;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;

namespace CityBankers
{
    /// <summary>
    /// Makes each always-online banker an idle outbound-tell worker. Manager is the
    /// sole scheduler; this agent only advertises availability and executes work
    /// explicitly assigned to its own character.
    /// </summary>
    public class TellQueueSenderAgent : ClientlessPluginEntry
    {
        private string _dataDir;
        private DateTime _nextHeartbeatUtc = DateTime.MinValue;
        private DateTime? _lastSentUtc;

        public override void Init(string pluginDir)
        {
            string settingsDir;
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out error))
                throw new InvalidOperationException(error);

            _dataDir = RuntimeStateStore.GetDataDirectory(settingsDir);
            CityDwellers.Shared.TellQueue.EnsureDirectories(_dataDir);
            Client.OnUpdate += Tick;
            Logger.Information(
                "TELL QUEUE sender initialized for " + Client.CharacterName + ".");
        }

        public override void Teardown()
        {
            Client.OnUpdate -= Tick;
            if (!string.IsNullOrWhiteSpace(_dataDir))
            {
                CityDwellers.Shared.TellQueue.DeleteHeartbeat(
                    _dataDir,
                    Client.CharacterName);
            }
        }

        private void Tick(object sender, double deltaTime)
        {
            DateTime now = DateTime.UtcNow;
            bool busy = Trade.IsTrading;
            if (_lastSentUtc.HasValue && _lastSentUtc.Value > now.AddSeconds(5))
            {
                Logger.Warning(
                    "TELL QUEUE detected a backward clock correction; resetting local pacing.");
                _lastSentUtc = null;
            }

            if (now >= _nextHeartbeatUtc)
            {
                CityDwellers.Shared.TellQueue.WriteHeartbeat(
                    _dataDir,
                    Client.CharacterName,
                    Client.InPlay,
                    busy,
                    _lastSentUtc);
                _nextHeartbeatUtc = now.AddSeconds(1);
            }

            if (busy)
            {
                CityDwellers.Shared.TellQueueJob relinquished;
                if (CityDwellers.Shared.TellQueue.TryRelinquishAssignment(
                        _dataDir,
                        Client.CharacterName,
                        out relinquished))
                {
                    Logger.Information(
                        "TELL QUEUE returned " + ShortId(relinquished.Id) +
                        " because " + Client.CharacterName +
                        " entered a trade; another idle sender may take it.");
                }
                return;
            }

            if (!Client.InPlay ||
                (_lastSentUtc.HasValue &&
                 _lastSentUtc.Value > now.AddMilliseconds(
                     -CityDwellers.Shared.TellQueue.SenderIntervalMilliseconds)))
            {
                return;
            }

            CityDwellers.Shared.TellQueueJob job;
            string assignmentPath;
            if (!CityDwellers.Shared.TellQueue.TryReadAssignment(
                    _dataDir,
                    Client.CharacterName,
                    out job,
                    out assignmentPath))
            {
                return;
            }

            bool success = false;
            string failure = null;
            try
            {
                if (Client.Chat == null || string.IsNullOrWhiteSpace(job.RecipientName))
                    throw new InvalidOperationException(
                        "A banker tell assignment requires a recipient name.");

                Client.Chat.SendPrivateMessage(job.RecipientName, job.Message, true);
                _lastSentUtc = now;
                success = true;
                Logger.Information(
                    "TELL QUEUE sent " + ShortId(job.Id) + " as " +
                    Client.CharacterName + " -> " + job.RecipientName + ".");
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Logger.Warning(
                    "TELL QUEUE send failed " + ShortId(job.Id) + " as " +
                    Client.CharacterName + ": " + failure);
            }
            finally
            {
                CityDwellers.Shared.TellQueue.Complete(
                    _dataDir,
                    job,
                    assignmentPath,
                    Client.CharacterName,
                    success,
                    failure);
            }
        }

        private static string ShortId(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value.Length <= 8
                ? value
                : value.Substring(value.Length - 8);
        }
    }
}
