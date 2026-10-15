using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityDwellers.Shared;

namespace MalisBuffBots
{
    // One instance per character AppDomain. Only Tick touches AO game state.
    internal static class CityBufferBridge
    {
        private static string _dataDir;
        private static DateTime _nextHeartbeat;
        private static DateTime? _lastSent;
        internal static int[] CatalogueSpellList { get; private set; }
        internal static BufferAdvertisedBuff[] CatalogueBuffs { get; private set; }
        public static bool Ready;

        private sealed class RequestBudget
        {
            public double Tokens = 4, Updated, NoticeAfter;
        }
        private static readonly Dictionary<uint, RequestBudget> RequestBudgets = new Dictionary<uint, RequestBudget>();
        private static double _backlogCheckAfter;
        private static bool _backlogged;

        internal static bool AdmitPublicRequest(uint sender)
        {
            bool notify = false, accepted = false;
            lock (RequestBudgets)
            {
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                RequestBudget budget;
                if (!RequestBudgets.TryGetValue(sender, out budget))
                {
                    if (RequestBudgets.Count >= 1024)
                    {
                        foreach (uint old in RequestBudgets.Where(p => now - p.Value.Updated >= 600).Select(p => p.Key).ToArray())
                            RequestBudgets.Remove(old);
                        if (RequestBudgets.Count >= 1024) return false;
                    }
                    budget = new RequestBudget { Updated = now };
                    RequestBudgets.Add(sender, budget);
                }
                budget.Tokens = Math.Min(4, budget.Tokens + (now - budget.Updated) / 2);
                budget.Updated = now;
                if (now >= _backlogCheckAfter)
                {
                    _backlogCheckAfter = now + 1;
                    bool wasBacklogged = _backlogged;
                    try { _backlogged = TellQueue.IsBacklogged(_dataDir, wasBacklogged ? 128 : 256); }
                    catch { _backlogged = true; }
                    if (wasBacklogged != _backlogged)
                        Logger.Warning(_backlogged ? "Buffer request admission paused: tell backlog or queue storage unavailable."
                            : "Buffer request admission resumed: tell backlog cleared.");
                }
                if (!_backlogged && budget.Tokens >= 1) { budget.Tokens--; accepted = true; }
                else if (!_backlogged && now >= budget.NoticeAfter)
                { budget.NoticeAfter = now + 10; notify = true; }
            }
            if (notify) SendPrivateMessage(sender, "Please slow down and let your queued buffs finish. Try again shortly.");
            return accepted;
        }

        public static void Start()
        {
            string settings, error;
            if (!SettingsPaths.TryEnsureDirectories(out settings, out _dataDir, out error))
                throw new InvalidOperationException(error);
            TellQueue.EnsureDirectories(_dataDir);
            CatalogueSpellList = null;
            CatalogueBuffs = null;
            var saved = ManagerMemory.Current.ReadBufferCatalogue(Client.CharacterName);
            if (saved != null && saved.Paid == Main.PaidPilot &&
                (!Main.PaidPilot || saved.Profession == Main.PaidProfession) &&
                !ManagerMemory.Current.BufferCatalogueAuditRequested(Client.CharacterName))
            {
                CatalogueSpellList = saved.KnownNanos.ToArray();
                CatalogueBuffs = saved.AdvertisedBuffs();
                Logger.Information("BUFFER catalogue restored: knownNanos=" + CatalogueSpellList.Length +
                    " advertised=" + CatalogueBuffs.Length + "; no discovery requested.");
            }
            Client.OnUpdate += Tick;
            Logger.Information("BUFFER status bridge and shared tell sender initialized for " + Client.CharacterName + ".");
        }

        public static void Stop()
        {
            Client.OnUpdate -= Tick;
            Ready = false;
            if (_dataDir != null) TellQueue.DeleteHeartbeat(_dataDir, Client.CharacterName);
            try { ManagerMemory.Current.MarkBufferBotOffline(Client.CharacterName); } catch { }
        }

        internal static bool InitializeCatalogue()
        {
            if (CatalogueSpellList != null) return true;
            int[] known = Main.UploadedSpellList();
            if (known.Length == 0) return false;
            var advertised = BufferBotCache.BuildAdvertisedBuffs(known);
            ManagerMemory.Current.SaveBufferCatalogue(Client.CharacterName,
                (int)DynelManager.LocalPlayer.Profession, Main.PaidPilot, known, advertised);
            CatalogueSpellList = known;
            CatalogueBuffs = advertised;
            LogCatalogueDiscovery(known, advertised);
            return true;
        }

        private static void LogCatalogueDiscovery(
            int[] spellList,
            BufferAdvertisedBuff[] advertised)
        {
            string tags = string.Join(
                ",",
                (advertised ?? new BufferAdvertisedBuff[0])
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Tag))
                    .Select(entry => entry.Tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));

            Logger.Information(
                "BUFFER catalogue learned and saved: knownNanos=" + (spellList?.Length ?? 0) +
                " advertised=" + (advertised?.Length ?? 0) +
                " tags=[" + tags + "].");

