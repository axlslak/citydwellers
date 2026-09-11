using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Public, read-only tell surface. Donation/storage locks protect physical mutation;
    /// they must not make the banker stop answering harmless questions.
    ///
    /// Kavem remains the administrator and is handled by BankingServiceAgent. This agent
    /// only serves non-admin players so existing admin replies are not duplicated.
    /// </summary>
    public class PublicReadOnlyCommandAgent : ClientlessPluginEntry
    {
        private string _settingsDir;
        private bool _enabled;

        public override void Init(string pluginDir)
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            if (!CentralCharacterGuard.IsCurrentCharacterCentral(
                _settingsDir,
                Client.CharacterName))
            {
                return;
            }

            _enabled = true;
            if (Client.Chat != null)
                Client.Chat.PrivateMessageReceived += OnPrivateMessage;

            Logger.Information("[CityBankers] PUBLIC READ-ONLY COMMANDS initialized on Central.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;
            if (Client.Chat != null)
                Client.Chat.PrivateMessageReceived -= OnPrivateMessage;
        }

        private void OnPrivateMessage(object sender, PrivateMessage message)
        {
            if (!_enabled || message == null ||
                TrustedOperators.IsTrustedAdmin(message.SenderName))
            {
                return;
            }

            try
            {
                string text = (message.Message ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return;

                string command = text.ToLowerInvariant();
                if (command == "stock" || command.StartsWith("stock ") ||
                    command == "don" || command == "donor")
                {
                    TellPlayer(
                        message.SenderName,
                        "Please ask " + SettingsPaths.ReadManagerCharacter(_settingsDir) +
                        " using #stock or #donor. Public commands and AP membership " +
                        "are handled there.");
                    return;
                }

                if (command == "queue" || command == "que")
                {
                    TellPlayer(message.SenderName, ReadOnlyCommandRouter.BuildQueueResponse(_settingsDir));
                    return;
                }

            }
            catch (Exception ex)
            {
                Logger.Error("[CityBankers] PUBLIC READ-ONLY COMMAND handling failed: " + ex);
            }
        }

        private void TellPlayer(string playerName, string message)
        {
            if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(message))
                return;
            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                playerName,
                CityBankersChatPalette.WhiteBaseMarkup(message));
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                "central",
                "PUBLIC TELL -> " + playerName + ": " + message);
        }
    }

    /// <summary>
    /// Recovers only failures known to have happened before AO completed the Central ->
    /// worker transfer. Recovery is allowed only when the complete expected multiset is
    /// physically visible in Central's normal inventory and there is no post-transfer
    /// evidence. Storage failures remain hard failures and are never touched here.
    /// </summary>
    public class PreTransferDispatchRecoveryAgent : ClientlessPluginEntry
    {
        private const int PollMilliseconds = 500;

        private string _settingsDir;
        private bool _enabled;
        private DateTime _nextPollUtc;
        private readonly HashSet<string> _attemptedBatchIds =
            new HashSet<string>(StringComparer.Ordinal);

        public override void Init(string pluginDir)
        {
            if (StartupCensusGate.UsesPhysicalRecovery) return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            if (!CentralCharacterGuard.IsCurrentCharacterCentral(
                _settingsDir,
                Client.CharacterName))
            {
                return;
            }

            _enabled = true;
            _nextPollUtc = DateTime.UtcNow;
            Client.OnUpdate += Tick;
            Logger.Information("[CityBankers] PRE-TRANSFER DISPATCH RECOVERY initialized on Central.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;
            Client.OnUpdate -= Tick;
            _attemptedBatchIds.Clear();
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!StartupCensusGate.IsOpen) return;
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;
            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (Trade.IsTrading)
                    return;

                DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
                DispatchBatchState batch = (queue?.Batches ?? new List<DispatchBatchState>())
                    .FirstOrDefault(candidate =>
                        candidate != null &&
                        !string.IsNullOrWhiteSpace(candidate.BatchId) &&
                        !_attemptedBatchIds.Contains(candidate.BatchId) &&
                        string.Equals(candidate.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                        IsRecoverablePreTransferFailure(candidate.LastError));
                if (batch == null || batch.Items == null || batch.Items.Count == 0)
                    return;

                // A live/stale command for this worker means the worker may still believe
                // it owns an active receive attempt. Do not guess across that boundary.
                if (File.Exists(RuntimeStateStore.GetDispatchCommandPath(
                    _settingsDir,
                    batch.Character)))
                {
                    return;
                }

                StorageBatchResult result = RuntimeStateStore.ReadStorageResult(
                    _settingsDir,
                    batch.Character);
                if (result != null &&
                    string.Equals(result.BatchId, batch.BatchId, StringComparison.Ordinal) &&
                    !IsWorkerPreReceiptFailure(result))
                {
                    return;
                }

                if (!HasExpectedMultisetInCentral(batch.Items))
                    return;

                string priorError = batch.LastError;
                List<TransferItemState> originalItems = batch.Items.ToList();
                int splitCount = SplitOversizedBatch(queue, batch);
                batch.Status = "queued";
                batch.UpdatedUtc = DateTime.UtcNow;
                batch.LastError =
                    "Recovered pre-transfer failure after verifying the complete expected " +
                    "item multiset in Central normal inventory. Prior error: " + priorError;
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
                _attemptedBatchIds.Add(batch.BatchId);

                RuntimeStateStore.AppendLedger(
                    _settingsDir,
                    new LedgerRecord
                    {
                        Utc = DateTime.UtcNow,
                        Event = "dispatch_requeued_after_return_verified",
                        TransactionId = batch.TransactionId,
                        BatchId = batch.BatchId,
                        Actor = Client.CharacterName,
                        Role = batch.Role,
                        Character = Client.CharacterName,
                        Source = "central-normal-inventory",
                        Destination = "dispatch-queue",
                        Message =
                            "Known pre-transfer failure recovered only after Central physically " +
                            "verified every expected occurrence. Prior error: " + priorError,
                        Items = originalItems.Select(item => new LedgerItem
                        {
                            UniqueIdentity = item?.UniqueIdentity,
                            AoId = item?.AoId ?? 0,
                            HighId = item?.HighId ?? 0,
                            Ql = item?.Ql ?? 0,
                            Name = item?.Name,
                            Role = batch.Role
                        }).ToList()
                    });

                string notice =
                    "Recovered failed " + batch.Role + " batch " + ShortId(batch.BatchId) +
                    ": verified " + originalItems.Count +
                    " expected item(s) back in Central inventory; " +
                    (splitCount > 1
                        ? "split into " + splitCount + " safe internal batches and requeued."
                        : "requeued for a clean retry.");
                Logger.Information("[CityBankers] " + notice);
                TellKavem(notice);
            }
            catch (Exception ex)
            {
                Logger.Error("[CityBankers] PRE-TRANSFER DISPATCH RECOVERY failed: " + ex);
            }
        }

        private static bool IsRecoverablePreTransferFailure(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return string.Equals(
                       error,
                       "Internal worker trade was declined. AO should have returned the offered items to Central inventory.",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       error,
                       "Routed item multiset changed before it could be added to worker trade.",
                       StringComparison.Ordinal) ||
                   error.StartsWith(
                       "Internal worker trade timed out. Any incomplete outgoing AO trade is declined so offered items return to Central inventory.",
                       StringComparison.Ordinal);
        }

        private static int SplitOversizedBatch(
            DispatchQueueState queue,
            DispatchBatchState batch)
        {
            if (queue == null || batch == null || batch.Items == null ||
                batch.Items.Count <= ServicePolicy.MaxInternalTradeItems)
            {
                return 1;
            }

            List<TransferItemState> original = batch.Items.ToList();
            int batchIndex = queue.Batches.IndexOf(batch);
            int splitCount = (original.Count + ServicePolicy.MaxInternalTradeItems - 1) /
                ServicePolicy.MaxInternalTradeItems;
            batch.Items = original.Take(ServicePolicy.MaxInternalTradeItems).ToList();

            for (int part = 1; part < splitCount; part++)
            {
                DateTime createdUtc = DateTime.UtcNow;
                queue.Batches.Insert(batchIndex + part, new DispatchBatchState
                {
                    BatchId = "batch-" + Guid.NewGuid().ToString("N"),
                    TransactionId = batch.TransactionId,
                    Role = batch.Role,
                    Character = batch.Character,
                    Status = "queued",
                    CreatedUtc = createdUtc,
                    UpdatedUtc = createdUtc,
                    AttemptCount = 0,
                    Items = original
                        .Skip(part * ServicePolicy.MaxInternalTradeItems)
                        .Take(ServicePolicy.MaxInternalTradeItems)
                        .ToList()
                });
            }

            return splitCount;
        }

        private static bool IsWorkerPreReceiptFailure(StorageBatchResult result)
        {
            if (result == null || result.Success || result.StoredCount != 0 ||
                string.IsNullOrWhiteSpace(result.Error))
            {
                return false;
            }

            return string.Equals(
                       result.Error,
                       "Internal Central trade was declined before completion.",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       result.Error,
                       "Timed out waiting for expected Central trade contents.",
                       StringComparison.Ordinal);
        }

        private static bool HasExpectedMultisetInCentral(
            IEnumerable<TransferItemState> expectedItems)
        {
            List<Item> available = Inventory.Items == null
                ? new List<Item>()
                : Inventory.Items
                    .Where(item => item != null && item.Slot.Type == IdentityType.Inventory)
                    .OrderBy(item => item.Slot.Instance)
                    .ToList();

            foreach (TransferItemState expected in
                expectedItems ?? Enumerable.Empty<TransferItemState>())
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
                    return false;
                available.RemoveAt(index);
            }
            return true;
        }

        private static bool IsUsableIdentity(string identity)
        {
            return !string.IsNullOrWhiteSpace(identity) &&
                !string.Equals(identity, Identity.None.ToString(), StringComparison.Ordinal);
        }

        private void TellKavem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                TrustedOperators.BootstrapAdmin,
                CityBankersChatPalette.WhiteBaseMarkup(message));
            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                "central",
                "TELL -> " + TrustedOperators.BootstrapAdmin + ": " + message);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";
            return value.Length <= 12 ? value : value.Substring(value.Length - 8);
        }
    }

    internal static class ReadOnlyCommandRouter
    {
        public static string BuildQueueResponse()
        {
            string settingsDir;
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out error))
                return "CityBankers queue is unavailable: " + error;
            return BuildQueueResponse(settingsDir);
        }

        public static string BuildQueueResponse(string settingsDir)
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(settingsDir);
            List<DispatchBatchState> batches = queue?.Batches ?? new List<DispatchBatchState>();
            if (batches.Count == 0)
                return "CityBankers queue: empty; donations=open.";

            string detail = string.Join(
                ", ",
                batches.Take(5).Select(batch =>
                    (batch.Role ?? "?") + "/" +
                    (batch.Items?.Count ?? 0) + "/" +
                    (batch.Status ?? "unknown")));
            if (batches.Count > 5)
                detail += " ...";

            return "CityBankers queue: " + detail + "; donations=locked.";
        }
    }

    internal static class CentralCharacterGuard
    {
        public static bool IsCurrentCharacterCentral(
            string settingsDir,
            string currentCharacter)
        {
            try
            {
                JObject config = SettingsPaths.ReadBankersSettings(settingsDir);
                JObject roles = config.GetValue("Roles", StringComparison.OrdinalIgnoreCase) as JObject;
                JObject central = roles?.Properties()
                    .FirstOrDefault(property => string.Equals(
                        property.Name,
                        "central",
                        StringComparison.OrdinalIgnoreCase))?.Value as JObject;
                string character = central?.GetValue(
                    "Character",
                    StringComparison.OrdinalIgnoreCase)?.ToString();
                return string.Equals(
                    character,
                    currentCharacter,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
