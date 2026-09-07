using System;

namespace CityDwellers.Shared
{
    public static class UtcTimestamp
    {
        public static readonly TimeSpan FutureTolerance =
            TimeSpan.FromMinutes(2);

        // Persisted fields whose names end in Utc have historically appeared
        // without a JSON offset. DateTime.ToUniversalTime() interprets an
        // Unspecified value in the machine's local zone, making the same file
        // mean different instants on different hosts. Treat those values as
        // UTC by contract; only explicitly Local values are converted.
        public static DateTime Normalize(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;

            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();

            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        public static DateTime? Normalize(DateTime? value)
        {
            return value.HasValue
                ? Normalize(value.Value)
                : (DateTime?)null;
        }

        public static bool IsFuture(DateTime value, DateTime nowUtc)
        {
            return Normalize(value) > Normalize(nowUtc) + FutureTolerance;
        }

        public static bool TryGetAge(
            DateTime observedUtc,
            DateTime nowUtc,
            out TimeSpan age)
        {
            age = Normalize(nowUtc) - Normalize(observedUtc);
            return age >= TimeSpan.Zero;
        }
    }
}
