using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CityBankers.Shared
{
    public sealed class WithdrawalState
    {
        public string Format = "citybankers-withdrawal-v1";
        public string Id;
        public string Status;
        public DateTime CreatedUtc;
        public DateTime UpdatedUtc;
        public DateTime? PickupExpiresUtc;
        public DateTime? DeliveredUtc;
        public string RequestedBy;
        public string RecipientMain;
        public List<string> AllowedCharacters = new List<string>();
        public string ActiveLedgerId;
        public string DonationTransactionId;
        public string SourceRole;
        public string SourceCharacter;
        public string SourceBag;
        public int SourceBagOuterSlot;
        public int SourceInnerSlot;
        public string SourceItemIdentity;
        public string ReturnBatchId;
        public string Error;
        public TransferItemState Item;
    }

    public static class WithdrawalStore
    {
        public const int PickupSeconds = 180;
        public const string FileName = "withdrawal.json";
        private const string MutexName = "CityBankers.Withdrawal.v1";

        public static string GetPath(string settingsDir)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(settingsDir), FileName);
        }

        public static WithdrawalState Load(string settingsDir)
        {
            WithdrawalState state = RuntimeStateStore.ReadJson<WithdrawalState>(GetPath(settingsDir));
            if (state != null)
            {
                state.CreatedUtc = NormalizeUtc(state.CreatedUtc);
                state.UpdatedUtc = NormalizeUtc(state.UpdatedUtc);
                if (state.PickupExpiresUtc.HasValue)
                    state.PickupExpiresUtc = NormalizeUtc(state.PickupExpiresUtc.Value);
                if (state.DeliveredUtc.HasValue)
                    state.DeliveredUtc = NormalizeUtc(state.DeliveredUtc.Value);
            }
            return state;
        }

        public static void Save(string settingsDir, WithdrawalState state)
        {
            if (state == null)
                throw new ArgumentNullException("state");
            state.UpdatedUtc = DateTime.UtcNow;
            RuntimeStateStore.WriteJsonAtomic(GetPath(settingsDir), state);
        }

        public static bool TryCreate(
            string settingsDir,
            WithdrawalState state,
            out WithdrawalState existing)
        {
            existing = null;
            using (var mutex = new Mutex(false, MutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new TimeoutException("Timed out acquiring the withdrawal lock.");
                    existing = Load(settingsDir);
                    if (IsActive(existing))
                        return false;
                    Save(settingsDir, state);
                    return true;
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        public static bool IsTerminal(WithdrawalState state)
        {
            return state == null ||
                string.Equals(state.Status, "completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state.Status, "expired", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsActive(WithdrawalState state)
        {
            return state != null && !IsTerminal(state);
        }

        public static bool IsAllowedCollector(WithdrawalState state, string character)
        {
            if (state == null || string.IsNullOrWhiteSpace(character))
                return false;
            return (state.AllowedCharacters ?? new List<string>()).Exists(name =>
                string.Equals(name, character, StringComparison.OrdinalIgnoreCase));
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
