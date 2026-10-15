using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BufferCatalogueBuff
    {
        public int Profession, Ncu;
        public bool IsGeneric;
        public string Name, Description, Tag, Type;
        public List<int> NanoIds = new List<int>();

        internal BufferAdvertisedBuff Advertised() => new BufferAdvertisedBuff
        {
            Profession = Profession, Ncu = Ncu, IsGeneric = IsGeneric,
            Name = Name, Description = Description, Tag = Tag, Type = Type,
            NanoIds = NanoIds.ToArray()
        };

        internal BufferCatalogueBuff Copy()
        {
            var copy = (BufferCatalogueBuff)MemberwiseClone();
            copy.NanoIds = NanoIds.ToList();
            return copy;
        }
    }

    [Serializable]
    public sealed class BufferCatalogue
    {
        public string Character;
        public int Profession;
        public bool Paid;
        public DateTime LearnedUtc;
        public List<int> KnownNanos = new List<int>();
        public List<BufferCatalogueBuff> Buffs = new List<BufferCatalogueBuff>();

        public BufferAdvertisedBuff[] AdvertisedBuffs() => Buffs.Select(b => b.Advertised()).ToArray();

        internal BufferCatalogue Copy() => new BufferCatalogue
        {
            Character = Character, Profession = Profession, Paid = Paid, LearnedUtc = LearnedUtc,
            KnownNanos = KnownNanos.ToList(), Buffs = Buffs.Select(b => b.Copy()).ToList()
        };
    }

    public sealed partial class ManagerMemory
    {
        private bool _bufferCatalogueAudit;
        private readonly HashSet<string> _auditedBufferCatalogues =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void ConfigureBufferCatalogueAudit(bool requested)
        {
            lock (_bufferBotSync) _bufferCatalogueAudit = requested;
        }

        public bool BufferCatalogueAuditRequested(string character)
        {
            lock (_bufferBotSync)
                return _bufferCatalogueAudit && !_auditedBufferCatalogues.Contains(character);
        }

        public BufferCatalogue ReadBufferCatalogue(string character)
        {
            lock (_accountingSync)
                return Accounting(null).BufferCatalogues.FirstOrDefault(c =>
                    string.Equals(c.Character, character, StringComparison.OrdinalIgnoreCase))?.Copy();
        }

        // Saved capabilities are never live presence, a team or a ready caster.
        public void RestoreBufferCatalogues(string[] configuredCharacters)
        {
            var configured = new HashSet<string>(configuredCharacters, StringComparer.OrdinalIgnoreCase);
            BufferCatalogue[] catalogues;
            lock (_accountingSync)
                catalogues = Accounting(null).BufferCatalogues.Where(c => configured.Contains(c.Character))
                    .Select(c => c.Copy()).ToArray();
            foreach (var catalogue in catalogues)
            {
                var paid = ReadPaidBuffer(catalogue.Character);
                if (catalogue.Paid != (paid != null) ||
                    (paid != null && catalogue.Profession != paid.Profession)) continue;
                PublishBufferBotInfo(new BufferBotInfo
                {
                    Character = catalogue.Character, Profession = catalogue.Profession,
                    SpellData = catalogue.KnownNanos.ToArray(),
                    AdvertisedBuffs = catalogue.AdvertisedBuffs()
                });
            }
        }

        // Once for a new provider or explicit audit, never on a heartbeat.
        public void SaveBufferCatalogue(string character, int profession, bool paid, int[] known,
            BufferAdvertisedBuff[] advertised)
        {
            if (string.IsNullOrWhiteSpace(character) || known == null || known.Length == 0)
                throw new ArgumentException("A ready buffer with known nanos is required.");
            var catalogue = new BufferCatalogue
            {
                Character = character, Profession = profession, Paid = paid, LearnedUtc = DateTime.UtcNow,
                KnownNanos = known.Where(id => id > 0).Distinct().OrderBy(id => id).ToList(),
                Buffs = (advertised ?? new BufferAdvertisedBuff[0]).Select(b => new BufferCatalogueBuff
                {
                    Profession = b.Profession, Ncu = b.Ncu, IsGeneric = b.IsGeneric,
                    Name = b.Name, Description = b.Description, Tag = b.Tag, Type = b.Type,
                    NanoIds = b.NanoIds.ToList()
                }).ToList()
            };
            ManagerAccounting.Transaction("Learn buffer catalogue", () =>
            {
                lock (_accountingSync)
                {
                    var state = Writing(ManagerAccounting.TransactionId);
                    state.BufferCatalogues = state.BufferCatalogues.Where(c =>
                        !string.Equals(c.Character, character, StringComparison.OrdinalIgnoreCase)).ToList();
                    state.BufferCatalogues.Add(catalogue);
                }
            });
            lock (_bufferBotSync) _auditedBufferCatalogues.Add(character);
        }
    }
}
