using File = CityDwellers.Shared.SqlFile;
using Directory = CityDwellers.Shared.SqlDirectory;
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

        public static string Enqueue(
            string dataDirectory,
            string sourceCharacter,
            string message)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("A SQL data namespace is required.", nameof(dataDirectory));
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("A channel message is required.", nameof(message));

            return SqlStore.WithLock("manager-channel-enqueue", () =>
            {
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
                File.WriteAllText(target, JsonConvert.SerializeObject(job));
                return job.Id;
            });
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
            return SqlStore.WithLock("manager-channel-sequence", () =>
            {
                string path = Path.Combine(directory, "sequence.txt");
                long value = 0;
                if (File.Exists(path) && !long.TryParse(File.ReadAllText(path).Trim(),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
                    throw new InvalidDataException("Invalid Manager channel sequence in MySQL.");
                value = checked(value + 1);
                File.WriteAllText(path, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return value;
            });
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
