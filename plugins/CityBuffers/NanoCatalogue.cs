using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;

namespace MalisBuffBots
{
    // Game metadata describes nanos; uploaded IDs establish this provider's
    // capabilities. Mali definitions contribute aliases/families, not an allowlist.
    internal static class NanoCatalogue
    {
        private static Dictionary<int, BufferNanoMetadata> _metadata;
        private static Dictionary<int, BufferNanoMetadata> Metadata => _metadata ?? (_metadata =
            JsonConvert.DeserializeObject<BufferNanoMetadata[]>(File.ReadAllText(
                System.IO.Path.Combine(Path.PLUGIN_DIR, "JSON", "NanoMetadata.json")))
                .ToDictionary(n => n.Id));

        internal static BufferNanoMetadata Describe(int id, int level)
        {
            BufferNanoMetadata data;
            if (Metadata.TryGetValue(id, out data))
            {
                var copy = data.Copy();
                copy.Level = Math.Max(copy.Level, level);
                return copy;
            }
            NanoItem nano;
            if (!ItemData.Find(id, out nano)) return new BufferNanoMetadata { Id = id, Level = level };
            return new BufferNanoMetadata { Id = id, Level = level, Name = nano.Name,
                Ncu = nano.NCU, DurationSeconds = (int)nano.TotalTime, NanoLine = (int)nano.NanoLine,
                StackingOrder = nano.StackingOrder, NanoCost = nano.Cost, Range = nano.Range,
                AttackMilliseconds = (int)(nano.AttackDelay * 1000),
                RechargeMilliseconds = (int)(nano.RechargeDelay * 1000) };
        }

        internal static void RestoreDefinitions()
        {
            foreach (var buff in ManagerMemory.Current.BufferBotInfos()
                .SelectMany(b => b.AdvertisedBuffs ?? new BufferAdvertisedBuff[0])) Add(buff);
            foreach (var buff in CityBufferBridge.CatalogueBuffs ?? new BufferAdvertisedBuff[0]) Add(buff);
            foreach (int profession in ManagerMemory.Current.ReadPaidBuffers().Select(p => p.Profession).Distinct())
            foreach (var nano in PaidBufferCatalogue.ForProfession(profession))
                Add(new BufferAdvertisedBuff { Profession = profession, Name = nano.Name,
                    Description = nano.Description, Tag = nano.Tags[0], Tags = nano.Tags,
                    Type = nano.IsTeam ? "Team" : "Single", NanoIds = new[] { nano.Id },
                    EffectIds = nano.EffectIds ?? new int[0], Nanos = new[] { Describe(nano.Id, nano.Level) } });
            // Every worker must understand a discovered ID before it can route
            // another worker's request. This loads definitions, not capabilities:
            // only an audit intersects them with a character's uploaded list.
            Discover(Metadata.Values.Where(n => n.CanBuffOthers).Select(n => n.Id).ToArray());
        }

        internal static void Discover(int[] uploaded)
        {
            var definitions = Main.BuffsJson.Entries.Values.SelectMany(e => e).ToArray();
            foreach (int id in uploaded)
            {
                if (definitions.Any(e => e.ContainsId(id))) continue;
                BufferNanoMetadata data;
                if (!Metadata.TryGetValue(id, out data) || !data.CanBuffOthers) continue;
                Add(new BufferAdvertisedBuff { Profession = data.Profession,
                    IsGeneric = data.Profession == 0, Name = data.Name, Description = data.Description,
                    Tag = id.ToString(), Tags = new[] { id.ToString() }, Type = "Single", Ncu = data.Ncu,
                    NanoIds = new[] { id }, Nanos = new[] { data.Copy() } });
            }
        }

        private static void Add(BufferAdvertisedBuff buff)
        {
            if (buff == null || buff.NanoIds == null || buff.NanoIds.Length == 0) return;
            var profession = (Profession)buff.Profession;
            List<NanoEntry> entries;
            if (!Main.BuffsJson.Entries.TryGetValue(profession, out entries))
                Main.BuffsJson.Entries[profession] = entries = new List<NanoEntry>();
            var tags = (buff.Tags ?? new string[0]).Concat(new[] { buff.Tag })
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var existing in entries.Where(e => e.LevelToId.Any(n => buff.NanoIds.Contains(n.Id))))
                existing.Tags = (existing.Tags ?? new string[0]).Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            int[] missing = buff.NanoIds.Where(id => !entries.Any(e => e.ContainsId(id))).ToArray();
            if (missing.Length == 0) return;
            entries.Add(new NanoEntry { Name = buff.Name, Description = buff.Description,
                Type = string.Equals(buff.Type, "Team", StringComparison.OrdinalIgnoreCase) ? CastType.Team : CastType.Single,
                Tags = tags.Length == 0 ? new[] { missing[0].ToString() } : tags,
                RemoveNanoIdUponCast = buff.EffectIds ?? new int[0],
                LevelToId = missing.Select(id => new LevelToIdMap { Id = id,
                    Level = (buff.Nanos ?? new BufferNanoMetadata[0]).FirstOrDefault(n => n.Id == id)?.Level ?? 0 }).ToArray() });
        }
    }
}
