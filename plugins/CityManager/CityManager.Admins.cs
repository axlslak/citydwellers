using System.Collections.Generic;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessAdminListCommand(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "adminlist"));
                return;
            }

            List<string> administrators = AdminListStore.Snapshot();
            string message =
                $"Administrators ({administrators.Count}): " +
                $"{string.Join(", ", administrators)}.";

            DevTrace($"ADMIN LIST viewed by={senderName} count={administrators.Count}.");
            Reply(target, message);
        }

        private void ProcessAdminCommand(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            bool add =
                parts.Length == 3 &&
                string.Equals(parts[1], "add", System.StringComparison.OrdinalIgnoreCase);
            bool del =
                parts.Length == 3 &&
                IsRemoveVerb(parts[1]);

            if (!add && !del)
            {
                Reply(target, Usage(target, "admin [add|del|rem|remove|delete] [character]"));
                return;
            }

            string canonicalName = ResolveCanonicalAltMain(parts[2]);

            bool changed;
            string message;

            if (add)
                changed = AdminListStore.TryAdd(canonicalName, out message);
            else
                changed = AdminListStore.TryRemove(canonicalName, out message);

            DevTrace(
                $"ADMIN LIST {parts[1].ToUpperInvariant()} actor={senderName} " +
                $"target={canonicalName} requested={parts[2]} changed={changed}; {message}");
            Reply(target, message);
        }
    }
}
