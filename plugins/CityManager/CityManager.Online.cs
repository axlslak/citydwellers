using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CityManager
{
    public partial class CityManager
    {
        // Shared with Bobsan presence under _altsSync. Never persisted as live state.
        private readonly Dictionary<uint, string> _observedOnlineGuests =
            new Dictionary<uint, string>();
        private bool _orgOnlineSnapshotReceived;

        private void ObserveGuestOnline(uint characterId, string name)
        {
            string normalized;
            string error;
            if (characterId == 0 ||
                string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                !TryNormalizeAltName(name, out normalized, out error))
                return;

            lock (_altsSync)
                _observedOnlineGuests[characterId] = normalized;
        }

        private void ProcessOnlineCommand(string[] parts, ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "online"));
                return;
            }

            string[] org;
            string[] guests;
            bool snapshotReceived;
            lock (_altsSync)
            {
                org = _onlineCharacters.OrderBy(name => name,
                    StringComparer.OrdinalIgnoreCase).ToArray();
                guests = _observedOnlineGuests.Values
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                snapshotReceived = _orgOnlineSnapshotReceived;
            }

            var body = new StringBuilder();
            body.Append(HelpHeader("Organization (" + org.Length + ")",
                "Last known presence from Bobsan's online snapshot and logon/logoff announcements."));
            if (!snapshotReceived)
                body.Append("No complete startup online snapshot received yet; this list may be incomplete.\n");
            AppendOnlineNames(body, org);
            body.Append("\n").Append(HelpHeader("Guest channel — observed (" + guests.Length + ")",
                "Seen speaking since startup. Known logoffs and leave/kick commands remove names; silent joins or departures may be missing."));
            AppendOnlineNames(body, guests);
            Reply(target, BuildBlobLinks(target, "Online", "Online", body.ToString()));
        }

        private static void AppendOnlineNames(StringBuilder body, string[] names)
        {
            if (names.Length == 0)
                body.Append("None known.\n");
            foreach (string name in names)
                body.Append(EscapeBlobText(name)).Append("\n");
        }
    }
}
