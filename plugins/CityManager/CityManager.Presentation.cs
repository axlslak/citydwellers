using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;

namespace CityManager
{
    public partial class CityManager
    {
        private const int OrgBlobPageSize = 5600;
        private const int GuestBlobPageSize = 6500;
        private const int TellBlobPageSize = 7200;

        private const string ColorTitle = "#89D2E8";
        private const string ColorGood = "#00DE42";
        private const string ColorWarn = "#F79410";
        private const string ColorBad = "#FF5050";
        private const string ColorCommand = "#F5C542";
        private const string ColorMuted = "#A0A0A0";
        private const string ColorText = "#D7E3EA";

        private void ProcessHelpCommand(
            string[] parts,
            ReplyTarget target,
            bool isAdmin)
        {
            string topic = parts.Length <= 1
                ? "overview"
                : string.Join(" ", parts.Skip(1)).Trim().ToLowerInvariant();

            string body;
            string title;
            if (!TryBuildHelpTopic(topic, target, isAdmin, out title, out body))
            {
                string unknown =
                    "<font color='" + ColorBad + "'>No help topic named " +
                    EscapeBlobText(topic) + ".</font>\n\n" +
                    "Open " + CommandLink(target, "help", "Help overview") +
                    " or " + CommandLink(target, "help commands", "Command list") + ".";
                Reply(target, BuildBlobLinks(target, "Help", "Help: unknown topic", unknown));
                return;
            }

            string links = BuildBlobLinks(target, title, "Help: " + title, body);
            Reply(
                target,
                "<font color='" + ColorTitle + "'>City Dwellers Help</font> " + links);
        }

        private bool TryBuildHelpTopic(
            string topic,
            ReplyTarget target,
            bool isAdmin,
            out string title,
            out string body)
        {
            title = "Overview";
            body = null;

            switch (topic)
            {
                case "":
                case "help":
                case "overview":
                    body = BuildHelpOverview(target, isAdmin);
                    return true;

                case "commands":
                case "cmd":
                case "cmdlist":
                case "list":
                    title = "Command List";
                    body = BuildCommandList(target, isAdmin);
                    return true;

                case "syntax":
                    title = "Command Syntax";
                    body = HelpHeader(
                        "How commands work",
                        "Use commands naturally in a tell, or prefix them with # in organization and guest chat.") +
                        HelpSyntaxLine(target, "help [topic]", "Open the manual or a specific topic.") +
                        HelpSyntaxLine(target, "status", "Open the live Manager status blob.") +
                        "\n<font color='" + ColorMuted + "'>Words in [brackets] are values you supply. " +
                        "A vertical bar means choose one option. Commands and buttons are case-insensitive.</font>";
                    return true;

                case "status":
                    title = "Status";
                    body = CommandHelp(
                        target,
                        "status",
                        "Open one operational view of the Manager, cloak, Flipper, Buddies, raid workflow, alt cache, and membership roster.",
                        "This is the first command to use when something feels stuck. Manager uptime is monotonic, so clock changes cannot make it lie.",
                        "Member",
                        null);
                    return true;

                case "cloak":
                    title = "Cloak";
                    body = CommandHelp(
                        target,
                        "cloak",
                        "Ask Flipper for the latest city-cloak observation, shield timer, and controller charge.",
                        "Use it before planning a raid. A cached answer is identified as cached; status also shows the recent cloak-event history.",
                        "Organization or guest; administrators may also use tells",
                        null);
                    return true;

                case "raid":
                case "raidassist":
                case "cancel":
                    title = "Raids";
                    body = BuildRaidHelp(target);
                    return true;

                case "alts":
                    title = "Alts";
                    body = BuildAltHelp(target, isAdmin);
                    return true;

                case "get":
                case "withdraw":
                    title = "Bank Pickup";
                    body = CommandHelp(
                        target,
                        "get [AO item ID]",
                        "Reserve one available CityBankers item and have its storage worker return it to Kbcentral.",
                        "Kbcentral tells you when the item is ready. Open trade within three minutes. Your known alts may collect it too.",
                        "Athen Paladins member",
                        "Alias: withdraw. The item leaves stock only after AO confirms the pickup trade finished.");
                    return true;

                case "guest":
                case "join":
                case "leave":
                    title = "Guest Channel";
                    body = BuildGuestHelp(target);
                    return true;

                case "restart":
                    if (!isAdmin)
                        return false;
                    title = "Restart Manager";
                    body = CommandHelp(
                        target,
                        "restart",
                        "Restart Apcmanager and reconnect its AO session.",
                        "Use this when the clientless AO session has degraded, especially when organization output loses its LocalPlayer organization stat.",
                        "Administrator",
                        "The current process starts a delayed replacement, then exits.");
                    return true;

                case "dump":
                case "diagnostics":
                    if (!isAdmin)
                        return false;
                    title = "Diagnostics";
                    body = CommandHelp(
                        target,
                        "dump",
                        "Write a timestamped diagnostic snapshot to disk and report its full path.",
                        "The guest channel carries new events live after it is connected. It never replays the old backlog. Use dump when Kavey needs a file for investigation.",
                        "Administrator",
                        null);
                    return true;

                case "admin":
                case "admins":
                case "administration":
                    if (!isAdmin)
                        return false;
                    title = "Administration";
                    body = BuildAdminHelp(target);
                    return true;

                case "positions":
                case "buddies":
                case "home":
                case "wakeup":
                case "sleep":
                case "spinup":
                case "spindown":
                    if (!isAdmin)
                        return false;
                    title = "Buddies";
                    body = BuildBuddyHelp(target);
                    return true;

                case "member":
                case "member add":
                case "member del":
                case "member remove":
                case "memberlist":
                    if (!isAdmin)
                        return false;
                    title = "Membership";
                    body = BuildMembershipHelp(target);
                    return true;

                case "invite":
                case "kick":
                    if (!isAdmin)
                        return false;
                    title = "Guest Administration";
                    body = BuildGuestAdminHelp(target);
                    return true;

                case "ban":
                case "unban":
                    if (!isAdmin)
                        return false;
                    title = "Access Control";
                    body = BuildAccessHelp(target);
                    return true;

                case "adminlist":
                case "admin add":
                case "admin del":
                    if (!isAdmin)
                        return false;
                    title = "Administrator List";
                    body = BuildAdministratorListHelp(target);
                    return true;

                case "recoverraid":
                    if (!isAdmin)
                        return false;
                    title = "Raid Recovery";
                    body = CommandHelp(
                        target,
                        "recoverraid [owner] [all|general] [level] [count]",
                        "Reconstruct a raid workflow after a Manager restart.",
                        "Use only when a real raid was in progress and its in-memory session was lost.",
                        "Administrator",
                        "Example: recoverraid Kavem all 200 6");
                    return true;
            }

            return false;
        }

