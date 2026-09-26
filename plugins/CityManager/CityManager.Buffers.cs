using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using AOSharp.Clientless;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private const int BufferControlTimeoutMilliseconds = 30000;
        private const int BufferPublicCommandTimeoutMilliseconds = 6000;
        private static readonly TimeSpan BufferAuthorityPublishInterval =
            TimeSpan.FromSeconds(1);
        private DateTime _nextBufferAuthorityPublishUtc = DateTime.MinValue;

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
            string senderName,
            string command,
            string[] parts,
            ReplyTarget target)
        {
            if (target == null || target.SenderId == 0)
            {
                Reply(target, "I could not resolve your AO character identity for this buff request.");
                return;
            }

            bool isCast = string.Equals(command, "cast", StringComparison.OrdinalIgnoreCase);
            bool isRebuff = string.Equals(command, "rebuff", StringComparison.OrdinalIgnoreCase);
            bool isBuffmacro = string.Equals(command, "buffmacro", StringComparison.OrdinalIgnoreCase);

            if ((isCast && parts.Length < 2) ||
                ((isRebuff || isBuffmacro) && parts.Length != 1))
            {
                Reply(target, isCast
                    ? Usage(target, "cast [buff tag...]")
                    : Usage(target, command));
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!ManagerMemory.Current.BufferBotInfos().Any(
                    snapshot => IsFreshReadyBuffer(snapshot, now)))
            {
                Reply(target, "No ready buffers are currently available.");
                return;
            }

            string id = Guid.NewGuid().ToString("N");
            var request = new BufferPublicCommandRequest
            {
                Id = id,
                Command = command.ToLowerInvariant(),
                SenderId = target.SenderId,
                SenderName = senderName,
                Arguments = isCast
                    ? parts.Skip(1).Select(value => value.ToLowerInvariant()).ToArray()
                    : new string[0],
                CreatedUtc = now
            };

            if (!ManagerMemory.Current.BeginBufferPublicCommand(request))
            {
                Reply(target, "The buffer command queue is busy. Try again shortly.");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    BufferPublicCommandOutcome outcome =
                        ManagerMemory.Current.WaitForBufferPublicCommandOutcome(
                            id, BufferPublicCommandTimeoutMilliseconds);

                    if (outcome == null)
                    {
                        Reply(
                            target,
                            "No ready buffer could see your character nearby. " +
                            "Stand near the buffer fleet and try again.");
                        return;
                    }

                    DevTrace(
                        $"BUFFER COMMAND {command} requester={senderName} " +
                        $"claimedBy={outcome.ClaimedBy ?? "unknown"} success={outcome.Success}.");

                    if (!outcome.Success)
                    {
                        Reply(
                            target,
                            "Buffers: " +
                            (outcome.Message ?? "the request could not be completed."));
                        return;
                    }

                    if (isBuffmacro)
                    {
                        string[] tags = outcome.Tags ?? new string[0];
                        if (tags.Length == 0)
                        {
                            Reply(target, "No recognized active buffs were available for a macro.");
                            return;
                        }

                        Reply(
                            target,
                            "<font color='" + ColorCommand + "'>/macro buffpreset /tell " +
                            Client.CharacterName + " cast " +
                            EscapeBlobText(string.Join(" ", tags)) +
                            "</font>");
                    }
                }
                catch (Exception ex)
                {
                    Reply(target, "Buffer command failed: " + ex.Message);
                }
                finally
                {
                    ManagerMemory.Current.FinishBufferPublicCommand(id);
                }
            });
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
                        snapshot.AdvertisedBuffs != null &&
                        snapshot.AdvertisedBuffs.Length != 0)
                    .ToList();

                if (snapshots.Count == 0)
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
                body.Append("<font color='").Append(ColorMuted)
                    .Append("'>Cached from buffer capability snapshots. READY means at least one advertising buffer is currently fresh and ready; CACHED remains visible while its buffer is offline.</font>\n\n");

                foreach (var profession in rows
                    .GroupBy(row => row.Buff.Profession)
                    .OrderBy(group => ((AOSharp.Common.GameData.Profession)group.Key).ToString(),
                        StringComparer.OrdinalIgnoreCase))
                {
                    string professionName =
                        ((AOSharp.Common.GameData.Profession)profession.Key).ToString();
                    body.Append("<font color='").Append(ColorTitle).Append("'><b>")
                        .Append(EscapeBlobText(professionName)).Append("</b></font>\n");

                    foreach (var buffGroup in profession
                        .GroupBy(row => (row.Buff.Tag ?? string.Empty) + "\u001f" +
                            (row.Buff.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                        .OrderBy(group => group.First().Buff.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        BufferAdvertisedBuff buff = buffGroup.First().Buff;
                        bool ready = buffGroup.Any(row => IsFreshReadyBuffer(row.Snapshot, now));
                        string providers = string.Join(", ", buffGroup
                            .Select(row => row.Snapshot.Character +
                                (IsFreshReadyBuffer(row.Snapshot, now) ? " ready" : " offline"))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

                        body.Append("  <font color='")
                            .Append(ready ? ColorGood : ColorMuted)
                            .Append("'>").Append(ready ? "READY" : "CACHED")
                            .Append("</font> ");

                        if (!string.IsNullOrWhiteSpace(buff.Tag))
                            body.Append("<font color='").Append(ColorCommand).Append("'>")
                                .Append(EscapeBlobText(buff.Tag)).Append("</font> ");

                        body.Append(EscapeBlobText(buff.Name));
                        if (!string.IsNullOrWhiteSpace(buff.Description))
                            body.Append(" - ").Append(EscapeBlobText(buff.Description));
                        if (buff.Ncu > 0)
                            body.Append(" - ").Append(buff.Ncu).Append(" NCU");
                        body.Append(" <font color='").Append(ColorMuted).Append("'>(")
                            .Append(EscapeBlobText(providers)).Append(")</font>\n");
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
                    if (accounts.Count == 0) { Reply(target, "No froob buffers enabled."); return; }
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
