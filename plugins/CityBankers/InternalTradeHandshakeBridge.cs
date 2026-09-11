using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    /// <summary>
    /// Narrow compatibility/recovery bridge for AOSharp.Clientless trade-window behavior.
    ///
    /// Live evidence showed that Central can populate and accept its local outgoing trade
    /// window while the receiving worker's TargetWindowCache remains empty. We do not
    /// weaken the trust boundary: the worker fallback is allowed only for configured
    /// Central, only with a matching persisted dispatch command, and only after AO reports
    /// that the other side has accepted. AO Finished plus the existing post-trade normal-
    /// inventory/storage verification remain the authority that an item actually moved.
    ///
    /// A second live retry showed that the first recovered batch could be attempted while
    /// a worker was still settling: Central opened the Extermination trade, but Kbexte never
    /// reached its own Worker trade opened state. Workers therefore publish a same-process,
    /// short-lived readiness heartbeat only after they can see configured Central and their
    /// bank is open. Failed-timeout recovery requeues only when that destination heartbeat
    /// is fresh and every expected item is physically back in Central normal inventory.
    /// </summary>
    public class InternalTradeHandshakeBridge : ClientlessPluginEntry
    {
        private const int ReadyHeartbeatSeconds = 1;
        private const int ReadyHeartbeatMaxAgeSeconds = 4;
        private const int RecoveryPollSeconds = 2;

        private string _settingsDir;
        private BridgeConfig _config;
        private string _role;
        private string _centralCharacter;
        private bool _isCentral;
        private bool _enabled;
        private DateTime _nextReadyHeartbeatUtc;
        private DateTime _nextRecoveryPollUtc;

        private bool _workerCentralTradeOpen;
        private string _workerBatchId;
        private bool _workerObservedCentralAccept;
        private readonly Stopwatch _centralAcceptSettling = Stopwatch.StartNew();
        private bool _workerFallbackAccepted;

        // Prevent one persistently broken batch from being requeued forever in one process.
        private readonly HashSet<string> _requeuedThisProcess =
            new HashSet<string>(StringComparer.Ordinal);

        public override void Init(string pluginDir)
        {
            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            // A census must not run accounting, handshake, or recovery writers.
            if (ServicePolicy.IsBagAuditMode())
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _config = LoadConfig();
            if (_config == null || _config.Roles == null)
            {
                Logger.Warning("INTERNAL TRADE BRIDGE disabled: the Bankers section could not be read.");
                return;
            }

            BridgeRole central;
            if (!TryGetRole("central", out central) ||
                central == null ||
                string.IsNullOrWhiteSpace(central.Character))
            {
                Logger.Warning("INTERNAL TRADE BRIDGE disabled: Central mapping is unavailable.");
                return;
            }

            _centralCharacter = central.Character;
            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role))
                return;

            _isCentral = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);
            _enabled = true;
            _nextReadyHeartbeatUtc = DateTime.MinValue;
            _nextRecoveryPollUtc = DateTime.UtcNow.AddSeconds(RecoveryPollSeconds);

            Trade.TradeOpened += OnTradeOpened;
            Trade.TradeStatusChanged += OnTradeStatusChanged;
            Client.OnUpdate += Tick;

            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "INTERNAL TRADE BRIDGE initialized; central=" + _isCentral + ".");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Trade.TradeOpened -= OnTradeOpened;
            Trade.TradeStatusChanged -= OnTradeStatusChanged;
            Client.OnUpdate -= Tick;

            if (!_isCentral)
                DeleteOwnReadyMarker();
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || !Client.InPlay)
                return;

            try
            {
                if (_isCentral)
                {
                    // Central recovery is owned by physical census and BankingService.
                    return;
                }

                PublishWorkerReadiness();
                TickWorkerFallback();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"INTERNAL TRADE BRIDGE tick failed character={Client.CharacterName}: {ex}");
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    _role,
                    "INTERNAL TRADE BRIDGE ERROR: " + ex);
            }
        }

        private void PublishWorkerReadiness()
        {
            if (_isCentral || DateTime.UtcNow < _nextReadyHeartbeatUtc)
                return;

            _nextReadyHeartbeatUtc = DateTime.UtcNow.AddSeconds(ReadyHeartbeatSeconds);

            PlayerChar central = DynelManager.Players.FirstOrDefault(p =>
                p != null && string.Equals(
                    p.Name,
                    _centralCharacter,
                    StringComparison.OrdinalIgnoreCase));

            bool bankOpen = Inventory.Bank != null && Inventory.Bank.IsOpen;
            if (central == null || !bankOpen)
            {
                DeleteOwnReadyMarker();
                return;
            }

            WorkerReadyMarker marker = new WorkerReadyMarker
            {
                ProcessId = Process.GetCurrentProcess().Id,
                Character = Client.CharacterName,
                Role = _role,
                CentralCharacter = _centralCharacter,
                CentralIdentity = central.Identity.ToString(),
                BankOpen = true,
                Utc = DateTime.UtcNow
            };

            File.WriteAllText(
                GetReadyMarkerPath(_settingsDir, Client.CharacterName),
                JsonConvert.SerializeObject(marker, Formatting.None));
        }

        private void OnTradeOpened(Identity target)
        {
            if (!StartupCensusGate.IsOpen) return;
            if (!_enabled || _isCentral || !Client.InPlay)
                return;

            string targetName = FindPlayerName(target);
            if (!string.Equals(
                    targetName,
                    _centralCharacter,
                    StringComparison.OrdinalIgnoreCase))
            {
                ResetWorkerTrade();
                return;
            }

            DispatchCommand command = BankingServiceAgent.CurrentInboundDispatch;
            if (!IsMatchingWorkerCommand(command))
            {
                ResetWorkerTrade();
                return;
            }

            _workerCentralTradeOpen = true;
            _workerBatchId = command.BatchId;
            _workerObservedCentralAccept = false;
            _workerFallbackAccepted = false;

            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "INTERNAL TRADE BRIDGE armed for batch=" + command.BatchId +
                "; expectedItems=" + (command.Items?.Count ?? 0) + ".");
        }

        private void OnTradeStatusChanged(Identity target, TradeStatus status)
        {
            if (!StartupCensusGate.IsOpen) return;
            if (!_enabled || _isCentral || !_workerCentralTradeOpen)
                return;

            if (status == TradeStatus.Accept)
            {
                if (!_workerObservedCentralAccept) _centralAcceptSettling.Restart();
                _workerObservedCentralAccept = true;
                return;
            }

            if (status == TradeStatus.Finished || status == TradeStatus.Declined)
                ResetWorkerTrade();
        }

        private void TickWorkerFallback()
        {
            if (!_workerCentralTradeOpen || _workerFallbackAccepted || !Trade.IsTrading)
                return;

            DispatchCommand command = BankingServiceAgent.CurrentInboundDispatch;
            if (!IsMatchingWorkerCommand(command) ||
                !string.Equals(command.BatchId, _workerBatchId, StringComparison.Ordinal))
            {
                ResetWorkerTrade();
                return;
            }

            // Normal path: when AOSharp exposes the complete remote item count,
            // BankingServiceAgent performs exact matching and accepts. This bridge stays
            // out of the way. A non-empty but incomplete cache is the same known AOSharp
            // observation failure as an empty cache once trusted Central has accepted.
            int visibleRemoteItems = Trade.TargetWindowCache?.Items?.Count ?? 0;
            int expectedItems = command.Items?.Count ?? 0;
            if (visibleRemoteItems == expectedItems)
                return;

            // Fallback path: the matching trade's status event reported Accept, meaning
            // configured Central accepted its own outgoing window. Trade.Status is the
            // worker's local status and does not reliably retain that remote event.
            // BankingServiceAgent on Central only accepts after its PlayerWindowCache
            // exactly matches the persisted dispatch.
            // The receiver cache may nevertheless remain empty or incomplete in
            // AOSharp.Clientless.
            if (!_workerObservedCentralAccept || _centralAcceptSettling.ElapsedMilliseconds < 500)
                return;

            _workerFallbackAccepted = true;
            Trade.Accept();

            string message =
                "Worker " + Client.CharacterName +
                " accepted trusted Central batch " + ShortId(command.BatchId) +
                " using incomplete-target-cache fallback " + visibleRemoteItems +
                "/" + expectedItems + " after Central accepted; " +
                "AO completion and post-trade inventory verification remain authoritative.";
            Logger.Information("[CityBankers] " + message);
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                message);
            RuntimeStateStore.AppendLedger(
                _settingsDir,
                new LedgerRecord
                {
                    Utc = DateTime.UtcNow,
                    Event = "worker_trade_cache_fallback_accept",
                    TransactionId = command.TransactionId,
                    BatchId = command.BatchId,
                    Actor = Client.CharacterName,
                    Role = _role,
                    Character = Client.CharacterName,
                    Source = _centralCharacter,
                    Destination = Client.CharacterName,
                    Message = message,
                    Items = ToLedgerItems(command.Items, _role)
                });
        }

        private void TickCentralRecovery()
        {
            if (DateTime.UtcNow < _nextRecoveryPollUtc)
                return;

            _nextRecoveryPollUtc = DateTime.UtcNow.AddSeconds(RecoveryPollSeconds);
            RequeueVerifiedReturnedTimeoutBatches();
        }

        private void RequeueVerifiedReturnedTimeoutBatches()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            List<DispatchBatchState> failed = (queue.Batches ?? new List<DispatchBatchState>())
                .Where(batch =>
                    batch != null &&
                    string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(batch.LastError) &&
                    batch.LastError.IndexOf(
                        "Internal worker trade timed out",
                        StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !_requeuedThisProcess.Contains(batch.BatchId ?? string.Empty))
                .ToList();

            if (failed.Count == 0)
                return;

            int requeued = 0;
            int waitingForWorker = 0;
            int unresolved = 0;
            foreach (DispatchBatchState batch in failed)
            {
                if (!IsWorkerReady(batch))
                {
                    waitingForWorker++;
                    continue;
                }

                string verificationError;
                if (!ExpectedItemsArePresentInCentralInventory(
                        batch.Items,
                        out verificationError))
                {
                    unresolved++;
                    RuntimeStateStore.AppendActivity(
                        _settingsDir,
                        Client.CharacterName,
                        _role,
                        "FAILED DISPATCH NOT REQUEUED batch=" + batch.BatchId +
                        ": " + verificationError);
                    continue;
                }

                batch.Status = "queued";
                batch.UpdatedUtc = DateTime.UtcNow;
                batch.LastError = null;
                _requeuedThisProcess.Add(batch.BatchId ?? string.Empty);
                requeued++;

                RuntimeStateStore.AppendLedger(
                    _settingsDir,
                    new LedgerRecord
                    {
                        Utc = DateTime.UtcNow,
                        Event = "dispatch_requeued_after_verified_return",
                        TransactionId = batch.TransactionId,
                        BatchId = batch.BatchId,
                        Actor = Client.CharacterName,
                        Role = batch.Role,
                        Character = Client.CharacterName,
                        Source = Client.CharacterName,
                        Destination = batch.Character,
                        Message =
                            "Prior internal trade timed out; destination worker published a fresh " +
                            "same-process readiness heartbeat and every expected item was physically " +
                            "observed back in Central normal inventory. The durable batch was safely " +
                            "re-queued with its original transaction and batch IDs.",
                        Items = ToLedgerItems(batch.Items, batch.Role)
                    });
            }

            if (requeued > 0)
            {
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                string summary =
                    "Dispatch recovery: requeued=" + requeued +
                    " ready+verified-return batch(es), unresolved=" + unresolved +
                    ", waitingWorker=" + waitingForWorker +
                    ". Existing transaction/batch IDs were preserved.";
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    _role,
                    summary);
                TellKavem(summary);
            }
        }

        private bool IsWorkerReady(DispatchBatchState batch)
        {
            if (batch == null || string.IsNullOrWhiteSpace(batch.Character))
                return false;

            WorkerReadyMarker marker;
            try
            {
                string path = GetReadyMarkerPath(_settingsDir, batch.Character);
                if (!File.Exists(path))
                    return false;
                marker = JsonConvert.DeserializeObject<WorkerReadyMarker>(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }

            if (marker == null ||
                marker.ProcessId != Process.GetCurrentProcess().Id ||
                !marker.BankOpen ||
                !string.Equals(marker.Character, batch.Character, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(marker.Role, batch.Role, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    marker.CentralCharacter,
                    Client.CharacterName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            double age = (DateTime.UtcNow - marker.Utc.ToUniversalTime()).TotalSeconds;
            return age >= -1 && age <= ReadyHeartbeatMaxAgeSeconds;
        }

        private bool ExpectedItemsArePresentInCentralInventory(
            List<TransferItemState> expected,
            out string error)
        {
            error = null;
            expected = expected ?? new List<TransferItemState>();
            if (expected.Count == 0)
            {
                error = "Batch has no expected items.";
                return false;
            }

            IEnumerable<Item> inventoryItems = Inventory.Items != null
                ? Inventory.Items
                : Enumerable.Empty<Item>();
            List<Item> available = inventoryItems
                .Where(item =>
                    item != null &&
                    item.Slot.Type == IdentityType.Inventory)
                .ToList();

            foreach (TransferItemState wanted in expected)
            {
                int index = FindMatchingInventoryItemIndex(available, wanted);
                if (index < 0)
                {
                    error =
                        "Expected item occurrence is not physically present on Central: " +
                        (wanted?.Name ?? "<unknown>") +
                        " AOID=" + (wanted?.AoId ?? 0) +
                        " QL=" + (wanted?.Ql ?? 0) + ".";
                    return false;
                }
                available.RemoveAt(index);
            }

            return true;
        }

        private static int FindMatchingInventoryItemIndex(
            List<Item> available,
            TransferItemState wanted)
        {
            if (wanted == null)
                return -1;

            if (!string.IsNullOrWhiteSpace(wanted.UniqueIdentity) &&
                !string.Equals(
                    wanted.UniqueIdentity,
                    Identity.None.ToString(),
                    StringComparison.Ordinal))
            {
                int unique = available.FindIndex(item => string.Equals(
                    item.UniqueIdentity.ToString(),
                    wanted.UniqueIdentity,
                    StringComparison.Ordinal));
                if (unique >= 0)
                    return unique;
            }

            List<int> candidates = available
                .Select((item, index) => new { item, index })
                .Where(pair =>
                    pair.item.Id == wanted.AoId &&
                    pair.item.HighId == wanted.HighId &&
                    pair.item.Ql == wanted.Ql)
                .Select(pair => pair.index)
                .ToList();

            return candidates.Count > 0 ? candidates[0] : -1;
        }

        private bool IsMatchingWorkerCommand(DispatchCommand command)
        {
            return !WithdrawalStore.LoadAll(_settingsDir).Any(row =>
                WithdrawalStore.HasStatus(row, "extracting") &&
                string.Equals(row.SourceCharacter, Client.CharacterName, StringComparison.OrdinalIgnoreCase)) &&
                command != null &&
                !string.IsNullOrWhiteSpace(command.BatchId) &&
                string.Equals(command.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    command.SourceCharacter,
                    _centralCharacter,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    command.DestinationCharacter,
                    Client.CharacterName,
                    StringComparison.OrdinalIgnoreCase) &&
                (command.Items?.Count ?? 0) > 0;
        }

        private BridgeConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<BridgeConfig>();
            }
            catch
            {
                return null;
            }
        }

        private string ResolveCurrentRole()
        {
            foreach (KeyValuePair<string, BridgeRole> pair in _config.Roles)
            {
                if (pair.Value != null && string.Equals(
                        pair.Value.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }
            return null;
        }

        private bool TryGetRole(string role, out BridgeRole value)
        {
            foreach (KeyValuePair<string, BridgeRole> pair in _config.Roles)
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

        private static string FindPlayerName(Identity identity)
        {
            PlayerChar player = DynelManager.Players.FirstOrDefault(p =>
                p != null && p.Identity == identity);
            return player?.Name;
        }

        private static string GetReadyMarkerPath(string settingsDir, string character)
        {
            string safe = string.Concat((character ?? "unknown").Where(char.IsLetterOrDigit));
            if (string.IsNullOrWhiteSpace(safe))
                safe = "unknown";
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(settingsDir),
                "citybankers-service-ready-" + safe + ".json");
        }

        private void DeleteOwnReadyMarker()
        {
            try
            {
                string path = GetReadyMarkerPath(_settingsDir, Client.CharacterName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private void TellKavem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                TrustedOperators.BootstrapAdmin,
                message);
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "TELL -> Kavem: " + message);
        }

        private static List<LedgerItem> ToLedgerItems(
            IEnumerable<TransferItemState> items,
            string role)
        {
            return (items ?? Enumerable.Empty<TransferItemState>())
                .Select(item => new LedgerItem
                {
                    UniqueIdentity = item?.UniqueIdentity,
                    AoId = item?.AoId ?? 0,
                    HighId = item?.HighId ?? 0,
                    Ql = item?.Ql ?? 0,
                    Name = item?.Name,
                    Role = role
                })
                .ToList();
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";
            return value.Length <= 12 ? value : value.Substring(value.Length - 8);
        }

        private void ResetWorkerTrade()
        {
            _workerCentralTradeOpen = false;
            _workerBatchId = null;
            _workerObservedCentralAccept = false;
            _workerFallbackAccepted = false;
        }

        private sealed class WorkerReadyMarker
        {
            public string Format = "citybankers-worker-ready-v1";
            public int ProcessId;
            public string Character;
            public string Role;
            public string CentralCharacter;
            public string CentralIdentity;
            public bool BankOpen;
            public DateTime Utc;
        }

        private sealed class BridgeConfig
        {
            public Dictionary<string, BridgeRole> Roles;
        }

        private sealed class BridgeRole
        {
            public string Username;
            public string Character;
        }
    }
}
