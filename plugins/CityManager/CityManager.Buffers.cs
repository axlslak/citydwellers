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
        private int _paidTeamCleanupCount = -1;
        private bool _paidTeamBatchReady;
        private DateTime _paidTeamBatchSinceUtc;
        private int[] _paidTeamCustomers = new int[0];
        private readonly HashSet<int> _paidTeamSkipped = new HashSet<int>();

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
            if (memory.PaidPoolUpdating) return;
            _nextPaidTeamTick = now.AddMilliseconds(500);
            try
            {
                var providers = memory.ReadPaidBuffers().Where(p => PaidBufferCatalogue.UsesManagerTeam(p.Profession)).ToArray();
                var pending = memory.PendingBufferPublicCommands(128)
                    .Where(r => PaidBufferCatalogue.RequiresManagerTeam(r) &&
                        memory.PaidTeamRequestReady(r) &&
                        providers.Any(p => string.Equals(p.Character, r.TargetCharacter,
                        StringComparison.OrdinalIgnoreCase))).ToArray();
                if (_paidTeamCharacter == null)
                {
                    _paidTeamCharacter = pending.FirstOrDefault(r => providers.Any(p => p.Running &&
                        string.Equals(p.Character, r.TargetCharacter, StringComparison.OrdinalIgnoreCase)))?.TargetCharacter ??
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
                if (running && _paidTeamBatchReady) _paidTeamStarted = true;
                if ((_paidTeamStarted && !running) || provider == null || provider.Blocked ||
                    ((!running || !_paidTeamBatchReady) && requests.Length == 0 && now >= _paidTeamInviteUntil))
                    _paidTeamClosing = true;

                var members = Team.Members
                    .Where(m => m.Identity != DynelManager.LocalPlayer.Identity)
                    .Select(m => m.Identity.Instance).ToArray();
                if (!_paidTeamClosing && PaidTeamDeliveryObserved(provider, members, now))
                {
                    DevTrace("PAID TEAM team buff delivery confirmed; closing completed customer batch.");
                    _paidTeamClosing = true;
                }
                if (_paidTeamStarted && _paidTeamBatchReady && provider != null &&
                    (provider.TeamCastAttemptedUtc < _paidTeamBatchSinceUtc || provider.TeamCastNano == 0) &&
                    !memory.HasPaidTeamWork(_paidTeamCharacter, _paidTeamCustomers)) _paidTeamClosing = true;

                if (_paidTeamClosing)
                {
                    memory.PublishPaidFixerTeam(null, 0, new int[0]);
                    // IsInTeam reads Manager's team stat; it does not prove that the
                    // other members have left. Keep ownership until their removal
                    // messages clear the roster, then leave Manager's empty team.
                    var remainingMembers = Team.Members.Where(m =>
                        m.Identity != DynelManager.LocalPlayer.Identity).ToArray();
                    if (_paidTeamOwnsTeam && (remainingMembers.Length != 0 || Team.IsInTeam))
                    {
                        if (_paidTeamCleanupCount != remainingMembers.Length)
                        {
                            _paidTeamCleanupCount = remainingMembers.Length;
                            _nextPaidTeamCleanup = now;
                        }
                        if (now >= _nextPaidTeamCleanup)
                        {
                            _nextPaidTeamCleanup = now.AddSeconds(2);
                            if (remainingMembers.Length != 0)
                            {
                                foreach (var member in remainingMembers) Team.Kick(member.Identity);
                                DevTrace("PAID TEAM Manager requested removal of " + remainingMembers.Length +
                                    " members; waiting for roster confirmation.");
                            }
                            else
                            {
                                Team.LeaveTeam();
                                DevTrace("PAID TEAM member removals confirmed; Manager requested leave.");
                            }
                        }
                        return;
                    }
                    _paidTeamCharacter = null;
                    _paidTeamOwnsTeam = _paidTeamStarted = _paidTeamClosing = false;
                    _paidTeamInviteId = 0;
                    _paidTeamInviteUntil = DateTime.MinValue;
                    _paidTeamBatchReady = false;
                    _paidTeamBatchSinceUtc = DateTime.MinValue;
                    _paidTeamCustomers = new int[0];
                    _paidTeamSkipped.Clear();
                    _paidTeamCleanupCount = -1;
                    _nextPaidTeamCleanup = DateTime.MinValue;
                    DevTrace("PAID TEAM Manager is out of the previous team; next batch may gather.");
                    return;
                }

                // Never commandeer an unrelated team Manager was already in.
                if (!_paidTeamOwnsTeam && Team.IsInTeam)
                {
                    memory.PublishPaidFixerTeam(null, 0, new int[0]);
                    return;
                }
                memory.PublishPaidFixerTeam(_paidTeamCharacter,
                    DynelManager.LocalPlayer.Identity.Instance, members, _paidTeamBatchReady);
                if (_paidTeamInviteId != 0 && members.Contains(_paidTeamInviteId))
                {
                    DevTrace("PAID TEAM team membership confirmed: " + _paidTeamInviteId + ".");
                    _paidTeamInviteId = 0;
                    _paidTeamInviteUntil = DateTime.MinValue;
                }
                if (!_paidTeamBatchReady)
                {
                    if (_paidTeamInviteId != 0)
                    {
                        if (now < _paidTeamInviteUntil) return;
                        _paidTeamSkipped.Add(_paidTeamInviteId);
                        memory.DeferPaidBuff(_paidTeamCharacter, _paidTeamInviteId,
                            PaidBufferCatalogue.ForProfession(provider.Profession).First(n => n.IsTeam).Id);
                        DevTrace("PAID TEAM invitation expired for " + _paidTeamInviteId + "; gathering other requesters.");
                        _paidTeamInviteId = 0;
                    }
                    // Aim for four customers, reserving Manager's and the buffer's
                    // slots. A smaller batch proceeds once no further eligible
                    // visible customer remains; four is never a minimum.
                    var next = requests.FirstOrDefault(r => !members.Contains(unchecked((int)r.SenderId)) &&
                        !_paidTeamSkipped.Contains(unchecked((int)r.SenderId)) &&
                        DynelManager.Players.Any(p => p.Identity.Instance == unchecked((int)r.SenderId)));
                    if (members.Length < 4 && next != null)
                    {
                        InvitePaidTeamMember(new Identity(IdentityType.SimpleChar, unchecked((int)next.SenderId)),
                            now, "requester " + next.SenderName);
                        return;
                    }
                    if (members.Length == 0) return;
                    _paidTeamCustomers = members.ToArray();
                    _paidTeamBatchSinceUtc = now;
                    _paidTeamBatchReady = true;
                    memory.PublishPaidFixerTeam(_paidTeamCharacter,
                        DynelManager.LocalPlayer.Identity.Instance, members, true);
                    DevTrace("PAID TEAM batch ready: " + _paidTeamCustomers.Length + " customers; buffer admission enabled.");
                }
                if (running)
                {
                    var snapshot = memory.BufferBotInfos().FirstOrDefault(b =>
                        string.Equals(b.Character, _paidTeamCharacter, StringComparison.OrdinalIgnoreCase));
                    var fixer = DynelManager.Players.FirstOrDefault(p => string.Equals(p.Name,
                        _paidTeamCharacter, StringComparison.OrdinalIgnoreCase));
                    if (fixer != null && IsFreshReadyBuffer(snapshot, now) &&
                        snapshot.IdentityInstance == fixer.Identity.Instance && members.Length > 0 &&
                        !members.Contains(fixer.Identity.Instance) &&
                        (_paidTeamInviteId != fixer.Identity.Instance || now >= _paidTeamInviteUntil))
                        InvitePaidTeamMember(fixer.Identity, now, "buffer " + _paidTeamCharacter);
                }
            }
            catch (Exception ex)
            {
                memory.PublishPaidFixerTeam(null, 0, new int[0]);
                Logger.Warning("Paid buffer team coordinator: " + ex.Message);
            }
        }

        private bool PaidTeamDeliveryObserved(PaidBufferSession provider, int[] members, DateTime now)
        {
            if (!_paidTeamBatchReady || provider == null || provider.TeamCastNano == 0 ||
                provider.TeamCastAttemptedUtc < _paidTeamBatchSinceUtc || now < provider.TeamReleaseAfterUtc)
                return false;
            var memory = ManagerMemory.Current;
            bool allSettled = true;
            foreach (int id in _paidTeamCustomers)
            {
                bool landed = provider.TeamCastMembers.Contains(id) &&
                    memory.ObservedCastLanded(id, provider.TeamCastNano, provider.TeamCastAttemptedUtc,
                        DateTime.MinValue, true) &&
                    (provider.TeamCastNano != 275043 || !memory.ObservedNcuNeedsRefresh(id));
                bool skip = !members.Contains(id) || !DynelManager.Players.Any(p => p.Identity.Instance == id) ||
                    !memory.PaidBuffRecipientCanReceive(provider.Character, id, provider.TeamCastNano) ||
                    now >= provider.TeamCastDeadlineUtc;
                if (landed)
                {
                    memory.ReportPaidBuffFinished(provider.Character, id, provider.TeamCastNano, true);
                    memory.ReportBufferCastFinished(unchecked((uint)id),
                        PaidBufferCatalogue.FindEffect(provider.TeamCastNano).Tags);
                }
                else if (skip)
                    memory.ReportPaidBuffFinished(provider.Character, id, provider.TeamCastNano, false);
                else allSettled = false;
            }
            return allSettled;
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
                    var resolved = SelectAndResolveBuffArguments(senderName, tags, level, target);
                    if (resolved.Length != 0) ServeBufferMacro(senderName, resolved, level, target);
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
                          Client.CharacterName + " buff add " + EscapeBlobText(string.Join(" ", found)) + "</font>");
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

            var freeNcuIds = new HashSet<int>(memory.BufferBotInfos()
                .Where(bot => !memory.IsPaidBuffer(bot.Character))
                .SelectMany(bot => bot.AdvertisedBuffs ?? new BufferAdvertisedBuff[0])
                .Where(buff => string.Equals(buff.Tag, "ncu", StringComparison.OrdinalIgnoreCase))
                .SelectMany(buff => buff.NanoIds ?? new int[0]));
            var freeNcuTags = ordinary.Where(tag => tag == "ncu" ||
                (int.TryParse(tag, out int id) && freeNcuIds.Contains(id))).ToArray();

            // Preserve the learned NCU prerequisite even when a copied macro
            // omitted it. Explicit NCU requests also run before everything else.
            if (freeNcuTags.Length == 0 && !paid.Any(n => n.Id == 275043) &&
                memory.ObservedNcuNeedsRefresh(unchecked((int)target.SenderId)))
            {
                var ncu = PaidBufferCatalogue.Find("fsc");
                if ((!level.HasValue || level.Value >= ncu.Level) &&
                    memory.ReadPaidBuffers().Any(p => p.Profession == 4)) paid.Insert(0, ncu);
            }

            // Publish the whole macro before any paid toon can be admitted. Local
            // caster queues draw from this pool; they never define its batch size.
            var knownNcuIds = new HashSet<int>(freeNcuIds.Concat(
                new[] { 275043, 275044, 275135 }));
            bool healthyNcu = memory.ReadNearbyPlayers().Any(p => p.CharacterId == unchecked((int)target.SenderId) &&
                p.Nanos.Any(n => knownNcuIds.Contains(n.Id) && n.FullSeconds > 0 &&
                    n.RemainingSeconds >= n.FullSeconds / 2));
            string ncuWatch = !healthyNcu && (freeNcuTags.Length != 0 || paid.Any(n => n.Id == 275043))
                ? memory.WatchBufferCast(target.SenderId, "ncu") : null;
            ordinary.RemoveAll(freeNcuTags.Contains);
            int queued = 0;
            memory.BeginPaidPoolUpdate();
            try
            {
                // MP composites replace each other; Neuronal Stimulator is separate.
                var composite = paid.Where(n => n.Profession == 12 && n.Id != 220345)
                    .OrderByDescending(n => n.Level).FirstOrDefault();
                foreach (var nano in paid.Where(n => n.Profession != 12 || n.Id == 220345 || n == composite))
                {
                    var provider = memory.ReadPaidBuffers().Where(p => p.Profession == nano.Profession &&
                        !p.Blocked && DateTime.UtcNow >= p.RetryAfterUtc)
                        .OrderByDescending(p => p.Running).FirstOrDefault();
                    if (provider == null)
                    {
                        Reply(target, "Can't cast " + nano.Name + ": its paid provider is unavailable.");
                        continue;
                    }
                    if (memory.BeginBufferPublicCommand(new BufferPublicCommandRequest {
                        Id = Guid.NewGuid().ToString("N"), Command = "cast", TargetCharacter = provider.Character,
                        SenderId = target.SenderId, SenderName = senderName,
                        Arguments = new[] { nano.Id.ToString() }, CreatedUtc = DateTime.UtcNow,
                        NcuWatchId = nano.Id == 275043 ? null : ncuWatch })) queued++;
                    else Reply(target, "Can't queue " + nano.Name + ": the buff pool is full.");
                }
                foreach (var group in new[] { freeNcuTags, ordinary.ToArray() })
                {
                    if (group.Length == 0) continue;
                    if (memory.BeginBufferPublicCommand(new BufferPublicCommandRequest {
                        Id = Guid.NewGuid().ToString("N"), Command = "cast", SenderId = target.SenderId,
                        SenderName = senderName, Arguments = group, CreatedUtc = DateTime.UtcNow,
                        NcuWatchId = ReferenceEquals(group, freeNcuTags) ? null : ncuWatch })) queued++;
                    else Reply(target, "Can't queue " + string.Join(" ", group) + ": the buff pool is full.");
                }
            }
            finally { memory.EndPaidPoolUpdate(); }
            if (queued != 0 && paid.Any(n => n.IsTeam))
                Reply(target, "Queued your buffs. Leave your current team and accept my invitation for the team buff.");
        }

        private BufferPublicCommandOutcome RunBufferCommand(
            string senderName, string command, string[] tags, ReplyTarget target, PaidBufferSession paid)
        {
            string id = Guid.NewGuid().ToString("N");
            string description = command == "cast" ? string.Join(" ", tags) : command;
            var memory = ManagerMemory.Current;
            try
            {
                var request = new BufferPublicCommandRequest {
                    Id = id, Command = command.ToLowerInvariant(), TargetCharacter = paid?.Character,
                    SenderId = target.SenderId, SenderName = senderName, Arguments = tags, CreatedUtc = DateTime.UtcNow };
                if (!memory.BeginBufferPublicCommand(request))
                {
                    Reply(target, "Could not queue " + description + ": the buffer queue is busy or its provider is unavailable.");
                    return null;
                }
                if (PaidBufferCatalogue.RequiresManagerTeam(request))
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
                body.Append(CommandLink(target, "buff mine", "My buffs"))
                    .Append(" - edit your remembered buffs\n")
                    .Append(CommandLink(target, "buff macro", "Buff macro"))
                    .Append(" - create a macro of current buffs\n")
                    .Append(CommandLink(target, "buff redo", "Rebuff"))
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
                        body.Append("  ").Append(CommandLink(target, "buff add " + buff.Tag, buff.Name));
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
                        body.Append("  ").Append(CommandLink(target, "buff add " + nano.Tags[0], nano.Name));
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
                    var bufferStates = ManagerMemory.Current.BufferBotInfos();
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
                            var state = bufferStates.FirstOrDefault(info =>
                                string.Equals(info.Character, account.Character, StringComparison.OrdinalIgnoreCase));
                            TimeSpan age;
                            if (state == null || !UtcTimestamp.TryGetAge(state.ObservedUtc, DateTime.UtcNow, out age) ||
                                age > TimeSpan.FromSeconds(5))
                                message = account.Character + ": waiting for a fresh buffer update.";
                            else
                                message = account.Character + ": " + (!state.InPlay ? "offline" : state.Ready ? "ready" : "starting") +
                                    ", " + ((Profession)state.Profession).ToString() + ", " + (state.SpellData?.Length ?? 0) +
                                    " known nanos, " + state.QueueLength + " queued buffs.";
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
