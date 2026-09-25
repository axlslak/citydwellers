using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private const int BufferControlTimeoutMilliseconds = 30000;
        private static readonly TimeSpan BufferAuthorityPublishInterval =
            TimeSpan.FromSeconds(1);
        private DateTime _nextBufferAuthorityPublishUtc = DateTime.MinValue;

        private void PublishBufferAuthoritySnapshot(bool force = false)
        {
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

            ManagerMemory.Current.PublishBufferAuthority(
                admins.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                ranked.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                members.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                new string[0]);
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
