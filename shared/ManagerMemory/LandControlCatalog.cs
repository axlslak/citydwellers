using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CityDwellers.Shared
{
    // Static factual AO geography/level data, transformed from Tyrbot's pinned
    // SQL exports. No upstream executable code or remote tower feed is used.
    public static class LandControlCatalog
    {
        private static readonly Dictionary<int, string[]> Playfields = Rows("LandControlPlayfields.tsv")
            .ToDictionary(r => Number(r[0]), r => r);
        private static readonly LandControlSite[] Sites = Rows("LandControlSites.tsv").Select(r => new LandControlSite {
            PlayfieldId = Number(r[0]), Number = Number(r[1]), MinQl = Number(r[2]), MaxQl = Number(r[3]),
            X = Number(r[4]), Y = Number(r[5]), FixedCloseSeconds = r[6] == "" ? (int?)null : Number(r[6]),
            Name = r[7], Enabled = r[8] == "1", Playfield = Playfields[Number(r[0])][1],
            ShortName = Playfields[Number(r[0])][2]
        }).ToArray();
        private static readonly int[][] Bounds = Rows("LandControlBounds.tsv").Select(r => r.Select(Number).ToArray()).ToArray();
        private static readonly int[][] Levels = Rows("LandControlLevels.tsv").Select(r => r.Select(Number).ToArray()).ToArray();

        private static IEnumerable<string[]> Rows(string name)
        {
            using (var stream = typeof(LandControlCatalog).Assembly.GetManifestResourceStream("CityDwellers." + name))
            {
                if (stream == null) throw new InvalidDataException("Missing land control resource " + name);
                using (var reader = new StreamReader(stream))
                    while (!reader.EndOfStream)
                    { string line = reader.ReadLine(); if (!string.IsNullOrWhiteSpace(line)) yield return line.Split('\t'); }
            }
        }
        private static int Number(string text) => int.Parse(text, CultureInfo.InvariantCulture);
        public static LandControlSite[] ReadSites() => Sites.Select(s => s.Copy()).ToArray();
        public static int? FindPlayfield(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (name.StartsWith("Playfield ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(10);
            var matches = Playfields.Where(p => p.Key.ToString(CultureInfo.InvariantCulture) == name ||
                string.Equals(p.Value[1], name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Value[2], name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();
            return matches.Length == 1 ? (int?)matches[0] : null;
        }
        public static int? FindSite(int playfield, int x, int y)
        {
            var matches = Bounds.Where(b => b[0] == playfield && x >= b[2] && x <= b[4] && y <= b[3] && y >= b[5])
                .Select(b => b[1]).Distinct().ToArray();
            return matches.Length == 1 ? (int?)matches[0] : null;
        }
        public static int[] PvpRange(int level)
        {
            if (level < 1 || level > 220) throw new ArgumentOutOfRangeException(nameof(level), "Player level must be 1–220.");
            var row = Levels.Single(r => r[0] == level); return new[] { row[1], row[2] };
        }
        public static string ControllerType(int ql) => ql < 34 ? "I" : ql < 82 ? "II" : ql < 129 ? "III" : ql < 177 ? "IV" : ql < 201 ? "V" : ql < 226 ? "VI" : "VII";
        public static int TowerAllowance(int level) => level < 15 ? 0 : level < 75 ? 1 : level < 150 ? 2 : level < 200 ? 3 : 4;
        internal static double Mod(double value, double divisor) => (value % divisor + divisor) % divisor;
        public static TimeSpan? UntilClose(LandControlSite site, DateTime at)
        {
            if (!site.CloseSeconds.HasValue) return null;
            double seconds = Mod(site.CloseSeconds.Value - at.TimeOfDay.TotalSeconds, 86400);
            return TimeSpan.FromSeconds(seconds == 0 ? 86400 : seconds);
        }
        public static int? Gas(LandControlSite site, DateTime at)
        {
            if (site.State != "occupied") return null;
            var until = UntilClose(site, at);
            if (!until.HasValue) return site.PenaltyUntilUtc > at ? (int?)25 : null;
            int regular = until.Value.TotalSeconds <= 3600 ? 5 : until.Value.TotalSeconds <= 21600 ? 25 : 75;
            return regular == 75 && site.PenaltyUntilUtc > at ? 25 : regular;
        }
    }
}
