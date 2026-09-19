using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.GameData;

namespace CityBankers
{
    // Clientless drops inventory counts and native split bookkeeping. Keep counts
    // beside its Item objects. Merges use matched replies; splits reproduce the
    // observed native transition locally and are not server acknowledgements.
    internal static class StackableItems
    {
        private const CharacterActionType StackItemsAction = (CharacterActionType)53;
        private sealed class CountValue { public CountValue() { } public int Value; }
        private static ConditionalWeakTable<Item, CountValue> counts = new ConditionalWeakTable<Item, CountValue>();
        private static readonly Dictionary<Identity, int> identities = new Dictionary<Identity, int>();
        private static readonly Queue<Action> afterNative = new Queue<Action>();
        private static bool installed;
        private sealed class PendingMerge
        {
            public Item Survivor, Consumed;
            public Identity SurvivorSlot, ConsumedSlot, Character;
            public int SurvivorCount, ConsumedCount;
            public List<Item> Before;
            public List<int> BeforeCounts;
            public readonly Stopwatch Age = Stopwatch.StartNew();
        }
        private static PendingMerge pendingMerge;
        public static bool IsStack(Item item) => item != null && CruPolicy.IsCru(item.Id);
        public static int Quantity(Item item)
        {
            if (item == null) return 0;
            if (!IsStack(item)) return 1;
            return ObservedQuantity(item) ?? -1;
        }

        // This is descriptive only. IsStack/Quantity still enforce the existing CRU policy.
        // Both interpolation endpoints must agree; missing/conflicting facts remain unknown.
        public static bool? StackableAttribute(Item item)
        {
            if (item == null) return null;
            var catalog = CityDwellers.Shared.ItemCatalog.Current;
            if (catalog == null) return null;
            bool? low = catalog.Find(item.Id)?.Stackable;
            bool? high = item.HighId > 0 && item.HighId != item.Id
                ? catalog.Find(item.HighId)?.Stackable : low;
            return low.HasValue && high == low ? low : null;
        }

        // Raw server evidence for any item, independent of the CRU service policy.
        // Do not substitute the service Quantity() fallback of one for missing data.
        public static int? ObservedQuantity(Item item)
        {
            if (item == null) return null;
            int value;
            if (item.UniqueIdentity != Identity.None && identities.TryGetValue(item.UniqueIdentity, out value)) return value;
            CountValue count;
            if (counts.TryGetValue(item, out count)) return count.Value;
            var unique = item as UniqueItem;
            return unique?.Stats != null && unique.Stats.TryGetValue(Stat.MultipleCount, out value) ? (int?)value : null;
        }
        private static void Set(Item item, int count)
        {
            if (item == null || count < 0) return;
            counts.GetOrCreateValue(item).Value = count;
            if (item.UniqueIdentity != Identity.None) identities[item.UniqueIdentity] = count;
        }
        public static void Install()
        {
            if (installed) return;
            installed = true;
            Client.MessageReceived += Receive;
            Client.OnUpdate += Tick;
            Client.Disconnected += ForgetPendingMerge;
        }
        private static void ForgetPendingMerge()
        {
            pendingMerge = null;
        }

