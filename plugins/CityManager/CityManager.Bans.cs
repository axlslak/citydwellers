using System;
using System.Collections.Generic;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessBanCommand(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            bool list =
                parts.Length == 2 &&
                (string.Equals(parts[1], "list", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parts[1], "print", StringComparison.OrdinalIgnoreCase));

            if (list)
            {
                List<string> banned = BanListStore.Snapshot();
                string message = banned.Count == 0
                    ? "Ban list is empty."
                    : $"Banned characters ({banned.Count}): {string.Join(", ", banned)}.";
                DevTrace($"BAN LIST viewed by={senderName} count={banned.Count}.");
                Reply(target, message);
                return;
            }

            bool add =
                parts.Length == 3 &&
                string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase);
            bool remove =
                parts.Length == 3 &&
                (string.Equals(parts[1], "del", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parts[1], "rem", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parts[1], "remove", StringComparison.OrdinalIgnoreCase));

            if (!add && !remove)
            {
                Reply(target, Usage(
                    target,
                    "ban add [character] | ban del|rem|remove [character] | ban list|print"));
                return;
            }

            string requestedName = parts[2];
            string canonicalName = ResolveCanonicalAltMain(requestedName);

            if (add && IsAdministrator(canonicalName))
            {
                string protectedMessage =
                    $"{canonicalName} is an administrator and cannot be banned.";
                DevTrace(
                    $"BAN DENIED actor={senderName} target={canonicalName}: administrator.");
                Reply(target, protectedMessage);
                return;
            }

            bool changed;
            string message;
            if (add)
                changed = BanListStore.TryAdd(canonicalName, out message);
            else
                changed = BanListStore.TryRemove(canonicalName, out message);

            if (changed)
                PublishBufferAuthoritySnapshot(true);

            DevTrace(
                $"BAN LIST {(add ? "ADD" : "REMOVE")} actor={senderName} " +
                $"target={canonicalName} requested={requestedName} changed={changed}; {message}");
            Reply(target, message);
        }
    }
}
