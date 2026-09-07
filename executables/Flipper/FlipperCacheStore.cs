using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CityDwellers.Shared;
using Newtonsoft.Json;

internal static class FlipperCacheStore
{
    private static readonly object Sync = new object();

    private static string _cachePath;
    private static int _freshSeconds;
    private static long _freshSavedTimestamp;
    private static DateTime _freshObservedUtc;

    public static void Initialize(string baseDirectory, int freshSeconds)
    {
        _cachePath = Path.Combine(baseDirectory, "cityflipper-cache.json");
        _freshSeconds = freshSeconds > 0 ? freshSeconds : 60;
        _freshSavedTimestamp = 0;
        _freshObservedUtc = default(DateTime);
    }

    public static bool TryGetFresh(out FlipperCacheSnapshot snapshot)
    {
        if (!TryGetAny(out snapshot))
            return false;

        lock (Sync)
        {
            if (_freshSavedTimestamp == 0 ||
                snapshot.ObservedUtc != _freshObservedUtc)
            {
                // A persisted record loaded after service restart has no
                // trustworthy elapsed-time anchor. It remains available as a
                // historical fallback, but must not suppress a live probe.
                return false;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - _freshSavedTimestamp;
            if (elapsedTicks < 0)
                return false;

            double elapsedSeconds =
                (double)elapsedTicks / Stopwatch.Frequency;
            if (elapsedSeconds > _freshSeconds)
                return false;

            if (snapshot.ShieldTimerInSeconds.HasValue &&
                snapshot.ShieldTimerInSeconds.Value > 0)
            {
                snapshot.ShieldTimerInSeconds = Math.Max(
                    0,
                    snapshot.ShieldTimerInSeconds.Value -
                    (int)Math.Floor(elapsedSeconds));
            }

            return true;
        }
    }

    public static bool TryGetAny(out FlipperCacheSnapshot snapshot)
    {
        snapshot = null;

        lock (Sync)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath))
                    return false;

                FlipperCacheRecord record = JsonConvert.DeserializeObject<FlipperCacheRecord>(
                    File.ReadAllText(_cachePath));

                if (record == null ||
                    !record.Confirmed ||
                    record.ObservedUtc == default(DateTime) ||
                    string.IsNullOrWhiteSpace(record.CloakState))
                {
                    return false;
                }

                DateTime observedUtc = UtcTimestamp.Normalize(record.ObservedUtc);
                if (UtcTimestamp.IsFuture(observedUtc, DateTime.UtcNow))
                {
                    Console.WriteLine(
                        $"Ignoring Flipper cache dated in the future: " +
                        $"observed={observedUtc:O}, now={DateTime.UtcNow:O}.");
                    QuarantineInvalidClockCache();
                    return false;
                }

                snapshot = new FlipperCacheSnapshot
                {
                    CloakState = record.CloakState,
                    // Without an in-process monotonic anchor, retain the
                    // original timer. This is conservative after restart and
                    // cannot make the shield appear ready early after a clock
                    // correction.
                    ShieldTimerInSeconds = record.ShieldTimerInSeconds,
                    ControllerCharge = record.ControllerCharge,
                    ObservedUtc = observedUtc,
                    Source = record.Source
                };

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SaveFromResult(FlipperLoader.FlipperResult result)
    {
        if (result == null || result.Canceled)
            return;

        string state = GetDictionaryValue(result.CloakInfo, "CloakState");
        int? timer = ParseInt(GetDictionaryValue(result.CloakInfo, "ShieldTimerInSeconds"));
        string source = "Flipper.Probe";

        if (result.ToggleRequested && result.ToggleSent)
        {
            if (!result.ToggleSucceeded ||
                string.IsNullOrWhiteSpace(result.PostToggleCloakState))
            {
                return;
            }

            state = result.PostToggleCloakState;
            timer = result.PostToggleShieldTimerInSeconds;
            source = "Flipper.ConfirmedToggle";
        }

        if (string.IsNullOrWhiteSpace(state))
            return;

        Save(
            new FlipperCacheRecord
            {
                ObservedUtc = DateTime.UtcNow,
                CloakState = state,
                ShieldTimerInSeconds = timer,
                ControllerCharge = result.ControllerCharge,
                Source = source,
                Confirmed = true
            });
    }

    private static void Save(FlipperCacheRecord record)
    {
        lock (Sync)
        {
            try
            {
                string tempPath = _cachePath + ".tmp";
                File.WriteAllText(
                    tempPath,
                    JsonConvert.SerializeObject(record, Formatting.Indented));

                if (File.Exists(_cachePath))
                    File.Delete(_cachePath);

                File.Move(tempPath, _cachePath);

                _freshObservedUtc = UtcTimestamp.Normalize(record.ObservedUtc);
                _freshSavedTimestamp = Stopwatch.GetTimestamp();
            }
            catch
            {
            }
        }
    }

    private static void QuarantineInvalidClockCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;

            string quarantinePath =
                _cachePath + ".invalid-clock-" + Guid.NewGuid().ToString("N");
            File.Move(_cachePath, quarantinePath);
            Console.WriteLine(
                $"Moved the invalid cache aside as " +
                $"{Path.GetFileName(quarantinePath)}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Unable to quarantine the invalid Flipper cache: {ex.Message}");
        }
    }

    private static string GetDictionaryValue(
        Dictionary<string, string> values,
        string key)
    {
        if (values == null)
            return null;

        string value;
        return values.TryGetValue(key, out value) ? value : null;
    }

    private static int? ParseInt(string value)
    {
        int parsed;
        return int.TryParse(value, out parsed) ? parsed : (int?)null;
    }

    private class FlipperCacheRecord
    {
        public DateTime ObservedUtc;
        public string CloakState;
        public int? ShieldTimerInSeconds;
        public float? ControllerCharge;
        public string Source;
        public bool Confirmed;
    }
}

internal class FlipperCacheSnapshot
{
    public DateTime ObservedUtc;
    public string CloakState;
    public int? ShieldTimerInSeconds;
    public float? ControllerCharge;
    public string Source;
}