        public static void CancelMerge(Item survivor, Item consumed)
        {
            if (pendingMerge != null && ReferenceEquals(pendingMerge.Survivor, survivor) &&
                ReferenceEquals(pendingMerge.Consumed, consumed)) pendingMerge = null;
        }
        public static void Flush()
        {
            while (afterNative.Count > 0)
            {
                try { afterNative.Dequeue()(); }
                catch (Exception ex) { Logger.Warning("[CityBankers] STACK quantity binding: " + ex.Message); }
            }
        }
        private static void Tick(object sender, double delta) => Flush();
        private static IEnumerable<Item> AllItems() => (Inventory.Items ?? new List<Item>())
            .Concat(Trade.PlayerWindowCache?.Items ?? new List<Item>())
            .Concat(Trade.TargetWindowCache?.Items ?? new List<Item>());
        private static void SetIdentity(Identity identity, int count)
        {
            if (identity == Identity.None || count < 0) return;
            identities[identity] = count;
            foreach (var item in AllItems().Where(i => i != null && (i.UniqueIdentity == identity || i.Slot == identity)).ToList())
            {
                Set(item, count);
                if (count == 0 && IsStack(item)) afterNative.Enqueue(() =>
                {
                    // The server explicitly reports no units; retire only that live
                    // CRU object if native handling did not already remove it.
                    (Inventory.Items as ICollection<Item>)?.Remove(item);
                });
            }
        }
        private static void Receive(object sender, Message message)
        {
            try { Observe(message); }
            catch (Exception ex) { Logger.Warning("[CityBankers] STACK observation: " + ex.Message); }
        }
        private static void Observe(Message message)
        {
            // This hook precedes native handling. Drain the previous message's work
            // before inspecting the next one; OnUpdate drains the final message.
            Flush();
            if (message?.Body == null) return;
            var full = message.Body as FullCharacterMessage;
            if (full != null)
            {
                pendingMerge = null;
                counts = new ConditionalWeakTable<Item, CountValue>();
                identities.Clear();
                afterNative.Enqueue(() =>
                {
                    foreach (var slot in full.InventorySlots ?? new InventorySlot[0])
                    {
                        var item = Inventory.Items?.FirstOrDefault(i => i.Slot.Instance == slot.Placement);
                        // Wire field is 16 bits; full-client inventory reads it unsigned.
                        Set(item, (ushort)slot.Count);
                        if (IsStack(item))
                        {
                            Logger.Information("[CityBankers] STACK login slot=" + item.Slot + "; CRU count=" + (ushort)slot.Count);
                        }
                    }
                });
                return;
            }
            var update = message.Body as InventoryUpdateMessage;
            if (update != null)
            {
                foreach (var slot in update.Items ?? new InventorySlot[0])
                {
                    SetIdentity(slot.Identity, (ushort)slot.Count);
                    afterNative.Enqueue(() =>
                    {
                        var container = Inventory.Containers?.FirstOrDefault(c => c.Identity == update.InventoryIdentity);
                        foreach (var item in container?.Items ?? new List<Item>())
                            if (item.Id == slot.ItemLowId && (item.Slot.Instance & 65535) == (slot.Placement & 65535))
                                Set(item, (ushort)slot.Count);
                    });
                }
                return;
            }
            var add = message.Body as AddTemplateMessage;
            if (add != null)
            {
                var before = new HashSet<Item>(Inventory.Items ?? new List<Item>());
                afterNative.Enqueue(() =>
                {
                    var added = (Inventory.Items ?? new List<Item>()).Where(i => !before.Contains(i) &&
                        i.Id == add.LowId && i.HighId == add.HighId && i.Ql == add.Quality).ToList();
                    if (added.Count == 1) Set(added[0], add.Count);
                    if (CruPolicy.IsCru(add.LowId)) Logger.Information("[CityBankers] STACK add-template count=" + add.Count + "; new objects=" + added.Count);
                });
                return;
            }
            var simple = message.Body as SimpleItemFullUpdateMessage;
            if (simple != null)
            {
                var count = simple.Stats?.FirstOrDefault(s => s.Value1 == Stat.MultipleCount);
                if (count != null) SetIdentity(simple.Identity, count.Value2);
                return;
            }
            var stat = message.Body as StatMessage;
            if (stat != null)
            {
                if (!identities.ContainsKey(stat.Identity) && !AllItems().Any(i => i != null &&
                    (i.UniqueIdentity == stat.Identity || i.Slot == stat.Identity))) return;
                var count = stat.Stats?.FirstOrDefault(s => s.Value1 == Stat.MultipleCount);
                if (count != null)
                {
                    int value = checked((int)count.Value2);
                    SetIdentity(stat.Identity, value);
                    Logger.Information("[CityBankers] STACK quantity identity=" + stat.Identity + "; count=" + value);
                }
                return;
            }
            var action = message.Body as CharacterActionMessage;
            if (action != null && action.Action == StackItemsAction) ObserveMerge(action);
            var template = message.Body as TemplateActionMessage;
            if (template != null)
            {
                // Match Clientless's trade-template route. Owner's live 13-unit
                // offer confirms Unknown1 carries quantity; native handling drops it.
                if (DynelManager.LocalPlayer == null ||
                    template.Identity != DynelManager.LocalPlayer.Identity ||
                    (template.Unknown2 != 6 && template.Unknown2 != 85) ||
                    template.Placement.Type != IdentityType.Inventory || template.Unknown1 <= 0)
                    return;
                var before = new HashSet<Item>(Trade.TargetWindowCache.Items);
                afterNative.Enqueue(() =>
                {
                    var added = Trade.TargetWindowCache.Items.Where(i => !before.Contains(i) &&
                        i.Id == template.ItemLowId && i.HighId == template.ItemHighId &&
                        i.Ql == template.Quality).ToList();
                    // Bind the actual new object, never a template-wide or reusable
                    // slot count. Clientless carries this object into inventory.
                    if (added.Count == 1) Set(added[0], template.Unknown1);
                    if (CruPolicy.IsCru(template.ItemLowId)) Logger.Information("[CityBankers] STACK trade-template count=" +
                        template.Unknown1 + "; new objects=" + added.Count);
                });
            }
        }
        public static Item Split(Item source, int quantity)
        {
            Flush();
            var items = Inventory.Items as ICollection<Item>;
            int originalCount = Quantity(source);
            if (!IsStack(source) || quantity != 1 || originalCount <= quantity ||
                originalCount > ushort.MaxValue || !Client.InPlay || Client.LocalDynelId == 0 ||
                Trade.IsTrading || pendingMerge != null || items == null || items.IsReadOnly ||
                source.UniqueIdentity != Identity.None || source.Slot.Type != IdentityType.Inventory ||
                !items.Contains(source) || items.Count(i => i.Slot == source.Slot) != 1)
                throw new InvalidOperationException("Invalid one-unit CRU split.");
            var before = items.ToList();
            var slots = before.Select(i => i.Slot).ToList();
            var beforeCounts = before.Where(IsStack).Select(Quantity).ToList();
            if (beforeCounts.Any(count => count <= 0) ||
                before.Where(i => i.Slot.Type == IdentityType.Inventory)
                    .GroupBy(i => i.Slot).Any(group => group.Count() != 1))
                throw new InvalidOperationException("Split requires known quantities and unique inventory slots.");
            // Native-client probes, including fresh-login persistence, place the
            // new unit in the lowest unused INTERNAL slot, not its visual grid cell.
            int? free = Enumerable.Range(0x40, 30)
                .Where(slot => !before.Any(i => i.Slot.Instance == slot))
                .Select(slot => (int?)slot).FirstOrDefault();
            if (!free.HasValue) throw new InvalidOperationException("No free inventory slot for split.");
            var newSlot = new Identity(IdentityType.Inventory, free.Value);
            // Construct before sending, so a local construction failure cannot
            // issue an operation which the caller might otherwise retry.
            var split = new Item(newSlot, Identity.None, source.Id, source.HighId, source.Ql);
            // Keep AOSharp's SplitItem/Target/Parameter2 request layout, but do not
            // inherit the N3 constructor's Unknown=1 header. Owner restart evidence
            // now proves BOTH values split physically; preserve the current sender.
            var packet = new CharacterActionMessage { Action = CharacterActionType.SplitItem,
                Unknown = 0, Unknown1 = 0, Unknown2 = 0,
                Identity = new Identity(IdentityType.SimpleChar, Client.LocalDynelId),
                Target = source.Slot, Parameter1 = 0, Parameter2 = quantity };
            Client.Send(packet);
            // Client.Send returning is not a server acknowledgement. Reproduce
            // the observed native-client transition only while our frozen state
            // still matches. On uncertainty the caller's runtime latch stays set.
            if (!Client.InPlay || Client.LocalDynelId != packet.Identity.Instance || Trade.IsTrading ||
                !ReferenceEquals(items, Inventory.Items) || items.Count != before.Count ||
                before.Where((item, index) => !items.Contains(item) || item.Slot != slots[index]).Any() ||
                !before.Where(IsStack).Select(Quantity).SequenceEqual(beforeCounts))
                throw new InvalidOperationException("Inventory changed during split send; preparation paused.");
            Set(split, quantity);
            items.Add(split);
            Set(source, originalCount - quantity);
            Logger.Information("[CityBankers] STACK split applied locally; source=" + source.Slot +
                "; remaining=" + Quantity(source) + "; new slot=" + split.Slot +
                "; quantity=" + quantity + "; no server acknowledgement expected.");
            return split;
        }
        private static void ObserveMerge(CharacterActionMessage action)
        {
            var pending = pendingMerge;
            if (pending == null || action.Identity != pending.Character ||
                action.Unknown != 0 || action.Unknown1 != 0 || action.Unknown2 != 0 ||
                action.Target != pending.SurvivorSlot ||
                action.Parameter1 != (int)pending.ConsumedSlot.Type ||
                action.Parameter2 != pending.ConsumedSlot.Instance) return;

            // Native Clientless ignores action 53. Apply only after native handling,
            // and only to the exact still-pending objects/counts captured at send.
            afterNative.Enqueue(() => ApplyMergeResponse(pending));
        }

