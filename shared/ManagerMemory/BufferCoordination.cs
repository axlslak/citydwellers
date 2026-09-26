using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BufferMemorySignal
    {
        public string Id;
        public string Kind;
        public string Payload;
        public DateTime CreatedUtc;

        internal BufferMemorySignal Copy() => (BufferMemorySignal)MemberwiseClone();
    }

    [Serializable]
    public sealed class BufferPublicCommandRequest
    {
        public string Id;
        public string Command;
        public uint SenderId;
        public string SenderName;
        public string[] Arguments;
        public DateTime CreatedUtc;
        public string ClaimedBy;
        public DateTime? ClaimedUtc;

        internal BufferPublicCommandRequest Copy()
        {
            var copy = (BufferPublicCommandRequest)MemberwiseClone();
            copy.Arguments = Arguments == null ? null : (string[])Arguments.Clone();
            return copy;
        }
    }

    [Serializable]
    public sealed class BufferPublicCommandOutcome
    {
        public string Id;
        public bool Success;
        public string Message;
        public string[] Tags;
        public string ClaimedBy;
        public DateTime CompletedUtc;

        internal BufferPublicCommandOutcome Copy()
        {
            var copy = (BufferPublicCommandOutcome)MemberwiseClone();
            copy.Tags = Tags == null ? null : (string[])Tags.Clone();
            return copy;
        }
    }

    public sealed partial class ManagerMemory
    {
        private const int BufferSignalLimit = 256;
        private const int BufferPublicCommandLimit = 128;
        private readonly Dictionary<string, Queue<BufferMemorySignal>> _bufferSignals =
            new Dictionary<string, Queue<BufferMemorySignal>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _bufferPublicCommandSync = new object();
        private readonly Dictionary<string, BufferPublicCommandRequest> _bufferPublicCommands =
            new Dictionary<string, BufferPublicCommandRequest>(StringComparer.Ordinal);
        private readonly Dictionary<string, BufferPublicCommandOutcome> _bufferPublicOutcomes =
            new Dictionary<string, BufferPublicCommandOutcome>(StringComparer.Ordinal);

        public void PublishBufferQueue(string character, int profession, string queueJson)
        {
            if (string.IsNullOrWhiteSpace(character))
                throw new ArgumentException("Buffer character required.");
            lock (_bufferBotSync)
            {
                BufferBotInfo info;
                if (!_bufferBots.TryGetValue(character, out info))
                {
                    info = new BufferBotInfo { Character = character, Profession = profession };
                    _bufferBots.Add(character, info);
                }
                info.Profession = profession;
                info.QueueJson = queueJson ?? "[]";
                info.QueueObservedUtc = DateTime.UtcNow;
            }
        }

        public bool EnqueueBufferSignalForProfession(int profession, string kind, string payload)
        {
            if (string.IsNullOrWhiteSpace(kind)) return false;
            string character = null;
            lock (_bufferBotSync)
            {
                DateTime freshAfter = DateTime.UtcNow.AddSeconds(-5);
                BufferBotInfo target = _bufferBots.Values
                    .Where(x => x.Profession == profession && x.InPlay && x.Ready &&
                                x.ObservedUtc >= freshAfter)
                    .OrderBy(x => x.Character, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                character = target?.Character;
                if (character == null) return false;

                Queue<BufferMemorySignal> queue;
                if (!_bufferSignals.TryGetValue(character, out queue))
                    _bufferSignals.Add(character, queue = new Queue<BufferMemorySignal>());
                if (queue.Count >= BufferSignalLimit) return false;
                queue.Enqueue(new BufferMemorySignal
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = kind,
                    Payload = payload,
                    CreatedUtc = DateTime.UtcNow
                });
                return true;
            }
        }

        public List<BufferMemorySignal> TakeBufferSignals(string character, int maximum)
        {
            var result = new List<BufferMemorySignal>();
            if (string.IsNullOrWhiteSpace(character) || maximum <= 0) return result;
            lock (_bufferBotSync)
            {
                Queue<BufferMemorySignal> queue;
                if (!_bufferSignals.TryGetValue(character, out queue)) return result;
                while (result.Count < maximum && queue.Count > 0)
                    result.Add(queue.Dequeue().Copy());
                if (queue.Count == 0) _bufferSignals.Remove(character);
            }
            return result;
        }

        public bool BeginBufferPublicCommand(BufferPublicCommandRequest request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Id) ||
                string.IsNullOrWhiteSpace(request.Command) ||
                request.SenderId == 0)
                return false;

            lock (_bufferPublicCommandSync)
            {
                PruneBufferPublicCommands(DateTime.UtcNow);
                if (_bufferPublicCommands.Count >= BufferPublicCommandLimit ||
                    _bufferPublicCommands.ContainsKey(request.Id) ||
                    _bufferPublicOutcomes.ContainsKey(request.Id))
                    return false;

                BufferPublicCommandRequest copy = request.Copy();
                if (copy.CreatedUtc == default(DateTime))
                    copy.CreatedUtc = DateTime.UtcNow;
                copy.ClaimedBy = null;
                copy.ClaimedUtc = null;
                _bufferPublicCommands.Add(copy.Id, copy);
                Monitor.PulseAll(_bufferPublicCommandSync);
                return true;
            }
        }

        public List<BufferPublicCommandRequest> PendingBufferPublicCommands(int maximum)
        {
            var result = new List<BufferPublicCommandRequest>();
            if (maximum <= 0) return result;

            lock (_bufferPublicCommandSync)
            {
                DateTime now = DateTime.UtcNow;
                PruneBufferPublicCommands(now);
                foreach (BufferPublicCommandRequest request in _bufferPublicCommands.Values
                    .Where(item => string.IsNullOrWhiteSpace(item.ClaimedBy))
                    .OrderBy(item => item.CreatedUtc)
                    .Take(maximum))
                {
                    result.Add(request.Copy());
                }
            }

            return result;
        }

        public bool TryClaimBufferPublicCommand(string id, string character)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(character))
                return false;

            lock (_bufferPublicCommandSync)
            {
                PruneBufferPublicCommands(DateTime.UtcNow);
                BufferPublicCommandRequest request;
                if (!_bufferPublicCommands.TryGetValue(id, out request) ||
                    !string.IsNullOrWhiteSpace(request.ClaimedBy))
                    return false;

                request.ClaimedBy = character;
                request.ClaimedUtc = DateTime.UtcNow;
                return true;
            }
        }

        public void CompleteBufferPublicCommand(BufferPublicCommandOutcome outcome)
        {
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.Id))
                return;

            lock (_bufferPublicCommandSync)
            {
                BufferPublicCommandRequest request;
                if (!_bufferPublicCommands.TryGetValue(outcome.Id, out request) ||
                    string.IsNullOrWhiteSpace(request.ClaimedBy))
                    return;

                if (!string.IsNullOrWhiteSpace(outcome.ClaimedBy) &&
                    !string.Equals(
                        outcome.ClaimedBy,
                        request.ClaimedBy,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                BufferPublicCommandOutcome copy = outcome.Copy();
                copy.ClaimedBy = request.ClaimedBy;
                if (copy.CompletedUtc == default(DateTime))
                    copy.CompletedUtc = DateTime.UtcNow;
                _bufferPublicCommands.Remove(outcome.Id);
                _bufferPublicOutcomes[outcome.Id] = copy;
                Monitor.PulseAll(_bufferPublicCommandSync);
            }
        }

        public BufferPublicCommandOutcome WaitForBufferPublicCommandOutcome(
            string id,
            int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            int timeout = Math.Max(0, timeoutMilliseconds);
            var elapsed = Stopwatch.StartNew();

            lock (_bufferPublicCommandSync)
            {
                BufferPublicCommandOutcome outcome;
                while (!_bufferPublicOutcomes.TryGetValue(id, out outcome))
                {
                    int remaining = timeout -
                        (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds);
                    if (remaining <= 0)
                    {
                        _bufferPublicCommands.Remove(id);
                        return null;
                    }
                    Monitor.Wait(_bufferPublicCommandSync, remaining);
                }

                return outcome.Copy();
            }
        }

        public void FinishBufferPublicCommand(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            lock (_bufferPublicCommandSync)
            {
                _bufferPublicCommands.Remove(id);
                _bufferPublicOutcomes.Remove(id);
            }
        }

        private void PruneBufferPublicCommands(DateTime now)
        {
            DateTime requestCutoff = now.AddSeconds(-15);
            DateTime outcomeCutoff = now.AddMinutes(-1);

            foreach (string id in _bufferPublicCommands
                .Where(pair => pair.Value.CreatedUtc < requestCutoff)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _bufferPublicCommands.Remove(id);
            }

            foreach (string id in _bufferPublicOutcomes
                .Where(pair => pair.Value.CompletedUtc < outcomeCutoff)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _bufferPublicOutcomes.Remove(id);
            }
        }

        internal void ClearBufferSignals(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync) _bufferSignals.Remove(character);
        }

    }
}
