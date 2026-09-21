using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CityDwellers.Shared
{
    // Automatic incident snapshots duplicated live state and business history.
    // They are retired. Calls are omitted at compile time, including argument
    // construction; actual receipts, history and lost/found are separate stores.
    public static class IncidentJournal
    {
        [Conditional("CITYDWELLERS_RETIRED_INCIDENT_TRACING")]
        public static void ObserveWrite(string path, object value) { }

        [Conditional("CITYDWELLERS_RETIRED_INCIDENT_TRACING")]
        public static void Record(string data, string trace, string actor, string stage, object detail,
            bool problem = false, IEnumerable<string> links = null, bool state = false) { }

        public static bool IsProblem(string text) => false;
    }
}
