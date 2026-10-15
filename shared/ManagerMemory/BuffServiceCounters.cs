using System;
using System.Collections.Generic;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BuffServiceCounters
    {
        public DateTime SinceUtc;
        public long Froob, Paid;
        public long Total => Froob + Paid;
        // Buff profession includes Generic; caster profession identifies who served it.
        public Dictionary<int, long> ByBuffProfession;
        public Dictionary<int, long> ByCasterProfession;
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _buffCounterSync = new object();
        private readonly DateTime _buffCountersSinceUtc = DateTime.UtcNow;
        private long _froobBuffsGiven, _paidBuffsGiven;
        private readonly Dictionary<int, long> _buffsByProfession = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _buffsByCasterProfession = new Dictionary<int, long>();

        // Called once at successful completion of a non-self cast. Host-lifetime
        // telemetry stays in memory and never adds SQL/file work to casting.
        public void RecordBuffGiven(bool paid, int buffProfession, int casterProfession)
        {
            lock (_buffCounterSync)
            {
                if (paid) _paidBuffsGiven++; else _froobBuffsGiven++;
                long count;
                _buffsByProfession.TryGetValue(buffProfession, out count);
                _buffsByProfession[buffProfession] = count + 1;
                _buffsByCasterProfession.TryGetValue(casterProfession, out count);
                _buffsByCasterProfession[casterProfession] = count + 1;
            }
        }

        public BuffServiceCounters ReadBuffServiceCounters()
        {
            lock (_buffCounterSync)
                return new BuffServiceCounters {
                    SinceUtc = _buffCountersSinceUtc, Froob = _froobBuffsGiven, Paid = _paidBuffsGiven,
                    ByBuffProfession = new Dictionary<int, long>(_buffsByProfession),
                    ByCasterProfession = new Dictionary<int, long>(_buffsByCasterProfession) };
        }
    }
}
