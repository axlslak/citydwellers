using System;

namespace CityBankers.Shared
{
    internal static class TellQueueClient
    {
        public static string Enqueue(
            string settingsDirectory,
            string sourceCharacter,
            string recipient,
            string message)
        {
            if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(message))
                return null;

            return CityDwellers.Shared.TellQueue.Enqueue(
                RuntimeStateStore.GetDataDirectory(settingsDirectory),
                sourceCharacter,
                recipient,
                null,
                message);
        }
    }
}
