using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    // Public callers retain their dataDirectory argument during the domain API
    // conversion; queues have no directory, file representation or SQL storage.
    public static class TellQueue
    {
        public const int HeartbeatFreshSeconds = 5;
        public const int AssignmentTimeoutSeconds = 45;
        public const int SenderIntervalMilliseconds = 1200;
        private static bool _heartbeatUnavailable;
        private static ManagerMemory Manager => ManagerMemory.Current;

        public static string Enqueue(string dataDirectory, string sourceCharacter,
            string recipientName, uint? recipientId, string message, string requiredSender = null)
            => Manager.EnqueueTell(sourceCharacter, recipientName, recipientId, message, requiredSender);
        public static void EnsureDirectories(string dataDirectory) { var manager = Manager; }
        public static void WriteHeartbeat(string dataDirectory, string character, bool inPlay,
            bool busy, DateTime? lastSentUtc) => Manager.ReportTellSender(character, inPlay, busy, lastSentUtc);
        public static void DeleteHeartbeat(string dataDirectory, string character) => Manager.RemoveTellSender(character);
        public static void WriteHeartbeatBestEffort(string dataDirectory, string character,
            bool inPlay, bool busy, DateTime? lastSentUtc, Action<string> report)
        {
            try
            {
                WriteHeartbeat(dataDirectory, character, inPlay, busy, lastSentUtc);
                if (_heartbeatUnavailable) report("TELL QUEUE sender publication recovered for " + character + ".");
                _heartbeatUnavailable = false;
            }
            catch (Exception ex)
            {
                if (!_heartbeatUnavailable) report("TELL QUEUE sender publication unavailable for " + character + ": " + ex.GetType().Name);
                _heartbeatUnavailable = true;
            }
        }
        public static bool IsBacklogged(string dataDirectory, int limit = 256) => Manager.TellsBacklogged(limit);
        public static List<TellQueueJob> ReadPending(string dataDirectory) => Manager.PendingTells();
        public static List<TellSenderHeartbeat> ReadFreshSenders(string dataDirectory, DateTime nowUtc) => Manager.AvailableTellSenders(nowUtc);
        public static bool HasAssignment(string dataDirectory) => Manager.HasTellAssignment();
        public static bool TryAssign(string dataDirectory, TellQueueJob job, string sender, DateTime nowUtc)
        {
            TellQueueJob assigned = job == null ? null : Manager.AssignTell(job.Id, sender, nowUtc);
            if (assigned == null) return false;
            job.AssignedSender = assigned.AssignedSender;
            job.AssignedUtc = assigned.AssignedUtc;
            job.Attempts = assigned.Attempts;
            return true;
        }
        public static bool TryRelinquishAssignment(string dataDirectory, string sender, out TellQueueJob job)
        { job = Manager.RelinquishTell(sender); return job != null; }
        public static bool TryReadAssignment(string dataDirectory, string sender, out TellQueueJob job, out string assignmentPath)
        {
            job = Manager.AssignedTell(sender);
            assignmentPath = job?.Id; // Opaque assignment identity; never a path.
            return job != null;
        }
        public static void Complete(string dataDirectory, TellQueueJob job, string assignmentPath,
            string sender, bool success, string error) => Manager.CompleteTell(job, sender, success, error);
        public static bool TryReadAcknowledgement(string dataDirectory, string jobId, out TellDeliveryAcknowledgement acknowledgement)
        { acknowledgement = Manager.TellAcknowledgement(jobId); return acknowledgement != null; }
        public static int RequeueStaleAssignments(string dataDirectory, DateTime nowUtc) => Manager.RequeueStaleTell(nowUtc);
    }

    public sealed partial class ManagerMemory
    {
        private const string TellFormat = "citydwellers-tell-queue-v1";
        private readonly object _tellSync = new object();
        private readonly Dictionary<string, TellQueueJob> _pendingTells = new Dictionary<string, TellQueueJob>(StringComparer.Ordinal);
        private readonly Dictionary<string, TellSenderHeartbeat> _tellSenders = new Dictionary<string, TellSenderHeartbeat>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TellDeliveryAcknowledgement> _tellAcknowledgements = new Dictionary<string, TellDeliveryAcknowledgement>(StringComparer.Ordinal);
        private readonly Queue<string> _acknowledgementOrder = new Queue<string>();
        private TellQueueJob _assignedTell;
        private long _tellSequence;

        public void ImportPendingTells(TellQueueJob[] jobs)
        {
            lock (_tellSync)
            {
                foreach (var old in jobs)
                {
                    if (old == null || old.Format != TellFormat || string.IsNullOrWhiteSpace(old.Id) ||
                        string.IsNullOrWhiteSpace(old.Message) ||
                        (string.IsNullOrWhiteSpace(old.RecipientName) &&
                         (!old.RecipientId.HasValue || old.RecipientId.Value == 0)))
                        throw new InvalidOperationException("Legacy pending tell is invalid.");
                }
                foreach (var old in jobs.OrderBy(x => x.Sequence))
                {
                    if (_pendingTells.ContainsKey(old.Id) || _assignedTell?.Id == old.Id) continue;
                    var job = old.Copy();
                    job.AssignedSender = null;
                    job.AssignedUtc = null;
                    _pendingTells.Add(job.Id, job);
                    _tellSequence = Math.Max(_tellSequence, job.Sequence);
                }
            }
        }
        public string EnqueueTell(string source, string recipient, uint? recipientId, string message, string requiredSender)
        {
            if (string.IsNullOrWhiteSpace(recipient) && (!recipientId.HasValue || recipientId.Value == 0))
                throw new ArgumentException("Tell recipient required.");
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Tell message required.");
            lock (_tellSync)
            {
                var job = new TellQueueJob { Format = TellFormat, Id = Guid.NewGuid().ToString("N"),
                    Sequence = checked(++_tellSequence), CreatedUtc = DateTime.UtcNow,
                    SourceCharacter = source, RecipientName = recipient, RecipientId = recipientId,
                    Message = message, RequiredSender = requiredSender };
                _pendingTells.Add(job.Id, job);
                return job.Id;
            }
        }
        public void ReportTellSender(string character, bool inPlay, bool busy, DateTime? lastSentUtc)
        {
            if (string.IsNullOrWhiteSpace(character)) throw new ArgumentException("Sender character required.");
            lock (_tellSync) _tellSenders[character] = new TellSenderHeartbeat { Format = TellFormat,
                Character = character, InPlay = inPlay, Busy = busy, UpdatedUtc = DateTime.UtcNow, LastSentUtc = lastSentUtc };
        }
        public void RemoveTellSender(string character) { lock (_tellSync) _tellSenders.Remove(character); }
        public bool TellsBacklogged(int limit) { lock (_tellSync) return _pendingTells.Count >= limit; }
        public List<TellQueueJob> PendingTells()
        { lock (_tellSync) return _pendingTells.Values.OrderBy(x => x.Sequence).Select(x => x.Copy()).ToList(); }
        public List<TellSenderHeartbeat> AvailableTellSenders(DateTime now)
        {
            lock (_tellSync) return _tellSenders.Values.Where(x => x.InPlay && !x.Busy &&
                x.UpdatedUtc >= now.AddSeconds(-TellQueue.HeartbeatFreshSeconds)).Select(x => x.Copy()).ToList();
        }
        public bool HasTellAssignment() { lock (_tellSync) return _assignedTell != null; }
        public TellQueueJob AssignTell(string id, string sender, DateTime now)
        {
            lock (_tellSync)
            {
                TellQueueJob job;
                TellSenderHeartbeat available;
                if (_assignedTell != null || string.IsNullOrWhiteSpace(sender) || !_pendingTells.TryGetValue(id, out job) ||
                    (!string.IsNullOrWhiteSpace(job.RequiredSender) && !string.Equals(job.RequiredSender, sender, StringComparison.OrdinalIgnoreCase)) ||
                    !_tellSenders.TryGetValue(sender, out available) || !available.InPlay || available.Busy ||
                    available.UpdatedUtc < now.AddSeconds(-TellQueue.HeartbeatFreshSeconds)) return null;
                job.AssignedSender = sender;
                job.AssignedUtc = now;
                job.Attempts++;
                _pendingTells.Remove(id);
                _assignedTell = job;
                return job.Copy();
            }
        }
        public TellQueueJob AssignedTell(string sender)
        { lock (_tellSync) return string.Equals(_assignedTell?.AssignedSender, sender, StringComparison.OrdinalIgnoreCase) ? _assignedTell?.Copy() : null; }
        public TellQueueJob RelinquishTell(string sender)
        {
            lock (_tellSync)
            {
                if (_assignedTell == null || !string.Equals(_assignedTell.AssignedSender, sender, StringComparison.OrdinalIgnoreCase)) return null;
                var job = _assignedTell;
                job.Attempts = Math.Max(0, job.Attempts - 1);
                RequeueTell();
                return job.Copy();
            }
        }
        private void RequeueTell()
        {
            var job = _assignedTell;
            job.AssignedSender = null;
            job.AssignedUtc = null;
            _pendingTells.Add(job.Id, job);
            _assignedTell = null;
        }
        public void CompleteTell(TellQueueJob attempt, string sender, bool success, string error)
        {
            lock (_tellSync)
            {
                var active = _assignedTell;
                if (attempt == null || active == null || active.Id != attempt.Id ||
                    !string.Equals(active.AssignedSender, sender, StringComparison.OrdinalIgnoreCase) ||
                    active.AssignedUtc != attempt.AssignedUtc || active.Attempts != attempt.Attempts) return;
                if (!success && active.Attempts < 5) { active.LastError = error; RequeueTell(); return; }
                _tellAcknowledgements[active.Id] = new TellDeliveryAcknowledgement { Format = TellFormat,
                    JobId = active.Id, SenderCharacter = sender, RecipientName = active.RecipientName,
                    SentUtc = DateTime.UtcNow, Success = success, Error = error };
                _acknowledgementOrder.Enqueue(active.Id);
                // Completed tells are transient receipt acknowledgements, not history.
                // Bound their lifetime by count without any background scan or writer.
                while (_acknowledgementOrder.Count > 4096) _tellAcknowledgements.Remove(_acknowledgementOrder.Dequeue());
                _assignedTell = null;
            }
        }
        public TellDeliveryAcknowledgement TellAcknowledgement(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_tellSync) { TellDeliveryAcknowledgement ack; return _tellAcknowledgements.TryGetValue(id, out ack) ? ack.Copy() : null; }
        }
        public int RequeueStaleTell(DateTime now)
        {
            lock (_tellSync)
            {
                if (_assignedTell == null || _assignedTell.AssignedUtc > now.AddSeconds(-TellQueue.AssignmentTimeoutSeconds)) return 0;
                _assignedTell.LastError = "Sender did not acknowledge the assignment before timeout.";
                RequeueTell();
                return 1;
            }
        }
    }

    [Serializable]
    public sealed class TellQueueJob
    {
        internal TellQueueJob Copy() => (TellQueueJob)MemberwiseClone();
        public string Format { get; set; }
        public string Id { get; set; }
        public long Sequence { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string SourceCharacter { get; set; }
        public string RecipientName { get; set; }
        public uint? RecipientId { get; set; }
        public string Message { get; set; }
        public string RequiredSender { get; set; }
        public int Attempts { get; set; }
        public string LastError { get; set; }
        public string AssignedSender { get; set; }
        public DateTime? AssignedUtc { get; set; }
    }

    [Serializable]
    public sealed class TellSenderHeartbeat
    {
        internal TellSenderHeartbeat Copy() => (TellSenderHeartbeat)MemberwiseClone();
        public string Format { get; set; }
        public string Character { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public bool InPlay { get; set; }
        public bool Busy { get; set; }
        public DateTime? LastSentUtc { get; set; }
    }

    [Serializable]
    public sealed class TellDeliveryAcknowledgement
    {
        internal TellDeliveryAcknowledgement Copy() => (TellDeliveryAcknowledgement)MemberwiseClone();
        public string Format { get; set; }
        public string JobId { get; set; }
        public string SenderCharacter { get; set; }
        public string RecipientName { get; set; }
        public DateTime SentUtc { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
