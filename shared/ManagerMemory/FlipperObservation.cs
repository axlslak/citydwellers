using System;
using System.Diagnostics;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class FlipperCacheSnapshot
    {
        public DateTime ObservedUtc;
        public string CloakState;
        public int? ShieldTimerInSeconds;
        public float? ControllerCharge;
        public string Source;
        internal FlipperCacheSnapshot Copy() => (FlipperCacheSnapshot)MemberwiseClone();
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _flipperSync = new object();
        private FlipperCacheSnapshot _flipper;
        private long _flipperObservedStamp;

        public void ObserveFlipper(FlipperCacheSnapshot observation)
        {
            if (observation == null || observation.ObservedUtc == default(DateTime) || string.IsNullOrWhiteSpace(observation.CloakState))
                throw new ArgumentException("A confirmed Flipper observation is required.");
            lock (_flipperSync)
            {
                _flipper = observation.Copy();
                _flipperObservedStamp = Stopwatch.GetTimestamp();
            }
        }
        public FlipperCacheSnapshot FlipperObservation(bool freshOnly, int freshSeconds)
        {
            lock (_flipperSync)
            {
                if (_flipper == null) return null;
                var snapshot = _flipper.Copy();
                double age = (Stopwatch.GetTimestamp() - _flipperObservedStamp) / (double)Stopwatch.Frequency;
                if (freshOnly)
                {
                    if (_flipperObservedStamp == 0 || age < 0 || age > freshSeconds) return null;
                    if (snapshot.ShieldTimerInSeconds > 0)
                        snapshot.ShieldTimerInSeconds = Math.Max(0, snapshot.ShieldTimerInSeconds.Value - (int)Math.Floor(age));
                }
                return snapshot;
            }
        }
        public void InvalidateFlipperFreshness() { lock (_flipperSync) _flipperObservedStamp = 0; }
    }
}