        private string BuildHelpOverview(ReplyTarget target, bool isAdmin)
        {
            var body = new StringBuilder();
            body.Append(HelpHeader(
                "City Dwellers Manager",
                "A practical manual for city cloak, raids, alts, membership, and the bot workers that support them."));
            body.Append("<font color='").Append(ColorText)
                .Append("'>Choose a section. Every orange label is clickable.</font>\n\n");
            body.Append(HelpMenuLine(target, "help commands", "Command list", "Everything members can use."));
            body.Append(HelpMenuLine(target, "help status", "Status", "Health, uptime, workers, cloak, and active work."));
            body.Append(HelpMenuLine(target, "help cloak", "Cloak", "Cloak observation and raid timing."));
            body.Append(HelpMenuLine(target, "help raid", "Raids", "Start, configure, assist, or cancel a raid."));
            body.Append(HelpMenuLine(target, "help alts", "Alts", "Main/alt lookup and cache behavior."));
            body.Append(HelpMenuLine(target, "help guest", "Guest channel", "Joining, leaving, and where replies go."));
            body.Append(HelpMenuLine(target, "help syntax", "Syntax", "Prefixes, arguments, and clickable commands."));

            if (isAdmin)
            {
                body.Append("\n<font color='").Append(ColorTitle).Append("'>Administrator manual</font>\n");
                body.Append(HelpMenuLine(target, "help admin", "Administration", "Members, bans, workers, guest access, and recovery."));
                body.Append(HelpMenuLine(target, "help buddies", "Buddies", "Worker lifecycle, positions, and home."));
                body.Append(HelpMenuLine(target, "help dump", "Diagnostics", "Create a log snapshot for debugging."));
            }

            body.Append("\n<font color='").Append(ColorMuted)
                .Append("'>In organization and guest chat, commands begin with #. In tells, # is optional.</font>");
            return body.ToString();
        }

