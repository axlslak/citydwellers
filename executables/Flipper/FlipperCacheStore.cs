using System;
using System.Collections.Generic;
using CityDwellers.Shared;

internal static class FlipperCacheStore
{
    private static int _freshSeconds;
    public static void Initialize(string dataDirectory, int freshSeconds)
    {
        _freshSeconds = freshSeconds > 0 ? freshSeconds : 60;
        ManagerMemory.Current.InvalidateFlipperFreshness();
    }
    public static bool TryGetFresh(out FlipperCacheSnapshot snapshot)
    {
        snapshot = ManagerMemory.Current.FlipperObservation(true, _freshSeconds);
        return snapshot != null;
    }
    public static bool TryGetAny(out FlipperCacheSnapshot snapshot)
    {
        snapshot = ManagerMemory.Current.FlipperObservation(false, _freshSeconds);
        return snapshot != null;
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