            if (!Main.PaidPilot)
            {
                var definitions = Main.BuffsJson.Entries.Values.SelectMany(entries => entries)
                    .Where(entry => entry != null && entry.LevelToId != null).ToArray();
                foreach (int id in (spellList ?? new int[0]).Distinct().OrderBy(id => id))
                {
                    var matched = definitions.Where(entry => entry.ContainsId(id)).ToArray();
                    var offered = (advertised ?? new BufferAdvertisedBuff[0])
                        .Where(entry => (entry.NanoIds ?? new int[0]).Contains(id)).ToArray();
                    string name = "unknown";
                    try
                    {
                        NanoItem nano;
                        if (ItemData.Find(id, out nano)) name = nano.Name;
                    }
                    catch { /* An unavailable name must not interrupt catalogue publication. */ }
                    Logger.Information("BUFFER catalogue nano=" + id + " name='" + name +
                        "' status=" + (offered.Length > 0 ? "advertised" :
                            matched.Length > 0 ? "defined-not-advertised" : "no-definition") +
                        " tags=[" + string.Join(",", offered.Select(entry => entry.Tag)) + "].");
                }
            }
        }

        // Match Mali's overload, including the existing suppress-log argument.
        public static void SendPrivateMessage(uint recipient, string message, bool logMessage = true)
        {
            if (recipient == 0)
            {
                Logger.Warning("BUFFER tell dropped: recipient id 0 is invalid.");
                return;
            }
            TellQueue.Enqueue(_dataDir, Client.CharacterName, null, recipient, message,
                Client.CharacterName); // Help links/team prompts belong to this buffer.
        }

        public static void SendPrivateMessage(int recipient, string message, bool logMessage = true)
            => SendPrivateMessage((uint)recipient, message, logMessage);

        internal static void Diagnostic(int recipient, string message)
        {
            string text = "BUFFER " + Client.CharacterName + " requester=" + recipient + ": " + message;
            Logger.Information(text);
            ManagerMemory.Current.ReportBufferDiagnostic(text);
        }

        internal static void PaidResult(int recipient, string message)
            => RequestResult(recipient, message);

        internal static void RequestResult(int recipient, string message)
        {
            if (recipient == 0) return;
            // Results use the shared queue; any online sender can deliver them.
            TellQueue.Enqueue(_dataDir, Client.CharacterName, null, (uint)recipient, message);
        }

        private static void Tick(object sender, double delta)
        {
            try
            {
                Main.Coordination?.DrainMemorySignals();
                if (Main.PaidPilot)
                {
                    bool inPlayNow = Client.InPlay && DynelManager.LocalPlayer != null;
                    bool parked = ManagerMemory.Current.UpdatePaidBufferActivity(Client.CharacterName,
                        Ready && inPlayNow,
                        !inPlayNow || Main.QueueProcessor == null ||
                        Main.QueueProcessor.PaidFixerPostCastHold ||
                        Main.QueueProcessor.Queue.AllEntries.Length != 0 || DynelManager.LocalPlayer.IsCasting);
                    if (parked)
                    {
                        Ready = false;
                        if (inPlayNow && Team.IsInTeam) Team.LeaveTeam();
                    }
                }
                DateTime now = DateTime.UtcNow;
                if (_lastSent > now.AddSeconds(5)) _lastSent = null;
                if (now >= _nextHeartbeat)
                {
                    _nextHeartbeat = now.AddSeconds(1);
                    bool inPlay = Client.InPlay && DynelManager.LocalPlayer != null;
                    int[] spellList = inPlay ? Main.EffectiveSpellList() : new int[0];
                    if (inPlay)
                    {
                        ManagerMemory.Current.PublishBufferBotInfo(new BufferBotInfo
                        {
                            Character = Client.CharacterName,
                            Profession = (int)DynelManager.LocalPlayer.Profession,
                            IdentityType = (int)DynelManager.LocalPlayer.Identity.Type,
                            IdentityInstance = DynelManager.LocalPlayer.Identity.Instance,
                            SpellData = spellList,
                            ObservedUtc = now,
                            InPlay = true,
                            Ready = Ready,
                            QueueLength = Main.QueueProcessor?.Queue.AllEntries.Length ?? 0,
                            AdvertisedBuffs = CatalogueBuffs
                        });
                    }
                    TellQueue.WriteHeartbeatBestEffort(_dataDir, Client.CharacterName, inPlay, false, _lastSent,
                        message => Logger.Warning(message));
                }
                if (!Client.InPlay || (_lastSent.HasValue &&
                    _lastSent.Value > now.AddMilliseconds(-TellQueue.SenderIntervalMilliseconds))) return;
                TellQueueJob job;
                string path;
                if (!TellQueue.TryReadAssignment(_dataDir, Client.CharacterName, out job, out path)) return;
                bool success = false;
                string error = null;
                try
                {
                    if (job.RecipientId.HasValue && job.RecipientId.Value != 0)
                        Client.SendPrivateMessage(job.RecipientId.Value, job.Message, false);
                    else if (!string.IsNullOrWhiteSpace(job.RecipientName))
                        Client.Chat.SendPrivateMessage(job.RecipientName, job.Message, true);
                    else throw new InvalidOperationException("Tell has no recipient.");
                    _lastSent = now;
                    success = true;
                    Logger.Information("TELL QUEUE sent " + job.Id + " as " + Client.CharacterName + ".");
                }
                catch (Exception ex) { error = ex.Message; Logger.Warning("BUFFER tell failed: " + error); }
                finally { TellQueue.Complete(_dataDir, job, path, Client.CharacterName, success, error); }
            }
            catch (Exception ex) { Logger.Warning("BUFFER bridge update: " + ex.Message); }
        }
    }
}
