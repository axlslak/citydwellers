using System;
using System.Diagnostics;
using System.Threading;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class AoLoginAdmission
    {
        public string Component;
        public string Character;
        public long Ticket;
        public long Wave;
        public int PositionInWave;
        public int WaveSize;
        public int WaveDelayMilliseconds;
        public long WaitedMilliseconds;
        public DateTime AdmittedUtc;
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _aoLoginSync = new object();
        private int _aoLoginWaveSize = 4;
        private int _aoLoginWaveDelayMilliseconds = 1000;
        private int _aoLoginWaveRemaining = 4;
        private long _aoLoginWaveOpenedTimestamp;
        private long _aoLoginWaveNumber;
        private long _aoLoginNextTicket;
        private long _aoLoginServingTicket;

        public void ConfigureAoLoginAdmission(
            GovernorAuthority authority,
            int maxStartsPerWave,
            int waveDelayMilliseconds)
        {
            RequireGovernor(authority);
            if (maxStartsPerWave <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxStartsPerWave));
            if (waveDelayMilliseconds < 1)
                throw new ArgumentOutOfRangeException(nameof(waveDelayMilliseconds));

            lock (_aoLoginSync)
            {
                if (_aoLoginNextTicket != _aoLoginServingTicket)
                    throw new InvalidOperationException(
                        "AO login admission cannot be reconfigured while callers are waiting.");

                _aoLoginWaveSize = maxStartsPerWave;
                _aoLoginWaveDelayMilliseconds = waveDelayMilliseconds;
                _aoLoginWaveRemaining = maxStartsPerWave;
                _aoLoginWaveOpenedTimestamp = 0;
                _aoLoginWaveNumber = 0;
                _aoLoginNextTicket = 0;
                _aoLoginServingTicket = 0;
                Monitor.PulseAll(_aoLoginSync);
            }
        }

        public AoLoginAdmission WaitForAoLoginAdmission(
            string component,
            string character)
        {
            if (string.IsNullOrWhiteSpace(component))
                throw new ArgumentException(
                    "AO login component is required.", nameof(component));
            if (string.IsNullOrWhiteSpace(character))
                throw new ArgumentException(
                    "AO login character is required.", nameof(character));

            long waitedFrom = Stopwatch.GetTimestamp();

            lock (_aoLoginSync)
            {
                long ticket = _aoLoginNextTicket++;

                while (true)
                {
                    while (ticket != _aoLoginServingTicket)
                        Monitor.Wait(_aoLoginSync);

                    long now = Stopwatch.GetTimestamp();
                    long waveDuration = MillisecondsToStopwatchTicks(
                        _aoLoginWaveDelayMilliseconds);

                    if (_aoLoginWaveOpenedTimestamp != 0 &&
                        now - _aoLoginWaveOpenedTimestamp >= waveDuration)
                    {
                        _aoLoginWaveRemaining = _aoLoginWaveSize;
                        _aoLoginWaveOpenedTimestamp = 0;
                    }

                    if (_aoLoginWaveRemaining <= 0)
                    {
                        long waitUntil =
                            _aoLoginWaveOpenedTimestamp + waveDuration;
                        int waitMilliseconds =
                            StopwatchTicksToWaitMilliseconds(waitUntil - now);
                        Monitor.Wait(_aoLoginSync, waitMilliseconds);
                        continue;
                    }

                    if (_aoLoginWaveOpenedTimestamp == 0)
                    {
                        _aoLoginWaveOpenedTimestamp = now;
                        _aoLoginWaveNumber++;
                    }

                    int position =
                        _aoLoginWaveSize - _aoLoginWaveRemaining + 1;
                    _aoLoginWaveRemaining--;
                    _aoLoginServingTicket++;
                    Monitor.PulseAll(_aoLoginSync);

                    long waitedTicks =
                        Stopwatch.GetTimestamp() - waitedFrom;
                    return new AoLoginAdmission
                    {
                        Component = component,
                        Character = character,
                        Ticket = ticket,
                        Wave = _aoLoginWaveNumber,
                        PositionInWave = position,
                        WaveSize = _aoLoginWaveSize,
                        WaveDelayMilliseconds =
                            _aoLoginWaveDelayMilliseconds,
                        WaitedMilliseconds =
                            waitedTicks <= 0 ? 0 :
                            (long)(waitedTicks * 1000.0 /
                                   Stopwatch.Frequency),
                        AdmittedUtc = DateTime.UtcNow
                    };
                }
            }
        }

        private static long MillisecondsToStopwatchTicks(
            int milliseconds)
        {
            return Math.Max(
                1L,
                (long)Math.Ceiling(
                    milliseconds * (double)Stopwatch.Frequency / 1000.0));
        }

        private static int StopwatchTicksToWaitMilliseconds(
            long ticks)
        {
            if (ticks <= 0) return 1;
            double milliseconds =
                ticks * 1000.0 / Stopwatch.Frequency;
            if (milliseconds >= int.MaxValue)
                return int.MaxValue;
            return Math.Max(1, (int)Math.Ceiling(milliseconds));
        }
    }
}
