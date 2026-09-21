using System;
using System.Collections.Generic;

namespace CityDwellers.Shared
{
    public static class ManagerChannelQueue
    {
        public static string Enqueue(string dataDirectory, string sourceCharacter, string message)
            => ManagerMemory.Current.EnqueueChannelMessage(sourceCharacter, message);
        public static bool TryReadNext(string dataDirectory, out ManagerChannelJob job, out string path)
        {
            job = ManagerMemory.Current.NextChannelMessage();
            path = job?.Id;
            return job != null;
        }
        public static void Complete(string path) => ManagerMemory.Current.CompleteChannelMessage(path);
    }

    [Serializable]
    public sealed class ManagerChannelJob
    {
        public string Format;
        public string Id;
        public long Sequence;
        public DateTime CreatedUtc;
        public string SourceCharacter;
        public string Message;
        internal ManagerChannelJob Copy() => (ManagerChannelJob)MemberwiseClone();
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _channelSync = new object();
        private readonly Queue<ManagerChannelJob> _channelMessages = new Queue<ManagerChannelJob>();
        private readonly HashSet<string> _channelMessageIds = new HashSet<string>(StringComparer.Ordinal);
        private long _channelSequence;
        public void ImportChannelMessages(ManagerChannelJob[] jobs)
        {
            lock (_channelSync)
            {
                foreach (var job in jobs)
                    if (job == null || string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.Message))
                        throw new InvalidOperationException("Legacy Manager channel message is invalid.");
                foreach (var job in jobs)
                {
                    if (!_channelMessageIds.Add(job.Id)) continue;
                    _channelMessages.Enqueue(job.Copy());
                    _channelSequence = Math.Max(_channelSequence, job.Sequence);
                }
            }
        }
        public string EnqueueChannelMessage(string source, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Channel message required.");
            lock (_channelSync)
            {
                var job = new ManagerChannelJob { Format = "citydwellers-manager-channel-v1",
                    Id = Guid.NewGuid().ToString("N"), Sequence = checked(++_channelSequence),
                    CreatedUtc = DateTime.UtcNow, SourceCharacter = source, Message = message };
                _channelMessageIds.Add(job.Id);
                _channelMessages.Enqueue(job);
                return job.Id;
            }
        }
        public ManagerChannelJob NextChannelMessage()
        { lock (_channelSync) return _channelMessages.Count == 0 ? null : _channelMessages.Peek().Copy(); }
        public void CompleteChannelMessage(string id)
        {
            lock (_channelSync)
                if (_channelMessages.Count != 0 && _channelMessages.Peek().Id == id)
                {
                    _channelMessages.Dequeue();
                    _channelMessageIds.Remove(id);
                }
        }
    }
}
