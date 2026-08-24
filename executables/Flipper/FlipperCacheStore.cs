using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

internal static class FlipperCacheStore
{
    private static readonly object Sync = new object();

    private static string _cachePath;
    private static int _freshSeconds;

    public static void Initialize(string settingsDirectory, int freshSeconds)
    {
        _cachePath = Path.Combine(settingsDirectory, "cityflipper-cache.json");
        _freshSeconds = freshSeconds > 0 ? freshSeconds : 60;
    }

    public static bool TryGetFresh(out FlipperCacheSnapshot snapshot)
    {
        if (!TryGetAny(out snapshot))
            return false;

        return DateTime.UtcNow - snapshot.ObservedUtc <=
               TimeSpan.FromSeconds(_freshSeconds);
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
                    record.ObservedUtc == default(DateTime) ||
                    string.IsNullOrWhiteSpace(record.CloakState))
                {
                    return false;
                }

                int? adjustedTimer = record.ShieldTimerInSeconds;
                if (adjustedTimer.HasValue && adjustedTimer.Value > 0)
                {
                    int elapsedSeconds = Math.Max(
                        0,
                        (int)Math.Floor((DateTime.UtcNow - record.ObservedUtc).TotalSeconds));

                    adjustedTimer = Math.Max(0, adjustedTimer.Value - elapsedSeconds);
                }

                snapshot = new FlipperCacheSnapshot
                {
                    CloakState = record.CloakState,
                    ShieldTimerInSeconds = adjustedTimer,
                    ControllerCharge = record.ControllerCharge,
                    ObservedUtc = record.ObservedUtc,
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
        if (result == null)
            return;

        string state = GetDictionaryValue(result.CloakInfo, "CloakState");
        int? timer = ParseInt(GetDictionaryValue(result.CloakInfo, "ShieldTimerInSeconds"));
        string source = "Flipper.Probe";

        if (result.ToggleRequested && result.ToggleSent)
        {
            if (string.Equals(
                result.InitialCloakState,
                "Enabled",
                StringComparison.OrdinalIgnoreCase))
            {
                state = "Disabled";
                timer = 3600;
                source = "Flipper.Toggle";
            }
            else if (string.Equals(
                result.InitialCloakState,
                "Disabled",
                StringComparison.OrdinalIgnoreCase))
            {
                state = "Enabled";
                timer = 0;
                source = "Flipper.Toggle";
            }
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
                Source = source
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
            }
            catch
            {
            }
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
