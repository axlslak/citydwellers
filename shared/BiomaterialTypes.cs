using System;
using System.Collections.Generic;

namespace CityDwellers.Shared
{
    // Template facts from items.json; clump identification follows Nadybot's !bio.
    // This is a logical catalogue, never an alias between physical item identities.
    public static class BiomaterialTypes
    {
        public sealed class Material
        {
            public int Number { get; private set; }
            public string Expansion { get; private set; }
            public bool IsClump { get; private set; }

            internal Material(int number, string expansion, bool isClump)
            { Number = number; Expansion = expansion; IsClump = isClump; }
        }

        private static readonly Dictionary<int, Material> ById = Build();
        private static readonly HashSet<int> NonTypes = new HashSet<int> {
            247102, 247103, 247104, 247105, 247106, 247107, 247108, 247109,
            247764, 254804, 247765, 254805, 256035
        };

        public static IEnumerable<int> AoIds => ById.Keys;
        public static bool TryGet(int aoid, out Material material) => ById.TryGetValue(aoid, out material);
        public static string DisplayName(int aoid, string name)
        {
            Material material;
            return TryGet(aoid, out material) && material.IsClump
                ? (name ?? "Solid Clump of Kyr'Ozch Bio-Material") + " (Type " + material.Number + ", " + material.Expansion + ")"
                : name;
        }
        public static bool IsRejected(int aoid, string name = null)
        {
            if (ById.ContainsKey(aoid)) return false;
            return NonTypes.Contains(aoid) ||
                string.Equals(name, "Solid Clump of Kyr'Ozch Bio-Material", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Pristine Kyr'Ozch Bio-Material", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Mutated Kyr'Ozch Bio-Material", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Kyr'Ozch Viral Serum", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<int, Material> Build()
        {
            var result = new Dictionary<int, Material>();
            Add(result, 1, "AI", 247683, 247684, 247707, 247708);
            Add(result, 2, "AI", 247685, 247686, 247709, 247710);
            Add(result, 3, "AI", 247693, 247694, 247717, 247718);
            Add(result, 4, "AI", 247687, 247688, 247711, 247712);
            Add(result, 5, "AI", 247689, 247690, 247713, 247714);
            Add(result, 12, "AI", 247691, 247692, 247715, 247716);
            Add(result, 13, "AI", 247695, 247696, 247719, 247720);
            Add(result, 48, "AI", 288672, 288673, 288699, 288700);
            Add(result, 76, "AI", 247673, 247674, 247697, 247698);
            Add(result, 112, "AI", 247675, 247676, 247699, 247700);
            Add(result, 240, "AI", 247677, 247678, 247701, 247702);
            Add(result, 880, "AI", 247679, 247680, 247703, 247704);
            Add(result, 992, "AI", 247681, 247682, 247705, 247706);
            Add(result, 18, "LE", 265321, 265322);
            Add(result, 34, "LE", 265323, 265324);
            Add(result, 64, "LE", 265333, 265334);
            Add(result, 295, "LE", 265329, 265330);
            Add(result, 328, "LE", 266353, 266352);
            Add(result, 468, "LE", 265335, 265336);
            Add(result, 687, "LE", 265327, 265328);
            Add(result, 812, "LE", 265325, 265326);
            Add(result, 935, "LE", 265331, 265332);
            return result;
        }

        private static void Add(Dictionary<int, Material> result, int number, string expansion,
            int lowId, int highId, params int[] clumpIds)
        {
            var processed = new Material(number, expansion, false);
            result.Add(lowId, processed);
            result.Add(highId, processed);
            var clump = new Material(number, expansion, true);
            foreach (int id in clumpIds) result.Add(id, clump);
        }
    }
}