        private string BuildCommandList(ReplyTarget target, bool isAdmin)
        {
            var body = new StringBuilder();
            body.Append(HelpHeader("Member commands", "The day-to-day City Dwellers command list."));
            body.Append(HelpSyntaxLine(target, "help [topic]", "Open help."));
            body.Append(HelpSyntaxLine(target, "status", "Open live system status."));
            body.Append(HelpSyntaxLine(target, "cloak", "Check city cloak through Flipper."));
            body.Append(HelpSyntaxLine(
                target,
                "stock [family [slot [targetQl]]]",
                "Browse CityBankers symbiant availability."));
            body.Append(HelpSyntaxLine(
                target,
                "donor [top|last|member]",
                "Browse all-time donor rankings and donation history."));
            body.Append(HelpSyntaxLine(
                target,
                "get [AO item ID]",
                "Reserve an item and collect it from Kbcentral within three minutes. Alias: withdraw."));
            body.Append(HelpSyntaxLine(target, "raid", "Open or resume raid setup."));
            body.Append(HelpSyntaxLine(target, "cancel [raid-token]", "Cancel your active raid."));
            body.Append(HelpSyntaxLine(target, "alts [character]", "Show known mains and alts."));
            body.Append(HelpSyntaxLine(target, "join", "Ask for a guest-channel invite."));
            body.Append(HelpSyntaxLine(target, "leave", "Leave the guest channel."));

            if (isAdmin)
            {
                body.Append("\n<font color='").Append(ColorTitle).Append("'>Administrator commands</font>\n");
                body.Append(HelpSyntaxLine(target, "invite [character]", "Invite a character to guest chat."));
                body.Append(HelpSyntaxLine(target, "kick [character]", "Remove a character from guest chat."));
                body.Append(HelpSyntaxLine(target, "positions", "Open live Buddy positions."));
                body.Append(HelpSyntaxLine(target, "home [level|all|status]", "Start or inspect home movement."));
                body.Append(HelpSyntaxLine(target, "wakeup [level] [index]", "Start one Buddy."));
                body.Append(HelpSyntaxLine(target, "sleep [index]", "Stop one Buddy."));
                body.Append(HelpSyntaxLine(target, "spinup [level] [count]", "Start a Buddy group."));
                body.Append(HelpSyntaxLine(target, "spindown [count]", "Stop a Buddy group."));
                body.Append(HelpSyntaxLine(target, "memberlist", "Show effective members."));
                body.Append(HelpSyntaxLine(target, "member [add|del] [character]", "Change live membership."));
                body.Append(HelpSyntaxLine(target, "adminlist", "Show administrators."));
                body.Append(HelpSyntaxLine(target, "admin [add|del] [character]", "Change administrators."));
                body.Append(HelpSyntaxLine(target, "ban [character]", "Deny bot access."));
                body.Append(HelpSyntaxLine(target, "unban [character]", "Remove a bot ban."));
                body.Append(HelpSyntaxLine(target, "recoverraid [owner] [all|general] [level] [count]", "Recover a raid after restart."));
                body.Append(HelpSyntaxLine(target, "dump", "Save a diagnostic snapshot."));
                body.Append(HelpSyntaxLine(target, "restart", "Restart Apcmanager and its AO session."));
            }

            return body.ToString();
        }

