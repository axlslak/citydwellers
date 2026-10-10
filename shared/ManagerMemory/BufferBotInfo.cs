using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BufferAdvertisedBuff
    {
        public int Profession;
        public bool IsGeneric;
        public string Name;
        public string Description;
        public string Tag;
        public string Type;
        public int Ncu;
        public int[] NanoIds = new int[0];

        internal BufferAdvertisedBuff Copy()
        {
            var copy = (BufferAdvertisedBuff)MemberwiseClone();
            copy.NanoIds = (NanoIds ?? new int[0]).ToArray();
            return copy;
        }
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
        public int QueueLength;
        public int TeamMemberId;
        public int TeamTrackerId;
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
            QueueLength = QueueLength,
            TeamMemberId = TeamMemberId,
            TeamTrackerId = TeamTrackerId,
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
                if (existing != null)
                {
                    copy.TeamMemberId = existing.TeamMemberId;
                    copy.TeamTrackerId = existing.TeamTrackerId;
                }
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
                    copy.QueueLength = 0;
                    copy.TeamMemberId = 0;
                    copy.TeamTrackerId = 0;
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

        public void PublishBufferTeamMember(string character, int memberId)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync)
            {
                BufferBotInfo info;
                if (_bufferBots.TryGetValue(character, out info)) info.TeamMemberId = memberId;
            }
        }

        public void PublishBufferTeamTracker(string character, int requesterId)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync)
            {
                BufferBotInfo info;
                if (_bufferBots.TryGetValue(character, out info)) info.TeamTrackerId = requesterId;
            }
        }
    }
}
