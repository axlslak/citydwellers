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
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class DispatchProposal
        {
            public string Kind;
            public string BatchId;
            public DispatchCommand Command;
            public ReturnOffer Return;
            public ExtractionProof Extraction;
            public BagAuditAgent.BagAuditResult Census;
            public ReceiptEvidence Cancellation;
            [JsonIgnore]
            public readonly TaskCompletionSource<string> Reply =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentQueue<DispatchProposal> _dispatchProposals =
            new ConcurrentQueue<DispatchProposal>();
        private CancellationTokenSource _ipcLifetime;
        private Task _ipcServer;
        private DispatchCommand _reservedDispatch;
        private Stopwatch _reservationAge;
        private Task<bool> _dispatchPreparation;
        private string _preparingBatch;
        private string _preparingContents;
        private readonly Stopwatch _ipcRetry = Stopwatch.StartNew();
        private readonly Dictionary<string, Task<string>> _storageInquiries = new Dictionary<string, Task<string>>();
        private readonly Dictionary<string, Stopwatch> _storageInquiryIntervals = new Dictionary<string, Stopwatch>();
        private readonly Dictionary<string, Stopwatch> _workerRetries = new Dictionary<string, Stopwatch>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _preparingAge = Stopwatch.StartNew();
        private static BankingServiceAgent _ipcOwner;
        private string _proposalError;
        internal static DispatchCommand CurrentInboundDispatch =>
            _ipcOwner?._workerCommand ?? _ipcOwner?._reservedDispatch;
        internal static bool CentralTransferBusy => _ipcOwner != null &&
            (_ipcOwner._activeBatch != null || _ipcOwner._donationCleanup != null || _ipcOwner._returnOffer != null || _ipcOwner._extraction != null ||
             (_ipcOwner._receipt != null && !_ipcOwner._donationActive));

        private static string BankerPipe(string character)
        {
            // All character AppDomains belong to one unified host. No wall-clock leases.
            return "CityDwellers.Bankers." + Process.GetCurrentProcess().Id + "." + character.ToLowerInvariant();
        }

        private void StartBankerIpc()
        {
            _ipcOwner = this;
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
                            _dispatchProposals.Enqueue(proposal);
                            // The AO update thread evaluates readiness and reserves capacity.
                            // The pipe thread never reads inventory or calls an AO operation.
                            Task done = await Task.WhenAny(proposal.Reply.Task,
                                Task.Delay(3000, token)).ConfigureAwait(false);
                            if (done == proposal.Reply.Task)
                                return await proposal.Reply.Task.ConfigureAwait(false);
                            proposal.Reply.TrySetCanceled();
                            return "busy";
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

        private static async Task<bool> AskWorkerToPrepare(DispatchCommand command)
        {
            try
            {
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(
                    BankerPipe(command.DestinationCharacter), JsonConvert.SerializeObject(new DispatchProposal
                    { Kind = "prepare", BatchId = command.BatchId, Command = command }),
                    1000, 4000).ConfigureAwait(false) == "ready:" + command.BatchId;
            }
            catch (Exception) { return false; }
        }

        private void TickBankerIpc()
        {
            if (_reservedDispatch != null && !Trade.IsTrading && _reservationAge.ElapsedMilliseconds > 15000)
                _reservedDispatch = null;
            DispatchProposal proposal;
            while (_dispatchProposals.TryDequeue(out proposal))
            {
                if (proposal.Reply.Task.IsCompleted) continue;
                try
                {
                    if (HandleCancellationProposal(proposal)) continue;
                    if (HandleLocalCensusProposal(proposal)) continue;
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
                    Inventory.Bank.IsOpen && !Trade.IsTrading && _storageJob == null &&
                    _workerCommand == null && _receipt == null && _withdrawal == null && _returnOffer == null && _extraction == null &&
                    command != null && command.Items != null && command.Items.Count > 0 &&
                    Inventory.NumFreeSlots >= command.Items.Count + 1 &&
                    !string.IsNullOrWhiteSpace(command.BatchId) && Guid.TryParseExact(command.AttemptId, "N", out commandAttempt) &&
                    string.Equals(command.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(command.SourceCharacter, _centralCharacter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(command.DestinationCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    (_reservedDispatch == null || (_reservedDispatch.BatchId == command.BatchId &&
                        _reservedDispatch.TransactionId == command.TransactionId && _reservedDispatch.AttemptId == command.AttemptId &&
                        MatchesExpected(_reservedDispatch.Items, command.Items)));
                if (ready)
                {
                    var storage = RuntimeStateStore.LoadStorageState(_settingsDir);
                    var worker = storage?.Workers?.SingleOrDefault(w =>
                        string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(w.Role, _role, StringComparison.OrdinalIgnoreCase));
                    ready = worker?.Bags != null && worker.Bags.Sum(b => b == null ? 0 :
                        Math.Max(0, b.Capacity - (b.Items?.Count ?? b.Capacity))) >= command.Items.Count;
                }
                if (ready)
                {
                    _reservedDispatch = command;
                    _reservationAge = Stopwatch.StartNew();
                }
                proposal.Reply.TrySetResult(ready ? "ready:" + command.BatchId : "busy");
            }
        }

        private bool WorkerPrepared(DispatchBatchState batch)
        {
            string contents = batch.Character + "/" + batch.TransactionId + "/" + batch.AttemptId + "/" +
                string.Join(";", (batch.Items ?? new List<TransferItemState>()).Select(CustodyKey).OrderBy(key => key));
            if (_dispatchPreparation == null || _preparingBatch != batch.BatchId || _preparingContents != contents)
            {
                if (_ipcRetry.ElapsedMilliseconds < 1000) return false;
                _preparingBatch = batch.BatchId;
                _preparingContents = contents;
                _preparingAge.Restart();
                _dispatchPreparation = AskWorkerToPrepare(new DispatchCommand
                {
                    BatchId = batch.BatchId, AttemptId = batch.AttemptId, TransactionId = batch.TransactionId, Role = batch.Role,
                    SourceCharacter = Client.CharacterName, DestinationCharacter = batch.Character,
                    Items = batch.Items, CreatedUtc = DateTime.UtcNow
                });
                return false;
            }
            if (!_dispatchPreparation.IsCompleted) return false;
            bool ready = _preparingAge.ElapsedMilliseconds < 10000 &&
                _dispatchPreparation.Status == TaskStatus.RanToCompletion && _dispatchPreparation.Result;
            _dispatchPreparation = null;
            _ipcRetry.Restart();
            if (!ready) _workerRetries[batch.Character] = Stopwatch.StartNew();
            return ready;
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
                _storageInquiries[batch.BatchId] = CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(batch.Character),
                    JsonConvert.SerializeObject(new DispatchProposal { Kind = "storage-result", BatchId = batch.BatchId }),
                    1000, 4000);
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