        private string BuildRaidHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Raids",
                    "The Manager guides setup in a blob, coordinates Buddies and Flipper, and keeps the owner informed through each stage.") +
                HelpSyntaxLine(target, "raid", "Open or resume raid setup in organization or guest chat.") +
                HelpSyntaxLine(target, "cancel [raid-token]", "Cancel the raid you own; administrators may cancel any raid.") +
                HelpSyntaxLine(target, "raidassist [count] [raid-token]", "Officer button used to contribute additional raiders.") +
                HelpSyntaxLine(target, "raidassist level [level] [raid-token]", "Officer button used to choose an assistance level.") +
                "\n<font color='" + ColorMuted + "'>Raid setup is deliberately staged. Follow the buttons in the current blob; old buttons expire safely.</font>";
        }

        private string BuildAltHelp(ReplyTarget target, bool isAdmin)
        {
            var body = new StringBuilder();
            body.Append(HelpHeader(
                "Alts",
                "The Manager keeps a local main/alt cache and can refresh it from the configured external alt bot."));
            body.Append(HelpSyntaxLine(target, "alts", "Show your known alt family and refresh it."));
            body.Append(HelpSyntaxLine(target, "alts [character]", "Look up another character."));
            body.Append(HelpSyntaxLine(target, "alts list", "List cached alt groups."));

            if (isAdmin)
            {
                body.Append(HelpSyntaxLine(target, "alts add [main] [alt]", "Add or correct a cached relationship."));
                body.Append(HelpSyntaxLine(target, "alts del [character]", "Remove a cached relationship."));
                body.Append(HelpSyntaxLine(target, "alts recover", "Queue a full officer-alt recovery sweep."));
            }

            body.Append("\n<font color='").Append(ColorMuted)
                .Append("'>Authorization follows the canonical main, so an administrator's known alts inherit the same access.</font>");
            return body.ToString();
        }

        private string BuildGuestHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Guest channel",
                    "The guest channel is a shared place for commands and concise live Manager diagnostics.") +
                HelpSyntaxLine(target, "join", "Ask the Manager to invite you.") +
                HelpSyntaxLine(target, "leave", "Leave the Manager's private channel.") +
                "\n<font color='" + ColorMuted + "'>Connecting the channel never dumps earlier telemetry. New diagnostic events appear live; administrators create a file with dump when history is needed.</font>";
        }

        private string BuildAdminHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Administration",
                    "Administrative commands change access, control worker characters, or recover state. Their individual pages explain intent and safe use.") +
                HelpMenuLine(target, "help member", "Membership", "Official roster plus live overrides.") +
                HelpMenuLine(target, "help adminlist", "Administrators", "Canonical admin identities.") +
                HelpMenuLine(target, "help ban", "Access control", "Explicit denies.") +
                HelpMenuLine(target, "help buddies", "Buddies", "Lifecycle and position tools.") +
                HelpMenuLine(target, "help invite", "Guest administration", "Invite and kick.") +
                HelpMenuLine(target, "help recoverraid", "Raid recovery", "Restart recovery only.") +
                HelpMenuLine(target, "help dump", "Diagnostics", "Save evidence for investigation.") +
                HelpMenuLine(target, "help restart", "Restart Manager", "Replace a degraded AO session.");
        }

        private string BuildBuddyHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Buddies",
                    "Buddies owns the raid characters. Commands target a level/index or a count and report completion back through the Manager.") +
                HelpSyntaxLine(target, "positions", "Open a colored position and health blob.") +
                HelpSyntaxLine(target, "wakeup [level] [index]", "Start one indexed character at a level.") +
                HelpSyntaxLine(target, "sleep [index]", "Stop one indexed character.") +
                HelpSyntaxLine(target, "spinup [level] [count]", "Start a counted group with a safety lease.") +
                HelpSyntaxLine(target, "spindown [count]", "Stop a counted group.") +
                HelpSyntaxLine(target, "home [level|all]", "Move active Buddies to their configured home positions.") +
                HelpSyntaxLine(target, "home status", "Show the retained home-job result.") +
                "\n<font color='" + ColorMuted + "'>Position detail is opened on demand. It is also recorded in diagnostics for an explicit dump, never sprayed into guest chat.</font>";
        }

        private string BuildMembershipHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Membership",
                    "Access starts with the official organization roster, then applies permanent and live overrides.") +
                HelpSyntaxLine(target, "memberlist", "Show the effective member list.") +
                HelpSyntaxLine(target, "member add [character]", "Add a live member override.") +
                HelpSyntaxLine(target, "member del [character]", "Remove a live member override.") +
                "\n<font color='" + ColorMuted + "'>remove, rem, and delete are accepted aliases for del.</font>";
        }

        private string BuildGuestAdminHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Guest administration",
                    "Invite or remove a named character from Apcmanager's private group.") +
                HelpSyntaxLine(target, "invite [character]", "Send a guest-channel invitation.") +
                HelpSyntaxLine(target, "kick [character]", "Remove a character from the guest channel.");
        }

        private string BuildAccessHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Access control",
                    "A ban is an explicit deny and overrides ordinary member access. Administrators cannot be banned.") +
                HelpSyntaxLine(target, "ban [character]", "Add a canonical character identity to the ban list.") +
                HelpSyntaxLine(target, "unban [character]", "Remove the canonical identity from the ban list.");
        }

        private string BuildAdministratorListHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Administrator list",
                    "Administrators may use privileged commands. Known alts resolve to their canonical main.") +
                HelpSyntaxLine(target, "adminlist", "Show all administrators.") +
                HelpSyntaxLine(target, "admin add [character]", "Grant administrator access.") +
                HelpSyntaxLine(target, "admin del [character]", "Remove administrator access.") +
                "\n<font color='" + ColorMuted + "'>remove, rem, and delete are accepted aliases for del.</font>";
        }

        private string CommandHelp(
            ReplyTarget target,
            string syntax,
            string description,
            string spirit,
            string access,
            string example)
        {
            var body = new StringBuilder();
            body.Append(HelpHeader(syntax, description));
            body.Append("<font color='").Append(ColorTitle).Append("'>Access</font>\n")
                .Append(EscapeBlobText(access)).Append("\n\n")
                .Append("<font color='").Append(ColorTitle).Append("'>Why it exists</font>\n")
                .Append(EscapeBlobText(spirit)).Append("\n\n")
                .Append(HelpSyntaxLine(target, syntax, "Run this command."));

            if (!string.IsNullOrWhiteSpace(example))
                body.Append("\n<font color='").Append(ColorMuted).Append("'>")
                    .Append(EscapeBlobText(example)).Append("</font>");

            return body.ToString();
        }

        private string HelpHeader(string heading, string description)
        {
            return "<font color='" + ColorTitle + "'><b>" + EscapeBlobText(heading) +
                   "</b></font>\n" +
                   "<font color='" + ColorText + "'>" + EscapeBlobText(description) +
                   "</font>\n\n";
        }

        private string HelpMenuLine(
            ReplyTarget target,
            string command,
            string label,
            string description)
        {
            return "  " + CommandLink(target, command, label) +
                   "  <font color='" + ColorMuted + "'>" +
                   EscapeBlobText(description) + "</font>\n";
        }

        private string HelpSyntaxLine(
            ReplyTarget target,
            string syntax,
            string description)
        {
            string renderedSyntax = syntax.IndexOf('[') >= 0
                ? "<font color='" + ColorCommand + "'>" +
                  EscapeBlobText(syntax) + "</font>"
                : CommandLink(target, syntax, syntax);

            return "  " + renderedSyntax +
                   "\n    <font color='" + ColorMuted + "'>" +
                   EscapeBlobText(description) + "</font>\n";
        }

        private string CommandLink(
            ReplyTarget target,
            string command,
            string label)
        {
            string route;
            if (target.IsOrg)
                route = "/o #" + command;
            else if (target.IsGuest)
                route = "/g Apcmanager #" + command;
            else
                route = "/tell Apcmanager #" + command;

            return "<a href='chatcmd://" + EscapeHref(route) + "'>" +
                   "<font color='" + ColorCommand + "'>" +
                   EscapeBlobText(label) + "</font></a>";
        }

        private void BeginServiceStatus(ReplyTarget target)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool raidFlipperBusy = IsRaidFlipperBusy();
                bool recoveryFlipperBusy = CityRaidAutomation.IsFlipperBusy();
                WorkerLinkStatus flipper = raidFlipperBusy
                    ? WorkerLinkStatus.Usable("reserved while watching City Controller charge")
                    : recoveryFlipperBusy
                        ? WorkerLinkStatus.Usable("reserved while checking cloak state")
                        : PingWorker("Flipper", FlipperPipeName);
                WorkerLinkStatus buddies = PingWorker("Buddies", BuddiesPipeName);

                string buddyActivity = buddies.IsUsable
                    ? GetBuddyActivitySummary()
                    : "Position inventory unavailable because Buddies is not linked.";
                string cloak = BuildCloakStatusSummary();
                string recovery = CityRaidAutomation.GetStatusText();
                string raid = BuildRaidStatusSummary();
                string alts = BuildAltStatusSummary();
                string membership = BuildMembershipStatusForBlob();
                string orgOutput = BuildOrgOutboundStatusSummary();

                string diagnostic =
                    "STATUS Manager=online uptime=" + FormatDuration(_managerUptime.Elapsed) +
                    "; " + alts +
                    "; Flipper=" + flipper.DiagnosticText +
                    "; Buddies=" + buddies.DiagnosticText +
                    "; " + buddyActivity;

                Logger.Information(diagnostic);
                RecordDiagnostic(diagnostic);

                string body = BuildStatusBody(
                    target,
                    flipper,
                    buddies,
                    buddyActivity,
                    cloak,
                    recovery,
                    raid,
                    alts,
                    membership,
                    orgOutput);

                string links = BuildBlobLinks(target, "System Status", "Open status", body);
                string healthColor = flipper.IsUsable && buddies.IsUsable
                    ? ColorGood
                    : ColorWarn;

                Reply(
                    target,
                    "<font color='" + healthColor + "'>Manager online</font> " +
                    "<font color='" + ColorMuted + "'>uptime " +
                    EscapeBlobText(FormatDuration(_managerUptime.Elapsed)) +
                    "</font> " + links);
            });
        }

        private string BuildStatusBody(
            ReplyTarget target,
            WorkerLinkStatus flipper,
            WorkerLinkStatus buddies,
            string buddyActivity,
            string cloak,
            string recovery,
            string raid,
            string alts,
            string membership,
            string orgOutput)
        {
            var body = new StringBuilder();
            body.Append(HelpHeader(
                "City Dwellers System Status",
                "A member-facing snapshot of the Manager and the city services it coordinates."));

            body.Append(StatusSection("Manager"));
            body.Append(StatusLine(true, "State", "Online and answering commands"));
            body.Append(StatusLine(true, "Uptime", FormatDuration(_managerUptime.Elapsed)));
            body.Append(StatusLine(true, "Started", _managerStartedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
            body.Append(StatusLine(_charInPlay, "AO session", _charInPlay ? "Character is in play" : "Waiting for character-in-play"));
            body.Append(StatusLine(_devChannelConfirmed, "Live diagnostics", _devChannelConfirmed
                ? "Connected; new events report live"
                : "Not confirmed; events are still kept on disk"));
            body.Append(StatusLine(!IsOrgOutboundDegraded(), "Org output", orgOutput));

            body.Append("\n").Append(StatusSection("City cloak"));
            body.Append(StatusLine(_status != CloakStatus.Unknown, "Cloak", cloak));
            body.Append(StatusLine(true, "Recovery", recovery));
            body.Append(BuildCloakHistoryForBlob(5));

            body.Append("\n").Append(StatusSection("Workers"));
            body.Append(StatusLine(flipper.IsUsable, "Flipper", flipper.PublicText + " — " + flipper.Detail));
            body.Append(StatusLine(buddies.IsUsable, "Buddies", buddies.PublicText + " — " + buddies.Detail));
            body.Append("  <font color='").Append(ColorMuted).Append("'>")
                .Append(EscapeBlobText(buddyActivity)).Append("</font>\n");

            body.Append("\n").Append(StatusSection("Current operations"));
            body.Append(StatusLine(true, "Raid", raid));
            body.Append(StatusLine(true, "Alts", alts));
            body.Append(StatusLine(true, "Members", membership));

            body.Append("\n").Append(StatusSection("Useful commands"));
            body.Append(HelpMenuLine(
                target,
                "cloak",
                "Refresh cloak",
                "Ask Flipper for a cloak observation."));
            body.Append(HelpMenuLine(
                target,
                "raid",
                "Open raid setup",
                "Begin or resume a raid request."));
            body.Append(HelpMenuLine(
                target,
                "help",
                "Open help",
                "Browse the full manual."));

            body.Append("\n<font color='").Append(ColorMuted)
                .Append("'>Worker detail reflects the instant this blob was created. Re-run status to refresh it.</font>");
            return body.ToString();
        }

        private string GetBuddyActivitySummary()
        {
            var request = new WorkerRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = "positions"
            };

            try
            {
                WorkerResponse response = SendWorkerRequest(
                    BuddiesPipeName,
                    request,
                    WorkerConnectTimeoutMs);

                if (!response.Ok)
                    return "Position inventory failed: " + (response.Message ?? "unknown failure") + ".";

                List<BuddyPositionSnapshot> positions =
                    response.Positions ?? new List<BuddyPositionSnapshot>();
                DateTime now = DateTime.UtcNow;
                int reporting = positions.Count(HasPositionReport);
                int fresh = positions.Count(position => IsFreshPositionReport(position, now));
                int inPlay = positions.Count(position => position.InPlay);
                int dead = positions.Count(position => position.Dead);
                return positions.Count + " active; " + reporting + " reporting; " +
                       fresh + " fresh; " + inPlay + " in play; " + dead + " dead.";
            }
            catch (Exception ex)
            {
                return "Position inventory unavailable: " + ex.Message + ".";
            }
        }

        private string BuildMembershipStatusForBlob()
        {
            lock (_membershipSync)
            {
                string sourceAge = _membershipLastSuccessfulFetchUtc.HasValue
                    ? FormatDuration(DateTime.UtcNow - _membershipLastSuccessfulFetchUtc.Value) + " ago"
                    : "never";
                string fetch = _membershipFetchInFlight ? ", refresh in progress" : string.Empty;
                return _officialMembers.Count + " official, " +
                       _permanentMembers.Count + " permanent, " +
                       _liveAddedMembers.Count + " live-added, " +
                       _liveRemovedMembers.Count + " live-removed; roster fetched " +
                       sourceAge + fetch;
            }
        }

        private string BuildCloakHistoryForBlob(int limit)
        {
            List<CloakEventRecord> events = LoadRecentCloakEvents(limit);
            var body = new StringBuilder();
            body.Append("  <font color='").Append(ColorTitle)
                .Append("'>Recent cloak history</font>\n");

            if (events.Count == 0)
            {
                body.Append("    <font color='").Append(ColorMuted)
                    .Append("'>No cloak events have been recorded yet.</font>\n");
                return body.ToString();
            }

            foreach (CloakEventRecord record in events)
            {
                string actor = string.IsNullOrWhiteSpace(record.Actor)
                    ? "system"
                    : record.Actor;
                string color = record.NewStatus == CloakStatus.Enabled
                    ? ColorGood
                    : record.NewStatus == CloakStatus.Disabled
                        ? ColorWarn
                        : ColorMuted;
                body.Append("    <font color='").Append(color).Append("'>")
                    .Append(EscapeBlobText(record.NewStatus.ToString()))
                    .Append("</font> ")
                    .Append(EscapeBlobText(record.OccurredUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)))
                    .Append(" by ")
                    .Append(EscapeBlobText(actor))
                    .Append(" <font color='").Append(ColorMuted).Append("'>(")
                    .Append(EscapeBlobText(record.Source ?? record.EventType ?? "unknown"))
                    .Append(")</font>\n");
            }

            return body.ToString();
        }

        private List<CloakEventRecord> LoadRecentCloakEvents(int limit)
        {
            var recent = new List<CloakEventRecord>();
            if (limit <= 0 || string.IsNullOrWhiteSpace(_eventsPath) || !File.Exists(_eventsPath))
                return recent;

            try
            {
                foreach (string line in File.ReadLines(_eventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        CloakEventRecord record =
                            JsonConvert.DeserializeObject<CloakEventRecord>(line);
                        if (record == null)
                            continue;

                        recent.Add(record);
                        recent.Sort((left, right) =>
                            right.OccurredUtc.CompareTo(left.OccurredUtc));
                        if (recent.Count > limit)
                            recent.RemoveAt(recent.Count - 1);
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Unable to read cloak history for status: " + ex.Message);
            }

            return recent;
        }

        private string StatusSection(string title)
        {
            return "<font color='" + ColorTitle + "'><b>" +
                   EscapeBlobText(title) + "</b></font>\n";
        }

        private string StatusLine(bool good, string label, string detail)
        {
            string color = good ? ColorGood : ColorWarn;
            return "  <font color='" + color + "'>●</font> " +
                   "<font color='" + ColorText + "'>" +
                   EscapeBlobText(label) + ":</font> " +
                   EscapeBlobText(detail) + "\n";
        }

        private void BeginDiagnosticDump(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "dump"));
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string directory = Path.Combine(_dataDir, "diagnostic-dumps");
                    Directory.CreateDirectory(directory);
                    string filename =
                        "apcmanager-dump-" +
                        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                        ".log";
                    string path = Path.Combine(directory, filename);

                    RecordDiagnostic("DUMP requested by " + senderName + ".");

                    var header = new StringBuilder();
                    header.AppendLine("City Dwellers Manager diagnostic dump");
                    header.AppendLine("CreatedUtc: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    header.AppendLine("RequestedBy: " + senderName);
                    header.AppendLine("ManagerStartedUtc: " + _managerStartedUtc.ToString("O", CultureInfo.InvariantCulture));
                    header.AppendLine("ManagerUptime: " + FormatDuration(_managerUptime.Elapsed));
                    header.AppendLine("Cloak: " + BuildCloakStatusSummary());
                    header.AppendLine("Raid: " + BuildRaidStatusSummary());
                    header.AppendLine("Alts: " + BuildAltStatusSummary());
                    header.AppendLine("Membership: " + BuildMembershipStatusForBlob());
                    header.AppendLine("OrgOutput: " + BuildOrgOutboundStatusSummary());
                    header.AppendLine();
                    header.AppendLine("Diagnostic log:");

                    lock (_devSync)
                    {
                        File.WriteAllText(path, header.ToString());
                        if (File.Exists(_diagnosticLogPath))
                            File.AppendAllText(path, File.ReadAllText(_diagnosticLogPath));
                        else
                            File.AppendAllLines(path, _diagnosticHistory.ToArray());
                    }

                    Logger.Warning("Manager diagnostic dump created: " + path);
                    string body =
                        HelpHeader(
                            "Diagnostic dump created",
                            "A timestamped copy of Manager state and retained diagnostics is ready to collect.") +
                        "<font color='" + ColorTitle + "'>File</font>\n" +
                        EscapeBlobText(path) + "\n\n" +
                        "<font color='" + ColorMuted + "'>The live guest feed continues with new events only. No backlog was sent to chat.</font>";
                    Reply(target, BuildBlobLinks(target, "Diagnostic Dump", "Open dump details", body));
                }
                catch (Exception ex)
                {
                    Logger.Error("Unable to create Manager diagnostic dump: " + ex);
                    Reply(target, "Diagnostic dump failed: " + ex.Message);
                }
            });
        }

        private string BuildBlobLinks(
            ReplyTarget target,
            string title,
            string label,
            string content)
        {
            List<string> pages = PaginateBlob(content, BlobPageSize(target));
            var links = new List<string>();

            for (int index = 0; index < pages.Count; index++)
            {
                string pageTitle = pages.Count == 1
                    ? title
                    : title + " — Page " + (index + 1) + "/" + pages.Count;
                string pageLabel = pages.Count == 1
                    ? label
                    : label + " " + (index + 1) + "/" + pages.Count;
                string payload =
                    "<font color='" + ColorTitle + "'><b>" +
                    EscapeBlobText(pageTitle) + "</b></font>\n\n" +
                    pages[index];
                links.Add(
                    "<a href=\"text://" + EscapeTextUri(payload) + "\">" +
                    "<font color='" + ColorCommand + "'>[" +
                    EscapeBlobText(pageLabel) + "]</font></a>");
            }

            return string.Join(" ", links);
        }

        private int BlobPageSize(ReplyTarget target)
        {
            if (target.IsOrg)
                return OrgBlobPageSize;
            if (target.IsGuest)
                return GuestBlobPageSize;
            return TellBlobPageSize;
        }

        private List<string> PaginateBlob(string content, int maxLength)
        {
            var pages = new List<string>();
            string remaining = content ?? string.Empty;

            while (remaining.Length > maxLength)
            {
                int split = remaining.LastIndexOf("\n\n", maxLength, StringComparison.Ordinal);
                if (split < maxLength / 2)
                    split = remaining.LastIndexOf('\n', maxLength);
                if (split < maxLength / 2)
                    split = remaining.LastIndexOf(' ', maxLength);
                if (split <= 0)
                    split = maxLength;

                pages.Add(remaining.Substring(0, split).Trim());
                remaining = remaining.Substring(split).TrimStart();
            }

            pages.Add(remaining.Trim());
            return pages;
        }

        private static string EscapeTextUri(string value)
        {
            return (value ?? string.Empty)
                .Replace("\"", "&quot;");
        }

        private static string EscapeHref(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("'", "&#39;")
                .Replace("\"", "&quot;");
        }

        private static string EscapeBlobText(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}
