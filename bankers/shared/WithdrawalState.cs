using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers.Shared
{
    public sealed class WithdrawalState
    {
        public string Format = "citybankers-withdrawal-v2";
        public string Id;
        public string OrderId;
        public long Revision;
        public int RecoveryAttempts;
        public int LiveInventoryRecoveryAttempts;
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
        public List<int> PreExtractionInventorySlots = new List<int>();
        public string ExtractedItemIdentity;
        public string CentralItemIdentity;
        public string ReturnBatchId;
        public string Error;
        public TransferItemState Item;
    }

    public sealed class WithdrawalQueueState
    {
        public string Format = "citybankers-withdrawal-queue-v2";
        public List<WithdrawalState> Withdrawals = new List<WithdrawalState>();
    }

    public static class WithdrawalStore
    {
        public const int PickupSeconds = 180;
        public const int MaximumOrders = 4;
        public const int MaximumOrderItems = 3;
        public const string FileName = "withdrawal.json";
        private const string MutexName = "CityBankers.Withdrawal.v1";

        public static string GetPath(string directory)
        {
            return Path.Combine(RuntimeStateStore.GetDataDirectory(directory), FileName);
        }

        // All readers fail closed: unreadable state is never an empty bank queue.
        private static List<WithdrawalState> Read(string directory)
        {
            string path = GetPath(directory);
            if (!File.Exists(path)) return new List<WithdrawalState>();
            JObject root = JObject.Parse(File.ReadAllText(path));
            List<WithdrawalState> rows;
            if (root["Withdrawals"] != null)
            {
                if ((string)root["Format"] != "citybankers-withdrawal-queue-v2")
                    throw new InvalidDataException("Unknown withdrawal queue format.");
                rows = root["Withdrawals"].ToObject<List<WithdrawalState>>();
            }
            else
            {
                string format = (string)root["Format"];
                if (format != "citybankers-withdrawal-v1" && format != "citybankers-withdrawal-v2")
                    throw new InvalidDataException("Unknown withdrawal state format.");
                rows = new List<WithdrawalState> { root.ToObject<WithdrawalState>() };
            }
            if (rows == null || rows.Any(row => row == null || string.IsNullOrWhiteSpace(row.Id)))
                throw new InvalidDataException("Invalid withdrawal queue.");
            if (rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                throw new InvalidDataException("Duplicate withdrawal IDs.");
            foreach (WithdrawalState row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.OrderId)) row.OrderId = row.Id;
                row.CreatedUtc = NormalizeUtc(row.CreatedUtc);
                row.UpdatedUtc = NormalizeUtc(row.UpdatedUtc);
                if (row.PickupExpiresUtc.HasValue) row.PickupExpiresUtc = NormalizeUtc(row.PickupExpiresUtc.Value);
                if (row.DeliveredUtc.HasValue) row.DeliveredUtc = NormalizeUtc(row.DeliveredUtc.Value);
            }
            return rows;
        }

        private static T Locked<T>(Func<T> action)
        {
            using (var mutex = new Mutex(false, MutexName))
            {
                bool entered = false;
                try
                {
                    try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (!entered) throw new TimeoutException("Withdrawal queue is busy.");
                    return action();
                }
                finally { if (entered) mutex.ReleaseMutex(); }
            }
        }

        public static List<WithdrawalState> LoadAll(string directory)
        {
            return Locked(() => Read(directory));
        }

        public static WithdrawalState Load(string directory)
        {
            return LoadAll(directory).FirstOrDefault(IsActive);
        }

        public static bool HasStatus(WithdrawalState row, string status)
        {
            return row != null && string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase);
        }

        // Mutate against current state, not a stale copy held by another client domain.
        public static T Update<T>(string directory, Func<List<WithdrawalState>, T> action)
        {
            return Locked(() =>
            {
                List<WithdrawalState> rows = Read(directory);
                string before = JsonConvert.SerializeObject(rows);
                T result = action(rows);
                if (!string.Equals(before, JsonConvert.SerializeObject(rows), StringComparison.Ordinal))
                    RuntimeStateStore.WriteJsonAtomic(GetPath(directory),
                        new WithdrawalQueueState { Withdrawals = rows });
                return result;
            });
        }

        public static void Save(string directory, WithdrawalState state)
        {
            Update(directory, rows =>
            {
                WithdrawalState current = rows.FirstOrDefault(row => row.Id == state.Id);
                if (current == null || current.Revision != state.Revision)
                    throw new InvalidOperationException("Withdrawal changed concurrently; reload before continuing.");
                state.Revision++;
                state.UpdatedUtc = DateTime.UtcNow;
                rows[rows.IndexOf(current)] = state;
                return true;
            });
        }

        public static void Touch(WithdrawalState row)
        {
            row.Revision++;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        public static bool TryAdd(string directory, WithdrawalState request, out string error)
        {
            string reason = null;
            bool added = Update(directory, rows =>
            {
                List<WithdrawalState> active = rows.Where(IsActive).ToList();
                List<WithdrawalState> own = active.Where(row => string.Equals(
                    row.RecipientMain, request.RecipientMain, StringComparison.OrdinalIgnoreCase)).ToList();
                if (active.Any(row => row.ActiveLedgerId == request.ActiveLedgerId))
                    reason = "That copy was just reserved. Please select it again.";
                else if (own.Any(row => HasStatus(row, "pickup-trading")))
                    reason = "Finish your open pickup trade before adding another item.";
                else if (own.Count >= MaximumOrderItems)
                    reason = "Your order already has three items. Collect the ready items first.";
                else if (own.Count == 0 && active.Select(row => row.OrderId).Distinct().Count() >= MaximumOrders)
                    reason = "All four pickup orders are occupied. Please try again after a pickup.";
                if (reason != null) return false;
                request.OrderId = own.Count > 0 ? own[0].OrderId : "order-" + Guid.NewGuid().ToString("N");
                request.Revision = 1;
                request.UpdatedUtc = DateTime.UtcNow;
                DateTime expires = DateTime.UtcNow.AddSeconds(PickupSeconds);
                foreach (WithdrawalState row in own.Where(row => HasStatus(row, "central-ready")))
                {
                    row.PickupExpiresUtc = expires;
                    Touch(row);
                }
                rows.Add(request);
                return true;
            });
            error = reason;
            return added;
        }

        public static bool IsTerminal(WithdrawalState state)
        {
            return state == null || HasStatus(state, "completed") || HasStatus(state, "expired");
        }

        public static bool IsActive(WithdrawalState state) { return state != null && !IsTerminal(state); }

        public static bool IsAllowedCollector(WithdrawalState state, string character)
        {
            return state != null && !string.IsNullOrWhiteSpace(character) &&
                (state.AllowedCharacters ?? new List<string>()).Any(name =>
                    string.Equals(name, character, StringComparison.OrdinalIgnoreCase));
        }

        public static bool OwnsCentralTrade(WithdrawalState state)
        {
            return HasStatus(state, "extracting") || HasStatus(state, "pickup-trading") ||
                HasStatus(state, "central-received");
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
