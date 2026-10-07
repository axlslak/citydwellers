using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private const int BufferControlTimeoutMilliseconds = 30000;
        private const int BufferPublicCommandTimeoutMilliseconds = 120000;
        private static readonly TimeSpan BufferAuthorityPublishInterval =
            TimeSpan.FromSeconds(1);
        private DateTime _nextBufferAuthorityPublishUtc = DateTime.MinValue;

        private DateTime _nextPaidTeamTick;
        private string _paidTeamCharacter;
        private bool _paidTeamOwnsTeam, _paidTeamStarted, _paidTeamClosing;
        private int _paidTeamInviteId;
        private DateTime _paidTeamInviteUntil;
        private DateTime _nextPaidTeamCleanup;

        // All AO team operations stay on Manager's update thread. The host only
        // consumes the fresh membership snapshot published from here.
        private void TickPaidFixerTeam()
        {
            var memory = ManagerMemory.Current;
            var now = DateTime.UtcNow;
            if (!Client.InPlay || DynelManager.LocalPlayer == null)
            {
                memory.PublishPaidFixerTeam(null, 0, new int[0]);
                return;
            }
            if (now < _nextPaidTeamTick) return;
            _nextPaidTeamTick = now.AddMilliseconds(500);
            try
            {
                var providers = memory.ReadPaidBuffers().Where(p => PaidBufferCatalogue.UsesManagerTeam(p.Profession)).ToArray();
                var pending = memory.PendingBufferPublicCommands(128)
                    .Where(r => providers.Any(p => string.Equals(p.Character, r.TargetCharacter,
                        StringComparison.OrdinalIgnoreCase))).ToArray();
                if (_paidTeamCharacter == null)
                {
                    _paidTeamCharacter = providers.FirstOrDefault(p => p.Running)?.Character ??
                        pending.FirstOrDefault()?.TargetCharacter;
                    if (_paidTeamCharacter == null)
                    {
                        memory.PublishPaidFixerTeam(null, 0, new int[0]);
                        return;
                    }
                }
                var provider = providers.FirstOrDefault(p => string.Equals(p.Character,
                    _paidTeamCharacter, StringComparison.OrdinalIgnoreCase));
                var requests = pending.Where(r => string.Equals(r.TargetCharacter,
                    _paidTeamCharacter, StringComparison.OrdinalIgnoreCase)).ToArray();
                bool running = provider != null && provider.Running;
                if (running) _paidTeamStarted = true;
                if ((_paidTeamStarted && !running) || provider == null || provider.Blocked ||
                    (!running && requests.Length == 0 && now >= _paidTeamInviteUntil))
                    _paidTeamClosing = true;

                if (_paidTeamClosing)
                {
                    memory.PublishPaidFixerTeam(null, 0, new int[0]);
                    if (_paidTeamOwnsTeam && Team.IsInTeam)
                    {
                        if (now >= _nextPaidTeamCleanup)
                        {
                            _nextPaidTeamCleanup = now.AddSeconds(2);
                            Team.Disband();
                            Team.LeaveTeam();
                        }
                        return;
                    }
                    _paidTeamCharacter = null;
                    _paidTeamOwnsTeam = _paidTeamStarted = _paidTeamClosing = false;
                    _paidTeamInviteId = 0;
                    _paidTeamInviteUntil = DateTime.MinValue;
                    return;
                }

                // Never commandeer an unrelated team Manager was already in.
                if (!_paidTeamOwnsTeam && Team.IsInTeam)
                {
                    memory.PublishPaidFixerTeam(null, 0, new int[0]);
                    return;
                }
                var members = Team.IsInTeam ? Team.Members
                    .Where(m => m.Identity != DynelManager.LocalPlayer.Identity)
                    .Select(m => m.Identity.Instance).ToArray() : new int[0];
                memory.PublishPaidFixerTeam(_paidTeamCharacter,
                    DynelManager.LocalPlayer.Identity.Instance, members);
                if (_paidTeamInviteId != 0 && members.Contains(_paidTeamInviteId))
                {
                    DevTrace("PAID TEAM team membership confirmed: " + _paidTeamInviteId + ".");
                    _paidTeamInviteId = 0;
                    _paidTeamInviteUntil = DateTime.MinValue;
                }
                if (running)
                {
                    var fixer = DynelManager.Players.FirstOrDefault(p => string.Equals(p.Name,
                        _paidTeamCharacter, StringComparison.OrdinalIgnoreCase));
                    if (fixer != null && members.Length > 0 && !members.Contains(fixer.Identity.Instance) &&
                        (_paidTeamInviteId != fixer.Identity.Instance || now >= _paidTeamInviteUntil))
                        InvitePaidTeamMember(fixer.Identity, now, "buffer " + _paidTeamCharacter);
                    return;
                }
                if (_paidTeamInviteId != 0 && now < _paidTeamInviteUntil) return;
                _paidTeamInviteId = 0;

                // Four customers + Manager + buffer = one normal six-person team.
                if (members.Length >= 4) return;
                var next = requests.FirstOrDefault(r => !members.Contains(unchecked((int)r.SenderId)) &&
                    DynelManager.Players.Any(p => p.Identity.Instance == unchecked((int)r.SenderId)));
                if (next != null)
                    InvitePaidTeamMember(new Identity(IdentityType.SimpleChar, unchecked((int)next.SenderId)),
                        now, "requester " + next.SenderName);
            }
            catch (Exception ex)
            {
                memory.PublishPaidFixerTeam(null, 0, new int[0]);
                Logger.Warning("Paid buffer team coordinator: " + ex.Message);
            }
        }

        private void InvitePaidTeamMember(Identity identity, DateTime now, string description)
        {
            _paidTeamOwnsTeam = true;
            _paidTeamInviteId = identity.Instance;
            _paidTeamInviteUntil = now.AddSeconds(15);
            Team.Invite(identity);
            DevTrace("PAID TEAM Manager invited " + description + ".");
        }

        private void PublishBufferAuthoritySnapshot(bool force = false)
        {
            foreach (BufferBanRequest request in ManagerMemory.Current.TakeBufferBanRequests(16))
            {
                string canonical = ResolveCanonicalAltMain(request.Character);
                if (IsAdministrator(canonical))
                {
                    DevTrace(
                        $"BUFFER AUTO-BAN DENIED source={request.Source} target={canonical}: administrator.");
                    continue;
                }

                string message;
                bool changed = BanListStore.TryAdd(canonical, out message);
                DevTrace(
                    $"BUFFER AUTO-BAN source={request.Source} target={canonical} " +
                    $"requested={request.Character} changed={changed}; {message}");
                if (changed) force = true;
            }

            DateTime now = DateTime.UtcNow;
            if (!force && now < _nextBufferAuthorityPublishUtc)
                return;
            _nextBufferAuthorityPublishUtc = now.Add(BufferAuthorityPublishInterval);

            var admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string admin in AdminListStore.Snapshot())
                AddBufferAuthorityIdentities(admin, admins);

            List<string> permanent;
            List<string> official;
            List<string> added;
            HashSet<string> removed;
            Dictionary<string, string> ranks;
            lock (_membershipSync)
            {
                permanent = _permanentMembers.ToList();
                official = _officialMembers.ToList();
                added = _liveAddedMembers.ToList();
                removed = new HashSet<string>(_liveRemovedMembers, StringComparer.OrdinalIgnoreCase);
                ranks = new Dictionary<string, string>(_officialMemberRanks, StringComparer.OrdinalIgnoreCase);
            }

            var memberRoots = new HashSet<string>(permanent, StringComparer.OrdinalIgnoreCase);
            memberRoots.UnionWith(official.Where(name => !removed.Contains(name)));
            memberRoots.UnionWith(added.Where(name => !removed.Contains(name)));

            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string member in memberRoots)
                AddBufferAuthorityIdentities(member, members);

            var ranked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ranks)
                if (!removed.Contains(pair.Key) &&
                    OrgRankAuthorizer.IsSquadCommanderOrHigher(pair.Value))
                    AddBufferAuthorityIdentities(pair.Key, ranked);

            members.UnionWith(admins);
            ranked.UnionWith(admins);

            var banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string bannedCharacter in BanListStore.Snapshot())
                AddBufferAuthorityIdentities(bannedCharacter, banned);

            ManagerMemory.Current.PublishBufferAuthority(
                admins.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                ranked.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                members.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                new string[0],
                banned.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private void AddBufferAuthorityIdentities(string character, HashSet<string> target)
        {
            foreach (string identity in GetAltIdentityCandidates(character))
                target.Add(identity);
        }

        private void BeginBufferControl(ReplyTarget target, string action, string character)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string id = Guid.NewGuid().ToString("N");
                bool began = false;
                try
                {
                    BufferAccount account = BufferSettings.Read().Active.FirstOrDefault(candidate =>
                        string.Equals(candidate.Character, character, StringComparison.OrdinalIgnoreCase));
                    if (account == null)
                    {
                        Reply(target, "No enabled buffer named " + character + ".");
                        return;
                    }

                    began = ManagerMemory.Current.BeginBufferControl(id, action, account.Character);
                    if (!began)
                    {
                        Reply(target, "Another buffer sleep/wakeup is already in progress.");
                        return;
                    }

                    BufferControlOperation result =
                        ManagerMemory.Current.WaitForBufferControlResult(
                            id, BufferControlTimeoutMilliseconds);
                    if (result == null || result.Result == null)
                    {
                        Reply(target, "Buffer " + action + " timed out; check the host log before retrying.");
                        return;
                    }

                    Reply(target, result.Success == true
                        ? "Buffers: " + result.Result
                        : "Buffers failed: " + result.Result);
                }
                catch (Exception ex)
                {
                    Reply(target, "Buffer " + action + " failed: " + ex.Message);
                }
                finally
                {
                    if (began) ManagerMemory.Current.FinishBufferControl(id);
                }
            });
        }

        private void BeginBufferPublicCommand(
            string senderName, string command, string[] parts, ReplyTarget target)
        {
            if (target == null || target.SenderId == 0)
            {
                Reply(target, "I could not resolve your AO character identity for this buff request.");
                return;
            }
            bool isCast = string.Equals(command, "cast", StringComparison.OrdinalIgnoreCase);
            if ((isCast && parts.Length < 2) || (!isCast && parts.Length != 1))
            {
                Reply(target, Usage(target, isCast ? "cast [buff tag...]" : command));
                return;
            }
            // Read the live dynel only on the AO command thread. Missing metadata
            // must not reject a macro; individual casters can check actual requirements.
            int? level = null;
            if (isCast)
            {
                var requester = DynelManager.Players.FirstOrDefault(p => p.Identity.Instance == target.SenderId);
                try { if (requester != null) level = requester.Level; } catch { }
            }
            var tags = parts.Skip(1).Select(t => t.ToLowerInvariant()).Distinct().ToArray();
            QueuePublicWork(target, () =>
            {
                if (isCast)
                {
                    ServeBufferMacro(senderName, tags, level, target);
                    return;
                }
                var outcome = RunBufferCommand(senderName, command, new string[0], target, null);
                if (outcome == null || !outcome.Success) return;
                if (string.Equals(command, "buffmacro", StringComparison.OrdinalIgnoreCase))
                {
                    var found = outcome.Tags ?? new string[0];
                    Reply(target, found.Length == 0
                        ? "No recognized active buffs were available for a macro."
                        : "<font color='" + ColorCommand + "'>/macro buffpreset /tell " +
                          Client.CharacterName + " cast " + EscapeBlobText(string.Join(" ", found)) + "</font>");
                }
            });
        }

        private void ServeBufferMacro(string senderName, string[] tags, int? level, ReplyTarget target)
        {
            var memory = ManagerMemory.Current;
            var ordinary = new List<string>();
            var paid = new List<PaidBufferNano>();
            foreach (string tag in tags)
            {
                var nano = PaidBufferCatalogue.Find(tag);
                bool configured = nano != null && memory.ReadPaidBuffers().Any(p => p.Profession == nano.Profession);
                // Shared Mali tags retain their free alternative when the paid role
                // is disabled, or this recipient needs the lower-level NCU line.
                if (nano == null || ((tag == "ncu" || tag == "iic") &&
                    (!configured || (tag == "ncu" && level.HasValue && level.Value < nano.Level))))
                {
                    ordinary.Add(tag);
                    continue;
                }
                if (!configured)
                {
                    Reply(target, "Can't cast " + nano.Name + " (" + tag + "): no enabled paid " +
                        PaidBufferCatalogue.ProfessionName(nano.Profession) + " is configured.");
                    continue;
                }
                if (level.HasValue && level.Value < nano.Level)
                {
                    Reply(target, "Can't cast " + nano.Name + " (" + tag + "): requires recipient level " + nano.Level + "+.");
                    continue;
                }
                if (!paid.Any(n => n.Id == nano.Id)) paid.Add(nano);
            }

            // Preserve the learned NCU prerequisite even when a copied macro
            // omitted it. Explicit NCU requests also run before everything else.
            if (!ordinary.Contains("ncu") && !paid.Any(n => n.Id == 275043) &&
                memory.ObservedNcuNeedsRefresh(unchecked((int)target.SenderId)))
            {
                var ncu = PaidBufferCatalogue.Find("fsc");
                if ((!level.HasValue || level.Value >= ncu.Level) &&
                    memory.ReadPaidBuffers().Any(p => p.Profession == 4)) paid.Insert(0, ncu);
            }

            bool fixerFirst = paid.Any(n => n.Id == 275043);
            var ncuNanos = paid.Where(n => fixerFirst && n.Profession == 4).ToArray();
            if (ncuNanos.Length != 0) ServePaidMacroPart(senderName, ncuNanos, target);
            if (ordinary.Remove("ncu"))
            {
                string watch = memory.WatchBufferCast(target.SenderId, "ncu");
                try
                {
                    var result = RunBufferCommand(senderName, "cast", new[] { "ncu" }, target, null);
                    if (result != null && result.Success && !memory.WaitForBufferCast(watch, 125000))
                        Reply(target, "NCU request has not finished yet; continuing with the other buffs I can provide.");
                }
                finally { memory.FinishBufferCastWatch(watch); }
            }

            if (ordinary.Count != 0)
                RunBufferCommand(senderName, "cast", ordinary.ToArray(), target, null);

            foreach (var group in paid.Where(n => !fixerFirst || n.Profession != 4).GroupBy(n => n.Profession))
                ServePaidMacroPart(senderName, group.ToArray(), target);
        }

        private void ServePaidMacroPart(string senderName, PaidBufferNano[] nanos, ReplyTarget target)
        {
            var memory = ManagerMemory.Current;
            string names = string.Join(", ", nanos.Select(n => n.Name));
            var provider = memory.ReadPaidBuffers().Where(p => p.Profession == nanos[0].Profession)
                .OrderBy(p => p.Blocked || DateTime.UtcNow < p.RetryAfterUtc)
                .ThenByDescending(p => p.Ready && !p.Draining)
                .ThenBy(p => p.Character, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            string unavailable = provider == null ? "no enabled provider is configured" :
                provider.Blocked ? provider.Character + " is unavailable after a cleanup failure" :
                DateTime.UtcNow < provider.RetryAfterUtc ? provider.Character + " is recovering from a failed session" : null;
            if (unavailable != null)
            {
                Reply(target, "Can't cast " + names + ": " + unavailable + ".");
                return;
            }
            RunBufferCommand(senderName, "cast", nanos.Select(n => n.Tags[0]).ToArray(), target, provider);
            if (memory.ReadPaidBuffer(provider.Character)?.Running == true &&
                !memory.WaitForPaidBufferIdle(provider.Character, 180000))
                Reply(target, names + ": " + provider.Character + " is still finishing its queue; remaining requests will wait their turn.");
        }

        private BufferPublicCommandOutcome RunBufferCommand(
            string senderName, string command, string[] tags, ReplyTarget target, PaidBufferSession paid)
        {
            string id = Guid.NewGuid().ToString("N");
            string description = command == "cast" ? string.Join(" ", tags) : command;
            var memory = ManagerMemory.Current;
            try
            {
                if (!memory.BeginBufferPublicCommand(new BufferPublicCommandRequest {
                    Id = id, Command = command.ToLowerInvariant(), TargetCharacter = paid?.Character,
                    SenderId = target.SenderId, SenderName = senderName, Arguments = tags, CreatedUtc = DateTime.UtcNow }))
                {
                    Reply(target, "Could not queue " + description + ": the buffer queue is busy or its provider is unavailable.");
                    return null;
                }
                if (paid != null && PaidBufferCatalogue.UsesManagerTeam(paid.Profession))
                    Reply(target, "Queued " + description + " with " + paid.Character +
                        ". Leave your current team and accept my invitation.");
                var outcome = memory.WaitForBufferPublicCommandOutcome(id, BufferPublicCommandTimeoutMilliseconds);
                if (outcome == null)
                    Reply(target, "Could not deliver " + description + ": no ready nearby buffer accepted it within two minutes.");
                else
                {
                    DevTrace("BUFFER COMMAND " + command + " requester=" + senderName +
                        " claimedBy=" + outcome.ClaimedBy + " success=" + outcome.Success + ".");
                    if (!outcome.Success || !string.IsNullOrWhiteSpace(outcome.Message))
                        Reply(target, description + ": " + (outcome.Message ?? "the request could not be completed."));
                }
                return outcome;
            }
            catch (Exception ex)
            {
                Reply(target, "Could not deliver " + description + ": " + ex.Message);
                return null;
            }
            finally { memory.FinishBufferPublicCommand(id); }
        }

        private void ProcessBufferBuffListCommand(string[] parts, ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "bufflist"));
                return;
            }

            QueuePublicWork(target, () =>
            {
                List<BufferBotInfo> snapshots = ManagerMemory.Current.BufferBotInfos()
                    .Where(snapshot => snapshot != null &&
                        !ManagerMemory.Current.IsPaidBuffer(snapshot.Character) &&
                        snapshot.AdvertisedBuffs != null &&
                        snapshot.AdvertisedBuffs.Length != 0)
                    .ToList();

                PaidBufferSession[] paidProviders = ManagerMemory.Current.ReadPaidBuffers()
                    .Where(p => PaidBufferCatalogue.ForProfession(p.Profession).Length != 0).ToArray();
                if (snapshots.Count == 0 && paidProviders.Length == 0)
                {
                    ReplyBufferCatalogue(target,
                        new[] { "No buffer capability catalogue has been observed yet." });
                    return;
                }

                DateTime now = DateTime.UtcNow;
                var rows = snapshots
                    .SelectMany(snapshot => snapshot.AdvertisedBuffs
                        .Where(buff => buff != null)
                        .Select(buff => new { Snapshot = snapshot, Buff = buff }))
                    .ToList();

                var body = new StringBuilder();
                body.Append(CommandLink(target, "buffmacro", "Buffmacro"))
                    .Append(" - create a macro of current buffs\n")
                    .Append(CommandLink(target, "rebuff", "Rebuff"))
                    .Append(" - refresh current buffs\n\n");

                foreach (var profession in rows
                    .GroupBy(row => new { row.Buff.IsGeneric, row.Buff.Profession })
                    .OrderBy(group => group.Key.IsGeneric ? 0 : 1)
                    .ThenBy(group => ((AOSharp.Common.GameData.Profession)group.Key.Profession).ToString(),
                        StringComparer.OrdinalIgnoreCase))
                {
                    string professionName = profession.Key.IsGeneric ? "Generic" :
                        ((AOSharp.Common.GameData.Profession)profession.Key.Profession).ToString();
                    body.Append("<img src=tdb://id:GFX_GUI_FRIENDLIST_SPLITTER>\n");
                    if (!profession.Key.IsGeneric)
                        body.Append("<img src=tdb://id:GFX_GUI_ICON_PROFESSION_")
                            .Append(profession.Key.Profession).Append("> ");
                    body.Append("<font color='").Append(ColorTitle).Append("'><b>")
                        .Append(EscapeBlobText(professionName)).Append("</b></font>\n");

                    foreach (var buffGroup in profession
                        .GroupBy(row => row.Buff.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(group => profession.Key.IsGeneric ? group.First().Buff.Tag : group.Key,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        BufferAdvertisedBuff buff = buffGroup.First().Buff;
                        bool ready = buffGroup.Any(row => IsFreshReadyBuffer(row.Snapshot, now));
                        body.Append("  ").Append(CommandLink(target, "cast " + buff.Tag, buff.Name));
                        if (string.Equals(buff.Type, "Team", StringComparison.OrdinalIgnoreCase))
                            body.Append(" <font color='#FBFF96'>[Team]</font>");
                        body.Append(" <font color='#F07171'>[")
                            .Append(EscapeBlobText(buff.Description)).Append("]</font>")
                            .Append(" <font color='#00BDBD'>[NCU: ").Append(buff.Ncu).Append("]</font>");
                        if (!ready) body.Append(" <font color='").Append(ColorMuted).Append("'>[Offline]</font>");
                        body.Append("\n");
                    }
                    body.Append("\n");
                }

                foreach (var profession in paidProviders.Where(p => p.Profession != 0)
                    .GroupBy(p => p.Profession).OrderBy(p => p.Key))
                {
                    body.Append("<img src=tdb://id:GFX_GUI_FRIENDLIST_SPLITTER>\n")
                        .Append("<img src=tdb://id:GFX_GUI_ICON_PROFESSION_").Append(profession.Key).Append("> ")
                        .Append("<b>").Append(PaidBufferCatalogue.ProfessionName(profession.Key))
                        .Append(" — on demand</b> (")
                        .Append(EscapeBlobText(string.Join(", ", profession.Select(p => p.Character))))
                        .Append(")\n");
                    foreach (var nano in PaidBufferCatalogue.ForProfession(profession.Key))
                    {
                        body.Append("  ").Append(CommandLink(target, "cast " + nano.Tags[0], nano.Name));
                        if (nano.IsTeam) body.Append(" <font color='#FBFF96'>[Team]</font>");
                        body.Append(" <font color='#F07171'>[").Append(EscapeBlobText(nano.Description))
                            .Append("]</font> <font color='#00BDBD'>[Level: ").Append(nano.Level)
                            .Append("+] [NCU: ").Append(nano.Ncu).Append("]</font>\n");
                    }
                    body.Append("\n");
                }

                List<string> links = BuildBlobLinks(
                    target,
                    "Buffer Buff Catalogue",
                    "Buff list",
                    body.ToString());

                ReplyBufferCatalogue(
                    target,
                    links.Select(link =>
                        "<font color='" + ColorTitle + "'>Apcmanager Buffs</font> " + link));
            });
        }

        private static bool IsFreshReadyBuffer(BufferBotInfo snapshot, DateTime now)
        {
            TimeSpan age;
            return snapshot != null &&
                snapshot.InPlay &&
                snapshot.Ready &&
                UtcTimestamp.TryGetAge(snapshot.ObservedUtc, now, out age) &&
                age <= TimeSpan.FromSeconds(5);
        }

        private void ReplyBufferCatalogue(ReplyTarget target, IEnumerable<string> messages)
        {
            if (target.Kind != ReplyKind.Tell)
            {
                Reply(target, messages);
                return;
            }

            foreach (string message in messages)
            {
                QueueTell(
                    target.SenderName,
                    target.SenderId != 0 ? (uint?)target.SenderId : null,
                    CityBankers.Shared.CityBankersChatPalette.StyleMarkup(message),
                    Client.CharacterName);
            }
        }

        private void BeginBufferStatus(ReplyTarget target)
        {
            QueuePublicWork(target, () =>
            {
                try
                {
                    var accounts = BufferSettings.Read().Active.ToList();
                    PaidBufferSession[] paidProviders = ManagerMemory.Current.ReadPaidBuffers();
                    foreach (var paid in paidProviders)
                        Reply(target, paid.Character + ": on-demand paid " + PaidBufferCatalogue.ProfessionName(paid.Profession) + ", " +
                            (paid.Blocked ? "blocked after cleanup failure; check host log" :
                             DateTime.UtcNow < paid.RetryAfterUtc ? "failure cooldown" :
                             paid.Draining && paid.Running ? "finishing queue and logging out" :
                             paid.Ready ? "ready" : paid.Running ? "starting" : "offline until requested") + ".");
                    if (accounts.Count == 0 && paidProviders.Length == 0) { Reply(target, "No buffers enabled."); return; }
                    foreach (var account in accounts)
                    {
                        string message;
                        if (ManagerMemory.Current.BufferSleeping(account.Character))
                        {
                            Reply(target, account.Character + ": sleeping for manual owner use.");
                            continue;
                        }
                        try
                        {
                            string response = LocalIpc.RequestLineAsync(BufferSettings.PipeName(account.Character),
                                "{\"Kind\":\"status\"}", 1000, 3000).GetAwaiter().GetResult();
                            var state = JObject.Parse(response);
                            DateTime? observed = (DateTime?)state["ObservedUtc"];
                            if (!observed.HasValue || observed.Value < DateTime.UtcNow.AddSeconds(-5))
                                message = account.Character + ": waiting for a fresh buffer update.";
                            else
                                message = account.Character + ": " + ((bool?)state["Ready"] == true ? "ready" : "starting") +
                                    ", " + (string)state["Profession"] + ", " + (int?)state["NanoCount"] +
                                    " known nanos, " + (int?)state["QueueLength"] + " queued buffs.";
                        }
                        catch (Exception) { message = account.Character + ": buffer status unavailable."; }
                        Reply(target, message);
                    }
                }
                catch (Exception) { Reply(target, "Unable to read Buffers configuration; check the host log."); }
            });
        }
    }
}



