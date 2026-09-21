using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using CityBankers;
using CityBankers.Shared;

namespace CityDwellers.Shared
{
    // A business transaction replaces only changed collections. All reads are
    // copies of Manager's working state; persistence is invoked once at commit.
    public sealed class AccountingState
    {
        public ActiveLedgerState Ledger;
        public CurrentStockState Stock;
        public StorageState Storage;
        public DispatchQueueState Dispatch;
        public WithdrawalQueueState Withdrawals;
        public LostItemsState LostItems;
        public List<LedgerRecord> Transactions = new List<LedgerRecord>();
        public List<ActiveHistoryRecord> ItemHistory = new List<ActiveHistoryRecord>();
        // Coordination participates in atomic admission, but never goes to SQL.
        public SymbiantIndexState ItemIndex;
        public List<RecoveryReservation> Reservations = new List<RecoveryReservation>();
        public Dictionary<string, BagAuditResult> AppliedCensuses = new Dictionary<string, BagAuditResult>(StringComparer.Ordinal);
        public Dictionary<string, DispatchCommand> Commands = new Dictionary<string, DispatchCommand>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, StorageBatchResult> Results = new Dictionary<string, StorageBatchResult>(StringComparer.OrdinalIgnoreCase);
        public List<ReceiptEvidence> Receipts = new List<ReceiptEvidence>();
        public List<ExtractionProof> Extractions = new List<ExtractionProof>();
        public List<ReturnOffer> Returns = new List<ReturnOffer>();
        public PhatzPolicyState PhatzPolicy = new PhatzPolicyState();
        public List<ItemTemplatePair> ItemPairs = new List<ItemTemplatePair>();
        public BagReserve Reserve = new BagReserve();
        public List<ReserveOperation> ReserveOperations = new List<ReserveOperation>();
        public List<BagHistoryRecord> BagHistory = new List<BagHistoryRecord>();
        public List<CancellationPair> Cancellations = new List<CancellationPair>();
        public BankTerminalState BankTerminal = new BankTerminalState();
        public CloakState Cloak;
        public List<CloakEvent> CloakEvents = new List<CloakEvent>();
        internal long LedgerVersion = 1, PolicyVersion = 1;
        internal AccountingState Fork() => (AccountingState)MemberwiseClone();
        internal bool BusinessChanged(AccountingState other) =>
            !ReferenceEquals(Ledger, other.Ledger) || !ReferenceEquals(Stock, other.Stock) ||
            !ReferenceEquals(Storage, other.Storage) || !ReferenceEquals(Dispatch, other.Dispatch) ||
            !ReferenceEquals(Withdrawals, other.Withdrawals) || !ReferenceEquals(LostItems, other.LostItems) ||
            !ReferenceEquals(Transactions, other.Transactions) || !ReferenceEquals(ItemHistory, other.ItemHistory) ||
            !ReferenceEquals(Receipts, other.Receipts) || !ReferenceEquals(Extractions, other.Extractions) ||
            !ReferenceEquals(Returns, other.Returns) || !ReferenceEquals(Cloak, other.Cloak) ||
            !ReferenceEquals(CloakEvents, other.CloakEvents) || !ReferenceEquals(PhatzPolicy, other.PhatzPolicy) ||
            !ReferenceEquals(ItemPairs, other.ItemPairs) || !ReferenceEquals(Reserve, other.Reserve) ||
            !ReferenceEquals(ReserveOperations, other.ReserveOperations) || !ReferenceEquals(BagHistory, other.BagHistory) || !ReferenceEquals(BankTerminal, other.BankTerminal);
    }

    public interface IAccountingPersistence
    {
        void Commit(AccountingState previous, AccountingState next);
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _accountingSync = new object();
        private AccountingState _accounting;
        private AccountingState _pendingAccounting;
        private IAccountingPersistence _accountingPersistence;
        private string _accountingTransaction;
        private string _accountingClient;
        public long GetLedgerRevision(string transaction) { lock (_accountingSync) return Accounting(transaction).LedgerVersion; }
        public long GetPolicyRevision(string transaction) { lock (_accountingSync) return Accounting(transaction).PolicyVersion; }

        public static void InitializeAccounting(AccountingState initial, IAccountingPersistence persistence)
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only ManagerHost can initialize accounting persistence.");
            if (initial == null || persistence == null) throw new ArgumentNullException();
            lock (Current._accountingSync)
            {
                if (Current._accounting != null) throw new InvalidOperationException("Accounting is already initialized.");
                Current._accounting = initial;
                Current._accountingPersistence = persistence;
            }
        }

