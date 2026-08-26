using System;

namespace CityManager
{
    public partial class CityManager
    {
        private void ProcessBanCommand(
            string senderName,
            string[] parts,
            ReplyTarget target,
            bool unban)
        {
            string command = unban ? "unban" : "ban";
            if (parts.Length != 2)
            {
                Reply(target, Usage(target, command + " [character]"));
                return;
            }

            string canonicalName = ResolveCanonicalAltMain(parts[1]);

            if (!unban && IsAdministrator(canonicalName))
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

            if (unban)
                changed = BanListStore.TryRemove(canonicalName, out message);
            else
                changed = BanListStore.TryAdd(canonicalName, out message);

            DevTrace(
                $"BAN LIST {(unban ? "REMOVE" : "ADD")} actor={senderName} " +
                $"target={canonicalName} requested={parts[1]} changed={changed}; {message}");
            Reply(target, message);
        }
    }
}
