using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BufferAdvertisedBuff
    {
        public int Profession;
        public string Name;
        public string Description;
        public string Tag;
        public string Type;
        public int Ncu;

        internal BufferAdvertisedBuff Copy() => (BufferAdvertisedBuff)MemberwiseClone();
    }

    [Serializable]
    public sealed class BufferBotInfo
    {
        public string Character;
        public int Profession;
        public int IdentityType;
        public int IdentityInstance;
        public int[] SpellData;
        public DateTime ObservedUtc;
        public bool InPlay;
        public bool Ready;
        public string QueueJson;
        public DateTime QueueObservedUtc;
        public BufferAdvertisedBuff[] AdvertisedBuffs;

        internal BufferBotInfo Copy() => new BufferBotInfo
        {
            Character = Character,
            Profession = Profession,
            IdentityType = IdentityType,
            IdentityInstance = IdentityInstance,
            SpellData = SpellData == null ? new int[0] : SpellData.ToArray(),
            ObservedUtc = ObservedUtc,
            InPlay = InPlay,
            Ready = Ready,
            QueueJson = QueueJson,
            QueueObservedUtc = QueueObservedUtc,
            AdvertisedBuffs = AdvertisedBuffs == null
                ? null
                : AdvertisedBuffs.Select(x => x?.Copy()).Where(x => x != null).ToArray()
        };
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _bufferBotSync = new object();
        private readonly Dictionary<string, BufferBotInfo> _bufferBots =
            new Dictionary<string, BufferBotInfo>(StringComparer.OrdinalIgnoreCase);

        public void PublishBufferBotInfo(BufferBotInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Character))
                throw new ArgumentException("Buffer character required.");
            lock (_bufferBotSync)
            {
                BufferBotInfo existing;
                string queueJson = null;
                DateTime queueObservedUtc = default(DateTime);
                if (_bufferBots.TryGetValue(info.Character, out existing))
                {
                    queueJson = existing.QueueJson;
                    queueObservedUtc = existing.QueueObservedUtc;
                }
                BufferBotInfo copy = info.Copy();
                copy.QueueJson = queueJson;
                copy.QueueObservedUtc = queueObservedUtc;
                if (copy.AdvertisedBuffs == null && existing != null)
                    copy.AdvertisedBuffs = existing.AdvertisedBuffs == null
                        ? null
                        : existing.AdvertisedBuffs.Select(x => x?.Copy()).Where(x => x != null).ToArray();
                _bufferBots[info.Character] = copy;
            }
        }

        public void MarkBufferBotOffline(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync)
            {
                BufferBotInfo existing;
                if (_bufferBots.TryGetValue(character, out existing))
                {
                    BufferBotInfo copy = existing.Copy();
                    copy.InPlay = false;
                    copy.Ready = false;
                    copy.ObservedUtc = DateTime.UtcNow;
                    _bufferBots[character] = copy;
                }
            }
            ClearBufferSignals(character);
        }

        public void ClearBufferBotInfo(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync) _bufferBots.Remove(character);
            ClearBufferSignals(character);
        }

        public List<BufferBotInfo> BufferBotInfos()
        {
            lock (_bufferBotSync) return _bufferBots.Values.Select(x => x.Copy()).ToList();
        }
    }
}
