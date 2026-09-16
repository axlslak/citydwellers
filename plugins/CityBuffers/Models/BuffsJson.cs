using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Serialization;
using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MalisBuffBots
{
    public class BuffsJson : JsonFile<Dictionary<Profession, List<NanoEntry>>>
    {
        public readonly Dictionary<Profession, List<NanoEntry>> Entries;

        public BuffsJson(string jsonPath) : base(jsonPath)
        {
            Entries = _data;
        }

        public bool FindByTags(IEnumerable<string> tags, out Dictionary<Profession, List<NanoEntry>> result)
        {
            result = new Dictionary<Profession, List<NanoEntry>>();

            if (tags == null || tags.Count() == 0)
                return false;

            var distinctTags = tags.Distinct();

            foreach (var entriesByProf in Entries)
            {
                List<NanoEntry> results = entriesByProf.Value
                    .Where(x => distinctTags.Any(y => x.Tags.Contains(y) || int.TryParse(y, out int id) && x.ContainsId(id)))
                    .ToList();

                if (results.Count() == 0)
                    continue;

                result.Add(entriesByProf.Key, results);
            }

            return result.Count > 0;
        }

        public bool FindMissingBuffs(IEnumerable<string> tags, out Dictionary<Profession, List<NanoEntry>> missingBuffs)
        {
            missingBuffs = null;

            if (!FindByTags(tags, out Dictionary<Profession, List<NanoEntry>> entries))
                return false;

            missingBuffs = entries.ToDictionary(entry => entry.Key, entry => entry.Value);

            if (DynelManager.LocalPlayer.Buffs.Count == 0)
                return true;

            foreach (var entry in entries)
            {
                foreach (var buff in DynelManager.LocalPlayer.Buffs.Select(x => x.Id))
                {
                    var buffToRemove = entry.Value.FirstOrDefault(x => x.ContainsId(buff));

                    if (buffToRemove == null || buffToRemove.Type == CastType.Team)
                        continue;

                    missingBuffs[entry.Key].Remove(buffToRemove);
                }
            }

            return missingBuffs.Count() != 0;
        }

        public bool FindByIds(IEnumerable<int> ids, out List<string> tags)
        {
            tags = new List<string>();

            if (!FindByIds(ids, out Dictionary<Profession, List<NanoEntry>> result))
                return false;

            foreach (var entry in result.Values.SelectMany(x => x))
                tags.Add(entry.Tags.FirstOrDefault());

            return tags.Count != 0;
        }

        public bool FindByIds(IEnumerable<int> ids, out Dictionary<Profession, List<NanoEntry>> result) => ProcessId(ids, out result);

        public bool FindById(int id, out (Profession, NanoEntry) result)
        {
            result = default;

            if (!ProcessId(new List<int> { id }, out Dictionary<Profession, List<NanoEntry>> results))
                return false;

            var firstResult = results.FirstOrDefault();
            result = (firstResult.Key, firstResult.Value.FirstOrDefault());

            return true;
        }

        private bool ProcessId(IEnumerable<int> ids, out Dictionary<Profession, List<NanoEntry>> result)
        {
            result = new Dictionary<Profession, List<NanoEntry>>();

            if (ids == null || ids.Count() == 0)
                return false;

            foreach (var entriesByProf in Entries)
            {
                List<NanoEntry> results = new List<NanoEntry>();

                foreach (var nanoEntry in entriesByProf.Value)
                {
                    if (!ids.Any(x => nanoEntry.RemoveNanoIdUponCast.Contains(x) || nanoEntry.LevelToId.Any(y => y.Id == x)))
                        continue;

                    results.Add(nanoEntry);
                }

                if (results.Count() == 0)
                    continue;

                result.Add(entriesByProf.Key, results);
            }

            return result.Count > 0;
        }
    }

    public class BuffEntry
    {
        [AoMember(0)]
        public NanoEntry NanoEntry { get; set; }

        [AoMember(1)]
        public Identity Requester { get; set; }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
                return true;

            if (obj is null || GetType() != obj.GetType())
                return false;

            BuffEntry other = (BuffEntry)obj;

            // Check NanoEntry equality using the NanoEntryComparer
            if (!NanoEntry.Equals(other.NanoEntry))
                return false;

            // Check if the Identity matches
            if (Requester != other.Requester)
                return false;

            return true;
        }

        public override int GetHashCode()
        {
            int hash = 17;

            // Combine hash codes of NanoEntry and Identity
            hash = hash * 31 + NanoEntry.GetHashCode();
            hash = hash * 31 + Requester.GetHashCode();

            return hash;
        }
    }
}
