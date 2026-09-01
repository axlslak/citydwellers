namespace CityDwellers.Shared
{
    public static class LoggingDefaults
    {
        // Preserve each machine's local clock while making it globally
        // unambiguous. Example: 2026-09-01T20:27:23.123+03:00.
        public const string ConsoleOutputTemplate =
            "[{Timestamp:yyyy-MM-dd'T'HH:mm:ss.fffzzz} {Level:u3}] " +
            "{Message:lj}{NewLine}{Exception}";
    }
}
