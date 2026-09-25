using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BufferBotInfo
    {
        public string Character;
        public int Profession;
        public int IdentityType;
        public int IdentityInstance;
        public int[] SpellData;

        internal BufferBotInfo Copy() => new BufferBotInfo
        {
            Character = Character,
            Profession = Profession,
            IdentityType = IdentityType,
            IdentityInstance = IdentityInstance,
            SpellData = SpellData == null ? new int[0] : SpellData.ToArray()
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
            lock (_bufferBotSync) _bufferBots[info.Character] = info.Copy();
        }

        public void ClearBufferBotInfo(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync) _bufferBots.Remove(character);
        }

        public List<BufferBotInfo> BufferBotInfos()
        {
            lock (_bufferBotSync) return _bufferBots.Values.Select(x => x.Copy()).ToList();
        }
    }
}
