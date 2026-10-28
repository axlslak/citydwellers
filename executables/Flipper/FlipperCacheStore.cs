using System;
using System.Collections.Generic;
using CityDwellers.Shared;

internal static class FlipperCacheStore
{
    // The existing shared cache belongs to AP. Alliance requests always observe
    // their own controller and must never read or overwrite that cache.
    public static bool UsePrimaryCache = true;
    private static int _freshSeconds;
    public static void Initialize(string dataDirectory, int freshSeconds)
    {
        _freshSeconds = freshSeconds > 0 ? freshSeconds : 60;
        ManagerMemory.Current.InvalidateFlipperFreshness();
    }
    public static bool TryGetFresh(out FlipperCacheSnapshot snapshot)
    {
        if (!UsePrimaryCache) { snapshot = null; return false; }
        snapshot = ManagerMemory.Current.FlipperObservation(true, _freshSeconds);
        return snapshot != null;
    }
    public static bool TryGetAny(out FlipperCacheSnapshot snapshot)
    {
        if (!UsePrimaryCache) { snapshot = null; return false; }
        snapshot = ManagerMemory.Current.FlipperObservation(false, _freshSeconds);
        // Unlike the fresh-cache read, this fallback still carries the timer
        // from its original observation. Return its remaining time, not that
        // old countdown as if it had just been read from the controller.
        if (snapshot != null && snapshot.ShieldTimerInSeconds > 0)
        {
            TimeSpan age;
            snapshot.ShieldTimerInSeconds = UtcTimestamp.TryGetAge(snapshot.ObservedUtc, DateTime.UtcNow, out age)
                ? (int)Math.Max(0, snapshot.ShieldTimerInSeconds.Value - Math.Floor(age.TotalSeconds))
                : (int?)null;
        }
        return snapshot != null;
    }

    public static void SaveFromResult(FlipperLoader.FlipperResult result)
    {
        if (!UsePrimaryCache || result == null || result.Canceled)
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

        ManagerMemory.Current.ObserveFlipper(
            new FlipperCacheSnapshot
            {
                ObservedUtc = DateTime.UtcNow,
                CloakState = state,
                ShieldTimerInSeconds = timer,
                ControllerCharge = result.ControllerCharge,
                Source = source
            });
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

}
