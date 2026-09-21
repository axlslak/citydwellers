using System;
using System.Linq;
using CityBankers;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    // Source adapter for older callers that use JObject for presentation.
    // The actual data and all mutations belong to Manager's typed RAM state.
    public static class BankerState
    {
        private static T Adapt<T>(object value) where T : class => value == null ? null :
            value as T ?? JObject.FromObject(value).ToObject<T>();
        public static T ReadLedger<T>() where T : class =>
            Adapt<T>(ManagerMemory.Current.ReadLedger(ManagerAccounting.TransactionId));
        public static T ReadStock<T>() where T : class =>
            Adapt<T>(ManagerMemory.Current.ReadStock(ManagerAccounting.TransactionId));
        public static T ReadLedgerForCharacter<T>(string character) where T : class
        {
            var ledger = ManagerMemory.Current.ReadLedger(ManagerAccounting.TransactionId);
            if (ledger != null) ledger.Items = ledger.Items.Where(item =>
                string.Equals(item.Character, character, StringComparison.OrdinalIgnoreCase)).ToList();
            return Adapt<T>(ledger);
        }
        public static void SaveLedger(object state) => ManagerAccounting.Transaction("Ledger", () =>
            ManagerMemory.Current.ChangeLedger(ManagerAccounting.TransactionId, Adapt<ActiveLedgerState>(state)));
        public static void SaveStock(object state) => ManagerAccounting.Transaction("Stock", () =>
            ManagerMemory.Current.ChangeStock(ManagerAccounting.TransactionId, Adapt<CurrentStockState>(state)));
        public static T ReadItemIndex<T>() where T : class => Adapt<T>(ManagerMemory.Current.ReadItemIndex(ManagerAccounting.TransactionId));
        public static long LedgerRevision => ManagerMemory.Current.GetLedgerRevision(ManagerAccounting.TransactionId);
        public static JArray LedgerTemplatePairs()
        {
            var ledger = ManagerMemory.Current.ReadLedger(ManagerAccounting.TransactionId);
            return new JArray((ledger?.Items ?? new System.Collections.Generic.List<ActiveLedgerItem>())
                .Where(item => item.HighId.HasValue && item.AoId != item.HighId)
                .Select(item => new { item.AoId, item.HighId }).Distinct().Select(item =>
                    new JObject { ["AoId"] = item.AoId, ["HighId"] = item.HighId }));
        }
    }
}