        private static void ApplyMergeResponse(PendingMerge pending)
        {
            if (!ReferenceEquals(pendingMerge, pending)) return;
            pendingMerge = null; // A repeated response cannot add the units twice.
            var items = Inventory.Items as ICollection<Item>;
            var current = (Inventory.Items ?? new List<Item>()).Where(i => IsStack(i) &&
                i.Slot.Type == IdentityType.Inventory).ToList();
            bool unchanged = current.Count == pending.Before.Count &&
                pending.Before.Select((item, index) => current.Contains(item) &&
                    Quantity(item) == pending.BeforeCounts[index]).All(value => value);
            if (!Client.InPlay || Client.LocalDynelId != pending.Character.Instance ||
                pending.Age.Elapsed.TotalSeconds >= 10 || items == null || items.IsReadOnly ||
                pending.Survivor.Slot != pending.SurvivorSlot || pending.Consumed.Slot != pending.ConsumedSlot ||
                Inventory.Items.Count(i => i.Slot == pending.SurvivorSlot) != 1 ||
                Inventory.Items.Count(i => i.Slot == pending.ConsumedSlot) != 1 || !unchanged)
            {
                Logger.Warning("[CityBankers] STACK action53 matched but inventory/connection changed or response expired; cache not modified.");
                return;
            }

            // Owner's matching response plus fresh login proved 1+52 -> 53 at
            // packet.Target, with the parameter-addressed stack gone. This is a
            // server-confirmed transition, never an optimistic send-side update.
            int merged = checked(pending.SurvivorCount + pending.ConsumedCount);
            if (!items.Remove(pending.Consumed)) return;
            Set(pending.Consumed, 0);
            Set(pending.Survivor, merged);
            Logger.Information("[CityBankers] STACK action53 applied; survivor=" + pending.SurvivorSlot +
                "; quantity=" + merged + "; consumed=" + pending.ConsumedSlot +
                "; CRU units=" + pending.BeforeCounts.Sum());
        }

