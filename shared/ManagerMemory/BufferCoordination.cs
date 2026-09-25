using System;
using System.Collections.Generic;
using System.Linq;

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

    public sealed partial class ManagerMemory
    {
        private const int BufferSignalLimit = 256;
        private readonly Dictionary<string, Queue<BufferMemorySignal>> _bufferSignals =
            new Dictionary<string, Queue<BufferMemorySignal>>(StringComparer.OrdinalIgnoreCase);

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

        internal void ClearBufferSignals(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync) _bufferSignals.Remove(character);
        }

    }
}
