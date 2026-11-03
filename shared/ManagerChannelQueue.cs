using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace CityDwellers.Shared
{
    public static class ManagerChannelQueue
    {
        public static string Enqueue(string dataDirectory, string sourceCharacter, string message)
            => ManagerMemory.Current.EnqueueChannelMessage(sourceCharacter, message);
        public static string EnqueueBankActivity(string sourceCharacter, BankActivity activity)
            => ManagerMemory.Current.EnqueueChannelMessage(sourceCharacter,
                JsonConvert.SerializeObject(activity), "bank.activity", activity.CoverageKeys);
        public static bool TryReadNext(string dataDirectory, out ManagerChannelJob job, out string path)
        {
            job = ManagerMemory.Current.NextChannelMessage();
            path = job?.Id;
            return job != null;
        }
        public static void Complete(string path) => ManagerMemory.Current.CompleteChannelMessage(path);
    }

    // Display-only envelope. Serialize before crossing AppDomains; mutable item lists
    // never become shared state and this record never participates in custody commits.
    public sealed class BankActivity
    {
        public string Kind;
        public string Outcome;
        public string OperationId;
        public string[] Correlations;
        public string[] CoverageKeys;
        public DateTime Utc;
        public DateTime? StartedUtc;
        public string Player;
        public string Main;
        public string Source;
        public string Destination;
        public string Stage;
        public string Detail;
        public List<CityBankers.Shared.LedgerItem> Items;
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
        public string Kind;
        internal ManagerChannelJob Copy() => (ManagerChannelJob)MemberwiseClone();
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _channelSync = new object();
        private readonly Queue<ManagerChannelJob> _channelMessages = new Queue<ManagerChannelJob>();
        private readonly HashSet<string> _channelMessageIds = new HashSet<string>(StringComparer.Ordinal);
        private long _channelSequence;
        private readonly HashSet<string> _bankActivityKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _bankActivityKeyOrder = new Queue<string>();
        public string EnqueueChannelMessage(string source, string message, string kind = null,
            string[] coverageKeys = null)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Channel message required.");
            lock (_channelSync)
            {
                string[] keys = (coverageKeys ?? new string[0]).Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (keys.Length > 0 && keys.All(key => _bankActivityKeys.Contains(key))) return null;
                // A saved per-item receipt can race the live grouped pickup notice.
                // Project only newly reported items; never announce an overlap twice.
                if (kind == "bank.activity" && keys.Any(key => _bankActivityKeys.Contains(key)))
                {
                    BankActivity activity = JsonConvert.DeserializeObject<BankActivity>(message);
                    if (activity?.Items == null || activity.CoverageKeys == null ||
                        activity.Items.Count != activity.CoverageKeys.Length)
                        throw new InvalidOperationException("Partially covered bank activity has no item-to-receipt mapping.");
                    int[] fresh = Enumerable.Range(0, activity.CoverageKeys.Length)
                        .Where(index => !_bankActivityKeys.Contains(activity.CoverageKeys[index])).ToArray();
                    activity.Items = fresh.Select(index => activity.Items[index]).ToList();
                    activity.CoverageKeys = fresh.Select(index => activity.CoverageKeys[index]).ToArray();
                    keys = activity.CoverageKeys;
                    message = JsonConvert.SerializeObject(activity);
                }
                var job = new ManagerChannelJob { Format = "citydwellers-manager-channel-v1",
                    Id = Guid.NewGuid().ToString("N"), Sequence = checked(++_channelSequence),
                    CreatedUtc = DateTime.UtcNow, SourceCharacter = source, Message = message, Kind = kind };
                _channelMessageIds.Add(job.Id);
                _channelMessages.Enqueue(job);
                foreach (string key in keys)
                    if (_bankActivityKeys.Add(key)) _bankActivityKeyOrder.Enqueue(key);
                while (_bankActivityKeyOrder.Count > 2048)
                    _bankActivityKeys.Remove(_bankActivityKeyOrder.Dequeue());
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
