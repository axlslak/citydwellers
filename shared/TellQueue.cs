using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

using Newtonsoft.Json;

namespace CityDwellers.Shared
{
    public static class TellQueue
    {
        public const int HeartbeatFreshSeconds = 5;
        public const int AssignmentTimeoutSeconds = 45;
        public const int SenderIntervalMilliseconds = 1200;

        private const string Format = "citydwellers-tell-queue-v1";
        private const string RootName = "tell-queue";
        private const string PendingName = "pending";
        private const string AssignedName = "assigned";
        private const string AcknowledgementsName = "acknowledgements";
        private const string SendersName = "senders";
        private const string FailedName = "failed";
        private const string AssignmentMutexName = "Local\\CityDwellersTellQueueAssignmentsV1";

        public static string Enqueue(
            string dataDirectory,
            string sourceCharacter,
            string recipientName,
            uint? recipientId,
            string message,
            string requiredSender = null)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
            if (string.IsNullOrWhiteSpace(recipientName) && !recipientId.HasValue)
                throw new ArgumentException("A tell recipient is required.", nameof(recipientName));
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("A tell message is required.", nameof(message));

            EnsureDirectories(dataDirectory);
            long sequence = NextSequence(dataDirectory);
            var job = new TellQueueJob
            {
                Format = Format,
                Id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" +
                     Guid.NewGuid().ToString("N"),
                Sequence = sequence,
                CreatedUtc = DateTime.UtcNow,
                SourceCharacter = sourceCharacter,
                RecipientName = recipientName,
                RecipientId = recipientId,
                Message = message,
                RequiredSender = requiredSender
            };

            AtomicWrite(
                Path.Combine(PendingDirectory(dataDirectory), job.Id + ".json"),
                job);
            return job.Id;
        }

        public static void EnsureDirectories(string dataDirectory)
        {
            Directory.CreateDirectory(PendingDirectory(dataDirectory));
            Directory.CreateDirectory(AssignedDirectory(dataDirectory));
            Directory.CreateDirectory(AcknowledgementsDirectory(dataDirectory));
            Directory.CreateDirectory(SendersDirectory(dataDirectory));
            Directory.CreateDirectory(FailedDirectory(dataDirectory));
        }

        public static void WriteHeartbeat(
            string dataDirectory,
            string character,
            bool inPlay,
            bool busy,
            DateTime? lastSentUtc)
        {
            EnsureDirectories(dataDirectory);
            AtomicWrite(
                HeartbeatPath(dataDirectory, character),
                new TellSenderHeartbeat
                {
                    Format = Format,
                    Character = character,
                    UpdatedUtc = DateTime.UtcNow,
                    InPlay = inPlay,
                    Busy = busy,
                    LastSentUtc = lastSentUtc
                });
        }

        public static void DeleteHeartbeat(string dataDirectory, string character)
        {
            DeleteIfExists(HeartbeatPath(dataDirectory, character));
        }

        public static List<TellQueueJob> ReadPending(string dataDirectory)
        {
            EnsureDirectories(dataDirectory);
            return ReadFiles<TellQueueJob>(PendingDirectory(dataDirectory))
                .Where(IsValidJob)
                .OrderBy(j => j.Sequence)
                .ThenBy(j => j.CreatedUtc)
                .ThenBy(j => j.Id, StringComparer.Ordinal)
                .ToList();
        }

