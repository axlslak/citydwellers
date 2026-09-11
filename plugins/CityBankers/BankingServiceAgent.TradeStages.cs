using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AOSharp.Clientless;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class TradeStageQuery
        {
            public Task<string> Request;
            public readonly Stopwatch Age = Stopwatch.StartNew();
            public bool Ready;
        }

        private readonly Dictionary<string, TradeStageQuery> _tradeStages = new Dictionary<string, TradeStageQuery>();
        private readonly HashSet<string> _recordedTradeStages = new HashSet<string>();
        private readonly Stopwatch _localDispatchAcceptAge = Stopwatch.StartNew();
        private bool _dispatchConfirmed;
        private Stopwatch _dispatchClosedAge;

        private bool DispatchClosedWithoutResult()
        {
            if (Trade.IsTrading || _afterReceipt != null)
            { _dispatchClosedAge = null; return false; }
            if (_dispatchClosedAge == null)
            { _dispatchClosedAge = Stopwatch.StartNew(); return false; }
            return _dispatchClosedAge.ElapsedMilliseconds >= 1000;
        }

        private DispatchCommand CurrentDispatchCommand()
        {
            if (!_isCentral) return _workerCommand;
            if (_activeBatch == null) return null;
            return new DispatchCommand
            {
                BatchId = _activeBatch.BatchId, AttemptId = _activeBatch.AttemptId,
                TransactionId = _activeBatch.TransactionId, Role = _activeBatch.Role,
                SourceCharacter = _centralCharacter, DestinationCharacter = _activeBatch.Character,
                Items = _activeBatch.Items
            };
        }

        private readonly Stopwatch _internalOpenedAge = Stopwatch.StartNew();
        private Task<string> _withdrawalPreparation;
        private readonly Stopwatch _withdrawalPreparationAge = Stopwatch.StartNew();
        private readonly Stopwatch _managedAddWait = Stopwatch.StartNew();

        private DispatchCommand CurrentInternalCommand()
        {
            if (_returnOffer != null && (_receipt?.Kind == "recovery-return-send" || _receipt?.Kind == "recovery-return-receive"))
                return new DispatchCommand { BatchId = _returnOffer.Id, AttemptId = _returnOffer.Id,
                    TransactionId = _returnOffer.TransactionId, Role = "recovery-return",
                    SourceCharacter = _returnOffer.Source, DestinationCharacter = _centralCharacter,
                    Items = new List<TransferItemState> { _returnOffer.Item } };
            if (_withdrawal != null && _receipt?.Kind == "withdrawal-transfer")
                return WithdrawalCommand(_withdrawal);
            return CurrentDispatchCommand();
        }

        private DispatchCommand WithdrawalCommand(WithdrawalState state) => new DispatchCommand
        {
            BatchId = state.Id, AttemptId = state.TransferAttemptId,
            TransactionId = state.DonationTransactionId, Role = "withdrawal-transfer",
            SourceCharacter = state.SourceCharacter, DestinationCharacter = _centralCharacter,
            Items = new List<TransferItemState> { state.Item }
        };

        private bool LocalInternalAccepted() => _receipt == null ? false :
            _receipt.Kind == "recovery-return-send" || _receipt.Kind == "recovery-return-receive" ? _returnAccepted :
            _receipt.Kind == "withdrawal-transfer" || _receipt.Kind == "withdrawal-pickup" ? _withdrawalAccepted :
            _isCentral ? _outgoingAccepted : _workerAccepted;

        private bool IsManagedReturnOrWithdrawal() => _receipt != null &&
            (_receipt.Kind == "recovery-return-send" || _receipt.Kind == "recovery-return-receive" || _receipt.Kind == "withdrawal-transfer");

        private bool HandleWithdrawalPreparation(DispatchProposal proposal)
        {
            if (proposal.Kind != "withdrawal-prepare") return false;
            var state = WithdrawalStore.LoadAll(_settingsDir).SingleOrDefault(r => r.Id == proposal.Command?.BatchId);
            bool ready = _isCentral && StartupCensusGate.IsOpen && Client.InPlay && Inventory.Bank.IsOpen &&
                !Trade.IsTrading && _receipt == null && _activeBatch == null && _returnOffer == null &&
                !_donationActive && _donationCleanup == null && _extraction == null && Inventory.NumFreeSlots >= 2 &&
                state != null && WithdrawalStore.HasStatus(state, "extracting") &&
                !WithdrawalStore.GetCensusCharacters(_settingsDir).Contains(state.SourceCharacter) &&
                SameDispatchAttempt(WithdrawalCommand(state), proposal.Command);
            proposal.Reply.TrySetResult(ready ? "ready:" + state.TransferAttemptId + ":prepare" : "pending");
            return true;
        }

        private void TickWithdrawalReceiver()
        {
            if (!_isCentral || _withdrawalPickupTrade || _withdrawalAccepted) return;
            var command = CurrentInternalCommand();
            if (!DispatchTradeIsCurrent(command) || !DispatchWindowsConsistent(command) ||
                !DispatchPeerReady("accepted")) return;
            _withdrawalAccepted = true;
            _localDispatchAcceptAge.Restart();
            Trade.Accept();
        }

        private static bool SameDispatchAttempt(DispatchCommand a, DispatchCommand b) =>
            a != null && b != null && a.AttemptId == b.AttemptId && a.BatchId == b.BatchId &&
            a.TransactionId == b.TransactionId && a.Role == b.Role &&
            string.Equals(a.SourceCharacter, b.SourceCharacter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.DestinationCharacter, b.DestinationCharacter, StringComparison.OrdinalIgnoreCase) &&
            SameManifest(a.Items, b.Items);

        private bool DispatchTradeIsCurrent(DispatchCommand command)
        {
            Guid attempt;
            if (command == null || !Guid.TryParseExact(command.AttemptId, "N", out attempt) ||
                !StartupCensusGate.IsOpen || !Client.InPlay || !Trade.IsTrading ||
                _afterReceipt != null || _receipt == null || _receipt.AttemptId != command.AttemptId ||
                _receipt.Direction != (string.Equals(command.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) ? -1 : 1) || !SameManifest(_receipt.PreparedItems, command.Items)) return false;
            string peer = string.Equals(command.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase)
                ? command.DestinationCharacter : command.SourceCharacter;
            return string.Equals(FindPlayerName(Trade.CurrentTarget), peer, StringComparison.OrdinalIgnoreCase) &&
                (_receipt.Kind == "recovery-return-send" || _receipt.Kind == "recovery-return-receive" ? _returnOpened :
                    _receipt.Kind == "withdrawal-transfer" ? _withdrawalTradeOpened :
                    _isCentral ? _outgoingOpened : _workerCommand != null);
        }

        // The receiver cache may be incomplete, but it may not contradict the
        // sender's exact offer. Count duplicate templates, not just item IDs.
        private static bool IsManifestSubset(IEnumerable<TransferItemState> seen, IEnumerable<TransferItemState> expected)
        {
            if (seen == null || expected == null || seen.Any(i => i == null) || expected.Any(i => i == null)) return false;
            var counts = expected.GroupBy(CustodyKey).ToDictionary(g => g.Key, g => g.Count());
            return seen.GroupBy(CustodyKey).All(g => counts.ContainsKey(g.Key) && counts[g.Key] >= g.Count());
        }

        private bool DispatchWindowsConsistent(DispatchCommand command)
        {
            var local = Trade.PlayerWindowCache?.Items;
            var remote = Trade.TargetWindowCache?.Items;
            if (command == null || _receipt == null || local == null || remote == null) return false;
            return _receipt.Direction < 0
                ? remote.Count == 0 && SameManifest(SnapshotTradeItems(local), command.Items)
                : local.Count == 0 && IsManifestSubset(SnapshotTradeItems(remote), command.Items);
        }

        private bool HandleTradeStageProposal(DispatchProposal proposal)
        {
            if (proposal.Kind != "trade-stage") return false;
            var command = CurrentInternalCommand();
            bool current = SameDispatchAttempt(command, proposal.Command) && DispatchTradeIsCurrent(command);
            bool ready = false;
            if (current && proposal.Stage == "opened")
                ready = (IsManagedReturnOrWithdrawal() ? _internalOpenedAge :
                    _isCentral ? _dispatchTradeAge : _workerTradeAge).ElapsedMilliseconds >= 500;
            else if (current && proposal.Stage == "accepted")
                ready = LocalInternalAccepted() && DispatchWindowsConsistent(command);
            // This handler only reports state from the AO update thread. It
            // never accepts/confirms a trade on behalf of an IPC request.
            proposal.Reply.TrySetResult(ready ? "ready:" + command.AttemptId + ":" + proposal.Stage : "pending");
            return true;
        }

        private bool DispatchPeerReady(string stage)
        {
            var command = CurrentInternalCommand();
            if (!DispatchTradeIsCurrent(command)) return false;
            string key = command.AttemptId + ":" + stage;
            TradeStageQuery query;
            if (!_tradeStages.TryGetValue(key, out query))
            {
                query = new TradeStageQuery();
                _tradeStages.Add(key, query);
                query.Request = AskDispatchStage(command, stage);
                return false;
            }
            if (query.Request != null && query.Request.IsCompleted)
            {
                query.Ready = query.Request.Status == TaskStatus.RanToCompletion &&
                    query.Request.Result == "ready:" + key && query.Age.ElapsedMilliseconds <= 1500;
                query.Request = null;
                if (query.Ready && _recordedTradeStages.Add(key))
                    PersistReceipt("peer-" + stage + "-acknowledged");
            }
            bool ready = query.Ready && query.Age.ElapsedMilliseconds <= 1500;
            if (query.Request == null && query.Age.ElapsedMilliseconds >= 500)
            {
                // Age measures request departure, not delayed reply arrival.
                // Keep an acknowledgement only until this refresh begins.
                query.Ready = false;
                query.Age.Restart();
                query.Request = AskDispatchStage(command, stage);
            }
            return ready;
        }

        private async Task<string> AskDispatchStage(DispatchCommand command, string stage)
        {
            try
            {
                string peer = string.Equals(command.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase)
                ? command.DestinationCharacter : command.SourceCharacter;
                return await CityDwellers.Shared.LocalIpc.RequestLineAsync(BankerPipe(peer),
                    JsonConvert.SerializeObject(new DispatchProposal { Kind = "trade-stage", Command = command, Stage = stage }),
                    1000, 4000).ConfigureAwait(false);
            }
            catch (Exception) { return "pending"; }
        }
    }
}
