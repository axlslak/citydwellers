using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.ChatMessages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityManager
{
    public partial class CityManager
    {
        private static readonly HashSet<string> OwnerCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test-orgchat", "orgchat" };
        private const int OrgChatProbeLimit = 14;
        private const int OrgChatProbeResolution = 50;
        private OrgChatTest _orgChatTest;

        private sealed class OrgChatSettings { public int PageSize; }
        private sealed class OrgChatTest
        {
            public ReplyTarget Owner, Channel;
            public string Token = Guid.NewGuid().ToString("N").Substring(0, 8);
            public bool Search, Waiting;
            public int Candidate, Confirmed, Unconfirmed, Upper, Attempts;
            public long NextStamp;
        }

        private bool IsOrgChatOwner(string name)
        {
            return string.Equals(name, DeveloperCharacter, StringComparison.OrdinalIgnoreCase) ||
                GetAltIdentityCandidates(name).Contains(DeveloperCharacter, StringComparer.OrdinalIgnoreCase);
        }

        private int CurrentOrgBlobPageSize()
        {
            lock (_orgOutputSync) return _orgBlobCurrentPageSize;
        }

        private void LoadOrgBlobBudgetState()
        {
            // Only an explicit owner setting survives startup. Discard the old
            // exploratory RAM budget; normal replies are never test traffic.
            string path = Path.Combine(_settingsDir, "config", "orgchat.json");
            try
            {
                if (!DiskFiles.Exists(path)) return;
                var saved = JsonConvert.DeserializeObject<OrgChatSettings>(DiskFiles.ReadAllText(path));
                if (saved == null || saved.PageSize < OrgBlobMinPageSize || saved.PageSize > OrgBlobMaxPageSize)
                    throw new InvalidDataException("PageSize must be between 512 and 32768 UTF-8 bytes.");
                lock (_orgOutputSync) _orgBlobCurrentPageSize = saved.PageSize;
            }
            catch (Exception ex)
            {
                Logger.Warning("Org chat setting could not be loaded; using the compiled default: " + ex.Message);
            }
        }

        private void ProcessOrgChatControl(string senderName, string command, string[] parts,
            ReplyTarget source, bool isOwner)
        {
            var owner = ReplyTarget.ForTell(source.SenderId, senderName);
            if (!isOwner)
            {
                Reply(owner, "This command is restricted to Kavem and his known alts.");
                return;
            }
            if (command == "orgchat")
            {
                if (parts.Length == 1 || (parts.Length == 2 && parts[1].Equals("size", StringComparison.OrdinalIgnoreCase)))
                {
                    Reply(owner, "Org chat limit: " + CurrentOrgBlobPageSize() +
                        " UTF-8 bytes. Set it with #orgchat size <bytes>. Test with #test-orgchat; stop with #test-orgchat stop.");
                    return;
                }
                int size;
                if (parts.Length != 3 || !parts[1].Equals("size", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(parts[2], out size) || size < OrgBlobMinPageSize || size > OrgBlobMaxPageSize)
                {
                    Reply(owner, "Usage: #orgchat size <512..32768 UTF-8 bytes>.");
                    return;
                }
                try
                {
                    lock (_orgOutputSync)
                    {
                        DiskFiles.WriteAllText(Path.Combine(_settingsDir, "config", "orgchat.json"),
                            JsonConvert.SerializeObject(new OrgChatSettings { PageSize = size }, Formatting.Indented));
                        _orgBlobCurrentPageSize = size;
                    }
                    DevTrace("ORG LIMIT set by=" + senderName + " bytes=" + size + ".");
                    Reply(owner, "Org chat limit saved: " + size + " UTF-8 bytes. Automatic adjustment is disabled.");
                }
                catch (Exception ex)
                {
                    Logger.Error("Org chat limit was not saved: " + ex);
                    Reply(owner, "Org chat limit was not changed: " + ex.Message);
                }
                return;
            }

            if (parts.Length == 2 && parts[1].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                lock (_orgOutputSync) _orgChatTest = null;
                Reply(owner, "Org chat test stopped. No further test packets will be sent.");
                return;
            }
            int candidate = CurrentOrgBlobPageSize();
            bool search = parts.Length == 1;
            if (!search && (parts.Length != 2 || !int.TryParse(parts[1], out candidate) ||
                candidate < OrgBlobMinPageSize || candidate > OrgBlobMaxPageSize))
            {
                Reply(owner, "Usage: #test-orgchat [bytes | stop]. No size starts a bounded search; a size sends one test.");
                return;
            }
            lock (_orgOutputSync)
            {
                if (_orgChatTest != null)
                {
                    Reply(owner, "An org chat test is already running. Use #test-orgchat stop to cancel it.");
                    return;
                }
                _orgChatTest = new OrgChatTest { Owner = owner, Search = search, Candidate = candidate,
                    Channel = source.IsOrg ? source : ReplyTarget.ForOrg(source.SenderId, null, null, senderName) };
            }
            Reply(owner, search
                ? "Org chat test started: at most 14 test messages, one at a time. Results arrive privately. The configured limit will stay unchanged."
                : "Sending one org chat test at " + candidate + " UTF-8 bytes; result arrives privately.");
        }

        private void TickOrgChatTest()
        {
            OrgChatTest test;
            lock (_orgOutputSync)
            {
                test = _orgChatTest;
                if (test == null || test.Waiting || Stopwatch.GetTimestamp() < test.NextStamp) return;
                test.Waiting = true;
                test.Attempts++;
            }
            string start = "<font color='#FFFFFF'>Org chat test " + test.Token + " " + test.Attempts +
                ": " + test.Candidate + "B <a href=\"text://Org chat delivery test\n";
            const string end = "\">[Test]</a></font>";
            string text = start + new string('x', test.Candidate - Encoding.UTF8.GetByteCount(start + end)) + end;
            if (!TrySendOrgMessage(test.Channel, text, null, test))
                FinishOrgChatTest(test, "Org send unavailable; test stopped.");
        }

        private void CompleteOrgChatProbe(OrgChatTest test, int length, bool delivered)
        {
            bool done;
            lock (_orgOutputSync)
            {
                if (!ReferenceEquals(_orgChatTest, test)) return;
                test.Waiting = false;
                if (delivered) test.Confirmed = Math.Max(test.Confirmed, length);
                else
                {
                    test.Unconfirmed = length;
                    test.Upper = test.Upper == 0 ? length : Math.Min(test.Upper, length);
                }
                done = !test.Search || test.Attempts >= OrgChatProbeLimit ||
                    test.Confirmed >= OrgBlobMaxPageSize ||
                    (test.Upper != 0 && test.Upper - test.Confirmed <= OrgChatProbeResolution);
                if (!done)
                {
                    int next = test.Upper == 0 ? Math.Min(OrgBlobMaxPageSize, test.Candidate * 2) :
                        ((test.Confirmed + test.Upper) / 2 / OrgChatProbeResolution) * OrgChatProbeResolution;
                    next = Math.Max(OrgBlobMinPageSize, next);
                    done = next == test.Candidate || (test.Upper != 0 && next >= test.Upper);
                    test.Candidate = next;
                    test.NextStamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                }
            }
            DevTrace("ORG MANUAL TEST bytes=" + length + " echo=" + delivered + " attempt=" + test.Attempts + ".");
            if (done) FinishOrgChatTest(test, "Org chat test finished.");
        }

        private void FinishOrgChatTest(OrgChatTest test, string reason)
        {
            lock (_orgOutputSync)
            {
                if (!ReferenceEquals(_orgChatTest, test)) return;
                _orgChatTest = null;
            }
            Reply(test.Owner, reason + " Largest echo-confirmed test: " + test.Confirmed +
                "B; last unconfirmed test: " + test.Unconfirmed +
                "B. A missing echo is not proof of a size limit. Current setting: " + CurrentOrgBlobPageSize() +
                "B. Set your chosen limit with #orgchat size <bytes>.");
        }

        private PendingOrgEcho NoteOrgEchoPending(string route, string text, int channelId,
            ReplyTarget target, string privateFallback, OrgChatTest test)
        {
            var pending = new PendingOrgEcho { Text = text ?? string.Empty, Route = route,
                Stamp = Stopwatch.GetTimestamp(), Length = Encoding.UTF8.GetByteCount(text ?? string.Empty),
                ChannelId = channelId, SenderId = Client.Chat == null ? 0 : Client.Chat.CharId,
                Target = target, PrivateFallback = privateFallback, Test = test };
            lock (_orgOutputSync) _pendingOrgEchoes.Add(pending);
            return pending;
        }

        private void ForgetOrgEcho(PendingOrgEcho pending)
        {
            if (pending != null) lock (_orgOutputSync) _pendingOrgEchoes.Remove(pending);
        }

        private void ObserveOrgEcho(GroupMsg observed)
        {
            PendingOrgEcho confirmed = null;
            List<PendingOrgEcho> expired;
            lock (_orgOutputSync)
            {
                if (observed != null)
                {
                    confirmed = _pendingOrgEchoes.FirstOrDefault(p => p.SenderId != 0 &&
                        p.SenderId == observed.SenderId && p.ChannelId == observed.ChannelId &&
                        string.Equals(p.Text, observed.Message, StringComparison.Ordinal));
                    if (confirmed != null)
                    {
                        _pendingOrgEchoes.Remove(confirmed);
                        _orgLastConfirmedBytes = confirmed.Length;
                        _orgMaxConfirmedBytes = Math.Max(_orgMaxConfirmedBytes, confirmed.Length);
                    }
                }
                long now = Stopwatch.GetTimestamp();
                expired = _pendingOrgEchoes.Where(p => now - p.Stamp > Stopwatch.Frequency * 15).ToList();
                foreach (var pending in expired) _pendingOrgEchoes.Remove(pending);
            }
            foreach (var pending in expired)
            {
                lock (_orgOutputSync) _orgLastUnconfirmedBytes = pending.Length;
                Logger.Warning("ORG ECHO UNCONFIRMED bytes=" + pending.Length + "; no org retry or size change.");
                if (pending.Test != null) CompleteOrgChatProbe(pending.Test, pending.Length, false);
                else
                {
                    SetOrgOutboundHealth(true, "no observed echo within 15s; reply continued privately");
                    if (pending.Target != null && pending.Target.SenderId != 0 && !string.IsNullOrEmpty(pending.PrivateFallback))
                        QueueTell(pending.Target, pending.PrivateFallback);
                }
            }
            if (confirmed != null)
            {
                SetOrgOutboundHealth(false, "delivery confirmed by observed echo via " + confirmed.Route);
                if (confirmed.Test != null) CompleteOrgChatProbe(confirmed.Test, confirmed.Length, true);
            }
        }

        private bool TrySendDirectGroupMessage(object channelId, string text, out string detail,
            ReplyTarget target, string privateFallback, OrgChatTest test)
        {
            detail = "no observed channel id";
            if (channelId == null) return false;
            PendingOrgEcho pending = null;
            try
            {
                int id = Convert.ToInt32(channelId, CultureInfo.InvariantCulture);
                if (id <= 0) return false;
                pending = NoteOrgEchoPending("raw game-connection GroupMsgMessage", text, id, target, privateFallback, test);
                Client.Send(new GroupMsgMessage { MessageType = GroupMessageType.Org, ChannelId = id, Text = text });
                detail = "Client.Send(GroupMsgMessage Org, channel " + id + ")";
                return true;
            }
            catch (Exception ex)
            {
                ForgetOrgEcho(pending);
                detail = "raw org-channel send failed: " + ex.Message;
                return false;
            }
        }

        private sealed class ReplyPages
        {
            public string Token, Title, Label, Heading, PreviewLink;
            public string[] Pages;
            public bool AdminOnly;
            public DateTime CreatedUtc = DateTime.UtcNow;
        }
        private readonly Dictionary<string, ReplyPages> _replyPages = new Dictionary<string, ReplyPages>(StringComparer.Ordinal);

        private string PrivatePageLink(ReplyPages reply, int page, string label)
        {
            return CommandLink(ReplyTarget.ForTell(0), "page " + reply.Token + " " + page, label);
        }

        private string ReplyPageNavigation(ReplyPages reply, int index)
        {
            var links = new List<string>();
            if (index > 0) links.Add(PrivatePageLink(reply, index, "Previous page"));
            if (index + 1 < reply.Pages.Length) links.Add(PrivatePageLink(reply, index + 2, "Next page (tell)"));
            return string.Join(" | ", links);
        }

        private List<string> BuildOrgBlobPreview(ReplyTarget target, string title, string label,
            string content, string heading)
        {
            int envelope = BlobEnvelope(title, label, heading) + 256; // tell navigation
            int privateBudget = Math.Max(256, TellBlobPageSize - envelope);
            string[] pages;
            bool preview = true;
            try
            {
                var orgPages = PaginateBlob(content, Math.Max(1,
                    Math.Min(CurrentOrgBlobPageSize(), TellBlobPageSize) - envelope));
                pages = new[] { orgPages[0] }.Concat(orgPages.Count > 1
                    ? PaginateBlob(string.Join("\n", orgPages.Skip(1)), privateBudget)
                    : Enumerable.Empty<string>()).ToArray();
            }
            catch (InvalidOperationException)
            {
                // One complete row cannot fit in org. Offer the private window;
                // never shrink/split/retry this command in organization chat.
                pages = PaginateBlob(content, privateBudget).ToArray();
                preview = false;
            }
            var reply = new ReplyPages { Token = Guid.NewGuid().ToString("N"), Title = title,
                Label = label, Heading = heading, Pages = pages, AdminOnly = target.AdminOnly };
            string link = preview ? RenderBlobPage(title, label, pages[0], heading, 0, pages.Length,
                ReplyPageNavigation(reply, 0)) : PrivatePageLink(reply, 1, label + " (tell)");
            reply.PreviewLink = link;
            lock (_orgOutputSync)
            {
                foreach (string old in _replyPages.Where(p => p.Value.CreatedUtc < DateTime.UtcNow.AddMinutes(-30))
                    .Select(p => p.Key).ToArray()) _replyPages.Remove(old);
                while (_replyPages.Count >= 64)
                    _replyPages.Remove(_replyPages.OrderBy(p => p.Value.CreatedUtc).First().Key);
                _replyPages.Add(reply.Token, reply);
            }
            return new List<string> { link };
        }

        private string PrivateOrgReply(string text, bool linkOnly = false)
        {
            lock (_orgOutputSync)
            {
                var reply = _replyPages.Values.FirstOrDefault(p => text.IndexOf(p.PreviewLink, StringComparison.Ordinal) >= 0);
                if (reply != null) return PrivatePageLink(reply, 1, reply.Label + " (tell)");
            }
            // Plain replies already fit their caller's normal tell path.
            return linkOnly ? string.Empty : text;
        }

        private void ProcessReplyPage(string[] parts, ReplyTarget source, bool isAdmin)
        {
            var target = ReplyTarget.ForTell(source.SenderId, source.SenderName);
            int page;
            ReplyPages reply = null;
            if (parts.Length == 3 && int.TryParse(parts[2], out page))
            {
                lock (_orgOutputSync) _replyPages.TryGetValue(parts[1], out reply);
            }
            else page = 0;
            if (reply == null || reply.CreatedUtc < DateTime.UtcNow.AddMinutes(-30) || page < 1 || page > reply.Pages.Length)
            {
                Reply(target, "That page has expired. Please run the original command again.");
                return;
            }
            if (reply.AdminOnly && !isAdmin)
            {
                Reply(target, "You are not authorized to open that page.");
                return;
            }
            Reply(target, RenderBlobPage(reply.Title, reply.Label, reply.Pages[page - 1], reply.Heading,
                page - 1, reply.Pages.Length, ReplyPageNavigation(reply, page - 1)));
        }
    }
}
