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

            string dataDirectory = RuntimeStateStore.GetDataDirectory(settingsDirectory);
            if (string.Equals(
                    recipient,
                    TrustedOperators.BootstrapAdmin,
                    StringComparison.OrdinalIgnoreCase))
            {
                return CityDwellers.Shared.ManagerChannelQueue.Enqueue(
                    dataDirectory,
                    sourceCharacter,
                    message);
            }

            return CityDwellers.Shared.TellQueue.Enqueue(
                dataDirectory,
                sourceCharacter,
                recipient,
                null,
                message);
        }
    }
}
