using System;
using System.Collections.Generic;
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
    // Clientless 1.0.16 drops InventorySlot.Count/AddTemplate.Count when constructing
    // Item objects. Keep server quantities beside those objects, without replacing
    // Clientless's inventory or inferring a successful operation from our request.
    internal static class StackableItems
    {
        private sealed class CountValue { public CountValue() { } public int Value; }
        private static ConditionalWeakTable<Item, CountValue> counts = new ConditionalWeakTable<Item, CountValue>();
        private static readonly Dictionary<Identity, int> identities = new Dictionary<Identity, int>();
        private static readonly Queue<Action> afterNative = new Queue<Action>();
        private static bool installed;
        public static bool IsStack(Item item) => item != null && CruPolicy.IsCru(item.Id);
        public static int Quantity(Item item)
        {
            if (item == null) return 0;
            if (!IsStack(item)) return 1;
            int value;
            if (item.UniqueIdentity != Identity.None && identities.TryGetValue(item.UniqueIdentity, out value)) return value;
            CountValue count;
            if (counts.TryGetValue(item, out count)) return count.Value;
            var unique = item as UniqueItem;
            return unique?.Stats != null && unique.Stats.TryGetValue(Stat.MultipleCount, out value) ? value : -1;
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
                counts = new ConditionalWeakTable<Item, CountValue>();
                identities.Clear();
                afterNative.Enqueue(() =>
                {
                    foreach (var slot in full.InventorySlots ?? new InventorySlot[0])
                    {
                        var item = Inventory.Items?.FirstOrDefault(i => i.Slot.Instance == slot.Placement);
                        // Wire field is 16 bits; full-client inventory reads it unsigned.
                        if (IsStack(item))
                        {
                            Set(item, (ushort)slot.Count);
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
                    if (!CruPolicy.IsCru(slot.ItemLowId)) continue;
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
            if (add != null && CruPolicy.IsCru(add.LowId))
            {
                var before = new HashSet<Item>(Inventory.Items ?? new List<Item>());
                afterNative.Enqueue(() =>
                {
                    var added = (Inventory.Items ?? new List<Item>()).Where(i => !before.Contains(i) &&
                        i.Id == add.LowId && i.HighId == add.HighId && i.Ql == add.Quality).ToList();
                    if (added.Count == 1) Set(added[0], add.Count);
                    Logger.Information("[CityBankers] STACK add-template count=" + add.Count + "; new objects=" + added.Count);
                });
                return;
            }
            var simple = message.Body as SimpleItemFullUpdateMessage;
            if (simple != null)
            {
                var templateId = simple.Stats?.FirstOrDefault(s => s.Value1 == Stat.ACGItemTemplateID);
                if (templateId == null || !CruPolicy.IsCru(templateId.Value2)) return;
                var count = simple.Stats?.FirstOrDefault(s => s.Value1 == Stat.MultipleCount);
                if (count != null) SetIdentity(simple.Identity, count.Value2);
                return;
            }
            var stat = message.Body as StatMessage;
            if (stat != null)
            {
                if (!identities.ContainsKey(stat.Identity) && !AllItems().Any(i => IsStack(i) &&
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
            if (action != null && (action.Action == CharacterActionType.SplitItem ||
                action.Action == CharacterActionType.Split || action.Action == CharacterActionType.UseItemOnItem))
                Logger.Information("[CityBankers] STACK server-action=" + action.Action + "; target=" + action.Target +
                    "; parameters=" + action.Parameter1 + "," + action.Parameter2);
            var template = message.Body as TemplateActionMessage;
            if (template != null && CruPolicy.IsCru(template.ItemLowId))
            {
                Logger.Information("[CityBankers] STACK template-action placement=" + template.Placement +
                    "; fields=" + template.Unknown1 + "," + template.Unknown2 + "," + template.Unknown3 + "," + template.Unknown4);
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
                    Logger.Information("[CityBankers] STACK trade-template count=" +
                        template.Unknown1 + "; new objects=" + added.Count);
                });
            }
        }
        public static void Split(Item source, int quantity)
        {
            if (quantity <= 0 || Quantity(source) <= quantity) throw new InvalidOperationException("Invalid stack split.");
            Client.Send(new CharacterActionMessage { Action = CharacterActionType.SplitItem,
                Target = source.Slot, Parameter2 = quantity });
        }
        public static void Merge(Item source, Item target)
        {
            if (source == null || target == null || ReferenceEquals(source, target) ||
                source.Id != target.Id || source.HighId != target.HighId || source.Ql != target.Ql)
                throw new InvalidOperationException("Stack templates differ.");
            Client.Send(new CharacterActionMessage { Action = CharacterActionType.UseItemOnItem,
                Target = source.Slot, Parameter1 = (int)target.Slot.Type, Parameter2 = target.Slot.Instance });
        }
    }
}
