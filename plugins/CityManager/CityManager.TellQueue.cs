using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private readonly List<string> _tellQueueSenders = new List<string>();
        private string _tellQueueCoordinator;
        private string _tellQueueLastSender;
        private DateTime _tellQueueNextHeartbeatUtc = DateTime.MinValue;
        private DateTime _tellQueueNextCoordinatorTickUtc = DateTime.MinValue;
        private DateTime? _tellQueueLastSentUtc;
        private bool _tellQueueInitialized;

        private void InitializeTellQueue()
        {
            try
            {
                TellQueue.EnsureDirectories(_dataDir);
                JObject root = JObject.Parse(
                    File.ReadAllText(Path.Combine(_settingsDir, "citydwellers.json")));

                JObject manager = GetObject(root, "Manager");
                JArray accounts = manager != null
                    ? GetToken(manager, "Accounts") as JArray
                    : null;
                if (accounts != null)
                {
                    foreach (JObject account in accounts.OfType<JObject>())
                        AddTellSender(GetString(account, "Character"));
                }

                _tellQueueCoordinator = _tellQueueSenders.FirstOrDefault();

                JObject bankers = GetObject(root, "Bankers");
                JObject roles = bankers != null ? GetObject(bankers, "Roles") : null;
                if (roles != null)
                {
                    foreach (JProperty role in roles.Properties())
                        AddTellSender(GetString(role.Value as JObject, "Character"));
                }

                _tellQueueInitialized = true;
                Logger.Information(
                    "TELL QUEUE initialized: coordinator=" +
                    (_tellQueueCoordinator ?? "none") + ", senders=" +
                    string.Join(", ", _tellQueueSenders) + ".");
            }
            catch (Exception ex)
            {
                Logger.Error("TELL QUEUE initialization failed: " + ex);
            }
        }

        private void ShutdownTellQueue()
        {
            if (!_tellQueueInitialized)
                return;
            TellQueue.DeleteHeartbeat(_dataDir, Client.CharacterName);
            _tellQueueInitialized = false;
        }

        private void TickTellQueue()
        {
            if (!_tellQueueInitialized)
                return;

            DateTime now = DateTime.UtcNow;
            if (now >= _tellQueueNextHeartbeatUtc)
            {
                TellQueue.WriteHeartbeat(
                    _dataDir,
                    Client.CharacterName,
                    Client.InPlay,
                    false,
                    _tellQueueLastSentUtc);
                _tellQueueNextHeartbeatUtc = now.AddSeconds(1);
            }

            TrySendAssignedTell(now);

            if (!string.Equals(
                    Client.CharacterName,
                    _tellQueueCoordinator,
                    StringComparison.OrdinalIgnoreCase) ||
                now < _tellQueueNextCoordinatorTickUtc)
            {
                return;
            }

            _tellQueueNextCoordinatorTickUtc = now.AddMilliseconds(100);
            int requeued = TellQueue.RequeueStaleAssignments(_dataDir, now);
            if (requeued > 0)
                Logger.Warning("TELL QUEUE recovered " + requeued + " stale assignment(s).");

            if (TellQueue.HasAssignment(_dataDir))
                return;

            TellQueueJob job = TellQueue.ReadPending(_dataDir).FirstOrDefault();
            if (job == null)
                return;

            HashSet<string> configured = new HashSet<string>(
                _tellQueueSenders,
                StringComparer.OrdinalIgnoreCase);
            List<TellSenderHeartbeat> available = TellQueue
                .ReadFreshSenders(_dataDir, now)
                .Where(h => configured.Contains(h.Character))
                .Where(h => string.IsNullOrWhiteSpace(job.RequiredSender) ||
                            string.Equals(
                                h.Character,
                                job.RequiredSender,
                                StringComparison.OrdinalIgnoreCase))
                .Where(h => !h.LastSentUtc.HasValue ||
                            h.LastSentUtc.Value > now.AddSeconds(5) ||
                            h.LastSentUtc.Value <= now.AddMilliseconds(
                                -TellQueue.SenderIntervalMilliseconds))
                .ToList();

            string sender = SelectNextTellSender(available);
            if (sender == null)
                return;

            if (TellQueue.TryAssign(_dataDir, job, sender, now))
            {
                _tellQueueLastSender = sender;
                Logger.Information(
                    "TELL QUEUE assigned " + ShortTellId(job.Id) + " -> " +
                    sender + " for " + (job.RecipientName ?? "character-id") + ".");
            }
        }

        private void TrySendAssignedTell(DateTime now)
        {
            if (_tellQueueLastSentUtc.HasValue &&
                _tellQueueLastSentUtc.Value > now.AddSeconds(5))
            {
                Logger.Warning(
                    "TELL QUEUE detected a backward clock correction; resetting local pacing.");
                _tellQueueLastSentUtc = null;
            }

            if (_tellQueueLastSentUtc.HasValue &&
                _tellQueueLastSentUtc.Value > now.AddMilliseconds(
                    -TellQueue.SenderIntervalMilliseconds))
            {
                return;
            }

            TellQueueJob job;
            string assignmentPath;
            if (!TellQueue.TryReadAssignment(
                    _dataDir,
                    Client.CharacterName,
                    out job,
                    out assignmentPath))
            {
                return;
            }

            bool success = false;
            string error = null;
            try
            {
                if (job.RecipientId.HasValue && job.RecipientId.Value != 0)
                    Client.SendPrivateMessage(job.RecipientId.Value, job.Message);
                else if (Client.Chat != null && !string.IsNullOrWhiteSpace(job.RecipientName))
                    Client.Chat.SendPrivateMessage(job.RecipientName, job.Message, true);
                else
                    throw new InvalidOperationException("No usable tell recipient was supplied.");

                success = true;
                _tellQueueLastSentUtc = now;
                Logger.Information(
                    "TELL QUEUE sent " + ShortTellId(job.Id) + " as " +
                    Client.CharacterName + " -> " + (job.RecipientName ?? "character-id") + ".");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Logger.Warning(
                    "TELL QUEUE send failed " + ShortTellId(job.Id) + " as " +
                    Client.CharacterName + ": " + error);
            }
            finally
            {
                TellQueue.Complete(
                    _dataDir,
                    job,
                    assignmentPath,
                    Client.CharacterName,
                    success,
                    error);
            }
        }

        private void QueueTell(ReplyTarget target, string text)
        {
            string requiredSender = string.IsNullOrWhiteSpace(target.SenderName)
                ? Client.CharacterName
                : null;
            TellQueue.Enqueue(
                _dataDir,
                Client.CharacterName,
                target.SenderName,
                target.SenderId != 0 ? (uint?)target.SenderId : null,
                text,
                requiredSender);
        }

        private string QueueTell(
            string recipientName,
            uint? recipientId,
            string text,
            string requiredSender = null)
        {
            return TellQueue.Enqueue(
                _dataDir,
                Client.CharacterName,
                recipientName,
                recipientId,
                text,
                requiredSender);
        }

        private string SelectNextTellSender(List<TellSenderHeartbeat> available)
        {
            if (available == null || available.Count == 0)
                return null;

            int previous = _tellQueueSenders.FindIndex(name => string.Equals(
                name,
                _tellQueueLastSender,
                StringComparison.OrdinalIgnoreCase));
            for (int offset = 1; offset <= _tellQueueSenders.Count; offset++)
            {
                string candidate = _tellQueueSenders[(previous + offset) % _tellQueueSenders.Count];
                if (available.Any(h => string.Equals(
                        h.Character,
                        candidate,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
            return null;
        }

        private void AddTellSender(string character)
        {
            if (!string.IsNullOrWhiteSpace(character) &&
                !_tellQueueSenders.Any(name => string.Equals(
                    name,
                    character,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _tellQueueSenders.Add(character);
            }
        }

        private static JObject GetObject(JObject value, string name)
        {
            return GetToken(value, name) as JObject;
        }

        private static JToken GetToken(JObject value, string name)
        {
            return value != null
                ? value.GetValue(name, StringComparison.OrdinalIgnoreCase)
                : null;
        }

        private static string GetString(JObject value, string name)
        {
            JToken token = GetToken(value, name);
            return token != null ? (string)token : null;
        }

        private static string ShortTellId(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value.Length <= 8
                ? value
                : value.Substring(value.Length - 8);
        }
    }
}