        public static void Merge(Item survivor, Item consumed)
        {
            if (!IsStack(survivor) || !IsStack(consumed) || ReferenceEquals(survivor, consumed) ||
                survivor.Id != consumed.Id || survivor.HighId != consumed.HighId || survivor.Ql != consumed.Ql)
                throw new InvalidOperationException("Stack templates differ.");
            if (survivor.Slot.Type != IdentityType.Inventory || consumed.Slot.Type != IdentityType.Inventory ||
                survivor.Slot == consumed.Slot)
                throw new InvalidOperationException("Stack merge requires two different inventory slots.");
            if (pendingMerge != null) throw new InvalidOperationException("A stack merge is already pending.");
            var before = (Inventory.Items ?? new List<Item>()).Where(i => IsStack(i) &&
                i.Slot.Type == IdentityType.Inventory).ToList();
            var beforeCounts = before.Select(Quantity).ToList();
            int survivorCount = Quantity(survivor), consumedCount = Quantity(consumed);
            if (!Client.InPlay || Client.LocalDynelId == 0 || !before.Contains(survivor) || !before.Contains(consumed) ||
                beforeCounts.Any(count => count <= 0) ||
                (long)survivorCount + consumedCount > ushort.MaxValue ||
                Inventory.Items.Count(i => i.Slot == survivor.Slot) != 1 ||
                Inventory.Items.Count(i => i.Slot == consumed.Slot) != 1 ||
                (survivor.UniqueIdentity != Identity.None && survivor.UniqueIdentity == consumed.UniqueIdentity))
                throw new InvalidOperationException("Stack merge requires known quantities and distinct live inventory records.");
            // ICE's CRU stacking routine sends action 53 (0x35), absent from the
            // SDK enum. The proven wire request is unchanged. Live restart evidence
            // establishes Target as the survivor, parameters as the consumed slot.
            var packet = new CharacterActionMessage { Action = StackItemsAction,
                Unknown = 0, Unknown1 = 0, Unknown2 = 0,
                Identity = new Identity(IdentityType.SimpleChar, Client.LocalDynelId),
                Target = survivor.Slot, Parameter1 = (int)consumed.Slot.Type,
                Parameter2 = consumed.Slot.Instance };
            pendingMerge = new PendingMerge { Survivor = survivor, Consumed = consumed,
                SurvivorSlot = survivor.Slot, ConsumedSlot = consumed.Slot, Character = packet.Identity,
                SurvivorCount = survivorCount, ConsumedCount = consumedCount,
                Before = before, BeforeCounts = beforeCounts };
            try { Client.Send(packet); }
            catch { CancelMerge(survivor, consumed); throw; }
        }
    }
}
