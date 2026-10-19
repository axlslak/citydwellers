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
        public List<string> Tags = new List<string>();
        public List<int> EffectIds = new List<int>();
        public List<BufferNanoMetadata> Nanos = new List<BufferNanoMetadata>();

        internal BufferAdvertisedBuff Advertised() => new BufferAdvertisedBuff
        {
            Profession = Profession, Ncu = Ncu, IsGeneric = IsGeneric,
            Name = Name, Description = Description, Tag = Tag, Type = Type,
            NanoIds = NanoIds.ToArray(), Tags = Tags.ToArray(), EffectIds = EffectIds.ToArray(),
            Nanos = Nanos.Select(n => n.Copy()).ToArray()
        };

        internal BufferCatalogueBuff Copy()
        {
            var copy = (BufferCatalogueBuff)MemberwiseClone();
            copy.NanoIds = NanoIds.ToList();
            copy.Tags = Tags.ToList();
            copy.EffectIds = EffectIds.ToList();
            copy.Nanos = Nanos.Select(n => n.Copy()).ToList();
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
        // Immutable snapshots: lookups under the paid pool lock must not acquire
        // the accounting or buffer-status locks (or perform SQL/game lookups).
        private volatile Dictionary<string, PaidBufferNano[]> _auditedServiceNanos =
            new Dictionary<string, PaidBufferNano[]>(StringComparer.OrdinalIgnoreCase);

        public PaidBufferNano[] AuditedServiceNanos(int profession) => _auditedServiceNanos.Values
            .SelectMany(n => n).Where(n => profession == 0 || n.Profession == profession).ToArray();

        public PaidBufferNano FindAuditedServiceNano(string tag)
        {
            int id;
            if (int.TryParse(tag, out id)) return FindAuditedServiceEffect(id);
            return _auditedServiceNanos.Values.SelectMany(n => n)
                .FirstOrDefault(n => n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        public PaidBufferNano FindAuditedServiceEffect(int id)
        {
            var nanos = _auditedServiceNanos.Values.SelectMany(n => n);
            return nanos.FirstOrDefault(n => n.Id == id) ?? nanos.FirstOrDefault(n => n.MatchesEffect(id));
        }

        public bool PaidBufferOffers(string character, int nanoId)
        {
            PaidBufferNano[] nanos;
            if (_auditedServiceNanos.TryGetValue(character, out nanos))
                return nanos.Any(n => n.MatchesEffect(nanoId));
            var provider = ReadPaidBuffer(character);
            return provider != null && PaidBufferCatalogue.SeedForProfession(provider.Profession)
                .Any(n => n.MatchesEffect(nanoId));
        }

        private void PublishAuditedServiceNanos(BufferCatalogue catalogue)
        {
            if (!catalogue.Paid) return;
            var nanos = catalogue.Buffs.SelectMany(b => b.NanoIds.Select(id =>
            {
                var data = b.Nanos.FirstOrDefault(n => n.Id == id);
                var seed = PaidBufferCatalogue.SeedForProfession(catalogue.Profession).FirstOrDefault(n => n.Id == id);
                return new PaidBufferNano(id, catalogue.Profession, data?.Level ?? seed?.Level ?? 0,
                    data?.Ncu ?? b.Ncu, data?.Name ?? b.Name, b.Description,
                    b.Tags.Concat(new[] { b.Tag }).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                    { Team = string.Equals(b.Type, "Team", StringComparison.OrdinalIgnoreCase),
                      EffectIds = b.EffectIds.Concat(seed?.EffectIds ?? new int[0]).Distinct().ToArray() };
            })).ToArray();
            lock (_bufferBotSync)
            {
                var next = new Dictionary<string, PaidBufferNano[]>(_auditedServiceNanos, StringComparer.OrdinalIgnoreCase);
                next[catalogue.Character] = nanos;
                _auditedServiceNanos = next;
            }
        }
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
                PublishAuditedServiceNanos(catalogue);
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
                    NanoIds = b.NanoIds.ToList(), Tags = (b.Tags ?? new string[0]).ToList(),
                    EffectIds = (b.EffectIds ?? new int[0]).ToList(),
                    Nanos = (b.Nanos ?? new BufferNanoMetadata[0]).Select(n => n.Copy()).ToList()
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
            PublishAuditedServiceNanos(catalogue);
            lock (_bufferBotSync) _auditedBufferCatalogues.Add(character);
        }
    }
}
