using System;
using System.Text;

namespace CityManager
{
    public partial class CityManager
    {
        // Append only, oldest first. Entries are dictated by the owner, never
        // inferred from commits, recovery notes or JSON. Keep the full history.
        private static readonly string[] ChangelogEntries =
        {
            "created changelog.",
            "now we have items.",
            "added command #cru to dispose of one single cru."
        };

        private void ProcessChangelogCommand(string[] parts, ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "changelog"));
                return;
            }

            var body = new StringBuilder();
            int first = Math.Max(0, ChangelogEntries.Length - 25);
            for (int index = first; index < ChangelogEntries.Length; index++)
                body.Append(index + 1).Append(". ")
                    .Append(EscapeBlobText(ChangelogEntries[index])).Append("\n");

            Reply(target, BuildBlobLinks(target, "Changelog", "Changelog", body.ToString()));
        }
    }
}