        private AccountingState Accounting(string transaction)
        {
            if (_accounting == null) throw new InvalidOperationException("Manager accounting is not initialized.");
            if (transaction == null) return _accounting;
            if (transaction != _accountingTransaction) throw new InvalidOperationException("Accounting transaction is no longer active.");
            return _pendingAccounting;
        }
        public void BeginAccounting(string transaction, string client)
        {
            if (string.IsNullOrEmpty(transaction)) throw new ArgumentException("Transaction identity required.");
            lock (_accountingSync)
            {
                while (_accountingTransaction != null) Monitor.Wait(_accountingSync);
                _pendingAccounting = Accounting(null).Fork();
                _accountingTransaction = transaction;
                _accountingClient = client;
            }
        }
        public void FinishAccounting(string transaction, bool commit)
        {
            lock (_accountingSync)
            {
                Accounting(transaction);
                try
                {
                    if (commit)
                    {
                        if (_accounting.BusinessChanged(_pendingAccounting))
                            _accountingPersistence.Commit(_accounting, _pendingAccounting);
                        _accounting = _pendingAccounting;
                    }
                }
                finally
                {
                    _pendingAccounting = null;
                    _accountingTransaction = _accountingClient = null;
                    Monitor.PulseAll(_accountingSync);
                }
            }
        }
        public void AbandonAccounting(string client)
        {
            lock (_accountingSync)
                if (_accountingTransaction != null && _accountingClient == client)
                    FinishAccounting(_accountingTransaction, false);
        }
        public BagAuditResult ReadAppliedCensus(string transaction, string run)
        {
            lock (_accountingSync)
            {
                BagAuditResult result;
                return Accounting(transaction).AppliedCensuses.TryGetValue(run, out result) ? result.Copy() : null;
            }
        }
        public void CompleteCensus(string transaction, BagAuditResult result)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.AppliedCensuses = new Dictionary<string, BagAuditResult>(state.AppliedCensuses, StringComparer.Ordinal);
                state.AppliedCensuses[result.RunId] = result.Copy();
            }
        }
        public ActiveLedgerState ReadLedger(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Ledger?.Copy(); }
        public CurrentStockState ReadStock(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Stock?.Copy(); }
        public StorageState ReadStorage(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Storage?.Copy(); }
        public DispatchQueueState ReadDispatch(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Dispatch?.Copy(); }
        public WithdrawalQueueState ReadWithdrawals(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Withdrawals?.Copy(); }
        public LostItemsState ReadLostItems(string transaction)
        { lock (_accountingSync) return Accounting(transaction).LostItems?.Copy(); }
        public void ChangeLedger(string transaction, ActiveLedgerState ledger)
        { lock (_accountingSync) { var state = Writing(transaction); state.Ledger = ledger?.Copy(); state.LedgerVersion++; } }
        public void ChangeStock(string transaction, CurrentStockState stock)
        { lock (_accountingSync) Writing(transaction).Stock = stock?.Copy(); }
        public void ChangeStorage(string transaction, StorageState storage)
        { lock (_accountingSync) Writing(transaction).Storage = storage?.Copy(); }
        public void ChangeDispatch(string transaction, DispatchQueueState dispatch)
        { lock (_accountingSync) Writing(transaction).Dispatch = dispatch?.Copy(); }
        public void ChangeWithdrawals(string transaction, WithdrawalQueueState withdrawals)
        { lock (_accountingSync) Writing(transaction).Withdrawals = withdrawals?.Copy(); }
        public void ChangeLostItems(string transaction, LostItemsState lost)
        { lock (_accountingSync) Writing(transaction).LostItems = lost?.Copy(); }
        public LedgerRecord[] ReadTransactions(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Transactions.Select(row => row.Copy()).ToArray(); }
        public ActiveHistoryRecord[] ReadItemHistory(string transaction)
        { lock (_accountingSync) return Accounting(transaction).ItemHistory.Select(row => row.Copy()).ToArray(); }
        public void RecordTransaction(string transaction, LedgerRecord record)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                var entry = record.Copy();
                if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                state.Transactions = new List<LedgerRecord>(state.Transactions) { entry };
            }
        }
        public void RecordItemHistory(string transaction, ActiveHistoryRecord record)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                var entry = record.Copy();
                if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                state.ItemHistory = new List<ActiveHistoryRecord>(state.ItemHistory) { entry };
            }
        }
        public List<RecoveryReservation> ReadReservations(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Reservations.Select(row => row.Copy()).ToList(); }
        public void ChangeReservations(string transaction, List<RecoveryReservation> rows)
        { lock (_accountingSync) Writing(transaction).Reservations = rows.Select(row => row.Copy()).ToList(); }
        public DispatchCommand ReadDispatchCommand(string transaction, string character)
        {
            lock (_accountingSync)
            {
                DispatchCommand command;
                return Accounting(transaction).Commands.TryGetValue(character, out command) ? command.Copy() : null;
            }
        }
        public void ChangeDispatchCommand(string transaction, string character, DispatchCommand command)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.Commands = new Dictionary<string, DispatchCommand>(state.Commands, StringComparer.OrdinalIgnoreCase);
                if (command == null) state.Commands.Remove(character);
                else state.Commands[character] = command.Copy();
            }
        }
        public StorageBatchResult ReadStorageResult(string transaction, string character)
        {
            lock (_accountingSync)
            {
                StorageBatchResult result;
                return Accounting(transaction).Results.TryGetValue(character, out result) ? result.Copy() : null;
            }
        }
        public void ChangeStorageResult(string transaction, string character, StorageBatchResult result)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.Results = new Dictionary<string, StorageBatchResult>(state.Results, StringComparer.OrdinalIgnoreCase);
                if (result == null) state.Results.Remove(character);
                else state.Results[character] = result.Copy();
            }
        }
        public ReceiptEvidence ReadReceipt(string transaction, string id)
        { lock (_accountingSync) return Accounting(transaction).Receipts.SingleOrDefault(row => row.Id == id)?.Copy(); }
        public void ChangeReceipt(string transaction, ReceiptEvidence value)
        {
            if (value == null || string.IsNullOrEmpty(value.Id)) throw new ArgumentException("Operation identity is required.");
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.Receipts = state.Receipts.Where(row => row.Id != value.Id).ToList();
                state.Receipts.Add(value.Copy());
            }
        }
        public ExtractionProof ReadExtraction(string transaction, string id)
        { lock (_accountingSync) return Accounting(transaction).Extractions.SingleOrDefault(row => row.Id == id)?.Copy(); }
        public void ChangeExtraction(string transaction, ExtractionProof value)
        {
            if (value == null || string.IsNullOrEmpty(value.Id)) throw new ArgumentException("Operation identity is required.");
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.Extractions = state.Extractions.Where(row => row.Id != value.Id).ToList();
                state.Extractions.Add(value.Copy());
            }
        }
        public ReturnOffer ReadReturn(string transaction, string id)
        { lock (_accountingSync) return Accounting(transaction).Returns.SingleOrDefault(row => row.Id == id)?.Copy(); }
        public void ChangeReturn(string transaction, ReturnOffer value)
        {
            if (value == null || string.IsNullOrEmpty(value.Id)) throw new ArgumentException("Operation identity is required.");
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.Returns = state.Returns.Where(row => row.Id != value.Id).ToList();
                state.Returns.Add(value.Copy());
            }
        }
        public SymbiantIndexState ReadItemIndex(string transaction)
        { lock (_accountingSync) return Accounting(transaction).ItemIndex?.Copy(); }
        public void ChangeItemIndex(string transaction, SymbiantIndexState index)
        { lock (_accountingSync) Writing(transaction).ItemIndex = index?.Copy(); }
        public CloakState ReadCloak(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Cloak?.Copy(); }
        public void ChangeCloak(string transaction, CloakState value)
        { lock (_accountingSync) Writing(transaction).Cloak = value?.Copy(); }
        public CloakEvent[] ReadCloakEvents(string transaction)
        { lock (_accountingSync) return Accounting(transaction).CloakEvents.Select(row => row.Copy()).ToArray(); }
        public void RecordCloakEvent(string transaction, CloakEvent value)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                var entry = value.Copy();
                if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                state.CloakEvents = new List<CloakEvent>(state.CloakEvents) { entry };
            }
        }
        public PhatzPolicyState ReadPhatzPolicy(string transaction)
        { lock (_accountingSync) return Accounting(transaction).PhatzPolicy.Copy(); }
        public void ChangePhatzPolicy(string transaction, PhatzPolicyState value)
        { lock (_accountingSync) { var state = Writing(transaction); state.PhatzPolicy = value.Copy(); state.PolicyVersion++; } }
        public ItemTemplatePair[] ReadItemPairs(string transaction)
        { lock (_accountingSync) return Accounting(transaction).ItemPairs.Select(row => row.Copy()).ToArray(); }
        public ItemTemplatePair[] MergeItemPairs(string transaction, ItemTemplatePair[] observations)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                var keys = new HashSet<string>(state.ItemPairs.Select(pair => pair.LowId + ":" + pair.HighId));
                var added = observations.Where(pair => pair != null && pair.LowId > 0 && pair.HighId > 0 && pair.LowId != pair.HighId)
                    .Where(pair => keys.Add(pair.LowId + ":" + pair.HighId)).Select(pair => pair.Copy()).ToList();
                if (added.Count > 0) { state.ItemPairs = state.ItemPairs.Concat(added).ToList(); state.PolicyVersion++; }
                return state.ItemPairs.Select(pair => pair.Copy()).ToArray();
            }
        }
        public BagReserve ReadReserve(string transaction)
        { lock (_accountingSync) return Accounting(transaction).Reserve.Copy(); }
        public void ChangeReserve(string transaction, BagReserve value)
        { lock (_accountingSync) Writing(transaction).Reserve = value.Copy(); }
        public void ChangeReserveOperation(string transaction, ReserveOperation value)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.ReserveOperations = state.ReserveOperations.Where(row => row.Character != value.Character).ToList();
                if (value.Phase != "completed") state.ReserveOperations.Add(value.Copy());
            }
        }
        public BagHistoryRecord[] ReadBagHistory(string transaction, string character)
        { lock (_accountingSync) return Accounting(transaction).BagHistory.Where(row => string.Equals(row.Character, character, StringComparison.OrdinalIgnoreCase)).Select(row => row.Copy()).ToArray(); }
        public void ReconcileBagHistory(string transaction, string character)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                var updates = state.BagHistory.Where(row => string.Equals(row.Character, character, StringComparison.OrdinalIgnoreCase) && row.Phase == "complete" && row.ReconciledUtc == DateTime.MinValue).ToList();
                if (updates.Count == 0) return;
                state.BagHistory = state.BagHistory.Except(updates).Concat(updates.Select(row =>
                { var copy = row.Copy(); copy.ReconciledUtc = DateTime.UtcNow; return copy; })).ToList();
            }
        }
        public ReceiptEvidence ReadCancellationReceipt(string transaction, string attempt, string kind)
        { lock (_accountingSync) return Accounting(transaction).Receipts.SingleOrDefault(row => row.AttemptId == attempt && row.Kind == kind)?.Copy(); }
        public CancellationPair ReadCancellation(string transaction, string attempt)
        { lock (_accountingSync) return Accounting(transaction).Cancellations.SingleOrDefault(row => row.AttemptId == attempt)?.Copy(); }
        public void ChangeCancellation(string transaction, CancellationPair value)
        {
            lock (_accountingSync)
            {
                var state = Writing(transaction);
                state.Cancellations = state.Cancellations.Where(row => row.AttemptId != value.AttemptId).ToList();
                state.Cancellations.Add(value.Copy());
            }
        }
        public BankTerminalState ReadBankTerminal(string transaction)
        { lock (_accountingSync) return Accounting(transaction).BankTerminal.Copy(); }
        public void ChangeBankTerminal(string transaction, BankTerminalState value)
        { lock (_accountingSync) Writing(transaction).BankTerminal = value.Copy(); }
        private AccountingState Writing(string transaction)
        {
            if (transaction == null) throw new InvalidOperationException("Accounting changes require a transaction.");
            return Accounting(transaction);
        }
    }

    public static class ManagerAccounting
    {
        [ThreadStatic] private static string _transaction;
        [ThreadStatic] private static bool _failed;
        public static string TransactionId => _transaction;
        public static T Transaction<T>(string description, Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (_transaction != null)
            {
                try { return action(); }
                catch { _failed = true; throw; }
            }
            var owner = ManagerMemory.Current;
            string id = Guid.NewGuid().ToString("N");
            owner.BeginAccounting(id, AppDomain.CurrentDomain.FriendlyName);
            _transaction = id;
            _failed = false;
            bool ended = false;
            try
            {
                T result = action();
                if (_failed) throw new InvalidOperationException("A nested accounting operation failed.");
                ended = true; // FinishAccounting releases ownership even if SQL commit fails.
                owner.FinishAccounting(id, true);
                return result;
            }
            finally
            {
                try { if (!ended) owner.FinishAccounting(id, false); }
                finally { _transaction = null; _failed = false; }
            }
        }
        public static void Transaction(string description, Action action)
            => Transaction(description, () => { action(); return 0; });
    }
}
