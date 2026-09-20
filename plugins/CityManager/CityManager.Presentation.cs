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
        // Owner-adjustable organization blob budget baseline in UTF-8 bytes.
        // Runtime delivery feedback moves the current budget around this default;
        // changing 5200 here still changes the baseline after the next rebuild.
        private const int OrgBlobPageSize = 5200;
        private const int OrgBlobProbeQuantum = 50;
        private const int OrgBlobMinProbeStep = 250;
        private const int OrgBlobProbeEvidenceMargin = 1024;
        private const int OrgBlobMinPageSize = 512;
        private const int OrgBlobMaxPageSize = 32768;
        private const int GuestBlobPageSize = 8000;
        private const int TellBlobPageSize = 7200;

        private const string ColorTitle = "#89D2E8";
        private const string ColorGood = "#00DE42";
        private const string ColorWarn = "#F79410";
        private const string ColorBad = "#FF5050";
        private const string ColorCommand = "#F5C542";
        private const string ColorMuted = "#A0A0A0";
        private const string ColorText = "#FFFFFF";

        // BuildBlobLinks registers the logical page behind each rendered org link.
        // Reply() consumes the matching template before send. If exact echo proof
        // later fails, the transport can rebuild that page at a smaller current
        // budget without re-running the command that produced it.
        private readonly object _orgBlobTemplateSync = new object();
        private readonly Dictionary<string, OrgBlobRetryTemplate> _orgBlobRetryTemplates =
            new Dictionary<string, OrgBlobRetryTemplate>(StringComparer.Ordinal);

        private sealed class OrgBlobRetryTemplate
        {
            public string Link;
            public string Title;
            public string Label;
            public string Content;
            public string HeadingMarkup;
            public DateTime CreatedUtc;
        }

        private sealed class OrgReplyRetryPlan
        {
            public ReplyTarget Target;
            public string RawText;
            public bool IsBlob;
            public string Prefix;
            public string Suffix;
            public OrgBlobRetryTemplate Blob;
        }

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

            List<string> links = BuildBlobLinks(target, title, "Help: " + title, body);
            Reply(
                target,
                links.Select(link => "<font color='" + ColorTitle + "'>City Dwellers Help</font> " + link));
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
                        HelpSyntaxLine(target, "buffers", "Show froob buffer readiness and queue counts.") +
                        "\n<font color='" + ColorMuted + "'>Words in [brackets] are values you supply. " +
                        "A vertical bar means choose one option. Commands and buttons are case-insensitive.</font>";
                    return true;

                case "online":
                    title = "Online";
                    body = HelpHeader("Online", "Organization presence reported by Bobsan and observed guest-channel speakers.") +
                        HelpSyntaxLine(target, "online", "Show both lists with counts. Guest observations may miss silent joins or departures.");
                    return true;

                case "changelog":
                    title = "Changelog";
                    body = HelpHeader("Changelog", "Show the latest 25 owner-written entries.") +
                        HelpSyntaxLine(target, "changelog", "Open the changelog.");
                    return true;

                case "items":
                case "i":
                case "itemid":
                    title = "Items";
                    body = HelpHeader("Items", "Search your local item database. Alias: i.") +
                        HelpSyntaxLine(target, "items [QL] <name words> [-excluded-word] [--page N]", "All name words must match; minus excludes a word. QL selects within observed low/high ranges, or exact QL for unpaired templates.") +
                        HelpSyntaxLine(target, "itemid <AOID>", "Open an exact template with NoDrop, Unique, Stackable and splitting attributes.") +
                        "\nExamples: #items combined commando; #items 300 combined; #itemid 257110.\n" +
                        "Ranges use actual low/high pairs from policy, ledger and observed trades. Unpaired templates remain exact; in-game availability is not inferred.";
                    return true;

                case "bankid":
                    title = "Bank Terminal";
                    body = CommandHelp(target, "bankid [Instance]",
                        "Show or update the office bank terminal's current Instance number.",
                        "Copy the decimal Instance shown in game. Saving retries closed banks immediately; no restart. Check status afterward. Reposting the same number retries it too.",
                        "Admins or Squad Commander and higher, including verified officer alts",
                        "Example: #bankid 1477725977. The value is saved across restarts.");
                    return true;

                case "status":
                    title = "Status";
                    body = CommandHelp(
                        target,
                        "status",
                        "Open one operational view of the Manager, cloak, workers, banker occupancy, storage work, withdrawals, recovery, tell queue, raid workflow, alt cache, and membership roster.",
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

                case "cru":
                    title = "CRU Pickup";
                    body = CommandHelp(target, "cru", "Request one Upgraded Controller Recompiler Unit.",
                        "Wait for the ready tell, then trade with Kbcentral within three minutes. CRU shares your normal pickup order and can be collected with other ready items. Donate CRU by trading with Kbcentral; it stays stacked there.",
                        "Athen Paladins member", "CRU is not included in stock listings or donor totals.");
                    return true;

                case "get":
                case "withdraw":
                    title = "Bank Pickup";
                    body = CommandHelp(
                        target,
                        "get [AO item ID]",
                        "Add an available item to your order: up to three items per member and four orders across the bank.",
                        "Each addition and arrival refreshes your three-minute pickup clock. Wait for the ready tell, then trade with Kbcentral and confirm normally to collect ready items. Known alts share your order. Central may ask you to retry during an internal transfer. Donations can continue while orders await pickup.",
                        "Athen Paladins member",
                        "Alias: withdraw. Reserved copies disappear from available stock. Confirm the player trade dialog normally; delivery is recorded only after AO Finished and inventory verification. Uncollected items return to storage.");
                    return true;

                case "lost":
                case "found":
                    title = topic == "lost" ? "Lost items" : "Found items";
                    body = CommandHelp(target, topic + " [item name]",
                        topic == "lost" ? "Browse unexpectedly missing items; intentional deletions and deliveries are excluded."
                            : "Browse current ledger items without a recorded donor.",
                        "Counts include all matches; windows show the newest 25. Search with words from the item name.",
                        "Athen Paladins member", "Read-only bookkeeping; these commands do not change stock.");
                    return true;

                case "pickups":
                case "takers":
                    title = "Pickup Records";
                    body = CommandHelp(target, "pickups [last|top|member|item <name or AOID>]",
                        "Show confirmed pickup totals and history.",
                        "last: latest 25 items; top: top 25 recipients; member: total and latest 10; item: name fragment or AOID, latest 25 matches.",
                        "Athen Paladins member", "Alias: takers. Includes CRU; alts are grouped by main. Only confirmed deliveries count. Times are UTC.");
                    return true;

                case "donor":
                case "donors":
                    title = "Donation Records";
                    body = CommandHelp(target, "donor [top|last|member]",
                        "Browse donor totals and recorded donation history, including known alts.",
                        "Top shows donor rankings; last shows the latest 25 donations; a member name shows their latest 10. Long results arrive in separate numbered windows.",
                        "Athen Paladins member", "Item links show quality and icons in the window.");
                    return true;

                case "bank":
                case "bankers":
                case "donation":
                    title = "City Bankers";
                    body = HelpHeader(title, "Stock, donations, pickup and current work.") +
                        HelpMenuLine(target, "lost", "Lost", "Unexpectedly missing items and their original records.") +
                        HelpMenuLine(target, "found", "Found", "Current items with no recorded donor.") +
                        HelpMenuLine(target, "stock", "Stock", "Browse available items; reserved copies are hidden.") +
                        HelpMenuLine(target, "phatz", "Phatz", "All available Phatz, counted and linked.") +
                        HelpMenuLine(target, "help get", "Pickup help", "Up to three items per order; three-minute pickup window.") +
                        HelpMenuLine(target, "cru", "CRU", "Collect one CRU from Central.") +
                        HelpMenuLine(target, "donor", "Donors", "Donation totals and history.") +
                        HelpMenuLine(target, "pickups", "Pickups", "Pickup totals, latest, top, member and item history. Alias: takers.") +
                        HelpMenuLine(target, "status", "Live status", "Banker readiness, occupancy, storage work, withdrawals, recovery and tell queue.") +
                        "\nTrade with Kbcentral to donate up to 10 accepted items. Item notices identify what will be stored or deleted as excess. Accept when finished editing, then confirm the dialog normally.\n" +
                        "Removing an acceptance entry does not itself delete stored items. Lowering a positive limit leaves existing stock alone; future excess donations are deleted after verified receipt.\n";
                    return true;

                case "stock":
                case "symb":
                case "symbs":
                case "spirit":
                case "spirits":
                case "dyna":
                case "nano":
                case "nanos":
                case "phat":
                case "phatz":
                    title = "Bank Stock";
                    body = CommandHelp(
                        target,
                        "stock",
                        "Show the complete CityBankers inventory overview.",
                        "Search with symb [family [slot [QL]]], spirit [slot [QL]], dyna [name|QL], or phatz [name|QL]. Bare phatz lists every stocked type with copy counts, item icons and GET links. Long windows arrive as separate numbered messages.",
                        "Athen Paladins member",
                        isAdmin
                            ? "Administrators: phatz add [linked AO item] [-1|positive max], phatz remove [AOID], and phatz list manage accepted Phatz items."
                            : "Aliases: symbs; spirits; nano/nanos; phat.");
                    return true;

                case "guest":
                case "join":
                case "leave":
                    title = "Guest Channel";
                    body = BuildGuestHelp(target);
                    return true;

                case "shutdown":
                    title = "Shutdown City Dwellers";
                    body = CommandHelp(
                        target,
                        "shutdown",
                        "Stop all City Dwellers bots and the unified host.",
                        "One command takes effect without a confirmation prompt. The actor, authority, channel and UTC time are logged before stopping.",
                        "Squad Commander or higher (including verified officer alts), or administrator",
                        "This is terminal: a manual start is required. Use restart to recycle only Manager.");
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
                        "Only Manager reconnects; the other City Dwellers bots remain online.");
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
                        "Use dump incidents to list recent transaction problems, then dump incident-ID for a small report. Problem reports are also saved automatically under data/incident-dumps. Bare dump retains the full diagnostic snapshot.",
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
            body.Append(HelpMenuLine(target, "help bankid", "Bank terminal", "Update a changed bank terminal Instance in game."));
            body.Append(HelpMenuLine(target, "help bankers", "City Bankers", "Donations, stock, pickups, storage work and tell delivery."));
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
                "stock",
                "Show all CityBankers stock families."));
            body.Append(HelpSyntaxLine(target,
                "symb [family [slot [targetQl]]]", "Search symbiants. Alias: symbs."));
            body.Append(HelpSyntaxLine(target,
                "spirit [slot [targetQl]]", "Search spirits like symbiants. Alias: spirits."));
            body.Append(HelpSyntaxLine(target,
                "dyna [name|QL]", "Search dyna nanos and instruction discs. Aliases: nano, nanos."));
            body.Append(HelpSyntaxLine(target,
                "phatz [name|QL]", "List all Phatz with copy counts and GET links, or filter by name/QL. Alias: phat."));
            body.Append(HelpSyntaxLine(
                target,
                "donor [top|last|member]",
                "Browse all-time donor rankings and donation history."));
            body.Append(HelpSyntaxLine(
                target,
                "get [AO item ID]",
                "Reserve an item and collect it from Kbcentral within three minutes. Alias: withdraw."));
            body.Append(HelpSyntaxLine(target, "shutdown", "SC+/admins: stop ALL bots; manual start required."));
            body.Append(HelpSyntaxLine(target, "pickups [last|top|member|item <name or AOID>]", "Confirmed pickup totals and history. Alias: takers."));
            body.Append(HelpSyntaxLine(target, "cru", "Collect one CRU from Kbcentral within three minutes."));
            body.Append(HelpSyntaxLine(target, "raid", "Open or resume raid setup."));
            body.Append(HelpSyntaxLine(target, "raid status", "View current raid information."));
            body.Append(HelpSyntaxLine(target, "cancel [raid-token]", "Cancel your active raid."));
            body.Append(HelpSyntaxLine(target, "online", "Show known org presence and observed guests."));
            body.Append(HelpSyntaxLine(target, "changelog", "Show the latest 25 owner-written entries."));
            body.Append(HelpSyntaxLine(target, "items [QL] <name words>", "Search item templates. Alias: i; itemid <AOID> shows attributes."));
            body.Append(HelpSyntaxLine(target, "alts [character]", "Show known mains and alts."));
            body.Append(HelpSyntaxLine(target, "join", "Ask for a guest-channel invite."));
            body.Append(HelpSyntaxLine(target, "leave", "Leave the guest channel."));

            if (isAdmin)
            {
                body.Append("\n<font color='").Append(ColorTitle).Append("'>Administrator commands</font>\n");
                body.Append(HelpSyntaxLine(target, "invite [character]", "Invite a character to guest chat."));
                body.Append(HelpSyntaxLine(target, "kick [character]", "Remove a character from guest chat."));
                body.Append(HelpSyntaxLine(target, "positions", "Open live Buddy positions."));
                body.Append(HelpSyntaxLine(target, "inventory [role|character]", "Inspect live banker inventory. Alias: inv."));
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
                body.Append(HelpSyntaxLine(target,
                    "phatz add [linked item] [-1|positive max]", "Accept the linked item family on Kbphatz; -1 keeps all. A finite limit counts known QL variants together. Excess incoming copies are deleted after receipt; existing stock is not trimmed."));
                body.Append(HelpSyntaxLine(target,
                    "phatz remove [AOID]", "Stop accepting that known Phatz family; this command does not delete stored items."));
                body.Append(HelpSyntaxLine(target,
                    "phatz list", "Open the accepted Phatz list with remove buttons."));
            }

            return body.ToString();
        }

        private string BuildRaidHelp(ReplyTarget target)
        {
            return HelpHeader(
                    "Raids",
                    "The Manager guides setup in a blob, coordinates Buddies and Flipper, and keeps the owner informed through each stage.") +
                HelpSyntaxLine(target, "raid", "Open or resume raid setup in organization or guest chat; linked alts share ownership.") +
                HelpSyntaxLine(target, "raid status", "View the current raid, requester and progress.") +
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
            string renderedSyntax = syntax.IndexOf('[') >= 0 ||
                string.Equals(syntax, "shutdown", StringComparison.OrdinalIgnoreCase)
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
            QueuePublicWork(target, () =>
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
                string raid = BuildRaidStatusSummary(true);
                string alts = BuildAltStatusSummary();
                string membership = BuildMembershipStatusForBlob();
                string orgOutput = BuildOrgOutboundStatusSummary();
                BankerStatusSnapshot bankers = BuildBankerStatusSnapshot();

                string diagnostic =
                    "STATUS Manager=online uptime=" + FormatDuration(_managerUptime.Elapsed) +
                    "; " + alts +
                    "; Flipper=" + flipper.DiagnosticText +
                    "; Buddies=" + buddies.DiagnosticText +
                    "; Bankers=" + bankers.DiagnosticText +
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
                    orgOutput,
                    bankers);

                List<string> links = BuildBlobLinks(target, "City Dwellers Status", "Open status", body, BuildStatusHeading());
                string healthColor = flipper.IsUsable && buddies.IsUsable && bankers.IsUsable
                    ? ColorGood
                    : ColorWarn;

                Reply(
                    target,
                    links.Select(link => "<font color='" + healthColor + "'>Manager online</font> " +
                    "<font color='" + ColorMuted + "'>uptime " +
                    EscapeBlobText(FormatDuration(_managerUptime.Elapsed)) +
                    "</font> " + link));
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
            string orgOutput,
            BankerStatusSnapshot bankers)
        {
            var body = new StringBuilder();
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

            body.Append("\n").Append(StatusSection("Workers"));
            body.Append(StatusLine(flipper.IsUsable, "Flipper", flipper.PublicText + " - " + flipper.Detail));
            body.Append(StatusLine(buddies.IsUsable, "Buddies", buddies.PublicText + " - " + buddies.Detail));
            body.Append("  <font color='").Append(ColorMuted).Append("'>")
                .Append(EscapeBlobText(buddyActivity)).Append("</font>\n");

            body.Append("\n").Append(StatusSection("City Bankers"));
            body.Append(bankers.Blob);

            body.Append("\n").Append(StatusSection("Current operations"));
            body.Append(StatusLine(true, "Raid", raid, true));
            body.Append(StatusLine(true, "Alts", alts));
            body.Append(StatusLine(true, "Members", membership));

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

        private string BuildStatusHeading()
        {
            string local = BuildIdentity.GetComponents().First(build => build.Name == "CityDwellers").Loaded ?? "unknown";
            RepositoryUpdateSnapshot update = RepositoryUpdates.Read();
            string online = update.LatestRevision ?? (update.State == "checking" ? "checking" : "unknown");
            return "<font color='" + ColorTitle + "'><b>City Dwellers Status</b></font> - " +
                BuildRevisionForBlob(local, update.State == "outdated") + " - " +
                BuildRevisionForBlob(online, update.State == "unavailable");
        }

        private string BuildRevisionForBlob(string revision, bool differs)
        {
            string color = revision == "checking" ? ColorCommand :
                !BuildIdentity.IsKnown(revision) ? ColorBad :
                differs || revision.EndsWith("-modified", StringComparison.Ordinal) ? ColorWarn : ColorGood;
            return "<font color='" + color + "'>" +
                EscapeBlobText(BuildIdentity.ShortRevision(revision)) + "</font>";
        }

        private void ReplyWithCloakHistory(ReplyTarget target, string summary)
        {
            var body = new StringBuilder();
            List<CloakEventRecord> events = LoadRecentCloakEvents(25, true);
            foreach (CloakEventRecord record in events)
            {
                string actor = string.IsNullOrWhiteSpace(record.Actor) ? "Unknown" : record.Actor;
                bool enabled = record.NewStatus == CloakStatus.Enabled;
                body.Append("<font color='").Append(ColorCommand).Append("'>")
                    .Append(UtcTimestamp.Normalize(record.OccurredUtc).ToString(
                        "dd-MMM-yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture))
                    .Append("</font> - <font color='").Append(ColorTitle).Append("'>")
                    .Append(EscapeBlobText(actor))
                    .Append("</font> <font color='").Append(enabled ? ColorGood : ColorBad)
                    .Append("'>").Append(enabled ? "enabled" : "disabled")
                    .Append("</font> cloak\n");
            }
            if (events.Count == 0)
                body.Append("No cloak changes have been observed yet.");

            Reply(target, BuildBlobLinks(target, "Cloak History", "Cloak History", body.ToString())
                .Select(link => summary + " " + link));
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

        private List<CloakEventRecord> LoadRecentCloakEvents(int limit, bool announcementsOnly = false)
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
                        // Probes/cache reads identify the observer, not the character who flipped it.
                        if (announcementsOnly &&
                            !((record.EventType == "cloak_on_announcement" && record.NewStatus == CloakStatus.Enabled) ||
                              (record.EventType == "cloak_off_announcement" && record.NewStatus == CloakStatus.Disabled)))
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

        private string StatusLine(bool good, string label, string detail, bool detailIsMarkup = false)
        {
            string color = good ? ColorGood : ColorWarn;
            return "  <font color='" + color + "'>" + (good ? "OK" : "WAIT") + "</font> " +
                   "<font color='" + ColorText + "'>" +
                   EscapeBlobText(label) + ":</font> " +
                   CityBankers.Shared.CityBankersChatPalette.StyleMarkup(detailIsMarkup ? detail : EscapeBlobText(detail)) + "\n";
        }

        private void BeginDiagnosticDump(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            if (parts.Length == 2)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (string.Equals(parts[1], "incidents", StringComparison.OrdinalIgnoreCase))
                        {
                            var incidents = CityDwellers.Shared.IncidentJournal.Recent(_dataDir);
                            var body = new StringBuilder("Recent transaction incidents (UTC)\n\n");
                            foreach (var incident in incidents)
                                body.Append(EscapeBlobText(incident.UpdatedUtc.ToString("O") + " " + incident.Trace))
                                    .Append("\n").Append(HelpMenuLine(target, "dump " + incident.Id, incident.Id, "Export this incident and related evidence.")).Append("\n");
                            if (incidents.Count == 0) body.Append("No incidents recorded yet.");
                            Reply(target, BuildBlobLinks(target, "Transaction incidents", "View incidents", body.ToString()));
                        }
                        else
                        {
                            string path = CityDwellers.Shared.IncidentJournal.Export(_dataDir, parts[1]);
                            Reply(target, BuildBlobLinks(target, "Incident dump", "File ready",
                                "Transaction evidence saved to:\n" + EscapeBlobText(path)));
                        }
                    }
                    catch (Exception ex) { Reply(target, "Incident dump unavailable: " + ex.Message); }
                });
                return;
            }
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
                    foreach (string build in BuildIdentity.DescribeComponents(true))
                        header.AppendLine("Build: " + build);
                    header.AppendLine("RepositoryUpdate: " + JsonConvert.SerializeObject(RepositoryUpdates.Read()));
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

        private List<string> BuildBlobLinks(
            ReplyTarget target,
            string title,
            string label,
            string content,
            string headingMarkup = null)
        {
            // Guest/tell keep fixed limits; organization uses the current adaptive
            // budget. Reserve room for the escaped title/link, page numbering and
            // callers' short summaries.
            int envelope = Encoding.UTF8.GetByteCount(EscapeTextUri(title ?? string.Empty)) +
                Encoding.UTF8.GetByteCount(EscapeBlobText(label ?? string.Empty)) + 512;
            if (headingMarkup != null)
                envelope += Encoding.UTF8.GetByteCount(EscapeTextUri(headingMarkup));
            int pageBudget = BlobPageSize(target);
            List<string> pages = PaginateBlob(content, Math.Max(256, pageBudget - envelope));
            var links = new List<string>();

            for (int index = 0; index < pages.Count; index++)
            {
                string pageTitle = pages.Count == 1
                    ? title
                    : title + " - Page " + (index + 1) + "/" + pages.Count;
                string pageLabel = pages.Count == 1
                    ? label
                    : label + " " + (index + 1) + "/" + pages.Count;
                string heading = headingMarkup ??
                    ("<font color='" + ColorTitle + "'><b>" + EscapeBlobText(pageTitle) + "</b></font>");
                if (headingMarkup != null && pages.Count > 1)
                    heading += " - Page " + (index + 1) + "/" + pages.Count;
                string payload = heading + "\n\n" +
                    CityBankers.Shared.CityBankersChatPalette.WhiteBaseMarkup(pages[index]);
                string link =
                    "<a href=\"text://" + EscapeTextUri(payload) + "\">" +
                    "<font color='" + ColorCommand + "'>[" +
                    EscapeBlobText(pageLabel) + "]</font></a>";
                links.Add(link);
                RegisterOrgBlobRetryTemplate(
                    target,
                    link,
                    pageTitle,
                    pageLabel,
                    pages[index],
                    heading);
            }

            return links;
        }

        private int BlobPageSize(ReplyTarget target)
        {
            if (target.IsOrg)
                return CurrentOrgBlobPageSize();
            if (target.IsGuest)
                return GuestBlobPageSize;
            return TellBlobPageSize;
        }

        private void RegisterOrgBlobRetryTemplate(
            ReplyTarget target,
            string link,
            string title,
            string label,
            string content,
            string headingMarkup)
        {
            if (target == null || !target.IsOrg || string.IsNullOrEmpty(link))
                return;

            var template = new OrgBlobRetryTemplate
            {
                Link = link,
                Title = title ?? string.Empty,
                Label = label ?? string.Empty,
                Content = content ?? string.Empty,
                HeadingMarkup = headingMarkup,
                CreatedUtc = DateTime.UtcNow
            };

            lock (_orgBlobTemplateSync)
            {
                _orgBlobRetryTemplates[link] = template;
                if (_orgBlobRetryTemplates.Count <= 512)
                    return;

                foreach (string old in _orgBlobRetryTemplates
                    .OrderBy(pair => pair.Value.CreatedUtc)
                    .Take(_orgBlobRetryTemplates.Count - 384)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    _orgBlobRetryTemplates.Remove(old);
                }
            }
        }

        private OrgReplyRetryPlan CaptureOrgReplyRetryPlan(
            ReplyTarget target,
            string rawText)
        {
            var plan = new OrgReplyRetryPlan
            {
                Target = target,
                RawText = rawText ?? string.Empty
            };

            if (target == null || !target.IsOrg || string.IsNullOrEmpty(rawText))
                return plan;

            lock (_orgBlobTemplateSync)
            {
                string matchedLink = null;
                int matchedIndex = int.MaxValue;
                foreach (string link in _orgBlobRetryTemplates.Keys)
                {
                    int index = rawText.IndexOf(link, StringComparison.Ordinal);
                    if (index >= 0 && index < matchedIndex)
                    {
                        matchedLink = link;
                        matchedIndex = index;
                    }
                }

                if (matchedLink == null)
                    return plan;

                OrgBlobRetryTemplate template = _orgBlobRetryTemplates[matchedLink];
                _orgBlobRetryTemplates.Remove(matchedLink);
                plan.IsBlob = true;
                plan.Blob = template;
                plan.Prefix = rawText.Substring(0, matchedIndex);
                plan.Suffix = rawText.Substring(matchedIndex + matchedLink.Length);
                return plan;
            }
        }

        private List<string> RebuildOrgReplyRetry(OrgReplyRetryPlan plan)
        {
            if (plan == null)
                return new List<string>();

            if (!plan.IsBlob || plan.Blob == null)
                return new List<string> { plan.RawText ?? string.Empty };

            List<string> links = BuildBlobLinks(
                plan.Target,
                plan.Blob.Title,
                plan.Blob.Label,
                plan.Blob.Content,
                plan.Blob.HeadingMarkup);

            return links.Select(link =>
                (plan.Prefix ?? string.Empty) +
                link +
                (plan.Suffix ?? string.Empty)).ToList();
        }

        private List<string> PaginateBlob(string content, int maxLength)
        {
            var pages = new List<string>();
            var page = new StringBuilder();
            int pageBytes = 0;
            // Page only between complete rows, never inside a link or font tag.
            foreach (string line in (content ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                int lineBytes = Encoding.UTF8.GetByteCount(EscapeTextUri(line)) + 1;
                if (lineBytes > maxLength)
                    throw new InvalidOperationException("A blob row exceeds the channel page budget.");
                if (pageBytes + lineBytes > maxLength && page.Length > 0)
                {
                    pages.Add(page.ToString().Trim());
                    page.Clear();
                    pageBytes = 0;
                }
                page.Append(line).Append('\n');
                pageBytes += lineBytes;
            }
            if (page.Length > 0 || pages.Count == 0) pages.Add(page.ToString().Trim());
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
