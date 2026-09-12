using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// First operational CityBankers service slice.
    ///
    /// Central accepts only the explicitly trusted bootstrap administrator, Kavem,
    /// serializes completed donations into a persistent dispatch queue, and trades one
    /// routed batch at a time to the configured storage worker. Workers accept only a
    /// matching Central dispatch command and physically place received items into the
    /// persisted bank-first bag topology.
    /// </summary>
    public partial class BankingServiceAgent : ClientlessPluginEntry
    {
        private const string CustodyHoldStatus = "custody-hold";

        private string _settingsDir;
        private ServiceConfig _config;
        private string _role;
        private string _centralCharacter;
        private bool _isCentral;
        private bool _enabled;
        private DateTime _nextSlowTickUtc;
        private string _lastImportedBaselineRunId;

        // Central: Kavem -> Central donation state.
        private bool _donationActive;
        private Identity _donationPartner = Identity.None;
        private string _donationPartnerName;
        private string _donationDispositionPartnerName;
        private string _donationTransactionId;
        private DateTime _donationOpenedUtc;
        private DateTime _donationLastChangeUtc;
        private List<TransferItemState> _donationPreviousOffer = new List<TransferItemState>();
        private List<TransferItemState> _donationSnapshot = new List<TransferItemState>();
        private bool _donationAccepted;
        private DonationCleanupState _donationCleanup;

        // Central: Central -> worker serialized dispatch state.
        private DispatchBatchState _activeBatch;
        private Identity _activeWorkerIdentity = Identity.None;
        private readonly Stopwatch _dispatchTradeAge = Stopwatch.StartNew();
        private bool _outgoingOpened;
        private int _outgoingAwaitingOfferCount;
        private readonly HashSet<int> _outgoingRequestedSlots = new HashSet<int>();
        private Identity _outgoingPendingSlot = Identity.None;
        private bool _outgoingPendingSlotSet;
        private readonly Stopwatch _outgoingAddAge = Stopwatch.StartNew();
        private int _outgoingPendingAddAttempts;
        private bool _outgoingAccepted;

        // Worker: expected Central -> worker receive state.
        private DispatchCommand _workerCommand;
        private readonly Stopwatch _workerTradeAge = Stopwatch.StartNew();
        private bool _workerAccepted;
        private StorageJob _storageJob;

        private enum StoragePhase
        {
            None,
            FindBag,
            MovingBagToInventory,
            OpeningBag,
            MovingItemIntoBag,
            ReturningBag
        }

        public override void Init(string pluginDir)
        {
            // Keep dependency-heavy startup outside this method: the loader suppresses
            // Init exceptions, including failures while the CLR prepares its body.
            try
            {
                if (ServicePolicy.IsBagAuditMode()) return;
                Logger.Information("BANKING SERVICE startup registered character=" + Client.CharacterName);
                if (StartupCensusGate.Defer(StartOperational)) return;
                StartOperational();
            }
            catch (Exception ex) { ReportStartupFailure(ex); }
        }

        private void StartOperational()
        {
            try
            {
                if (_enabled) return;
                Logger.Information("BANKING SERVICE startup entering character=" + Client.CharacterName);
                InitializeOperational();
            }
            catch (Exception ex) { ReportStartupFailure(ex); }
        }

        private void ReportStartupFailure(Exception error)
        {
            Logger.Error("BANKING SERVICE initialization failed character=" + Client.CharacterName + ": " + error);
            try { Teardown(); }
            catch (Exception cleanup) { Logger.Error("BANKING SERVICE startup cleanup failed: " + cleanup); }
            // Do not hide a deterministic startup error behind repeated physical audits.
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InitializeOperational()
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _config = LoadConfig();
            if (_config == null || _config.Roles == null)
            {
                Logger.Warning("BANKING SERVICE disabled: the Bankers section could not be read.");
                return;
            }

            RoleConfig central;
            if (!TryGetRole("central", out central) ||
                central == null ||
                string.IsNullOrWhiteSpace(central.Character))
            {
                Logger.Warning("BANKING SERVICE disabled: Central mapping is unavailable.");
                return;
            }

            _centralCharacter = central.Character;
            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role))
            {
                Logger.Warning(
                    $"BANKING SERVICE disabled: {Client.CharacterName} is not mapped to a banker role.");
                return;
            }

            _isCentral = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);
            _enabled = true;
            _nextSlowTickUtc = DateTime.UtcNow;

            Trade.TradeOpened += OnTradeOpened;
            Trade.TradeStatusChanged += OnTradeStatusChanged;
            Client.OnUpdate += Tick;
            StartBankerIpc();

            if (_isCentral && Client.Chat != null)
                Client.Chat.PrivateMessageReceived += OnPrivateMessage;

            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "BANKING SERVICE initialized. bootstrapAdmin=" +
                TrustedOperators.BootstrapAdmin + "; role=" + _role + ".");

            Logger.Information(
                $"BANKING SERVICE initialized character={Client.CharacterName} role={_role} " +
                $"central={_isCentral} trustedAdmin={TrustedOperators.BootstrapAdmin}.");
        }

        private BankingServiceAgent _successor;
        private BankingServiceAgent _lifecycleRoot;
        private Action _resumeAfterCensus;

        internal static bool QuiesceForCensus(string directory)
        {
            var actor = _ipcOwner;
            if (actor == null) return true; // Startup actors have not been initialized yet.
            if (Trade.IsTrading) { TryDeclineTrade(); return false; }
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, Client.CharacterName + ".retired-operations.json"), new
            {
                Receipt = actor._receipt, Batch = actor._activeBatch, Command = actor._workerCommand,
                Reserved = actor._reservedDispatch, Storage = actor._storageJob,
                Return = actor._returnOffer, Withdrawal = actor._withdrawal, Pickups = actor._pickupItems,
                Extraction = actor._extraction, LocalCensus = actor._localCensus,
                DispatchCensus = actor._dispatchCensus, WithdrawalCensus = actor._withdrawalCensus,
                Donation = actor._donationSnapshot, Cleanup = actor._donationCleanup,
                DeliveryInferred = false
            });
            var root = actor._lifecycleRoot ?? actor;
            actor.Teardown();
            actor._enabled = false;
            DispatchProposal proposal;
            while (actor._dispatchProposals.TryDequeue(out proposal)) proposal.Reply.TrySetResult("pending");
            root._resumeAfterCensus = () =>
            {
                root._resumeAfterCensus = null;
                root._successor = new BankingServiceAgent { _lifecycleRoot = root };
                try { root._successor.Init(null); }
                catch (Exception ex) { StartupCensusGate.Block("Operational reinitialization failed: " + ex.Message); }
            };
            if (!StartupCensusGate.Defer(root._resumeAfterCensus)) root._resumeAfterCensus();
            return true;
        }

        public override void Teardown()
        {
            StartupCensusGate.CancelDeferred(StartOperational);
            if (_resumeAfterCensus != null) StartupCensusGate.CancelDeferred(_resumeAfterCensus);
            _resumeAfterCensus = null;
            _successor?.Teardown();
            _successor = null;
            if (!_enabled)
                return;

            Trade.TradeOpened -= OnTradeOpened;
            Trade.TradeStatusChanged -= OnTradeStatusChanged;
            Client.OnUpdate -= Tick;
            _ipcLifetime?.Cancel();
            if (_ipcOwner == this)
            {
                _ipcOwner = null;
                StartupCensusGate.ClearOperational();
            }
            if (_isCentral && Client.Chat != null)
                Client.Chat.PrivateMessageReceived -= OnPrivateMessage;

            _enabled = false;
            try { RuntimeStateStore.AppendActivity(_settingsDir, Client.CharacterName, _role, "BANKING SERVICE teardown."); }
            catch (Exception ex) { Logger.Warning("[CityBankers] Teardown activity unavailable: " + ex.Message); }
        }

        private readonly Stopwatch _operationalHeartbeatAge = Stopwatch.StartNew();

        private string _blockedRecovery;
        private readonly Stopwatch _blockedRecoveryAge = Stopwatch.StartNew();

        private bool TickRecoveryOwnership()
        {
            if (_localCensus != null) return false; // Full bag scans may legitimately take minutes.
            string pending = _dispatchDispute?.AttemptId ?? (_withdrawalDispute ? "withdrawal" : null) ?? _withdrawalCensus?.Id ??
                _storageRecovery?.RunId ?? (_returnLocalVerified ? _returnOffer?.Id : null) ??
                (_extractionPhase == ExtractionPhase.Commit ? _extraction?.Id : null);
            if (pending != _blockedRecovery)
            {
                _blockedRecovery = pending;
                _blockedRecoveryAge.Restart();
            }
            if (pending == null || _blockedRecoveryAge.ElapsedMilliseconds < 60000) return false;
            StartupCensusGate.Block("Recovery ownership/peer accounting did not converge: " + pending +
                "; retire retained operations through a fresh coordinated census.");
            return true;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (StartupCensusGate.RosterRecoveryActive || !StartupCensusGate.IsCurrentParticipant) return;
            if (TickRecoveryOwnership()) return;
            if (TickWithdrawalCensus()) return;
            if (TickDispatchCensus()) return;
            if (TickLocalCensus()) return;
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || !Client.InPlay)
                return;

            try
            {
                if (_ipcServer == null || _ipcServer.IsCompleted)
                    throw new InvalidOperationException("Banker IPC server is not running.", _ipcServer?.Exception);
                TickBankerIpc();
                if (_operationalHeartbeatAge.ElapsedMilliseconds >= 500)
                {
                    StartupCensusGate.PublishOperational();
                    _operationalHeartbeatAge.Restart();
                }
                if (_localCensus != null || !StartupCensusGate.IsOpen) return;
                TickCancellationOutbox();
                if (TickStorageRecovery()) return;
                TickInternalConfirmation();
                if (TickPhysicalReceipt()) return;
                if (TickRecoveryExtraction()) return;
                if (TickReturnTransfer()) return;
                if (_isCentral)
                {
                    foreach (var pending in RuntimeStateStore.LoadDispatchQueue(_settingsDir).Batches
                        .Where(batch => batch.Status == "transferred").ToList())
                        PollStorageResult(pending);
                    // Startup census owns the baseline. Never replace it later
                    // with an older manual storage-baseline.json snapshot.
                    if (TickWithdrawalCentral())
                        return;
                    TickDonation();
                    TickDonationCleanup();
                    DetectLocalInventoryDifference();
                    if (_localCensus != null) return;
                    if (TryRecoverFailedDispatch()) return;
                    TickDispatch();
                    if (_localCensus != null) return;
                    StartRecoveryExtraction();
                }
                else
                {
                    if (_storageJob != null) { TickStorageJob(); return; }
                    if (_reservedDispatch == null && _workerCommand == null && TickWithdrawalWorker())
                        return;
                    TickWorkerTrade();
                    DetectLocalInventoryDifference();
                    if (_localCensus != null) return;
                    TickLocalStorageRecovery();
                    TickLooseReturnRecovery();
                    StartRecoveryExtraction();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"BANKING SERVICE tick failed character={Client.CharacterName}: {ex}");
                StartupCensusGate.Block("Banking operation failed; evidence retained: " + ex);
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    _role,
                    "ERROR tick: " + ex);
            }
        }

        // ---------------------------------------------------------------------
        // Trusted tell interface (Central only)
        // ---------------------------------------------------------------------

        private void OnPrivateMessage(object sender, PrivateMessage message)
        {
            if (!StartupCensusGate.IsOpen) return;
            if (!_isCentral || message == null)
                return;

            try
            {
                if (!TrustedOperators.IsTrustedAdmin(message.SenderName))
                {
                    RuntimeStateStore.AppendActivity(
                        _settingsDir,
                        Client.CharacterName,
                        _role,
                        "Ignored untrusted tell from " +
                        (message.SenderName ?? "<unknown>") + ".");
                    return;
                }

                string text = (message.Message ?? string.Empty).Trim();
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    _role,
                    "TELL <- Kavem: " + text);

                string command = text.ToLowerInvariant();
                if (command == "stock" || command.StartsWith("stock ") ||
                    command == "don" || command == "donor")
                {
                    TellPlayer(
                        message.SenderName,
                        "Public Banker information now lives on " +
                        SettingsPaths.ReadManagerCharacter(_settingsDir) +
                        ". Use #stock or #donor there.");
                    return;
                }

                if (command == "status")
                {
                    TellKavem(BuildStatusMessage());
                    return;
                }

                if (command == "queue")
                {
                    TellKavem(BuildQueueMessage());
                    return;
                }

                if (command == "bags")
                {
                    TellKavem(BuildBagMessage());
                    return;
                }

                TellKavem(
                    "CityBankers operator commands: status | queue | bags. " +
                    "Public #stock and #donor commands live on Apcmanager. " +
                    "You are the bootstrap admin; your trades with Central are accepted under the current max-10 policy.");
            }
            catch (Exception ex)
            {
                Logger.Error($"BANKING SERVICE tell handling failed: {ex}");
            }
        }

        private string BuildStatusMessage()
        {
            StorageState storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            CurrentStockState stock = RuntimeStateStore.LoadCurrentStock(_settingsDir);
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            return
                "CityBankers status: baseline=" +
                (storage != null && !string.IsNullOrWhiteSpace(storage.BaselineRunId)
                    ? storage.BaselineRunId
                    : "MISSING") +
                "; stock=" + (stock?.Items?.Count ?? 0) +
                "; queue=" + (queue?.Batches?.Count ?? 0) +
                "; trade=" + (Trade.IsTrading ? "open" : "idle") + ".";
        }

        private string BuildStockMessage()
        {
            CurrentStockState stock = RuntimeStateStore.LoadCurrentStock(_settingsDir);
            List<StockItemState> items = stock?.Items ?? new List<StockItemState>();
            string byRole = string.Join(
                ", ",
                new[] { "artillery", "infantry", "control", "support", "extermination", "spirit", "dyna", "phatz" }
                    .Select(role => role + "=" + items.Count(i => string.Equals(
                        i.Role, role, StringComparison.OrdinalIgnoreCase))));
            int misplaced = items.Count(i =>
                !string.IsNullOrWhiteSpace(i.PhysicalRole) &&
                !string.Equals(i.Role, i.PhysicalRole, StringComparison.OrdinalIgnoreCase));
            return "CityBankers stock: total=" + items.Count + "; " + byRole +
                "; misplaced=" + misplaced + ".";
        }

        private string BuildQueueMessage()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            List<DispatchBatchState> batches = queue?.Batches ?? new List<DispatchBatchState>();
            if (batches.Count == 0)
                return "CityBankers queue: empty.";

            return "CityBankers queue: " + string.Join(
                ", ",
                batches.Take(5).Select(b =>
                    b.Role + "/" + (b.Items?.Count ?? 0) + "/" + b.Status)) +
                (batches.Count > 5 ? " ..." : string.Empty) + ".";
        }

        private string BuildBagMessage()
        {
            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (state == null || state.Workers == null)
                return "CityBankers bags: no authoritative operational baseline yet; run CityDwellers.exe bankers-bagaudit.";

            return "CityBankers bags: " + string.Join(
                ", ",
                state.Workers.OrderBy(w => w.Role).Select(w =>
                {
                    int bags = w.Bags?.Count ?? 0;
                    int used = w.Bags?.Sum(b => b.Items?.Count ?? 0) ?? 0;
                    int free = w.Bags?.Sum(b => Math.Max(0, b.Capacity - (b.Items?.Count ?? 0))) ?? 0;
                    return w.Role + "=" + bags + " bags/" + used + " used/" + free + " free";
                })) + ".";
        }

        // ---------------------------------------------------------------------
        // AO trade callbacks
        // ---------------------------------------------------------------------

        private void DeclineIncomingTrade(string targetName, string reason)
        {
            ReportTransferProgress("TRADE DECLINED", null, "partner=" + targetName + "; " + reason);
            try { TellDirectPlayer(targetName, reason); }
            catch (Exception ex) { Logger.Warning("Trade decline notice unavailable: " + ex.Message); }
            Trade.Decline();
        }

        private void OnTradeOpened(Identity target)
        {
            if (!StartupCensusGate.IsOpen) return;
            if (!_enabled || !Client.InPlay)
                return;

            try
            {
                string targetName = FindPlayerName(target);
                if (_withdrawalCensus != null || _withdrawalDispute ||
                    WithdrawalStore.GetWithdrawalCensusId(_settingsDir, Client.CharacterName) != null)
                { DeclineIncomingTrade(targetName, "Central is reconciling withdrawal custody. Please retry after recovery completes."); return; }
                if (_extraction != null || _storageRecovery != null)
                { DeclineIncomingTrade(targetName, "Central is moving a reserved item or recovering storage. Please retry shortly."); return; }
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    _role,
                    "TRADE OPEN target=" + (targetName ?? target.ToString()) + ".");

                if (TryReturnTradeOpened(target, targetName)) return;
                if (TryHandleWithdrawalTradeOpened(target, targetName))
                    return;
                if (WithdrawalStore.LoadAll(_settingsDir).Any(WithdrawalStore.OwnsCentralTrade))
                {
                    TellDirectPlayer(targetName, "Central is transferring an item. Please try your trade again shortly.");
                    Trade.Decline();
                    return;
                }

                if (_isCentral)
                {
                    if (_activeBatch != null && target == _activeWorkerIdentity)
                    {
                        _outgoingOpened = true;
                        _dispatchTradeAge.Restart();
                        return;
                    }

                    if (HasUnresolvedDispatchWork())
                    {
                        TellDirectPlayer(
                            targetName,
                            "CityBankers is finishing pending storage work. Please try your trade again when the queue is clear.");
                        RuntimeStateStore.AppendActivity(
                            _settingsDir,
                            Client.CharacterName,
                            _role,
                            "QUEUE PRIORITY declined unrelated trade from " +
                            (targetName ?? target.ToString()) + ".");
                        Trade.Decline();
                        return;
                    }

                    if (TrustedOperators.IsTrustedAdmin(targetName))
                    {
                        BeginDonation(target);
                        return;
                    }

                    Logger.Warning(
                        $"BANKING SERVICE declining untrusted trade on Central from " +
                        $"{targetName ?? target.ToString()}.");
                    Trade.Decline();
                    return;
                }

                if (string.Equals(
                    targetName,
                    _centralCharacter,
                    StringComparison.OrdinalIgnoreCase))
                {
                    BeginWorkerTrade(target);
                    return;
                }

                Logger.Warning(
                    $"BANKING SERVICE worker {Client.CharacterName} declining non-Central trade " +
                    $"from {targetName ?? target.ToString()}.");
                Trade.Decline();
            }
            catch (Exception ex)
            {
                Logger.Error($"BANKING SERVICE TradeOpened handling failed: {ex}");
                StartupCensusGate.Block("Trade preparation failed: " + ex);
                TryDeclineTrade();
            }
        }

        private void OnTradeStatusChanged(Identity target, TradeStatus status)
        {
            if (!StartupCensusGate.IsOpen) return;
            if (!_enabled)
                return;

            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "TRADE STATUS target=" + target + " status=" + status + ".");

            try
            {
                if (_withdrawalCensus != null || _withdrawalDispute ||
                    WithdrawalStore.GetWithdrawalCensusId(_settingsDir, Client.CharacterName) != null) return;
                if (TryReturnTradeStatus(status)) return;
                if (TryHandleWithdrawalTradeStatus(target, status))
                    return;
                if (status == TradeStatus.Accept && _isCentral && _donationActive)
                {
                    if (!Trade.IsTrading || Trade.CurrentTarget != _donationPartner)
                        return;

                    List<TransferItemState> offered = SnapshotTradeItems(Trade.TargetWindowCache.Items);
                    if (!MatchesExpected(offered, _donationPreviousOffer))
                    {
                        AnnounceDonationChanges(offered);

                        _donationPreviousOffer = new List<TransferItemState>(offered);
                        _donationLastChangeUtc = DateTime.UtcNow;
                        _donationAccepted = false;
                    }

                    if (offered.Count > ServicePolicy.MaxTradeItems)
                    {
                        RejectDonation(
                            "More than " + ServicePolicy.MaxTradeItems +
                            " items were offered; current CityBankers policy is max ten per trade.",
                            offered);
                        return;
                    }

                    string validationError;
                    if (!ValidateDonationItems(offered, out validationError))
                    {
                        RejectDonation(validationError, offered);
                        return;
                    }

                    if (offered.Count == 0)
                    {
                        RejectDonation("Empty donation trade cannot be accepted.", offered);
                        return;
                    }

                    _donationSnapshot = new List<TransferItemState>(offered);
                    RecordDonationOffer();
                    _donationAccepted = true;
                    RuntimeStateStore.AppendActivity(
                        _settingsDir,
                        Client.CharacterName,
                        _role,
                        "PLAYER DONATION ACCEPT snapshot ready items=" + offered.Count +
                        "; bypassing 30-second edit timer and allowing immediate Confirm.");
                    return;
                }

                if (status == TradeStatus.Confirm)
                {
                    if (_isCentral && _donationActive)
                    {
                        RuntimeStateStore.AppendActivity(
                            _settingsDir,
                            Client.CharacterName,
                            _role,
                            "PLAYER DONATION CONFIRM observed; BankingService did not echo Trade.Confirm().");
                        return;
                    }

                    if (_isCentral && _activeBatch != null)
                    {
                        QueueInternalConfirmation(_activeWorkerIdentity);
                        return;
                    }

                    if (!_isCentral && _workerCommand != null)
                    {
                        QueueInternalConfirmation(Trade.CurrentTarget);
                        return;
                    }
                }

                if (status == TradeStatus.Finished)
                {
                    if (_isCentral && _donationActive)
                    {
                        AwaitPhysicalReceipt(_donationSnapshot, FinishDonation);
                        return;
                    }

                    if (_isCentral && _activeBatch != null)
                    {
                        if (string.Equals(_activeBatch.Status, "transferred", StringComparison.OrdinalIgnoreCase))
                            return; // duplicate Finished after verified sender completion
                        AwaitPhysicalReceipt(_activeBatch.Items, FinishOutgoingDispatchTrade);
                        return;
                    }

                    if (!_isCentral && _workerCommand != null)
                    {
                        AwaitPhysicalReceipt(_workerCommand.Items, FinishWorkerReceiveTrade);
                        return;
                    }
                }

                if (status == TradeStatus.Declined)
                {
                    if (_afterReceipt != null && _receipt != null && _receipt.Direction != 0)
                    {
                        StartupCensusGate.Block("Conflicting Declined after Finished; custody evidence retained.");
                        return;
                    }
                    VerifyCancelledReceipt();
                    if (_isCentral && _donationActive)
                    {
                        TellDonationPartner("Donation trade declined/cancelled; nothing was recorded as received.");
                        AppendTradeLedger("player_trade_declined", _donationTransactionId, null, null,
                            "Player donation did not complete.", _donationSnapshot);
                        ResetDonation();
                        return;
                    }

                    if (_isCentral && _activeBatch != null)
                    {
                        FailActiveBatch(
                            "Internal worker trade was declined. AO should have returned the offered items to Central inventory.");
                        return;
                    }

                    if (!_isCentral && _workerCommand != null)
                        FailWorkerCommand("Internal Central trade was declined before completion.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"BANKING SERVICE trade-status handling failed: {ex}");
                StartupCensusGate.Block("Trade handler failed; custody requires review: " + ex);
            }
        }

        // ---------------------------------------------------------------------
        // Player -> Central donation
        // ---------------------------------------------------------------------

        private void BeginDonation(Identity partner)
        {
            string partnerName = FindPlayerName(partner) ?? partner.ToString();
            if (Inventory.NumFreeSlots < ServicePolicy.MaxTradeItems)
            {
                TellDirectPlayer(partnerName, "Central needs room for a full donation. Please try again after pending storage or pickups finish.");
                Trade.Decline();
                return;
            }
            if (HasUnresolvedDispatchWork())
            {
                TellDirectPlayer(
                    partnerName,
                    "CityBankers is finishing pending storage work; your new donation trade was declined. Please try again when the queue is clear.");
                Trade.Decline();
                return;
            }

            StorageState storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (storage == null)
            {
                TellDirectPlayer(
                    partnerName,
                    "CityBankers has no operational bag baseline yet. This trade is being declined safely.");
                Trade.Decline();
                return;
            }

            _donationActive = true;
            _donationPartner = partner;
            _donationPartnerName = partnerName;
            _donationTransactionId = "don-" + Guid.NewGuid().ToString("N");
            PrepareReceipt("donation", _donationTransactionId, null, null, 1);
            _donationOpenedUtc = DateTime.UtcNow;
            _donationLastChangeUtc = _donationOpenedUtc;
            _donationPreviousOffer = new List<TransferItemState>();
            _donationSnapshot = new List<TransferItemState>();
            _donationAccepted = false;

            TellDonationPartner(
                CityBankersChatPalette.DonationProgress(
                    0,
                    ServicePolicy.MaxTradeItems) +
                "Trade opened. Add up to " + ServicePolicy.MaxTradeItems +
                " accepted bank items. Take your time: Central waits for " +
                ServicePolicy.DonationInactivitySeconds +
                " seconds of unchanged trade contents before proceeding.");
            AppendTradeLedger(
                "player_trade_opened",
                _donationTransactionId,
                null,
                null,
                "Player donation trade opened.",
                null);
        }

        private void TickDonation()
        {
            if (!_donationActive || !Trade.IsTrading)
                return;

            DispatchQueueState existingQueue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            if (existingQueue != null && existingQueue.Batches != null && existingQueue.Batches.Count > 0)
            {
                RejectDonation(
                    "Central's storage queue became busy while this donation was open. Please retry after the queue clears.",
                    SnapshotTradeItems(Trade.TargetWindowCache.Items));
                return;
            }

            List<TransferItemState> offered = SnapshotTradeItems(Trade.TargetWindowCache.Items);
            if (!MatchesExpected(offered, _donationPreviousOffer))
            {
                AnnounceDonationChanges(offered);
                _donationPreviousOffer = new List<TransferItemState>(offered);
                _donationLastChangeUtc = DateTime.UtcNow;
                _donationAccepted = false;
            }

            if (offered.Count > ServicePolicy.MaxTradeItems)
            {
                RejectDonation(
                    "More than " + ServicePolicy.MaxTradeItems +
                    " items were offered; current CityBankers policy is max ten per trade.",
                    offered);
                return;
            }

            string validationError;
            if (!ValidateDonationItems(offered, out validationError))
            {
                RejectDonation(validationError, offered);
                return;
            }

            if (offered.Count == 0)
            {
                if ((DateTime.UtcNow - _donationOpenedUtc).TotalSeconds >=
                    ServicePolicy.EmptyTradeTimeoutSeconds)
                    RejectDonation("Empty donation trade timed out.", offered);
                return;
            }

            if (_donationAccepted)
                return;
            if ((DateTime.UtcNow - _donationLastChangeUtc).TotalSeconds <
                ServicePolicy.DonationInactivitySeconds)
                return;

            _donationSnapshot = offered;
            RecordDonationOffer();
            _donationAccepted = true;
            TellDonationPartner(
                "Donation stable: accepting " + offered.Count +
                " item(s). Complete/confirm the AO trade normally.");
            Trade.Accept();
        }

        private bool ValidateDonationItems(List<TransferItemState> offered, out string error)
        {
            error = null;
            foreach (TransferItemState item in offered)
            {
                string destination;
                if (!SymbiantCatalog.TryGetDestinationRole(_settingsDir, item.AoId, out destination))
                {
                    error =
                        "Unmanaged item in donation: " + item.Name + " AOID=" + item.AoId +
                        ". The entire trade is being declined safely.";
                    return false;
                }

                if (string.Equals(destination, "central", StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        "This accepted item is routed to Central rather than a storage worker: " +
                        item.Name + ". The entire trade is being declined safely.";
                    return false;
                }
                RoleConfig storageRole;
                if (!TryGetRole(destination, out storageRole) || storageRole == null ||
                    string.IsNullOrWhiteSpace(storageRole.Character))
                {
                    error = "Accepted item " + item.Name + " routes to unconfigured role '" +
                        destination + "'. The entire trade is being declined safely.";
                    return false;
                }
            }
            return true;
        }

        private void RejectDonation(string reason, List<TransferItemState> offered)
        {
            _donationSnapshot = offered ?? new List<TransferItemState>();
            TellDonationPartner("Donation declined: " + reason);
            AppendTradeLedger(
                "player_trade_rejected",
                _donationTransactionId,
                null,
                null,
                reason,
                _donationSnapshot);
            TryDeclineTrade();
            ResetDonation();
        }

        private void FinishDonation()
        {
            List<TransferItemState> received = _donationSnapshot != null
                ? new List<TransferItemState>(_donationSnapshot)
                : new List<TransferItemState>();
            string transactionId = _donationTransactionId;
            string donorName = _donationPartnerName;

            // This method is entered only after the physical inventory gain is verified.
            // Persist accounting before disposition; logs are diagnostic, not the commit path.
            ActiveLedgerStore.RecordDonation(_settingsDir, transactionId, donorName,
                Client.CharacterName, DateTime.UtcNow, received);

            AppendTradeLedger(
                "player_trade_completed",
                transactionId,
                null,
                null,
                "AO server completed player donation; received items are now in Central normal inventory.",
                received);

            CurrentStockState stock = RuntimeStateStore.LoadCurrentStock(_settingsDir);
            var projectedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            List<TransferItemState> storeItems = new List<TransferItemState>();
            List<TransferItemState> deleteItems = new List<TransferItemState>();
            foreach (TransferItemState item in received)
            {
                string key = TemplateKey(item);
                int count;
                if (!projectedCounts.TryGetValue(key, out count))
                    count = CountStoredCopies(stock, item);

                SymbiantCatalog.AcceptanceRule rule = GetAcceptanceRule(item.AoId);
                if (SymbiantCatalog.IsAtRetentionLimit(rule, count))
                    deleteItems.Add(item);
                else
                {
                    storeItems.Add(item);
                    count++;
                }
                projectedCounts[key] = count;
            }

            ResetDonation();
            _donationDispositionPartnerName = donorName;

            if (deleteItems.Count > 0)
            {
                _donationCleanup = new DonationCleanupState
                {
                    TransactionId = transactionId,
                    ReceivedCount = received.Count,
                    StoreItems = storeItems,
                    DeleteItems = deleteItems,
                    DeleteIndex = 0,
                    DeletedCount = 0
                };
                TellDonationPartner(
                    "Donation received. " + deleteItems.Count +
                    " item(s) exceed their configured per-AOID retention cap; Central will delete those excess copies and verify each deletion before dispatching the remaining " +
                    storeItems.Count + " item(s).");
                return;
            }

            QueueDonationForDispatch(transactionId, received.Count, storeItems, 0);
        }

        private void TickDonationCleanup()
        {
            if (!_isCentral || _donationCleanup == null)
                return;

            DonationCleanupState cleanup = _donationCleanup;
            if (cleanup.PendingDelete != null)
            {
                List<Item> remaining = FindInventoryItems(cleanup.PendingDelete);
                if (remaining.Count < cleanup.PendingBeforeCount)
                {
                    TransferItemState deleted = cleanup.PendingDelete;
                    ActiveLedgerStore.ArchiveActiveItem(_settingsDir, cleanup.TransactionId,
                        deleted.AoId, DateTime.UtcNow, "deleted_overcap", null, Client.CharacterName);
                    cleanup.DeletedCount++;
                    cleanup.DeleteIndex++;
                    cleanup.PendingDelete = null;
                    cleanup.PendingBeforeCount = 0;
                    cleanup.DeleteDeadlineUtc = DateTime.MinValue;
                    RuntimeStateStore.AppendLedger(
                        _settingsDir,
                        new LedgerRecord
                        {
                            Utc = DateTime.UtcNow,
                            Event = "donation_overcap_deleted",
                            TransactionId = cleanup.TransactionId,
                            Actor = Client.CharacterName,
                            Role = "central",
                            Character = Client.CharacterName,
                            Source = "normal-inventory",
                            Destination = "deleted",
                            Message = "AO inventory state confirmed one excess copy disappeared after Item.Delete().",
                            Items = new List<LedgerItem>
                            {
                                ToLedgerItem(deleted, "central", null, null, null)
                            }
                        });
                    RuntimeStateStore.AppendActivity(
                        _settingsDir,
                        Client.CharacterName,
                        _role,
                        "DELETE VERIFIED one copy of " + deleted.Name + " QL" + deleted.Ql +
                        " at exact-template retention cap.");
                    return;
                }

                if (DateTime.UtcNow >= cleanup.DeleteDeadlineUtc)
                    FailDonationCleanup(
                        "AO did not confirm deletion of " + cleanup.PendingDelete.Name +
                        " QL" + cleanup.PendingDelete.Ql + " before the verification timeout.");
                return;
            }

            if (cleanup.DeleteIndex >= cleanup.DeleteItems.Count)
            {
                string transactionId = cleanup.TransactionId;
                int receivedCount = cleanup.ReceivedCount;
                int deletedCount = cleanup.DeletedCount;
                List<TransferItemState> storeItems = new List<TransferItemState>(cleanup.StoreItems);
                _donationCleanup = null;
                QueueDonationForDispatch(transactionId, receivedCount, storeItems, deletedCount);
                return;
            }

            TransferItemState expected = cleanup.DeleteItems[cleanup.DeleteIndex];
            List<Item> matches = FindInventoryItems(expected);
            if (matches.Count == 0)
            {
                FailDonationCleanup(
                    "Refusing over-retention deletion because Central has no matching loose inventory copy of " +
                    expected.Name + " QL" + expected.Ql + ".");
                return;
            }

            cleanup.PendingDelete = expected;
            cleanup.PendingBeforeCount = matches.Count;
            cleanup.DeleteDeadlineUtc = DateTime.UtcNow.AddMilliseconds(
                ServicePolicy.DeleteVerifyTimeoutMs);
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "DELETE REQUESTED one of " + matches.Count + " matching loose copies of " +
                expected.Name + " QL" + expected.Ql +
                " because projected exact-template stock exceeds configured cap " +
                GetAcceptanceRule(expected.AoId).MaxCopies + ".");
            matches[0].Delete();
        }

        private void FailDonationCleanup(string error)
        {
            DonationCleanupState cleanup = _donationCleanup;
            if (cleanup == null)
                return;

            List<TransferItemState> remainingOverflow = cleanup.DeleteItems
                .Skip(cleanup.DeleteIndex)
                .ToList();
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            AddDonationDispatchBatches(queue, cleanup.TransactionId, cleanup.StoreItems);
            if (remainingOverflow.Count > 0)
            {
                queue.Batches.Add(new DispatchBatchState
                {
                    BatchId = "delete-hold-" + Guid.NewGuid().ToString("N"),
                    TransactionId = cleanup.TransactionId,
                    Role = "central-delete",
                    Character = Client.CharacterName,
                    Status = "failed",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                    AttemptCount = 1,
                    LastError = error,
                    Items = remainingOverflow
                });
            }
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "donation_overcap_delete_failed",
                    TransactionId = cleanup.TransactionId,
                    Actor = Client.CharacterName,
                    Role = "central",
                    Character = Client.CharacterName,
                    Source = "normal-inventory",
                    Destination = "central-delete-hold",
                    Message = error +
                        " Excess item(s) remain physical AO truth on Central; a failed queue hold prevents new player trades until reconciled.",
                    Items = remainingOverflow.Select(item =>
                        ToLedgerItem(item, "central", null, null, null)).ToList()
                });
            TellDonationPartner(
                "DELETE FAILURE: " + error +
                " The excess item remains on Central and is NOT being reported as deleted. Storeable items from this donation may continue through the queue, but a failed central-delete hold will block new donations until the physical state is reconciled.");
            _donationDispositionPartnerName = null;
            _donationCleanup = null;
        }

        private void QueueDonationForDispatch(
            string transactionId,
            int receivedCount,
            List<TransferItemState> storeItems,
            int deletedCount)
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            AddDonationDispatchBatches(queue, transactionId, storeItems);
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            foreach (var batch in queue.Batches.Where(b => b.TransactionId == transactionId && b.Status == "queued"))
                ReportTransferProgress("QUEUED", batch.BatchId,
                    "transaction=" + transactionId + "; Central -> " + batch.Character + "; awaiting transfer", batch.Items);
            int queuedBatches = queue.Batches.Count(b =>
                string.Equals(b.TransactionId, transactionId, StringComparison.Ordinal) &&
                string.Equals(b.Status, "queued", StringComparison.OrdinalIgnoreCase));
            AppendTradeLedger(
                "donation_disposition_completed",
                transactionId,
                null,
                null,
                "Donation disposition complete: received=" + receivedCount +
                ", store=" + (storeItems?.Count ?? 0) +
                ", deleted=" + deletedCount +
                ", queuedBatches=" + queuedBatches + ".",
                storeItems);
            TellDonationPartner(
                "Donation completed: Central received " + receivedCount +
                " item(s); " + (storeItems?.Count ?? 0) +
                " queued for storage in " + queuedBatches +
                " worker batch(es); " + deletedCount +
                " excess item(s) deleted and verified.");
            _donationDispositionPartnerName = null;
        }

        private void AddDonationDispatchBatches(
            DispatchQueueState queue,
            string transactionId,
            IEnumerable<TransferItemState> items)
        {
            if (queue == null)
                throw new InvalidOperationException("Dispatch queue state is unavailable.");

            foreach (IGrouping<string, TransferItemState> group in
                (items ?? Enumerable.Empty<TransferItemState>()).GroupBy(item =>
                {
                    string destination;
                    return SymbiantCatalog.TryGetDestinationRole(_settingsDir, item.AoId, out destination)
                        ? destination
                        : "unmanaged";
                }, StringComparer.OrdinalIgnoreCase))
            {
                RoleConfig destination;
                if (!TryGetRole(group.Key, out destination) ||
                    destination == null ||
                    string.IsNullOrWhiteSpace(destination.Character))
                {
                    AppendTradeLedger(
                        "dispatch_not_queued",
                        transactionId,
                        null,
                        group.Key,
                        "No configured storage character for routed role; items remain safely on Central.",
                        group.ToList());
                    continue;
                }

                List<TransferItemState> routedItems = group.ToList();
                for (int offset = 0; offset < routedItems.Count;
                    offset += ServicePolicy.MaxInternalTradeItems)
                {
                    DateTime createdUtc = DateTime.UtcNow;
                    queue.Batches.Add(new DispatchBatchState
                    {
                        BatchId = "batch-" + Guid.NewGuid().ToString("N"),
                        TransactionId = transactionId,
                        Role = group.Key,
                        Character = destination.Character,
                        Status = "queued",
                        CreatedUtc = createdUtc,
                        UpdatedUtc = createdUtc,
                        AttemptCount = 0,
                        Items = routedItems
                            .Skip(offset)
                            .Take(ServicePolicy.MaxInternalTradeItems)
                            .ToList()
                    });
                }
            }
        }

        private void ResetDonation()
        {
            _donationActive = false;
            _donationPartner = Identity.None;
            _donationPartnerName = null;
            _donationTransactionId = null;
            _donationOpenedUtc = DateTime.MinValue;
            _donationLastChangeUtc = DateTime.MinValue;
            _donationPreviousOffer.Clear();
            _donationSnapshot.Clear();
            _donationAccepted = false;
        }

        // ---------------------------------------------------------------------
        // Central serialized dispatch queue
        // ---------------------------------------------------------------------

        private void TickDispatch()
        {
            if (!_isCentral || _donationActive || _donationCleanup != null)
                return;

            if (_activeBatch != null)
            {
                if (string.Equals(_activeBatch.Status, "transferred", StringComparison.OrdinalIgnoreCase))
                {
                    PollStorageResult(_activeBatch);
                    _activeBatch = null;
                    return;
                }
                if (_dispatchTradeAge.Elapsed.TotalSeconds >=
                    ServicePolicy.TradeTimeoutSeconds)
                {
                    FailActiveBatch(
                        "Internal worker trade timed out. Any incomplete outgoing AO trade is declined so offered items return to Central inventory.");
                    return;
                }
                if (_outgoingOpened && DispatchClosedWithoutResult())
                {
                    FailActiveBatch("Dispatch trade closed without a terminal callback; checking physical cancellation.");
                    return;
                }
                if (_outgoingOpened)
                    TickOutgoingTrade();
                return;
            }

            if (Trade.IsTrading)
                return;
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            var censusing = WithdrawalStore.GetCensusCharacters(_settingsDir);
            DispatchBatchState next = queue.Batches.FirstOrDefault(b =>
                !censusing.Contains(b.Character) &&
                string.Equals(b.Status, "queued", StringComparison.OrdinalIgnoreCase) &&
                WorkerRetryDue(b.Character) &&
                DynelManager.Players.Any(player => player != null &&
                    string.Equals(player.Name, b.Character, StringComparison.OrdinalIgnoreCase)) &&
                !queue.Batches.Any(pending => UnresolvedDispatch(pending) &&
                    (string.Equals(pending.Character, b.Character, StringComparison.OrdinalIgnoreCase) ||
                     (pending.Items ?? new List<TransferItemState>()).Any(item =>
                        (b.Items ?? new List<TransferItemState>()).Any(candidate => CustodyKey(candidate) == CustodyKey(item))))));
            if (next == null)
            {
                var waiting = queue.Batches.FirstOrDefault(b => string.Equals(b.Status, "queued", StringComparison.OrdinalIgnoreCase));
                if (waiting != null)
                    ReportTransferWait(waiting, censusing.Contains(waiting.Character) ? "Worker is in census recovery." :
                        !DynelManager.Players.Any(player => player != null && string.Equals(player.Name, waiting.Character, StringComparison.OrdinalIgnoreCase)) ?
                        "Worker is not visible nearby." : "Worker retry delay or unresolved earlier transfer prevents dispatch.");
                return;
            }

            PlayerChar worker = DynelManager.Players.FirstOrDefault(p =>
                p != null && string.Equals(p.Name, next.Character, StringComparison.OrdinalIgnoreCase));
            if (worker == null)
                return;

            if (string.IsNullOrWhiteSpace(next.AttemptId))
            {
                next.AttemptId = Guid.NewGuid().ToString("N");
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            }
            if (!WorkerPrepared(next))
            {
                ReportTransferWait(next, "Awaiting worker preparation acknowledgement; items remain on Central.");
                return;
            }
            _transferWaits.Remove(next.BatchId);

            List<Item> centralItems = FindDistinctInventoryItems(next.Items);
            if (centralItems.Count != (next.Items?.Count ?? 0))
            {
                MarkBatchFailed(
                    queue,
                    next,
                    "Expected routed item multiset is not present in Central normal inventory; refusing to guess across missing copies.");
                StartLocalCensus("Dispatch source check failed before trading; refresh Central's physical stock and queued routing.");
                return;
            }

            RuntimeStateStore.DeleteIfExists(
                RuntimeStateStore.GetStorageResultPath(_settingsDir, next.Character));
            RuntimeStateStore.DeleteIfExists(
                RuntimeStateStore.GetDispatchCommandPath(_settingsDir, next.Character));
            RuntimeStateStore.WriteDispatchCommand(
                _settingsDir,
                new DispatchCommand
                {
                    BatchId = next.BatchId,
                    AttemptId = next.AttemptId,
                    TransactionId = next.TransactionId,
                    Role = next.Role,
                    SourceCharacter = Client.CharacterName,
                    DestinationCharacter = next.Character,
                    CreatedUtc = DateTime.UtcNow,
                    Items = next.Items != null
                        ? new List<TransferItemState>(next.Items)
                        : new List<TransferItemState>()
                });
            next.TransferNeverStarted = false;
            next.Status = "trading";
            next.AttemptCount++;
            next.UpdatedUtc = DateTime.UtcNow;
            next.LastError = null;
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            _activeBatch = next;
            _activeWorkerIdentity = worker.Identity;
            _dispatchTradeAge.Restart();
            _outgoingOpened = false;
            _outgoingAwaitingOfferCount = 0;
            _outgoingRequestedSlots.Clear();
            _outgoingPendingSlot = Identity.None;
            _outgoingPendingSlotSet = false;
            _outgoingPendingAddAttempts = 0;
            _outgoingAccepted = false;
            AppendTradeLedger(
                "dispatch_trade_opening",
                next.TransactionId,
                next.BatchId,
                next.Role,
                "Central opening serialized internal trade to " + next.Character + ".",
                next.Items);
            PrepareReceipt("dispatch-send", next.TransactionId, next.BatchId, next.Items, -1);
            Trade.Open(worker.Identity);
        }

        private void TickOutgoingTrade()
        {
            if (_activeBatch == null || !Trade.IsTrading)
                return;
            if (!DispatchPeerReady("opened")) return;
            if (Trade.TargetWindowCache?.Items == null) return;
            if (Trade.TargetWindowCache.Items.Count != 0)
            {
                FailActiveBatch("Worker offered an unexpected reciprocal item during dispatch.");
                return;
            }

            List<Item> liveOffered = Trade.PlayerWindowCache?.Items ?? new List<Item>();
            List<TransferItemState> offered = SnapshotTradeItems(liveOffered);
            if (!InternalOfferSettled(offered)) return;
            if (MatchesExpected(offered, _activeBatch.Items) && offered.Count > 0)
            {
                if (!_outgoingAccepted)
                {
                    _outgoingAccepted = true;
                    _localDispatchAcceptAge.Restart();
                    PersistReceipt("local-offer-accepted");
                    Trade.Accept();
                }
                return;
            }

            if (_outgoingAccepted)
            {
                FailActiveBatch("Central's offered manifest changed after acceptance; declining before confirmation.");
                return;
            }
            if (offered.Count >= (_activeBatch.Items?.Count ?? 0))
            {
                FailActiveBatch(
                    "Central trade window reached the expected item count but not the persisted batch multiset.");
                return;
            }

            // AO can publish AddItem acknowledgements incrementally.  Do not burst a
            // multi-item batch into one update or issue the next move until the local
            // trade window has visibly grown by the prior requested occurrence.
            if (offered.Count < _outgoingAwaitingOfferCount)
            {
                if (_outgoingPendingSlotSet &&
                    _outgoingPendingAddAttempts < ServicePolicy.InternalAddItemMaxAttempts &&
                    _outgoingAddAge.ElapsedMilliseconds >=
                    ServicePolicy.InternalAddItemRetryMilliseconds)
                {
                    // AO can drop an AddItem request without changing the local window.
                    // Resend only the same pending slot: never advance to another item
                    // until the expected offered count acknowledges this occurrence.
                    Trade.AddItem(_outgoingPendingSlot);
                    _outgoingPendingAddAttempts++;
                    _outgoingAddAge.Restart();
                    RuntimeStateStore.AppendActivity(
                        _settingsDir,
                        Client.CharacterName,
                        _role,
                        "INTERNAL TRADE resent pending AddItem for batch=" +
                        _activeBatch.BatchId + "; attempt=" +
                        _outgoingPendingAddAttempts + "/" +
                        ServicePolicy.InternalAddItemMaxAttempts + ".");
                }
                return;
            }

            _outgoingPendingSlot = Identity.None;
            _outgoingPendingSlotSet = false;
            _outgoingPendingAddAttempts = 0;

            var missing = new List<TransferItemState>(
                _activeBatch.Items ?? new List<TransferItemState>());
            foreach (TransferItemState actual in offered)
            {
                int index = missing.FindIndex(expected => SameTransferItem(actual, expected));
                if (index < 0)
                {
                    FailActiveBatch(
                        "Central trade window contains an item outside the persisted batch.");
                    return;
                }
                missing.RemoveAt(index);
            }
            if (missing.Count == 0)
                return;

            Item next = FindInventoryItems(missing[0]).FirstOrDefault(item =>
                !_outgoingRequestedSlots.Contains(item.Slot.Instance));
            if (next == null)
            {
                FailActiveBatch(
                    "Next routed item occurrence is no longer available for incremental trade staging.");
                return;
            }

            Trade.AddItem(next.Slot);
            _outgoingRequestedSlots.Add(next.Slot.Instance);
            _outgoingAwaitingOfferCount = offered.Count + 1;
            _outgoingPendingSlot = next.Slot;
            _outgoingPendingSlotSet = true;
            _outgoingPendingAddAttempts = 1;
            _outgoingAddAge.Restart();
        }

        private void FinishOutgoingDispatchTrade()
        {
            if (_activeBatch == null)
                return;

            ActiveLedgerStore.MarkDispatched(_settingsDir, _activeBatch.TransactionId,
                _activeBatch.Character, _activeBatch.Items, Client.CharacterName, _receipt.LedgerIds);

            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            DispatchBatchState stored = FindBatch(queue, _activeBatch.BatchId);
            if (stored != null)
            {
                stored.Status = "transferred";
                stored.UpdatedUtc = DateTime.UtcNow;
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                _activeBatch = stored;
            }
            else
                _activeBatch.Status = "transferred";

            AppendTradeLedger(
                "dispatch_trade_completed",
                _activeBatch.TransactionId,
                _activeBatch.BatchId,
                _activeBatch.Role,
                "AO server completed Central -> " + _activeBatch.Character +
                " trade. Waiting for physical storage result.",
                _activeBatch.Items);
            _activeWorkerIdentity = Identity.None;
            _outgoingOpened = false;
            _outgoingAwaitingOfferCount = 0;
            _outgoingRequestedSlots.Clear();
            _outgoingPendingSlot = Identity.None;
            _outgoingPendingSlotSet = false;
            _outgoingPendingAddAttempts = 0;
            _outgoingAccepted = false;
            _dispatchTradeAge.Restart();
            // Sender custody is confirmed. Waiting for this worker's storage must not
            // occupy Central's trade slot or block another destination.
            _activeBatch = null;
        }

        private void PollStorageResult(DispatchBatchState batch)
        {
            if (batch == null)
                return;
            StorageBatchResult result = ReadWorkerStorageReply(batch);
            if (result == null || !string.Equals(
                    result.BatchId,
                    batch.BatchId,
                    StringComparison.Ordinal))
            {
                ReportTransferWait(batch, "Transfer completed; awaiting verified storage acknowledgement.");
                return;
            }
            _transferWaits.Remove(batch.BatchId);

            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            DispatchBatchState stored = FindBatch(queue, batch.BatchId);
            if (result.Success && (result.ExpectedCount != (batch.Items?.Count ?? 0) ||
                result.StoredCount != result.ExpectedCount))
            {
                result.Success = false;
                result.Error = "Worker storage acknowledgment count does not match this dispatch.";
            }
            if (result.Success)
            {
                if (stored != null)
                    queue.Batches.Remove(stored);
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                if (batch.AttemptId != null) _appliedDispatchReceipts.Remove(batch.AttemptId);
                AppendTradeLedger(
                    "dispatch_stored",
                    batch.TransactionId,
                    batch.BatchId,
                    batch.Role,
                    "Worker confirmed physical placement of " + result.StoredCount +
                    "/" + result.ExpectedCount + " item(s).",
                    batch.Items);
            }
            else
            {
                if (stored != null)
                {
                    stored.Status = "failed";
                    stored.LastError = result.Error;
                    stored.UpdatedUtc = DateTime.UtcNow;
                    RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                }
                AppendTradeLedger(
                    "dispatch_storage_failed",
                    batch.TransactionId,
                    batch.BatchId,
                    batch.Role,
                    result.Error,
                    batch.Items);
                TellKavem(
                    "STORAGE FAILURE " + batch.Role + ": " + result.Error +
                    " Physical custody is retained; automatic recovery requires both transfer receipts and a fresh worker audit.");
            }
            _storageInquiryIntervals.Remove(batch.BatchId);
        }

        private void FailActiveBatch(string error)
        {
            VerifyCancelledReceipt();
            if (_activeBatch == null)
                return;
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            DispatchBatchState stored = FindBatch(queue, _activeBatch.BatchId);
            if (stored != null)
            {
                stored.Status = "failed";
                stored.LastError = error;
                stored.UpdatedUtc = DateTime.UtcNow;
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            }
            AppendTradeLedger(
                "dispatch_failed",
                _activeBatch.TransactionId,
                _activeBatch.BatchId,
                _activeBatch.Role,
                error,
                _activeBatch.Items);
            TellKavem("Dispatch failure " + _activeBatch.Role + ": " + error);
            RuntimeStateStore.DeleteIfExists(
                RuntimeStateStore.GetDispatchCommandPath(_settingsDir, _activeBatch.Character));
            _activeBatch = null;
            _activeWorkerIdentity = Identity.None;
            _outgoingOpened = false;
            _outgoingAwaitingOfferCount = 0;
            _outgoingRequestedSlots.Clear();
            _outgoingPendingSlot = Identity.None;
            _outgoingPendingSlotSet = false;
            _outgoingPendingAddAttempts = 0;
            _outgoingAccepted = false;
            TryDeclineTrade();
        }

        private void MarkBatchFailed(DispatchQueueState queue, DispatchBatchState batch, string error)
        {
            batch.Status = "failed";
            batch.TransferNeverStarted = true;
            batch.LastError = error;
            batch.UpdatedUtc = DateTime.UtcNow;
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
            AppendTradeLedger(
                "dispatch_failed",
                batch.TransactionId,
                batch.BatchId,
                batch.Role,
                error,
                batch.Items);
            TellKavem("Dispatch failure " + batch.Role + ": " + error);
        }

        // ---------------------------------------------------------------------
        // Worker receive trade and physical placement
        // ---------------------------------------------------------------------

        private void BeginWorkerTrade(Identity centralIdentity)
        {
            if (_storageJob != null)
            {
                Trade.Decline();
                TellKavem(
                    "Worker " + Client.CharacterName +
                    " declined a new Central trade because a prior storage batch is still active.");
                return;
            }

            DispatchCommand command = _reservedDispatch;
            if (command == null ||
                _reservationAge == null || _reservationAge.ElapsedMilliseconds > 15000 ||
                !string.Equals(command.Role, _role, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(command.DestinationCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(command.SourceCharacter, _centralCharacter, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning(
                    $"BANKING SERVICE worker {Client.CharacterName} received Central trade " +
                    "without a matching dispatch command; declining.");
                Trade.Decline();
                return;
            }
            _workerCommand = command;
            _reservedDispatch = null;
            PrepareReceipt("dispatch-receive", command.TransactionId, command.BatchId, command.Items, 1);
            _workerTradeAge.Restart();
            _workerAccepted = false;
        }

        private void TickWorkerTrade()
        {
            if (_isCentral || _workerCommand == null || _storageJob != null)
                return;
            if (DispatchClosedWithoutResult())
            {
                FailWorkerCommand("Dispatch trade closed without a terminal callback; checking physical cancellation.");
                return;
            }
            if (_workerTradeAge.Elapsed.TotalSeconds >= ServicePolicy.TradeTimeoutSeconds)
            {
                FailWorkerCommand("Timed out waiting for Central's attempt-bound offer acknowledgement.");
                return;
            }
            if (!Trade.IsTrading || Trade.TargetWindowCache?.Items == null || Trade.PlayerWindowCache?.Items == null) return;
            List<TransferItemState> offered = SnapshotTradeItems(Trade.TargetWindowCache.Items);
            if (Trade.PlayerWindowCache.Items.Count != 0 || !IsManifestSubset(offered, _workerCommand.Items))
            {
                FailWorkerCommand("Dispatch trade windows contradict the prepared item manifest.");
                return;
            }
            if (_workerAccepted) return;
            if (!InternalOfferSettled(offered) || !DispatchPeerReady("accepted")) return;
            // An incomplete receiver cache is permitted only after live IPC
            // confirms Central accepted its exact complete local offer.
            _workerAccepted = true;
            _localDispatchAcceptAge.Restart();
            PersistReceipt("local-offer-accepted");
            Trade.Accept();
        }

        private void FinishWorkerReceiveTrade()
        {
            if (_workerCommand == null)
                return;
            DispatchCommand command = _workerCommand;
            _workerCommand = null;
            _workerAccepted = false;
            RuntimeStateStore.DeleteIfExists(
                RuntimeStateStore.GetDispatchCommandPath(_settingsDir, Client.CharacterName));
            _storageJob = new StorageJob
            {
                Command = command,
                Index = 0,
                Phase = StoragePhase.FindBag,
                PhaseStartedUtc = DateTime.UtcNow,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(ServicePolicy.ItemMoveTimeoutMs),
                StoredCount = 0
            };
            AppendTradeLedger(
                "worker_trade_completed",
                command.TransactionId,
                command.BatchId,
                _role,
                "Worker received " + (command.Items?.Count ?? 0) +
                " item(s) from Central into normal inventory.",
                command.Items);
        }

        private void TickStorageJob()
        {
            if (_isCentral || _storageJob == null)
                return;
            try
            {
                if (_storageJob.Command == null || _storageJob.Command.Items == null)
                {
                    FailStorageJob("Storage command is empty.");
                    return;
                }
                if (_storageJob.Index >= _storageJob.Command.Items.Count)
                {
                    CompleteStorageJob();
                    return;
                }
                switch (_storageJob.Phase)
                {
                    case StoragePhase.FindBag: StartStorageItem(); break;
                    case StoragePhase.MovingBagToInventory: ProcessStorageBagMoveToInventory(); break;
                    case StoragePhase.OpeningBag: ProcessStorageBagOpen(); break;
                    case StoragePhase.MovingItemIntoBag: ProcessItemMoveIntoBag(); break;
                    case StoragePhase.ReturningBag: ProcessStorageBagReturn(); break;
                    default: FailStorageJob("Invalid storage phase " + _storageJob.Phase + "."); break;
                }
            }
            catch (Exception ex)
            {
                FailStorageJob("Storage exception: " + ex);
            }
        }

        private void StartStorageItem()
        {
            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (state == null)
            {
                FailStorageJob("Persistent storage baseline/state is missing.");
                return;
            }
            TransferItemState expected = _storageJob.Command.Items[_storageJob.Index];
            Item inventoryItem = FindStorageInventoryItem(expected);
            if (inventoryItem == null)
            {
                if (DateTime.UtcNow < _storageJob.DeadlineUtc)
                    return;
                FailStorageJob(
                    "Received item is not visible in worker normal inventory: " +
                    expected.Name + " AOID=" + expected.AoId + ".");
                return;
            }
            StorageBagState bag = RuntimeStateStore.FindNextFreeBag(state, _role, Client.CharacterName);
            if (bag == null)
            {
                FailStorageJob("No persisted storage bag with a free inner slot remains.");
                return;
            }
            _storageJob.Expected = expected;
            _storageJob.ActualItemIdentity = IsUsableIdentity(inventoryItem.UniqueIdentity.ToString())
                ? inventoryItem.UniqueIdentity.ToString()
                : null;
            _storageJob.Bag = bag;
            _storageJob.BagLiveIdentity = null;
            _storageJob.InnerSlot = -1;
            if (string.Equals(bag.Source, "bank", StringComparison.OrdinalIgnoreCase))
            {
                Item liveBag = FindBankBagAtOuterSlot(bag.OuterSlotInstance);
                if (liveBag == null)
                {
                    FailStorageJob(
                        "Persisted bank bag is not present at expected outer slot " +
                        bag.OuterSlotInstance + ". Physical AO state differs from baseline.");
                    return;
                }
                _storageJob.BagLiveIdentity = liveBag.UniqueIdentity.ToString();
                _storageJob.Phase = StoragePhase.MovingBagToInventory;
                SetStorageDeadline(ServicePolicy.BagMoveTimeoutMs);
                liveBag.MoveToInventory();
                return;
            }
            Item inventoryBag = FindInventoryBagAtOuterSlot(bag.OuterSlotInstance);
            if (inventoryBag == null)
            {
                FailStorageJob(
                    "Persisted inventory bag is not present at expected outer slot " +
                    bag.OuterSlotInstance + ". Physical AO state differs from baseline.");
                return;
            }
            _storageJob.BagLiveIdentity = inventoryBag.UniqueIdentity.ToString();
            _storageJob.Phase = StoragePhase.OpeningBag;
            SetStorageDeadline(ServicePolicy.BagOpenTimeoutMs);
            inventoryBag.Use();
        }

        private void ProcessStorageBagMoveToInventory()
        {
            Item bag = FindInventoryBagByIdentity(_storageJob.BagLiveIdentity);
            if (bag != null)
            {
                _storageJob.Phase = StoragePhase.OpeningBag;
                SetStorageDeadline(ServicePolicy.BagOpenTimeoutMs);
                bag.Use();
                return;
            }
            if (DateTime.UtcNow >= _storageJob.DeadlineUtc)
                FailStorageJob(
                    "Bank bag did not arrive in normal inventory before staging timeout; stopping before touching another bag.");
        }

        private void ProcessStorageBagOpen()
        {
            Container container = FindContainerByIdentity(_storageJob.BagLiveIdentity);
            if (container != null && container.IsOpen)
            {
                Item item = FindStorageInventoryItem(_storageJob.Expected);
                if (item == null)
                {
                    FailStorageJob("Received item disappeared from normal inventory before bag insertion.");
                    return;
                }
                if (container.IsFull)
                {
                    FailStorageJob(
                        "Live bag is full even though persisted state expected free space. Reconcile before continuing.");
                    return;
                }
                _storageJob.Phase = StoragePhase.MovingItemIntoBag;
                SetStorageDeadline(ServicePolicy.ItemMoveTimeoutMs);
                item.MoveToContainer(container);
                return;
            }
            if (DateTime.UtcNow >= _storageJob.DeadlineUtc)
                FailStorageJob("Bag did not open/materialize before timeout.");
        }

        private void ProcessItemMoveIntoBag()
        {
            Container container = FindContainerByIdentity(_storageJob.BagLiveIdentity);
            if (container != null && container.Items != null)
            {
                var occupiedInnerSlots = new HashSet<int>(
                    (_storageJob.Bag?.Items ?? new List<StoredItemState>())
                        .Where(item => item != null)
                        .Select(item => item.InnerSlot));
                Item observed = container.Items.FirstOrDefault(item =>
                    MatchesItem(item, _storageJob.Expected, _storageJob.ActualItemIdentity) &&
                    !occupiedInnerSlots.Contains(item.Slot.Instance & 0xFFFF));
                if (observed != null)
                {
                    _storageJob.InnerSlot = observed.Slot.Instance & 0xFFFF;
                    _storageJob.ObservedStoredItemIdentity = IsUsableIdentity(observed.UniqueIdentity.ToString())
                        ? observed.UniqueIdentity.ToString()
                        : null;
                    _storageJob.BagHandle = container.Handle;
                    if (string.Equals(_storageJob.Bag.Source, "bank", StringComparison.OrdinalIgnoreCase))
                    {
                        Item liveBag = FindInventoryBagByIdentity(_storageJob.BagLiveIdentity);
                        if (liveBag == null)
                        {
                            FailStorageJob(
                                "Stored item is inside staged bank bag, but the bag is no longer visible in normal inventory for return.");
                            return;
                        }
                        _storageJob.Phase = StoragePhase.ReturningBag;
                        SetStorageDeadline(ServicePolicy.BagMoveTimeoutMs);
                        liveBag.MoveToBank();
                        return;
                    }
                    CommitStoredItem();
                    return;
                }
            }
            if (DateTime.UtcNow >= _storageJob.DeadlineUtc)
                FailStorageJob("AO did not confirm the item in a newly occupied bag slot before timeout.");
        }

        private void ProcessStorageBagReturn()
        {
            Item bankBag = FindBankBagByIdentity(_storageJob.BagLiveIdentity);
            if (bankBag != null)
            {
                if (bankBag.Slot.Instance != _storageJob.Bag.OuterSlotInstance)
                {
                    FailStorageJob(
                        "Bank bag returned to unexpected outer slot " + bankBag.Slot.Instance +
                        " instead of " + _storageJob.Bag.OuterSlotInstance +
                        "; stopping for reconciliation.");
                    return;
                }
                CommitStoredItem();
                return;
            }
            if (DateTime.UtcNow >= _storageJob.DeadlineUtc)
                FailStorageJob("Staged bank bag did not return to bank before timeout.");
        }

        private void CommitStoredItem()
        {
            string error;
            if (!RuntimeStateStore.RecordPlacement(
                _settingsDir,
                _storageJob.Command.TransactionId,
                _role,
                Client.CharacterName,
                _storageJob.Bag.Source,
                _storageJob.Bag.OuterSlotInstance,
                _storageJob.BagLiveIdentity,
                _storageJob.BagHandle,
                _storageJob.Expected,
                _storageJob.ObservedStoredItemIdentity,
                _storageJob.InnerSlot,
                out error))
            {
                FailStorageJob("AO placement succeeded but persistent state update failed: " + error);
                return;
            }
            LedgerRecord record = new LedgerRecord
            {
                Utc = DateTime.UtcNow,
                Event = "item_stored",
                TransactionId = _storageJob.Command.TransactionId,
                BatchId = _storageJob.Command.BatchId,
                Actor = Client.CharacterName,
                Role = _role,
                Character = Client.CharacterName,
                Source = "normal-inventory",
                Destination =
                    _storageJob.Bag.Source + ":" + _storageJob.Bag.OuterSlotInstance +
                    "/inner:" + _storageJob.InnerSlot,
                Message = "AO confirmed item inside storage bag and required bank-bag return was verified.",
                Items = new List<LedgerItem>
                {
                    ToLedgerItem(
                        _storageJob.Expected,
                        _role,
                        _storageJob.Bag.Source,
                        _storageJob.Bag.OuterSlotInstance,
                        _storageJob.InnerSlot)
                }
            };
            RuntimeStateStore.AppendLedger(_settingsDir, record);
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "STORED " + _storageJob.Expected.Name + " QL" + _storageJob.Expected.Ql +
                " -> " + _storageJob.Bag.Source + ":" + _storageJob.Bag.OuterSlotInstance +
                "/inner:" + _storageJob.InnerSlot + ".");
            ReportTransferProgress("STORED VERIFIED", _storageJob.Command.BatchId,
                "transaction=" + _storageJob.Command.TransactionId + "; destination=" + Client.CharacterName +
                "; " + _storageJob.Expected.Name + " QL" + _storageJob.Expected.Ql +
                " AOID=" + _storageJob.Expected.AoId + "; " + _storageJob.Bag.Source + ":" +
                _storageJob.Bag.OuterSlotInstance + "/inner:" + _storageJob.InnerSlot);
            _storageJob.StoredCount++;
            _storageJob.Index++;
            _storageJob.Expected = null;
            _storageJob.Bag = null;
            _storageJob.BagLiveIdentity = null;
            _storageJob.ActualItemIdentity = null;
            _storageJob.ObservedStoredItemIdentity = null;
            _storageJob.InnerSlot = -1;
            _storageJob.BagHandle = 0;
            _storageJob.Phase = StoragePhase.FindBag;
            SetStorageDeadline(ServicePolicy.ItemMoveTimeoutMs);
        }

        private void CompleteStorageJob()
        {
            StorageJob job = _storageJob;
            if (!job.LocalRecovery) RuntimeStateStore.WriteStorageResult(
                _settingsDir,
                new StorageBatchResult
                {
                    BatchId = job.Command.BatchId,
                    TransactionId = job.Command.TransactionId,
                    Role = _role,
                    Character = Client.CharacterName,
                    CompletedUtc = DateTime.UtcNow,
                    Success = true,
                    ExpectedCount = job.Command.Items?.Count ?? 0,
                    StoredCount = job.StoredCount
                });
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "STORAGE BATCH COMPLETE batch=" + job.Command.BatchId +
                " stored=" + job.StoredCount + ".");
            ReportTransferProgress("STORAGE BATCH COMPLETE", job.Command.BatchId,
                "destination=" + Client.CharacterName + "; verified stored=" + job.StoredCount +
                "/" + (job.Command.Items?.Count ?? 0));
            if (job.Command?.AttemptId != null)
            {
                ReceiptEvidence storedReceipt;
                if (_appliedDispatchReceipts.TryGetValue(job.Command.AttemptId, out storedReceipt))
                    _lastStoredDispatchReceipt = storedReceipt;
                _appliedDispatchReceipts.Remove(job.Command.AttemptId);
            }
            _storageJob = null;
        }

        private void FailStorageJob(string error)
        {
            if (_storageJob == null)
                return;
            StorageJob job = _storageJob;
            Logger.Error(
                $"BANKING SERVICE storage failure character={Client.CharacterName} " +
                $"batch={job.Command?.BatchId}: {error}");
            if (!job.LocalRecovery) RuntimeStateStore.WriteStorageResult(
                _settingsDir,
                new StorageBatchResult
                {
                    BatchId = job.Command?.BatchId,
                    TransactionId = job.Command?.TransactionId,
                    Role = _role,
                    Character = Client.CharacterName,
                    CompletedUtc = DateTime.UtcNow,
                    Success = false,
                    ExpectedCount = job.Command?.Items?.Count ?? 0,
                    StoredCount = job.StoredCount,
                    Error = error
                });
            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "storage_failed",
                    TransactionId = job.Command?.TransactionId,
                    BatchId = job.Command?.BatchId,
                    Actor = Client.CharacterName,
                    Role = _role,
                    Character = Client.CharacterName,
                    Message = error
                });
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "STORAGE FAILURE: " + error);
            ReportTransferProgress("STORAGE FAILURE", job.Command?.BatchId,
                "destination=" + Client.CharacterName + "; verified stored=" + job.StoredCount + "; " + error);
            TellKavem(
                "STORAGE FAILURE on " + Client.CharacterName + ": " + error +
                " No further items in this batch will be moved until reconciliation.");
            _storageJob = null;
            if (!job.LocalRecovery) RequestStorageRecovery(job);
            if (job.LocalRecovery && job.Phase != StoragePhase.FindBag)
                StartLocalCensus("Local storage move requires a fresh physical census: " + error);
        }

        private void FailWorkerCommand(string error)
        {
            VerifyCancelledReceipt();
            if (_workerCommand == null)
                return;
            DispatchCommand command = _workerCommand;
            RuntimeStateStore.WriteStorageResult(
                _settingsDir,
                new StorageBatchResult
                {
                    BatchId = command.BatchId,
                    TransactionId = command.TransactionId,
                    Role = _role,
                    Character = Client.CharacterName,
                    CompletedUtc = DateTime.UtcNow,
                    Success = false,
                    ExpectedCount = command.Items?.Count ?? 0,
                    StoredCount = 0,
                    Error = error
                });
            RuntimeStateStore.DeleteIfExists(
                RuntimeStateStore.GetDispatchCommandPath(_settingsDir, Client.CharacterName));
            TellKavem("Worker trade failure on " + Client.CharacterName + ": " + error);
            _workerCommand = null;
            _workerAccepted = false;
            TryDeclineTrade();
        }

        // ---------------------------------------------------------------------
        // Fresh bagaudit -> operational state import
        // ---------------------------------------------------------------------

        private void ImportFreshBaselineIfNeeded()
        {
            if (!_isCentral || DateTime.UtcNow < _nextSlowTickUtc)
                return;
            _nextSlowTickUtc = DateTime.UtcNow.AddSeconds(1);
            string baselinePath = Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "storage-baseline.json");
            if (!File.Exists(baselinePath))
                return;
            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(baselinePath));
            }
            catch
            {
                return;
            }
            string runId = root["runId"]?.ToString();
            if (string.IsNullOrWhiteSpace(runId))
                return;
            StorageState current = RuntimeStateStore.LoadStorageState(_settingsDir);
            if (current != null && string.Equals(current.BaselineRunId, runId, StringComparison.Ordinal))
            {
                _lastImportedBaselineRunId = runId;
                return;
            }
            StorageState imported = ConvertBaseline(root, runId);
            if (imported == null || imported.Workers == null || imported.Workers.Count != 5)
                return;
            RuntimeStateStore.SaveStorageBaseline(_settingsDir, imported, "audit-" + runId);
            _lastImportedBaselineRunId = runId;
            TellKavem(
                "Storage baseline imported: run " + runId + ", workers=" +
                imported.Workers.Count + ", bags=" +
                imported.Workers.Sum(w => w.Bags?.Count ?? 0) + ", stock=" +
                imported.Workers.Sum(w => w.Bags?.Sum(b => b.Items?.Count ?? 0) ?? 0) + ".");
        }

        private static StorageState ConvertBaseline(JObject root, string runId)
        {
            var state = new StorageState
            {
                BaselineRunId = runId,
                UpdatedUtc = DateTime.UtcNow,
                Workers = new List<StorageWorkerState>()
            };
            JObject workers = root["workers"] as JObject;
            if (workers == null)
                return null;
            foreach (JProperty property in workers.Properties())
            {
                JObject sourceWorker = property.Value as JObject;
                if (sourceWorker == null)
                    continue;
                var worker = new StorageWorkerState
                {
                    Role = sourceWorker["role"]?.ToString() ?? property.Name,
                    Character = sourceWorker["character"]?.ToString(),
                    ObservedUtc = ParseUtc(sourceWorker["observedUtc"]),
                    Bags = new List<StorageBagState>()
                };
                JArray bags = sourceWorker["bags"] as JArray;
                if (bags != null)
                {
                    foreach (JObject sourceBag in bags.OfType<JObject>())
                    {
                        var bag = new StorageBagState
                        {
                            Source = sourceBag["source"]?.ToString(),
                            OuterSlotType = sourceBag["finalOuterSlotType"]?.ToString(),
                            OuterSlotInstance = IntToken(sourceBag["finalOuterSlotInstance"]),
                            LastUniqueIdentity = sourceBag["uniqueIdentity"]?.ToString(),
                            LastHandle = 0,
                            Capacity = 21,
                            Items = new List<StoredItemState>()
                        };
                        JArray items = sourceBag["items"] as JArray;
                        if (items != null)
                        {
                            foreach (JObject sourceItem in items.OfType<JObject>())
                            {
                                bag.Items.Add(new StoredItemState
                                {
                                    UniqueIdentity = sourceItem["UniqueIdentity"]?.ToString(),
                                    AoId = IntToken(sourceItem["LowId"]),
                                    HighId = IntToken(sourceItem["HighId"]),
                                    Ql = IntToken(sourceItem["Ql"]),
                                    Name = sourceItem["Name"]?.ToString(),
                                    InnerSlot = IntToken(sourceItem["SlotInstance"]) & 0xFFFF,
                                    ObservedUtc = worker.ObservedUtc,
                                    TransactionId = "audit-" + runId
                                });
                            }
                        }
                        worker.Bags.Add(bag);
                    }
                }
                state.Workers.Add(worker);
            }
            return state;
        }

        // ---------------------------------------------------------------------
        // Common helpers
        // ---------------------------------------------------------------------

        private ServiceConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<ServiceConfig>();
            }
            catch (Exception ex)
            {
                Logger.Warning($"BANKING SERVICE could not read the Bankers section: {ex.Message}");
                return null;
            }
        }

        private string ResolveCurrentRole()
        {
            foreach (KeyValuePair<string, RoleConfig> pair in _config.Roles)
            {
                if (pair.Value != null && string.Equals(
                        pair.Value.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase))
                    return pair.Key;
            }
            return null;
        }

        private bool TryGetRole(string role, out RoleConfig value)
        {
            foreach (KeyValuePair<string, RoleConfig> pair in _config.Roles)
            {
                if (string.Equals(pair.Key, role, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private bool HasUnresolvedDispatchWork()
        {
            return _activeBatch != null || _donationCleanup != null || _receipt != null;
        }

        private void AnnounceDonationChanges(List<TransferItemState> offered)
        {
            List<TransferItemState> current = offered ?? new List<TransferItemState>();
            List<TransferItemState> previous =
                _donationPreviousOffer ?? new List<TransferItemState>();
            List<TransferItemState> added = MultisetDifference(current, previous);
            List<TransferItemState> removed = MultisetDifference(previous, current);
            List<TransferItemState> alreadyOffered = new List<TransferItemState>(previous);

            foreach (TransferItemState item in removed)
            {
                int removedIndex = alreadyOffered.FindIndex(candidate =>
                    SameTransferItem(candidate, item));
                if (removedIndex >= 0)
                    alreadyOffered.RemoveAt(removedIndex);
                AnnounceDonationItemRemoved(item);
            }

            int tradeIndex = Math.Max(0, current.Count - added.Count);
            foreach (TransferItemState item in added)
            {
                tradeIndex++;
                int earlierCopies = alreadyOffered.Count(candidate =>
                    candidate != null && candidate.AoId == item.AoId);
                AnnounceDonationItemAdded(item, tradeIndex, earlierCopies);
                alreadyOffered.Add(item);
            }
        }

        private void AnnounceDonationItemAdded(
            TransferItemState item,
            int tradeIndex,
            int earlierCopiesInTrade)
        {
            if (item == null)
                return;
            string progress =
                "Accepting item " + CityBankersChatPalette.Cyan(tradeIndex.ToString()) +
                "/" + CityBankersChatPalette.Cyan(ServicePolicy.MaxTradeItems.ToString()) +
                " — ";
            string itemDescription = BuildItemLink(item) + " (QL " +
                CityBankersChatPalette.Cyan(item.Ql.ToString()) + ")";
            SymbiantCatalog.AcceptanceRule rule;
            if (!SymbiantCatalog.TryGetRule(_settingsDir, item.AoId, out rule))
            {
                TellDonationPartner(
                    progress + CityBankersChatPalette.Red("REJECTED") + " " +
                    itemDescription + " — not accepted.");
                return;
            }
            CurrentStockState stock = RuntimeStateStore.LoadCurrentStock(_settingsDir);
            int count = CountStoredCopies(stock, item);
            int projectedStored = count + Math.Max(0, earlierCopiesInTrade);
            string destination = GetDonationDestinationCharacter(rule);
            if (SymbiantCatalog.IsAtRetentionLimit(rule, projectedStored))
            {
                TellDonationPartner(
                    progress + CityBankersChatPalette.Red("DELETING") + " " +
                    itemDescription + " — " +
                    CityBankersChatPalette.Cyan(projectedStored.ToString()) +
                    " already stored on " + CityBankersChatPalette.Yellow(destination) + ".");
                return;
            }
            int copyNumber = projectedStored + 1;
            TellDonationPartner(
                progress + CityBankersChatPalette.Green("STORING") + " " +
                CityBankersChatPalette.Cyan(copyNumber.ToString()) +
                OrdinalSuffix(copyNumber) + " " + itemDescription + " on " +
                CityBankersChatPalette.Yellow(destination) + ".");
        }

        private string GetDonationDestinationCharacter(
            SymbiantCatalog.AcceptanceRule rule)
        {
            RoleConfig destination;
            return rule != null && TryGetRole(rule.Role, out destination) &&
                destination != null && !string.IsNullOrWhiteSpace(destination.Character)
                ? destination.Character
                : rule?.Role ?? "storage";
        }

        private static string OrdinalSuffix(int value)
        {
            int lastTwo = Math.Abs(value) % 100;
            if (lastTwo >= 11 && lastTwo <= 13)
                return "th";
            switch (Math.Abs(value) % 10)
            {
                case 1: return "st";
                case 2: return "nd";
                case 3: return "rd";
                default: return "th";
            }
        }

        private SymbiantCatalog.AcceptanceRule GetAcceptanceRule(int aoId)
        {
            SymbiantCatalog.AcceptanceRule rule;
            if (!SymbiantCatalog.TryGetRule(_settingsDir, aoId, out rule))
                throw new InvalidOperationException(
                    "AOID " + aoId + " is absent from Central's acceptance policy.");
            return rule;
        }

        private void AnnounceDonationItemRemoved(TransferItemState item)
        {
            if (item != null)
                TellDonationPartner(
                    "You removed " + BuildItemLink(item) +
                    " from the trade. It will not be received.");
        }

        private static string BuildItemLink(TransferItemState item)
        {
            if (item == null)
                return "<unknown item>";
            return "<a href='itemref://" + item.AoId + "/" + item.HighId + "/" +
                item.Ql + "'>" + EscapeChatText(item.Name) + "</a>";
        }

        private static string Color(string text, string color)
        {
            return "<font color='" + color + "'>" + EscapeChatText(text) + "</font>";
        }

        private static string EscapeChatText(string text)
        {
            return (text ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static int CountStoredCopies(CurrentStockState stock, TransferItemState item)
        {
            if (item == null)
                return 0;
            return (stock?.Items ?? new List<StockItemState>()).Count(candidate =>
                candidate != null &&
                candidate.AoId == item.AoId);
        }

        private static List<TransferItemState> MultisetDifference(
            IEnumerable<TransferItemState> left,
            IEnumerable<TransferItemState> right)
        {
            List<TransferItemState> remaining = new List<TransferItemState>(
                right ?? Enumerable.Empty<TransferItemState>());
            List<TransferItemState> difference = new List<TransferItemState>();
            foreach (TransferItemState item in left ?? Enumerable.Empty<TransferItemState>())
            {
                int index = remaining.FindIndex(candidate => SameTransferItem(item, candidate));
                if (index >= 0)
                    remaining.RemoveAt(index);
                else
                    difference.Add(item);
            }
            return difference;
        }

        private static string TemplateKey(TransferItemState item)
        {
            if (item == null)
                return string.Empty;
            return item.AoId.ToString();
        }

        private static List<TransferItemState> SnapshotTradeItems(IEnumerable<Item> items)
        {
            return (items ?? Enumerable.Empty<Item>())
                .Where(item => item != null)
                .OrderBy(item => item.Slot.Instance)
                .Select(item => new TransferItemState
                {
                    UniqueIdentity = IsUsableIdentity(item.UniqueIdentity.ToString())
                        ? item.UniqueIdentity.ToString()
                        : null,
                    AoId = item.Id,
                    HighId = item.HighId,
                    Ql = item.Ql,
                    Name = item.Name ?? string.Empty
                })
                .ToList();
        }

        private static string ItemKey(TransferItemState item)
        {
            if (item == null)
                return string.Empty;
            if (IsUsableIdentity(item.UniqueIdentity))
                return item.UniqueIdentity;
            return item.AoId + ":" + item.HighId + ":" + item.Ql + ":" + item.Name;
        }

        private static bool IsUsableIdentity(string identity)
        {
            return !string.IsNullOrWhiteSpace(identity) &&
                !string.Equals(identity, Identity.None.ToString(), StringComparison.Ordinal);
        }

        private static bool MatchesExpected(
            List<TransferItemState> actual,
            List<TransferItemState> expected)
        {
            actual = actual ?? new List<TransferItemState>();
            expected = expected ?? new List<TransferItemState>();
            if (actual.Count != expected.Count)
                return false;
            var remaining = new List<TransferItemState>(actual);
            foreach (TransferItemState wanted in expected)
            {
                int index = remaining.FindIndex(candidate => SameTransferItem(candidate, wanted));
                if (index < 0)
                    return false;
                remaining.RemoveAt(index);
            }
            return remaining.Count == 0;
        }

        private static bool SameTransferItem(TransferItemState left, TransferItemState right)
        {
            if (left == null || right == null)
                return false;
            if (IsUsableIdentity(left.UniqueIdentity) &&
                IsUsableIdentity(right.UniqueIdentity) &&
                string.Equals(left.UniqueIdentity, right.UniqueIdentity, StringComparison.Ordinal))
                return true;
            return left.AoId == right.AoId &&
                left.HighId == right.HighId &&
                left.Ql == right.Ql;
        }

        private Item FindInventoryItem(TransferItemState expected)
        {
            return FindInventoryItems(expected).FirstOrDefault();
        }

        private List<Item> FindInventoryItems(TransferItemState expected)
        {
            if (Inventory.Items == null || expected == null)
                return new List<Item>();
            List<WithdrawalState> reservations = _isCentral
                ? WithdrawalStore.LoadAll(_settingsDir) : new List<WithdrawalState>();
            List<Item> normal = Inventory.Items
                .Where(item => item != null && item.Slot.Type == IdentityType.Inventory &&
                    !IsReservedForPickup(item, reservations))
                .OrderBy(item => item.Slot.Instance)
                .ToList();
            if (IsUsableIdentity(expected.UniqueIdentity))
            {
                List<Item> unique = normal.Where(item =>
                    string.Equals(
                        item.UniqueIdentity.ToString(),
                        expected.UniqueIdentity,
                        StringComparison.Ordinal)).ToList();
                if (unique.Count > 0)
                    return unique;
            }
            return normal.Where(item =>
                item.Id == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql).ToList();
        }

        private List<Item> FindDistinctInventoryItems(IEnumerable<TransferItemState> expectedItems)
        {
            List<WithdrawalState> reservations = _isCentral
                ? WithdrawalStore.LoadAll(_settingsDir) : new List<WithdrawalState>();
            List<Item> available = Inventory.Items == null
                ? new List<Item>()
                : Inventory.Items
                    .Where(item => item != null && item.Slot.Type == IdentityType.Inventory &&
                        !IsReservedForPickup(item, reservations))
                    .OrderBy(item => item.Slot.Instance)
                    .ToList();
            var selected = new List<Item>();
            foreach (TransferItemState expected in expectedItems ?? Enumerable.Empty<TransferItemState>())
            {
                int index = -1;
                if (IsUsableIdentity(expected?.UniqueIdentity))
                {
                    index = available.FindIndex(item => string.Equals(
                        item.UniqueIdentity.ToString(),
                        expected.UniqueIdentity,
                        StringComparison.Ordinal));
                }
                if (index < 0 && expected != null)
                {
                    index = available.FindIndex(item =>
                        item.Id == expected.AoId &&
                        item.HighId == expected.HighId &&
                        item.Ql == expected.Ql);
                }
                if (index < 0)
                    return new List<Item>();
                selected.Add(available[index]);
                available.RemoveAt(index);
            }
            return selected;
        }

        private static bool MatchesItem(Item item, TransferItemState expected, string observedIdentity)
        {
            if (item == null || expected == null)
                return false;
            if (IsUsableIdentity(observedIdentity) && string.Equals(
                item.UniqueIdentity.ToString(), observedIdentity, StringComparison.Ordinal))
                return true;
            if (IsUsableIdentity(expected.UniqueIdentity) && string.Equals(
                item.UniqueIdentity.ToString(), expected.UniqueIdentity, StringComparison.Ordinal))
                return true;
            return item.Id == expected.AoId &&
                item.HighId == expected.HighId &&
                item.Ql == expected.Ql;
        }

        private static Item FindBankBagAtOuterSlot(int slot)
        {
            return Inventory.Bank.Items?.FirstOrDefault(item =>
                item != null &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                item.Slot.Instance == slot);
        }

        private static Item FindInventoryBagAtOuterSlot(int slot)
        {
            return Inventory.Items?.FirstOrDefault(item =>
                item != null &&
                item.Slot.Type == IdentityType.Inventory &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                item.Slot.Instance == slot);
        }

        private static Item FindInventoryBagByIdentity(string identity)
        {
            return Inventory.Items?.FirstOrDefault(item =>
                item != null &&
                item.Slot.Type == IdentityType.Inventory &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                string.Equals(item.UniqueIdentity.ToString(), identity, StringComparison.Ordinal));
        }

        private static Item FindBankBagByIdentity(string identity)
        {
            return Inventory.Bank.Items?.FirstOrDefault(item =>
                item != null &&
                item.UniqueIdentity.Type == IdentityType.Container &&
                string.Equals(item.UniqueIdentity.ToString(), identity, StringComparison.Ordinal));
        }

        private static Container FindContainerByIdentity(string identity)
        {
            return Inventory.Containers?.FirstOrDefault(container =>
                container != null &&
                string.Equals(container.Identity.ToString(), identity, StringComparison.Ordinal));
        }

        private static string FindPlayerName(Identity identity)
        {
            PlayerChar player = DynelManager.Players.FirstOrDefault(p =>
                p != null && p.Identity == identity);
            return player?.Name;
        }

        private void TellPlayer(string playerName, string message)
        {
            if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(message))
                return;
            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                playerName,
                CityBankersChatPalette.StyleMarkup(message));
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "TELL -> " + playerName + ": " + message);
        }

        private void TellDonationPartner(string message)
        {
            string partnerName = !string.IsNullOrWhiteSpace(_donationPartnerName)
                ? _donationPartnerName
                : _donationDispositionPartnerName;
            TellDirectPlayer(partnerName, message, "DONOR TELL");
        }

        private void TellDirectPlayer(
            string playerName,
            string message,
            string activityLabel = "PLAYER TELL")
        {
            if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(message))
                return;
            TellQueueClient.EnqueueDirectTell(
                _settingsDir,
                Client.CharacterName,
                playerName,
                CityBankersChatPalette.StyleMarkup(message));
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                activityLabel + " -> " + playerName + ": " + message);
        }

        private void TellKavem(string message)
        {
            TellPlayer(TrustedOperators.BootstrapAdmin, message);
        }

        // Observability is independent of custody/accounting: a channel failure
        // must never retry a physical move or invalidate its verified result.
        private void ReportTransferProgress(string stage, string batchId, string message,
            IEnumerable<TransferItemState> items = null)
        {
            try
            {
                string prefix = "BANKERS " + stage + " batch=" + (batchId ?? "-") + " ";
                var details = new List<string> { message ?? string.Empty };
                if (items != null)
                    foreach (TransferItemState item in items.Where(item => item != null))
                    {
                        Logger.Information(prefix + item.Name + " QL" + item.Ql + " AOID=" + item.AoId);
                        try
                        {
                            CityDwellers.Shared.ManagerChannelQueue.Enqueue(
                                RuntimeStateStore.GetDataDirectory(_settingsDir), Client.CharacterName,
                                CityBankersChatPalette.StyleMarkup("BANKERS " + CityBankersChatPalette.Stage(stage) +
                                    " batch=" + System.Security.SecurityElement.Escape(batchId ?? "-") + " " +
                                    CityBankersChatPalette.ItemLabel(item.AoId, item.HighId, item.Ql, item.Name)));
                        }
                        catch (Exception ex) { Logger.Warning("BANKERS item telemetry unavailable: " + ex.Message); }
                    }
                foreach (string detail in details)
                {
                    Logger.Information(prefix + detail);
                    // Keep each channel message bounded, including long failure text.
                    for (int offset = 0; offset < Math.Max(1, detail.Length); offset += 400)
                    {
                        string part = detail.Substring(offset, Math.Min(400, detail.Length - offset));
                        try
                        {
                            CityDwellers.Shared.ManagerChannelQueue.Enqueue(
                                RuntimeStateStore.GetDataDirectory(_settingsDir), Client.CharacterName,
                                CityBankersChatPalette.StyleMarkup("BANKERS " + CityBankersChatPalette.Stage(stage) +
                                    " batch=" + System.Security.SecurityElement.Escape(batchId ?? "-") + " " +
                                    System.Security.SecurityElement.Escape(part)));
                        }
                        catch (Exception ex) { Logger.Warning("BANKERS telemetry channel unavailable: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.Warning("BANKERS telemetry unavailable: " + ex.Message); }
                catch { } // Reporting must not change the outcome of the operation.
            }
        }

        private readonly Dictionary<string, Stopwatch> _transferWaits =
            new Dictionary<string, Stopwatch>();
        private void ReportTransferWait(DispatchBatchState batch, string reason)
        {
            Stopwatch previous;
            if (_transferWaits.TryGetValue(batch.BatchId, out previous) &&
                previous.ElapsedMilliseconds < 30000) return;
            _transferWaits[batch.BatchId] = Stopwatch.StartNew();
            ReportTransferProgress("WAITING", batch.BatchId, "Destination=" + batch.Character + "; " + reason);
        }

        private void AppendTradeLedger(
            string eventName,
            string transactionId,
            string batchId,
            string role,
            string message,
            IEnumerable<TransferItemState> items)
        {
            bool playerTrade = !string.IsNullOrWhiteSpace(eventName) &&
                eventName.StartsWith("player_trade_", StringComparison.OrdinalIgnoreCase);
            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = eventName,
                    TransactionId = transactionId,
                    BatchId = batchId,
                    Actor = Client.CharacterName,
                    Role = role,
                    Character = Client.CharacterName,
                    Source = playerTrade ? _donationPartnerName : null,
                    Destination = playerTrade ? Client.CharacterName : null,
                    Message = message,
                    Items = items?.Select(item => ToLedgerItem(
                        item,
                        role,
                        null,
                        null,
                        null)).ToList()
                });
            if (batchId != null && (eventName == "dispatch_stored" || eventName == "dispatch_failed" ||
                eventName == "dispatch_storage_failed")) _transferWaits.Remove(batchId);
            ReportTransferProgress(eventName, batchId,
                "transaction=" + transactionId + "; role=" + role + "; " + message, items);
        }

        private static LedgerItem ToLedgerItem(
            TransferItemState item,
            string role,
            string bagSource,
            int? bagOuterSlot,
            int? innerSlot)
        {
            return new LedgerItem
            {
                UniqueIdentity = item?.UniqueIdentity,
                AoId = item?.AoId ?? 0,
                HighId = item?.HighId ?? 0,
                Ql = item?.Ql ?? 0,
                Name = item?.Name,
                Role = role,
                BagSource = bagSource,
                BagOuterSlot = bagOuterSlot,
                InnerSlot = innerSlot
            };
        }

        private static DispatchBatchState FindBatch(DispatchQueueState queue, string batchId)
        {
            return queue?.Batches?.FirstOrDefault(b => string.Equals(
                b.BatchId,
                batchId,
                StringComparison.Ordinal));
        }

        private static DateTime ParseUtc(JToken token)
        {
            DateTime value;
            return token != null && DateTime.TryParse(token.ToString(), out value)
                ? value.ToUniversalTime()
                : DateTime.UtcNow;
        }

        private static int IntToken(JToken token)
        {
            int value;
            return token != null && int.TryParse(token.ToString(), out value)
                ? value
                : 0;
        }

        private void SetStorageDeadline(int milliseconds)
        {
            if (_storageJob == null)
                return;
            _storageJob.PhaseStartedUtc = DateTime.UtcNow;
            _storageJob.DeadlineUtc = DateTime.UtcNow.AddMilliseconds(milliseconds);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";
            return value.Length <= 12 ? value : value.Substring(value.Length - 8);
        }

        private static void TryDeclineTrade()
        {
            try
            {
                if (Trade.IsTrading)
                    Trade.Decline();
            }
            catch
            {
            }
        }

        private sealed class ServiceConfig
        {
            public Dictionary<string, RoleConfig> Roles;
        }

        private sealed class RoleConfig
        {
            public string Username;
            public string Character;
        }

        private sealed class DonationCleanupState
        {
            public string TransactionId;
            public int ReceivedCount;
            public List<TransferItemState> StoreItems = new List<TransferItemState>();
            public List<TransferItemState> DeleteItems = new List<TransferItemState>();
            public int DeleteIndex;
            public int DeletedCount;
            public TransferItemState PendingDelete;
            public int PendingBeforeCount;
            public DateTime DeleteDeadlineUtc;
        }

        private sealed class StorageJob
        {
            public bool LocalRecovery;
            public int RecoverySlot;
            public DispatchCommand Command;
            public int Index;
            public int StoredCount;
            public StoragePhase Phase;
            public DateTime PhaseStartedUtc;
            public DateTime DeadlineUtc;
            public TransferItemState Expected;
            public StorageBagState Bag;
            public string BagLiveIdentity;
            public string ActualItemIdentity;
            public string ObservedStoredItemIdentity;
            public int InnerSlot;
            public int BagHandle;
        }
    }
}
