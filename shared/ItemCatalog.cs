using File = CityDwellers.Shared.SqlFile;
using Directory = CityDwellers.Shared.SqlDirectory;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    // Immutable template facts. Missing stats remain unknown, never false by default.
    public sealed class ItemDefinition
    {
        public int AoId { get; private set; }
        public string Name { get; private set; }
        public int? Quality { get; private set; }
        public uint? Flags { get; private set; }
        public uint? Can { get; private set; }
        public bool? NoDrop => Has(Flags, 0x04000000u);
        public bool? Unique => Has(Flags, 0x08000000u);
        public bool? Stackable => Has(Can, 0x00000200u);
        public bool? CantSplit => Has(Can, 0x01000000u);
        public bool? Splittable => Can.HasValue ? (bool?)(Stackable == true && CantSplit == false) : null;
        private static bool? Has(uint? bits, uint mask) => bits.HasValue ? (bool?)((bits.Value & mask) != 0) : null;
        internal ItemDefinition(int id, string name, int? quality, uint? flags, uint? can)
        { AoId = id; Name = name; Quality = quality; Flags = flags; Can = can; }
    }

    public sealed class ItemSearchMatch
    {
        public ItemDefinition Template;
        public int LowId;
        public int HighId;
        public int? Quality;
        public int? LowQuality;
        public int? HighQuality;
        public bool ObservedPair;
    }

    public sealed class ItemCatalogSnapshot
    {
        private readonly Dictionary<int, ItemDefinition> byId;
        private readonly ItemDefinition[] entries;
        public int Count => entries.Length;
        internal ItemCatalogSnapshot(IEnumerable<ItemDefinition> items)
        {
            byId = items.ToDictionary(i => i.AoId);
            entries = byId.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Quality).ThenBy(i => i.AoId).ToArray();
        }
        public ItemDefinition Find(int aoId)
        { ItemDefinition value; return byId.TryGetValue(aoId, out value) ? value : null; }

        // Match all positive words, exclude all minus-prefixed words. No regex from chat.
        public ItemDefinition[] Search(string query, int? quality, int page, int pageSize, out int total)
        {
            if (page < 1 || pageSize < 1 || pageSize > 100) throw new ArgumentOutOfRangeException();
            string[] terms = (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var matches = entries.Where(i => (!quality.HasValue || i.Quality == quality) && terms.All(t =>
                t.Length > 1 && t[0] == '-' ? i.Name.IndexOf(t.Substring(1), StringComparison.OrdinalIgnoreCase) < 0
                : i.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));
            // Exact names first; remaining order is stable across pages/restarts.
            var ordered = matches.OrderBy(i => string.Equals(i.Name, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
            total = ordered.Length;
            long offset = ((long)page - 1) * pageSize;
            return offset >= total ? new ItemDefinition[0] : ordered.Skip((int)offset).Take(pageSize).ToArray();
        }
        public ItemSearchMatch[] SearchFamilies(string query, int? quality, ItemFamilyIndex families,
            int page, int pageSize, out int total)
        {
            if (page < 1 || pageSize < 1 || pageSize > 100) throw new ArgumentOutOfRangeException();
            string[] terms = (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var found = new List<ItemSearchMatch>();
            var matching = entries.Where(i => terms.All(t => t.Length > 1 && t[0] == '-'
                ? i.Name.IndexOf(t.Substring(1), StringComparison.OrdinalIgnoreCase) < 0
                : i.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));
            foreach (var group in matching.GroupBy(i => families.Key(i.AoId)))
            {
                var covered = new HashSet<int>();
                foreach (var pair in families.PairsFor(group.Key))
                {
                    var low = Find(pair.LowId); var high = Find(pair.HighId);
                    if (low?.Quality == null || high?.Quality == null || low.Quality < 1 || low.Quality >= high.Quality) continue;
                    covered.Add(low.AoId); covered.Add(high.AoId);
                    if (quality.HasValue && (quality < low.Quality || quality > high.Quality)) continue;
                    found.Add(new ItemSearchMatch { Template = high, LowId = low.AoId, HighId = high.AoId,
                        Quality = quality ?? high.Quality, LowQuality = low.Quality, HighQuality = high.Quality, ObservedPair = true });
                }
                foreach (var item in group.Where(i => !covered.Contains(i.AoId) && (!quality.HasValue || quality == i.Quality)))
                    found.Add(new ItemSearchMatch { Template = item, LowId = item.AoId, HighId = item.AoId,
                        Quality = item.Quality, LowQuality = item.Quality, HighQuality = item.Quality });
            }
            var ordered = found.OrderBy(i => string.Equals(i.Template.Name, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(i => i.Template.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.LowId).ThenBy(i => i.HighId).ToArray();
            total = ordered.Length;
            long offset = ((long)page - 1) * pageSize;
            return offset >= total ? new ItemSearchMatch[0] : ordered.Skip((int)offset).Take(pageSize).ToArray();
        }

        internal IEnumerable<ItemDefinition> Entries => entries;
    }

    // Shared source is compiled into each plugin, as with the other shared services.
    // Each AppDomain has its own immutable compact snapshot. A process mutex
    // serializes expensive cache construction without holding a gameplay SQL lock
    // while parsing. The exclusive runtime DB lease prevents another host writer.
    public static class ItemCatalog
    {
        private static readonly object Sync = new object();
        private static volatile ItemCatalogSnapshot snapshot;
        private static bool loading;
        private static DateTime retryAfter;
        private static string status = "Item catalogue has not loaded yet.";
        private const string CacheMagic = "citydwellers-items-v1";
        public static string Status { get { lock (Sync) return status; } }
        public static ItemCatalogSnapshot Current { get { StartLoading(); return snapshot; } }
        public static ItemDefinition Find(int aoId) => Current?.Find(aoId);
        public static void StartLoading()
        {
            lock (Sync)
            {
                if (snapshot != null || loading || DateTime.UtcNow < retryAfter) return;
                loading = true; status = "Item catalogue is loading; try again shortly.";
                ThreadPool.QueueUserWorkItem(_ => LoadBackground());
            }
        }
        private static void LoadBackground()
        {
            try
            {
                string root = Environment.GetEnvironmentVariable("CITYDWELLERS_RUNTIME_ROOT");
                string path = Path.Combine(string.IsNullOrWhiteSpace(root) ? AppDomain.CurrentDomain.BaseDirectory : root, "data", "items.json");
                if (!File.Exists(path)) throw new FileNotFoundException("The items.json catalogue is absent. Place it in the data folder.");
                ItemCatalogSnapshot result = WithCacheConstructionLock(() =>
                {
                    long length = File.GetLength(path), stamp = File.GetLastWriteTimeUtc(path).Ticks;
                    string cache = path + ".index-v1.bin";
                    ItemCatalogSnapshot found = ReadCache(cache, length, stamp);
                    if (found == null)
                    {
                        found = ReadDump(path);
                        if (File.GetLength(path) != length || File.GetLastWriteTimeUtc(path).Ticks != stamp)
                            throw new IOException("The item catalogue changed while reading.");
                        WriteCache(cache, found, length, stamp);
                    }
                    return found;
                });
                lock (Sync) { snapshot = result; status = result.Count.ToString(CultureInfo.InvariantCulture) + " item templates loaded."; }
            }
            catch (Exception ex)
            {
                lock (Sync) { status = "Item catalogue unavailable: " + ex.Message; retryAfter = DateTime.UtcNow.AddSeconds(30); }
            }
            finally { lock (Sync) loading = false; }
        }
        private static ItemCatalogSnapshot WithCacheConstructionLock(Func<ItemCatalogSnapshot> build)
        {
            using (var mutex = new Mutex(false, "Local\\CityDwellers.Items." + System.Diagnostics.Process.GetCurrentProcess().Id))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(TimeSpan.FromMinutes(5)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("Item catalogue cache construction is busy; retry shortly.");
                    return build();
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }

        private static ItemCatalogSnapshot ReadDump(string path)
        {
            var items = new List<ItemDefinition>();
            using (var stream = File.OpenRead(path))
            using (var text = new StreamReader(stream))
            using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 128 })
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartArray) throw new InvalidDataException("Expected a tinkerparser JSON array.");
                bool ended = false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndArray) { ended = true; break; }
                    if (reader.TokenType != JsonToken.StartObject) throw new InvalidDataException("Expected an item object.");
                    int id = 0; string name = null; int? ql = null; uint? flags = null, can = null;
                    bool objectEnded = false;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.EndObject) { objectEnded = true; break; }
                        if (reader.TokenType != JsonToken.PropertyName) throw new InvalidDataException("Invalid item field.");
                        string field = (string)reader.Value;
                        if (!reader.Read()) throw new InvalidDataException("Truncated item field.");
                        if (field == "AOID") id = checked((int)ReadInteger(reader));
                        else if (field == "Name")
                        {
                            if (reader.TokenType != JsonToken.String && reader.TokenType != JsonToken.Null) throw new InvalidDataException("Invalid item name.");
                            name = (string)reader.Value;
                        }
                        else if (field == "StatValues")
                        {
                            if (reader.TokenType != JsonToken.StartArray) throw new InvalidDataException("Expected StatValues array.");
                            foreach (JToken stat in JArray.Load(reader))
                            {
                                int key = (int)stat["Stat"];
                                if (key != 0 && key != 30 && key != 54) continue;
                                JToken raw = stat["RawValue"];
                                if (raw == null || raw.Type != JTokenType.Integer) throw new InvalidDataException("Invalid item stat value.");
                                long value = (long)raw;
                                if (key == 54) ql = checked((int)value);
                                else
                                {
                                    if (value < int.MinValue || value > uint.MaxValue) throw new InvalidDataException("Invalid 32-bit item flags.");
                                    if (key == 0) flags = unchecked((uint)value); else can = unchecked((uint)value);
                                }
                            }
                        }
                        else reader.Skip();
                    }
                    if (!objectEnded || id <= 0) throw new InvalidDataException("Incomplete item template.");
                    items.Add(new ItemDefinition(id, name ?? "Unnamed item", ql, flags, can));
                }
                if (!ended || reader.Read() || items.Count == 0) throw new InvalidDataException("Incomplete or trailing item catalogue data.");
            }
            return new ItemCatalogSnapshot(items);
        }
        private static long ReadInteger(JsonReader reader)
        {
            if (reader.TokenType != JsonToken.Integer) throw new InvalidDataException("Expected integer.");
            return Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
        }
        private static ItemCatalogSnapshot ReadCache(string path, long length, long stamp)
        {
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadString() != CacheMagic || reader.ReadInt64() != length || reader.ReadInt64() != stamp) return null;
                    int count = reader.ReadInt32();
                    if (count < 1 || count > 1000000) return null;
                    var items = new List<ItemDefinition>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int id = reader.ReadInt32(); string name = reader.ReadString();
                        int? ql = reader.ReadBoolean() ? (int?)reader.ReadInt32() : null;
                        uint? flags = reader.ReadBoolean() ? (uint?)reader.ReadUInt32() : null;
                        uint? can = reader.ReadBoolean() ? (uint?)reader.ReadUInt32() : null;
                        if (id <= 0) return null;
                        items.Add(new ItemDefinition(id, name, ql, flags, can));
                    }
                    if (reader.BaseStream.Position != reader.BaseStream.Length) return null;
                    return new ItemCatalogSnapshot(items);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (FormatException) { return null; }
        }
        private static void WriteCache(string path, ItemCatalogSnapshot data, long length, long stamp)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(CacheMagic); writer.Write(length); writer.Write(stamp); writer.Write(data.Count);
                    foreach (var item in data.Entries)
                    {
                        writer.Write(item.AoId); writer.Write(item.Name);
                        writer.Write(item.Quality.HasValue); if (item.Quality.HasValue) writer.Write(item.Quality.Value);
                        writer.Write(item.Flags.HasValue); if (item.Flags.HasValue) writer.Write(item.Flags.Value);
                        writer.Write(item.Can.HasValue); if (item.Can.HasValue) writer.Write(item.Can.Value);
                    }
                }
                File.WriteAllBytes(path, stream.ToArray());
            }
        }
    }
}
