using File = CityDwellers.Shared.SqlFile;
using Directory = CityDwellers.Shared.SqlDirectory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json;

namespace CityDwellers.Shared
{
    // Evidence comes from an actual low/high item link or a physical ledger row.
    // Equal names, numeric adjacency and matching flags never create an edge.
    public sealed class ItemTemplatePair
    {
        public int LowId;
        public int HighId;
    }

    public sealed class ItemFamilyIndex
    {
        private readonly Dictionary<int, int> keys = new Dictionary<int, int>();
        private readonly Dictionary<int, int[]> members;
        private readonly ItemTemplatePair[] pairs;
        public ItemFamilyIndex(IEnumerable<ItemTemplatePair> observed)
        {
            pairs = (observed ?? Enumerable.Empty<ItemTemplatePair>())
                .Where(p => p != null && p.LowId > 0 && p.HighId > 0 && p.LowId != p.HighId)
                .GroupBy(p => p.LowId + ":" + p.HighId)
                .Select(g => new ItemTemplatePair { LowId = g.First().LowId, HighId = g.First().HighId }).ToArray();
            var adjacency = new Dictionary<int, HashSet<int>>();
            foreach (var p in pairs)
            {
                if (!adjacency.ContainsKey(p.LowId)) adjacency[p.LowId] = new HashSet<int>();
                if (!adjacency.ContainsKey(p.HighId)) adjacency[p.HighId] = new HashSet<int>();
                adjacency[p.LowId].Add(p.HighId); adjacency[p.HighId].Add(p.LowId);
            }
            members = new Dictionary<int, int[]>();
            foreach (int id in adjacency.Keys)
            {
                if (keys.ContainsKey(id)) continue;
                var group = new HashSet<int>(); var pending = new Stack<int>(); pending.Push(id);
                while (pending.Count > 0)
                {
                    int current = pending.Pop(); if (!group.Add(current)) continue;
                    foreach (int next in adjacency[current]) pending.Push(next);
                }
                int key = group.Min(); var ids = group.OrderBy(i => i).ToArray(); members[key] = ids;
                foreach (int value in ids) keys[value] = key;
            }
        }
        public int Key(int id) { int key; return keys.TryGetValue(id, out key) ? key : id; }
        public int[] Members(int id) { int[] values; return members.TryGetValue(Key(id), out values) ? (int[])values.Clone() : new[] { id }; }
        public ItemTemplatePair[] Pairs => pairs.Select(p => new ItemTemplatePair { LowId = p.LowId, HighId = p.HighId }).ToArray();
        public ItemTemplatePair[] PairsFor(int id) => Pairs.Where(p => Key(p.LowId) == Key(id)).ToArray();
    }
    internal static class ItemPairEvidenceStore
    {
        public static ItemTemplatePair[] Merge(string path, IEnumerable<ItemTemplatePair> observations)
        {
            return SqlStore.WithLock("item-pair-evidence", () =>
            {
                var stored = File.Exists(path)
                    ? JsonConvert.DeserializeObject<ItemTemplatePair[]>(File.ReadAllText(path))
                    : new ItemTemplatePair[0];
                if (stored == null || stored.Any(p => p == null || p.LowId <= 0 || p.HighId <= 0 || p.LowId == p.HighId))
                    throw new InvalidDataException("Invalid SQL item pair evidence.");
                var result = new ItemFamilyIndex(stored.Concat(observations ?? Enumerable.Empty<ItemTemplatePair>())).Pairs
                    .OrderBy(p => p.LowId).ThenBy(p => p.HighId).ToArray();
                if (!result.Select(p => p.LowId + ":" + p.HighId).SequenceEqual(
                    stored.OrderBy(p => p.LowId).ThenBy(p => p.HighId).Select(p => p.LowId + ":" + p.HighId)))
                    File.WriteAllText(path, JsonConvert.SerializeObject(result, Formatting.Indented));
                return result;
            });
        }
    }
}