        public static List<TellSenderHeartbeat> ReadFreshSenders(
            string dataDirectory,
            DateTime nowUtc)
        {
            EnsureDirectories(dataDirectory);
            return ReadFiles<TellSenderHeartbeat>(SendersDirectory(dataDirectory))
                .Where(h => h != null &&
                            string.Equals(h.Format, Format, StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(h.Character) &&
                            h.InPlay &&
                            !h.Busy &&
                            h.UpdatedUtc >= nowUtc.AddSeconds(-HeartbeatFreshSeconds))
                .ToList();
        }

        public static bool HasAssignment(string dataDirectory)
        {
            EnsureDirectories(dataDirectory);
            return Directory.GetFiles(AssignedDirectory(dataDirectory), "*.json").Length != 0;
        }

        public static bool TryAssign(
            string dataDirectory,
            TellQueueJob job,
            string sender,
            DateTime nowUtc)
        {
            if (!IsValidJob(job) || string.IsNullOrWhiteSpace(sender))
                return false;

            string pendingPath = Path.Combine(PendingDirectory(dataDirectory), job.Id + ".json");
            string assignedPath = AssignmentPath(dataDirectory, sender, job.Id);
            job.AssignedSender = sender;
            job.AssignedUtc = nowUtc;
            job.Attempts++;

            return WithAssignmentLock(() =>
            {
                try
                {
                    AtomicWrite(pendingPath, job);
                    File.Move(pendingPath, assignedPath);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
            });
        }

        public static bool TryRelinquishAssignment(
            string dataDirectory,
            string sender,
            out TellQueueJob relinquishedJob)
        {
            TellQueueJob result = null;
            bool relinquished = WithAssignmentLock(() =>
            {
                TellQueueJob job;
                string assignmentPath;
                if (!TryReadAssignment(dataDirectory, sender, out job, out assignmentPath))
                    return false;

                job.AssignedSender = null;
                job.AssignedUtc = null;
                if (job.Attempts > 0)
                    job.Attempts--;

                try
                {
                    string pendingPath =
                        Path.Combine(PendingDirectory(dataDirectory), job.Id + ".json");
                    File.Move(assignmentPath, pendingPath);
                    AtomicWrite(pendingPath, job);
                    result = job;
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            });
            relinquishedJob = result;
            return relinquished;
        }

        public static bool TryReadAssignment(
            string dataDirectory,
            string sender,
            out TellQueueJob job,
            out string assignmentPath)
        {
            EnsureDirectories(dataDirectory);
            assignmentPath = Directory
                .GetFiles(AssignedDirectory(dataDirectory), SafeToken(sender) + "--*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            job = Read<TellQueueJob>(assignmentPath);
            return IsValidJob(job);
        }

        public static void Complete(
            string dataDirectory,
            TellQueueJob job,
            string assignmentPath,
            string sender,
            bool success,
            string error)
        {
            if (!success && job != null && job.Attempts < 5)
            {
                job.LastError = error;
                job.AssignedSender = null;
                job.AssignedUtc = null;
                try
                {
                    AtomicWrite(
                        Path.Combine(PendingDirectory(dataDirectory), job.Id + ".json"),
                        job);
                    DeleteIfExists(assignmentPath);
                }
                catch (IOException)
                {
                }
                return;
            }

            var acknowledgement = new TellDeliveryAcknowledgement
            {
                Format = Format,
                JobId = job != null ? job.Id : null,
                SenderCharacter = sender,
                RecipientName = job != null ? job.RecipientName : null,
                SentUtc = DateTime.UtcNow,
                Success = success,
                Error = error
            };

            if (job != null && !string.IsNullOrWhiteSpace(job.Id))
            {
                if (!success)
                {
                    AtomicWrite(
                        Path.Combine(FailedDirectory(dataDirectory), job.Id + ".json"),
                        job);
                }
                AtomicWrite(
                    Path.Combine(AcknowledgementsDirectory(dataDirectory), job.Id + ".json"),
                    acknowledgement);
            }

            DeleteIfExists(assignmentPath);
        }

        public static bool TryReadAcknowledgement(
            string dataDirectory,
            string jobId,
            out TellDeliveryAcknowledgement acknowledgement)
        {
            acknowledgement = Read<TellDeliveryAcknowledgement>(
                Path.Combine(AcknowledgementsDirectory(dataDirectory), jobId + ".json"));
            return acknowledgement != null &&
                   string.Equals(acknowledgement.Format, Format, StringComparison.Ordinal) &&
                   string.Equals(acknowledgement.JobId, jobId, StringComparison.Ordinal);
        }

        public static int RequeueStaleAssignments(string dataDirectory, DateTime nowUtc)
        {
            EnsureDirectories(dataDirectory);
            int count = 0;
            foreach (string path in Directory.GetFiles(AssignedDirectory(dataDirectory), "*.json"))
            {
                TellQueueJob job = Read<TellQueueJob>(path);
                if (!IsValidJob(job) || !job.AssignedUtc.HasValue ||
                    job.AssignedUtc.Value > nowUtc.AddSeconds(-AssignmentTimeoutSeconds))
                {
                    continue;
                }

                job.LastError = "Sender did not acknowledge the assignment before timeout.";
                job.AssignedSender = null;
                job.AssignedUtc = null;
                try
                {
                    string pendingPath = Path.Combine(PendingDirectory(dataDirectory), job.Id + ".json");
                    AtomicWrite(pendingPath, job);
                    File.Delete(path);
                    count++;
                }
                catch (IOException)
                {
                }
            }
            return count;
        }

        private static bool IsValidJob(TellQueueJob job)
        {
            return job != null &&
                   string.Equals(job.Format, Format, StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(job.Id) &&
                   (!string.IsNullOrWhiteSpace(job.RecipientName) || job.RecipientId.HasValue) &&
                   !string.IsNullOrWhiteSpace(job.Message);
        }

        private static IEnumerable<T> ReadFiles<T>(string directory)
        {
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            {
                T value = Read<T>(path);
                if (value != null)
                    yield return value;
            }
        }

        private static T Read<T>(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return default(T);
            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch
            {
                return default(T);
            }
        }

        private static void AtomicWrite(string path, object value)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(path);
                        File.Move(temp, path);
                    }
                    catch (IOException)
                    {
                        File.Delete(path);
                        File.Move(temp, path);
                    }
                }
                else
                    File.Move(temp, path);
            }
            finally
            {
                DeleteIfExists(temp);
            }
        }

        private static long NextSequence(string dataDirectory)
        {
            string path = Path.Combine(Root(dataDirectory), "sequence.txt");
            using (var mutex = new Mutex(false, "Local\\CityDwellersTellQueueV1"))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                        throw new TimeoutException("Timed out acquiring the tell queue sequence.");

                    long sequence = 0;
                    if (File.Exists(path))
                    {
                        long.TryParse(
                            File.ReadAllText(path).Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out sequence);
                    }

                    sequence++;
                    string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(temp, sequence.ToString(CultureInfo.InvariantCulture));
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                        File.Move(temp, path);
                    }
                    finally
                    {
                        DeleteIfExists(temp);
                    }
                    return sequence;
                }
                finally
                {
                    if (acquired)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static T WithAssignmentLock<T>(Func<T> action)
        {
            using (var mutex = new Mutex(false, AssignmentMutexName))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                        return default(T);
                    return action();
                }
                finally
                {
                    if (acquired)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }

        private static string Root(string dataDirectory) => Path.Combine(dataDirectory, RootName);
        private static string PendingDirectory(string dataDirectory) => Path.Combine(Root(dataDirectory), PendingName);
        private static string AssignedDirectory(string dataDirectory) => Path.Combine(Root(dataDirectory), AssignedName);
        private static string AcknowledgementsDirectory(string dataDirectory) => Path.Combine(Root(dataDirectory), AcknowledgementsName);
        private static string SendersDirectory(string dataDirectory) => Path.Combine(Root(dataDirectory), SendersName);
        private static string FailedDirectory(string dataDirectory) => Path.Combine(Root(dataDirectory), FailedName);
        private static string HeartbeatPath(string dataDirectory, string character) => Path.Combine(SendersDirectory(dataDirectory), SafeToken(character) + ".json");
        private static string AssignmentPath(string dataDirectory, string sender, string id) => Path.Combine(AssignedDirectory(dataDirectory), SafeToken(sender) + "--" + id + ".json");

        private static string SafeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value.Replace(' ', '_');
        }
    }

    public sealed class TellQueueJob
    {
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

    public sealed class TellSenderHeartbeat
    {
        public string Format { get; set; }
        public string Character { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public bool InPlay { get; set; }
        public bool Busy { get; set; }
        public DateTime? LastSentUtc { get; set; }
    }

    public sealed class TellDeliveryAcknowledgement
    {
        public string Format { get; set; }
        public string JobId { get; set; }
        public string SenderCharacter { get; set; }
        public string RecipientName { get; set; }
        public DateTime SentUtc { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
