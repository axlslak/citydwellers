using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;

namespace MalisBuffBots
{
    // One instance per character AppDomain. Only Tick touches AO game state.
    internal static class CityBufferBridge
    {
        private static string _dataDir;
        private static DateTime _nextHeartbeat;
        private static DateTime? _lastSent;
        private static CancellationTokenSource _lifetime;
        private static Task _server;
        private static volatile string _snapshot = "{}";
        private static string _lastCatalogueFingerprint;
        private static bool _catalogueReadyLogged;
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
            _lifetime = new CancellationTokenSource();
            string pipeName = BufferSettings.PipeName(Client.CharacterName);
            _server = Serve(pipeName, _lifetime.Token);
            Client.OnUpdate += Tick;
            Logger.Information("BUFFER status bridge and shared tell sender initialized for " + Client.CharacterName + ".");
        }

        public static void Stop()
        {
            Client.OnUpdate -= Tick;
            Ready = false;
            _lifetime?.Cancel();
            if (_server != null) try { _server.Wait(1500); } catch (AggregateException) { }
            if (_dataDir != null) TellQueue.DeleteHeartbeat(_dataDir, Client.CharacterName);
            try { ManagerMemory.Current.MarkBufferBotOffline(Client.CharacterName); } catch { }
            _lifetime?.Dispose();
            _lifetime = null;
        }

        private static void LogCatalogueChange(
            int[] spellList,
            BufferAdvertisedBuff[] advertised)
        {
            if (!Ready)
                return;

            string fingerprint = string.Join(
                "|",
                (advertised ?? new BufferAdvertisedBuff[0])
                    .Where(entry => entry != null)
                    .OrderBy(entry => entry.Profession)
                    .ThenBy(entry => entry.Tag ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(entry =>
                        entry.Profession + ":" +
                        (entry.Tag ?? string.Empty) + ":" +
                        (entry.Name ?? string.Empty)));

            if (_catalogueReadyLogged &&
                string.Equals(
                    _lastCatalogueFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
                return;

            string tags = string.Join(
                ",",
                (advertised ?? new BufferAdvertisedBuff[0])
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Tag))
                    .Select(entry => entry.Tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));

            Logger.Information(
                "BUFFER catalogue " +
                (_catalogueReadyLogged ? "changed" : "ready") +
                ": knownNanos=" + (spellList?.Length ?? 0) +
                " advertised=" + (advertised?.Length ?? 0) +
                " tags=[" + tags + "].");

            _lastCatalogueFingerprint = fingerprint;
            _catalogueReadyLogged = true;
        }

        private static async Task Serve(string name, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    using (stop.Register(() => pipe.Dispose()))
                    {
                        await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                        await LocalIpc.RespondAsync(pipe, request => Task.FromResult(_snapshot),
                            5000, stop).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    if (stop.IsCancellationRequested) break;
                    Logger.Warning("BUFFER status IPC: " + ex.Message);
                    try { await Task.Delay(1000, stop).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
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

        private static void Tick(object sender, double delta)
        {
            try
            {
                Main.Ipc?.DrainMemorySignals();
                DateTime now = DateTime.UtcNow;
                if (_lastSent > now.AddSeconds(5)) _lastSent = null;
                if (now >= _nextHeartbeat)
                {
                    _nextHeartbeat = now.AddSeconds(1);
                    bool inPlay = Client.InPlay && DynelManager.LocalPlayer != null;
                    _snapshot = JsonConvert.SerializeObject(new {
                        Character = Client.CharacterName, Kind = "froob", InPlay = inPlay,
                        Ready = inPlay && Ready, ObservedUtc = now,
                        Profession = inPlay ? ((Profession)DynelManager.LocalPlayer.Profession).ToString() : "Unknown",
                        NanoCount = inPlay ? DynelManager.LocalPlayer.SpellList.Count() : 0,
                        QueueLength = Main.QueueProcessor?.Queue.AllEntries.Length ?? 0
                    });
                    if (inPlay)
                    {
                        int[] spellList = DynelManager.LocalPlayer.SpellList ?? new int[0];
                        BufferAdvertisedBuff[] advertised =
                            IPCBotCacheData.BuildAdvertisedBuffs(spellList);

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
                            AdvertisedBuffs = advertised
                        });

                        LogCatalogueChange(spellList, advertised);
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
