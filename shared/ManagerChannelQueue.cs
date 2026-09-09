using System;
using System.IO;
using System.Linq;
using System.Threading;

using Newtonsoft.Json;

namespace CityDwellers.Shared
{
    public static class ManagerChannelQueue
    {
        private const string Format = "citydwellers-manager-channel-v1";
        private const string QueueDirectoryName = "manager-channel";
        private const string SequenceMutexName =
            "CityDwellers.ManagerChannelQueue.Sequence.v1";

        public static string Enqueue(
            string dataDirectory,
            string sourceCharacter,
            string message)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("A channel message is required.", nameof(message));

            string directory = GetDirectory(dataDirectory);
            Directory.CreateDirectory(directory);
            long sequence = NextSequence(directory);
            var job = new ManagerChannelJob
            {
                Format = Format,
                Id = Guid.NewGuid().ToString("N"),
                Sequence = sequence,
                CreatedUtc = DateTime.UtcNow,
                SourceCharacter = sourceCharacter,
                Message = message
            };
            string target = Path.Combine(
                directory,
                sequence.ToString("D20") + "-" + job.Id + ".json");
            string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonConvert.SerializeObject(job));
            File.Move(temporary, target);
            return job.Id;
        }

        public static bool TryReadNext(
            string dataDirectory,
            out ManagerChannelJob job,
            out string path)
        {
            string directory = GetDirectory(dataDirectory);
            Directory.CreateDirectory(directory);
            path = Directory.GetFiles(directory, "*.json")
                .OrderBy(candidate => candidate, StringComparer.Ordinal)
                .FirstOrDefault();
            job = Read(path);
            return job != null &&
                   string.Equals(job.Format, Format, StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(job.Id) &&
                   !string.IsNullOrWhiteSpace(job.Message);
        }

        public static void Complete(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }

        private static string GetDirectory(string dataDirectory)
        {
            return Path.Combine(dataDirectory, "tell-queue", QueueDirectoryName);
        }

        private static long NextSequence(string directory)
        {
            using (var mutex = new Mutex(false, SequenceMutexName))
            {
                bool entered = false;
                try
                {
                    try
                    {
                        entered = mutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        entered = true;
                    }

                    if (!entered)
                        throw new IOException(
                            "Timed out waiting for the Manager channel sequence lock.");

                    string path = Path.Combine(directory, "sequence.txt");
                    long value = 0;
                    if (File.Exists(path))
                        long.TryParse(File.ReadAllText(path).Trim(), out value);
                    value++;
                    string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(temporary, value.ToString());
                    if (File.Exists(path))
                        File.Replace(temporary, path, null);
                    else
                        File.Move(temporary, path);
                    return value;
                }
                finally
                {
                    if (entered)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static ManagerChannelJob Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<ManagerChannelJob>(
                    File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class ManagerChannelJob
    {
        public string Format;
        public string Id;
        public long Sequence;
        public DateTime CreatedUtc;
        public string SourceCharacter;
        public string Message;
    }
}
