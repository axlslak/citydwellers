using System;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class WorldBossRecord
    {
        public string Boss, Phase, Source, Message;
        public DateTime SpawnedUtc, VulnerableUtc, KilledUtc, ObservedUtc;
        internal WorldBossRecord Copy() => (WorldBossRecord)MemberwiseClone();
    }

    public sealed partial class ManagerMemory
    {
        public WorldBossRecord[] ReadWorldBosses(string transaction)
        {
            lock (_accountingSync)
                return Accounting(transaction).WorldBosses.Select(row => row.Copy()).ToArray();
        }

        public bool ObserveWorldBoss(string transaction, string boss, string phase,
            DateTime observedUtc, int immortalSeconds, string source, string message)
        {
            if (boss != "Tarasque" && boss != "Vizaresh" && boss != "The Hollow Reaper")
                throw new ArgumentException("Unknown world boss.");
            if (phase != "spawn" && phase != "immortal" && phase != "vulnerable" && phase != "killed")
                throw new ArgumentException("Unknown world boss announcement.");
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                var previous = state.WorldBosses.SingleOrDefault(row => row.Boss == boss);
                // A native notice can arrive through both the game and chat paths.
                if (previous != null && (observedUtc <= previous.ObservedUtc ||
                    (previous.Message == message && observedUtc - previous.ObservedUtc < TimeSpan.FromSeconds(5))))
                    return false;
                var record = previous?.Copy() ?? new WorldBossRecord { Boss = boss };
                int fullImmunity = boss == "Tarasque" ? 1800 : boss == "Vizaresh" ? 420 : 900;
                if (phase == "spawn")
                {
                    // Tarasque repeats its arrival notice halfway through immunity.
                    if (record.SpawnedUtc == default(DateTime) || record.KilledUtc >= record.SpawnedUtc ||
                        observedUtc - record.SpawnedUtc > TimeSpan.FromSeconds(fullImmunity))
                        record.SpawnedUtc = observedUtc;
                    record.KilledUtc = default(DateTime);
                }
                if (phase == "spawn" || phase == "immortal")
                    record.VulnerableUtc = observedUtc.AddSeconds(immortalSeconds);
                else if (phase == "vulnerable")
                    record.VulnerableUtc = observedUtc;
                else
                    record.KilledUtc = observedUtc;
                record.Phase = phase;
                record.ObservedUtc = observedUtc;
                record.Source = source;
                record.Message = message;
                state.WorldBosses = state.WorldBosses.Where(row => row.Boss != boss).ToList();
                state.WorldBosses.Add(record);
                return true;
            }
        }
    }
}
