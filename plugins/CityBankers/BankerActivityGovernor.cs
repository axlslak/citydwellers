using System;
using System.Diagnostics;
using System.Threading;

namespace CityBankers
{
    // AOSharp keeps its own 64 Hz network/update pump. This governor controls
    // how often CityBankers enters expensive business polling while preserving
    // immediate wake-up for real work.
    internal static class BankerActivityGovernor
    {
        internal const int ActiveLeaseMilliseconds = 5000;
        internal const int ActiveTickMilliseconds = 62; // about 16 Hz
        internal const int VegetativeBankingMilliseconds = 2000;
        internal const int VegetativeUtilityMilliseconds = 1000;
        internal const int VegetativeTellMilliseconds = 500;

        private static long _activeUntil;

        internal static void Wake(int milliseconds = ActiveLeaseMilliseconds)
        {
            long now = Stopwatch.GetTimestamp();
            long extension = TicksForMilliseconds(Math.Max(1, milliseconds));
            long target = now + extension;
            while (true)
            {
                long current = Interlocked.Read(ref _activeUntil);
                if (current >= target) return;
                if (Interlocked.CompareExchange(ref _activeUntil, target, current) == current)
                    return;
            }
        }

        internal static bool IsActive =>
            Stopwatch.GetTimestamp() < Interlocked.Read(ref _activeUntil);

        // The caller owns lastTick. If local work exists we refresh the activity
        // lease; otherwise the same callback falls back to its vegetative cadence.
        internal static bool Due(
            ref long lastTick,
            bool localWork,
            int vegetativeMilliseconds)
        {
            if (localWork) Wake();

            long now = Stopwatch.GetTimestamp();
            int interval = IsActive
                ? ActiveTickMilliseconds
                : Math.Max(1, vegetativeMilliseconds);
            long required = TicksForMilliseconds(interval);
            long previous = Interlocked.Read(ref lastTick);
            if (previous != 0 && now - previous < required)
                return false;

            Interlocked.Exchange(ref lastTick, now);
            return true;
        }

        private static long TicksForMilliseconds(int milliseconds) =>
            (Stopwatch.Frequency * (long)milliseconds) / 1000L;
    }
}
