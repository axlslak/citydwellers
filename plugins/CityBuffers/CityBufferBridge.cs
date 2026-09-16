using System;
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
        public static bool Ready;

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
            Logger.Information("BUFFER IPC and tell sender initialized for " + Client.CharacterName + ".");
        }

        public static void Stop()
        {
            Client.OnUpdate -= Tick;
            Ready = false;
            _lifetime?.Cancel();
            if (_server != null) try { _server.Wait(1500); } catch (AggregateException) { }
            if (_dataDir != null) TellQueue.DeleteHeartbeat(_dataDir, Client.CharacterName);
            _lifetime?.Dispose();
            _lifetime = null;
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
            TellQueue.Enqueue(_dataDir, Client.CharacterName, null, recipient, message,
                Client.CharacterName); // Help links/team prompts belong to this buffer.
        }

        public static void SendPrivateMessage(int recipient, string message, bool logMessage = true)
            => SendPrivateMessage((uint)recipient, message, logMessage);

        private static void Tick(object sender, double delta)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                if (_lastSent > now.AddSeconds(5)) _lastSent = null;
                if (now >= _nextHeartbeat)
                {
                    bool inPlay = Client.InPlay && DynelManager.LocalPlayer != null;
                    _snapshot = JsonConvert.SerializeObject(new {
                        Character = Client.CharacterName, Kind = "froob", InPlay = inPlay,
                        Ready = inPlay && Ready, ObservedUtc = now,
                        Profession = inPlay ? ((Profession)DynelManager.LocalPlayer.Profession).ToString() : "Unknown",
                        NanoCount = inPlay ? DynelManager.LocalPlayer.SpellList.Count() : 0,
                        QueueLength = Main.QueueProcessor?.Queue.AllEntries.Length ?? 0
                    });
                    TellQueue.WriteHeartbeat(_dataDir, Client.CharacterName, inPlay, false, _lastSent);
                    _nextHeartbeat = now.AddSeconds(1);
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
