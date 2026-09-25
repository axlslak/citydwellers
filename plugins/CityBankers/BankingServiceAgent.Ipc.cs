using TradeTrace = CityDwellers.Shared.TradeTrace;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class DispatchProposal
        {
            private int _claim; // 0 queued, 1 owned by AO thread, 2 cancelled before execution
            public bool TryBegin() => Interlocked.CompareExchange(ref _claim, 1, 0) == 0;
            public bool TryCancel()
            {
                if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0) return false;
                Reply.TrySetCanceled();
                return true;
            }
            public string Kind;
            public WithdrawalState CruRequest;
            public string BatchId;
            public string Stage;
            public DispatchCensusGrant DispatchCensus;
            public DispatchCommand Command;
            public ReturnOffer Return;
            public ExtractionProof Extraction;
            public BagAuditResult Census;
            public ReceiptEvidence Cancellation;
            public StorageRecoveryRequest StorageRecovery;
            [JsonIgnore]
            public readonly TaskCompletionSource<string> Reply =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentQueue<DispatchProposal> _dispatchProposals =
            new ConcurrentQueue<DispatchProposal>();
        private int _queuedProposals;
        private CancellationTokenSource _ipcLifetime;
        private Task _ipcServer;
        private DispatchCommand _reservedDispatch;
        private Stopwatch _reservationAge;
        private Task<string> _dispatchPreparation;
        private readonly Dictionary<string, string> _workerPreparationReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _preparingBatch;
        private string _preparingContents;
        private readonly Stopwatch _ipcRetry = Stopwatch.StartNew();
        private readonly Dictionary<string, Task<string>> _storageInquiries = new Dictionary<string, Task<string>>();
        private readonly Dictionary<string, Stopwatch> _storageInquiryIntervals = new Dictionary<string, Stopwatch>();
        private readonly Dictionary<string, Stopwatch> _workerRetries = new Dictionary<string, Stopwatch>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _preparingAge = Stopwatch.StartNew();
        private static BankingServiceAgent _ipcOwner;
        private BankerMemoryWake _bankerMemoryWake;
        private string _proposalError;

        private sealed class BankerMemoryWake : CityDwellers.Shared.BankerSignalWake
        {
            public override void Wake() => BankerActivityGovernor.Wake();
        }
        internal static DispatchCommand CurrentInboundDispatch =>
            _ipcOwner?._workerCommand ?? _ipcOwner?._reservedDispatch;
        internal static bool CentralTransferBusy => _ipcOwner != null &&
            (_ipcOwner._afterReceipt != null || _ipcOwner._reserveOperation != null || _ipcOwner._stackOperation != null || _ipcOwner._activeBatch != null || _ipcOwner._donationCleanup != null || _ipcOwner._returnOffer != null || _ipcOwner._extraction != null ||
             (_ipcOwner._receipt != null && !_ipcOwner._donationActive));

        internal static bool OwnsDonationTrade(Identity partner) => _ipcOwner != null &&
            _ipcOwner._donationActive && _ipcOwner._donationPartner == partner &&
            _ipcOwner._receipt?.Kind == "donation" && _ipcOwner._afterReceipt == null;

        private static string BankerPipe(string character)
        {
            // All character AppDomains belong to one unified host. No wall-clock leases.
            return "CityDwellers.Bankers." + Process.GetCurrentProcess().Id + "." + character.ToLowerInvariant();
        }

        private void StartBankerIpc()
        {
            _ipcOwner = this;
            _bankerMemoryWake = new BankerMemoryWake();
            CityDwellers.Shared.ManagerMemory.Current.RegisterBankerSignalWake(
                Client.CharacterName, _bankerMemoryWake);
            _ipcLifetime = new CancellationTokenSource();
            string pipeName = BankerPipe(Client.CharacterName);
            _ipcServer = ServeBankerIpc(pipeName, _ipcLifetime.Token);
        }

        private async Task ServeBankerIpc(string name, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    using (token.Register(() => pipe.Dispose()))
                    {
                        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                        await CityDwellers.Shared.LocalIpc.RespondAsync(pipe, async line =>
                        {
                            var proposal = JsonConvert.DeserializeObject<DispatchProposal>(line ?? "null");
                            if (proposal == null) return "busy";
                            if (Interlocked.Increment(ref _queuedProposals) > 64)
                            { Interlocked.Decrement(ref _queuedProposals); return "busy"; }
                            _dispatchProposals.Enqueue(proposal);
                            BankerActivityGovernor.Wake();
                            // The AO update thread evaluates readiness and reserves capacity.
                            // The pipe thread never reads inventory or calls an AO operation.
                            Task done = await Task.WhenAny(proposal.Reply.Task,
                                Task.Delay(3000, token)).ConfigureAwait(false);
                            if (done == proposal.Reply.Task)
                                return await proposal.Reply.Task.ConfigureAwait(false);
                            // Timeout may cancel only unclaimed work. A started admission
                            // can still commit; never tell its caller it was rejected.
                            return proposal.TryCancel() ? "busy" : "pending";
                        }, 5000, token).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    if (!token.IsCancellationRequested)
                    {
                        try { await Task.Delay(250, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
            }
        }

        private static async Task<string> SendBankerMemory(
            string character, DispatchProposal proposal, int timeoutMilliseconds = 4000)
        {
            try
            {
                var request = new CityDwellers.Shared.BankerSignalRequest(
                    JsonConvert.SerializeObject(proposal));
                if (!CityDwellers.Shared.ManagerMemory.Current.EnqueueBankerSignal(character, request))
                    return "busy";
                Task completed = await Task.WhenAny(
                    request.ReplyTask, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false);
                if (completed == request.ReplyTask)
                    return await request.ReplyTask.ConfigureAwait(false);
                return request.TryCancel() ? "busy" : "pending";
            }
            catch (Exception)
            {
                return "pending";
            }
        }

        private void DrainBankerMemorySignals()
        {
            while (Volatile.Read(ref _queuedProposals) < 64)
            {
                var request = CityDwellers.Shared.ManagerMemory.Current.TakeBankerSignal(
                    Client.CharacterName);
                if (request == null) return;
                if (!request.TryBegin()) continue;

                DispatchProposal proposal;
                try { proposal = JsonConvert.DeserializeObject<DispatchProposal>(request.Payload ?? "null"); }
                catch (JsonException) { request.Reply("busy"); continue; }
                if (proposal == null) { request.Reply("busy"); continue; }

                if (Interlocked.Increment(ref _queuedProposals) > 64)
                {
                    Interlocked.Decrement(ref _queuedProposals);
                    request.Reply("busy");
                    continue;
                }

                var replyTarget = request;
                proposal.Reply.Task.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        replyTarget.Reply(task.Result);
                    else
                        replyTarget.Reply("pending");
                }, TaskScheduler.Default);
                _dispatchProposals.Enqueue(proposal);
                BankerActivityGovernor.Wake();
            }
        }

        private static async Task<string> AskWorkerToPrepare(DispatchCommand command)
        {
            try
            {
                return await SendBankerMemory(command.DestinationCharacter,
                    new DispatchProposal { Kind = "prepare", BatchId = command.BatchId, Command = command })
                    .ConfigureAwait(false);
            }
            catch (Exception) { return "busy:Worker preparation IPC did not respond."; }
        }

        private void TickBankerIpc()
        {
            DrainBankerMemorySignals();
            if (_reservedDispatch != null && !Trade.IsTrading && _reservationAge.ElapsedMilliseconds > 15000)
                _reservedDispatch = null;
            DispatchProposal proposal;
            int processed = 0;
            while (processed++ < 32 && _dispatchProposals.TryDequeue(out proposal))
            {
                Interlocked.Decrement(ref _queuedProposals);
                if (!proposal.TryBegin()) continue;
                try
                {
                    if (proposal.Kind == "wake")
                    {
                        proposal.Reply.TrySetResult("awake");
                        continue;
                    }
                    if (proposal.Kind == "dispatch-census-request" || proposal.Kind == "dispatch-census-prepare" ||
                        proposal.Kind == "storage-recovery" || proposal.Kind == "local-census")
                    {
                        proposal.Reply.TrySetResult("denied:Physical audit requires explicit administrator action.");
                        continue;
                    }
                    if (HandleCruProposal(proposal)) continue;
                    if (_stackOperation != null || _reserveOperation != null) { proposal.Reply.TrySetResult("busy"); continue; }
                    // Disabled: if (HandleDispatchCensusProposal(proposal)) continue;
                    if (HandleWithdrawalPreparation(proposal)) continue;
                    if (HandleTradeStageProposal(proposal)) continue;
                    // Disabled: if (HandleStorageRecoveryProposal(proposal)) continue;
                    if (HandleCancellationProposal(proposal)) continue;
                    // First-startup admission calls the merger directly.
                    // Disabled: if (HandleLocalCensusProposal(proposal)) continue;
                    if (HandleExtractionProposal(proposal)) continue;
                    if (HandleReturnProposal(proposal)) continue;
                }
                catch (Exception ex)
                {
                    // These handlers commit retained proof or reserve an offer;
                    // they never perform AO movement. The owning operation stays
                    // reserved and retries instead of disabling Central's IPC.
                    proposal.Reply.TrySetResult("pending");
                    if (_proposalError != ex.Message)
                        Logger.Warning("[CityBankers] Recovery IPC commit pending: " + ex.Message);
                    _proposalError = ex.Message;
                    continue;
                }
                if (proposal.Kind == "storage-result")
                {
                    // The worker owns this durable record; live communication is IPC.
                    var result = RuntimeStateStore.ReadStorageResult(_settingsDir, Client.CharacterName);
                    proposal.Reply.TrySetResult(result != null && result.BatchId == proposal.BatchId
                        ? JsonConvert.SerializeObject(result) : "pending");
                    continue;
                }
                DispatchCommand command = proposal.Command;
                Guid commandAttempt;
                bool ready = proposal.Kind == "prepare" && !_isCentral && StartupCensusGate.IsOpen && Client.InPlay &&
                    !CensusReservedHere() &&
                    Inventory.Bank.IsOpen && !Trade.IsTrading && _storageJob == null && _storageRecovery == null &&
                    _workerCommand == null && _receipt == null && _withdrawal == null && _returnOffer == null && _extraction == null &&
                    command != null && command.Items != null && command.Items.Count > 0 &&
                    !string.IsNullOrWhiteSpace(command.BatchId) && Guid.TryParseExact(command.AttemptId, "N", out commandAttempt) &&
                    string.Equals(command.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(command.SourceCharacter, _centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(command.DestinationCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    (_reservedDispatch == null || (_reservedDispatch.BatchId == command.BatchId &&
                        _reservedDispatch.TransactionId == command.TransactionId && _reservedDispatch.AttemptId == command.AttemptId &&
                        MatchesExpected(_reservedDispatch.Items, command.Items)));
                if (ready)
                {
                    // A delayed prepare from a retired actor cannot reserve a
                    // trade after a new census has rebuilt physical routing.
                    var batch = RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches.SingleOrDefault(b =>
                        b.BatchId == command.BatchId && b.AttemptId == command.AttemptId && b.Status == "queued");
                    ready = batch != null && batch.TransactionId == command.TransactionId &&
                        string.Equals(batch.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        SameManifest(batch.Items, command.Items);
                }
                if (ready)
                {
                    var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                    var worker = storage?.Workers?.SingleOrDefault(w =>
                        string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(w.Role, _role, StringComparison.OrdinalIgnoreCase));
                    ready = IsReserveBatch(command.Items) ? ReserveWorkerReady(command) :
                        worker?.Bags != null && worker.Bags.Sum(b => b == null ? 0 :
                        Math.Max(0, b.Capacity - (b.Items?.Count ?? b.Capacity))) >= command.Items.Count;
                }
                string spaceReason = null;
                if (ready && Inventory.NumFreeSlots < command.Items.Count + 1)
                {
                    ready = false;
                    spaceReason = "Worker inventory has " + Inventory.NumFreeSlots + " free slot(s); needs " +
                        (command.Items.Count + 1) + " for " + command.Items.Count + " item(s) plus bag handling.";
                }
                if (ready)
                {
                    _reservedDispatch = command;
                    _reservationAge = Stopwatch.StartNew();
                }
                proposal.Reply.TrySetResult(ready ? "ready:" + command.BatchId : spaceReason == null ? "busy" : "busy:" + spaceReason);
            }
        }

        private bool WorkerPrepared(DispatchBatchState batch)
        {
            string contents = batch.Character + "/" + batch.TransactionId + "/" + batch.AttemptId + "/" +
                string.Join(";", (batch.Items ?? new List<TransferItemState>()).Select(CustodyKey).OrderBy(key => key));
            if (_dispatchPreparation == null || _preparingBatch != batch.BatchId || _preparingContents != contents)
            {
                // A 1000ms floor between preparation attempts, named with its
                // constant: on a first attempt this can gate the whole batch.
                if (_ipcRetry.ElapsedMilliseconds < 1000)
                {
                    TradeTrace.Wait(_dispatchSpan, "memory-retry-floor-1000ms");
                    return false;
                }
                _preparingBatch = batch.BatchId;
                _preparingContents = contents;
                _preparingAge.Restart();
                // Stage 2.
                TradeTrace.Mark(_dispatchSpan, "worker.prepare.requested");
                TradeTrace.Count(_dispatchSpan, TradeTrace.IpcRoundTrips);
                _dispatchPreparation = AskWorkerToPrepare(new DispatchCommand
                {
                    BatchId = batch.BatchId, AttemptId = batch.AttemptId, TransactionId = batch.TransactionId, Role = batch.Role,
                    SourceCharacter = Client.CharacterName, DestinationCharacter = batch.Character,
                    Items = batch.Items, CreatedUtc = DateTime.UtcNow
                });
                return false;
            }
            if (!_dispatchPreparation.IsCompleted)
            {
                TradeTrace.Wait(_dispatchSpan, "worker-prepare-memory-reply");
                return false;
            }
            string reply = _dispatchPreparation.Status == TaskStatus.RanToCompletion ? _dispatchPreparation.Result : null;
            bool ready = _preparingAge.ElapsedMilliseconds < 10000 && reply == "ready:" + batch.BatchId;
            // Stage 3.
            TradeTrace.Mark(_dispatchSpan,
                ready ? "worker.prepare.ready" : "worker.prepare.refused",
                detail: ready ? null : (reply ?? "no reply"));
            if (!ready && reply != null && reply.StartsWith("busy:", StringComparison.Ordinal))
                _workerPreparationReasons[batch.Character] = reply.Substring(5);
            else
                _workerPreparationReasons.Remove(batch.Character);
            _dispatchPreparation = null;
            _ipcRetry.Restart();
            if (!ready) _workerRetries[batch.Character] = Stopwatch.StartNew();
            return ready;
        }

        private string WorkerPreparationReason(string character, string fallback)
        {
            string reason;
            return _workerPreparationReasons.TryGetValue(character, out reason) ? reason : fallback;
        }

        private bool WorkerRetryDue(string character)
        {
            Stopwatch retry;
            return !_workerRetries.TryGetValue(character, out retry) || retry.ElapsedMilliseconds >= 3000;
        }

        private StorageBatchResult ReadWorkerStorageReply(DispatchBatchState batch)
        {
            Task<string> inquiry;
            if (!_storageInquiries.TryGetValue(batch.BatchId, out inquiry))
            {
                Stopwatch interval;
                if (_storageInquiryIntervals.TryGetValue(batch.BatchId, out interval) && interval.ElapsedMilliseconds < 1000)
                    return null;
                _storageInquiries[batch.BatchId] = SendBankerMemory(batch.Character,
                    new DispatchProposal { Kind = "storage-result", BatchId = batch.BatchId });
                return null;
            }
            if (!inquiry.IsCompleted) return null;
            _storageInquiries.Remove(batch.BatchId);
            _storageInquiryIntervals[batch.BatchId] = Stopwatch.StartNew();
            if (inquiry.IsFaulted) { var observed = inquiry.Exception; return null; }
            if (inquiry.IsCanceled || inquiry.Result == "pending" || inquiry.Result == "busy") return null;
            try
            {
                var result = JsonConvert.DeserializeObject<StorageBatchResult>(inquiry.Result ?? "null");
                return result?.BatchId == batch.BatchId ? result : null;
            }
            catch (JsonException) { return null; }
        }
    }
}
