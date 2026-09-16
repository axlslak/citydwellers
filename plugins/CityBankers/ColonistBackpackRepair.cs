using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;
using SmokeLounge.AOtomation.Messaging.GameData;

namespace CityBankers
{
    // TEMPORARY one-run migration. Called only by StartupCensusGate while actors
    // are quiesced, BEFORE the physical census. Remove after owner confirms rollout.
    // The separate StorageBagPolicy is permanent.
    internal sealed class ColonistBackpackRepair : IDisposable
    {
        private const string Version = "colonist-backpack-repair-v1";
        private readonly string _donePath, _progressPath;
        private readonly HashSet<string> _opened = new HashSet<string>();
        private readonly HashSet<string> _fullTargets = new HashSet<string>();
        private readonly Stopwatch _age = new Stopwatch();
        private Func<bool> _verify;
        private Action _after;
        private string _operation, _source, _target, _failure;
        private bool _targetFromBank, _done;
        private readonly EquipSlot _back;

        public ColonistBackpackRepair(string settings)
        {
            string directory = Path.Combine(RuntimeStateStore.GetDataDirectory(settings), "colonist-backpack-repair-v1");
            Directory.CreateDirectory(directory);
            _donePath = Path.Combine(directory, Client.CharacterName.ToLowerInvariant() + ".done.json");
            _progressPath = Path.Combine(directory, Client.CharacterName.ToLowerInvariant() + ".progress.json");
            // Resolve the SDK's named equipment slot; never guess a packet slot.
            _back = (EquipSlot)Enum.Parse(typeof(EquipSlot), "Cloth_Back");
            var saved = CensusApplication.ReadExisting<JObject>(_donePath);
            _done = saved != null && (string)saved["Format"] == Version &&
                (string)saved["Character"] == Client.CharacterName && (bool?)saved["Completed"] == true;
            Inventory.ContainerOpened += ContainerOpened;
        }

        public void Dispose() { Inventory.ContainerOpened -= ContainerOpened; }
        private void ContainerOpened(Container container)
        {
            if (container != null && container.IsOpen) _opened.Add(container.Identity.ToString());
        }
        private static string Id(Item item) => item.UniqueIdentity.ToString();
        private static IEnumerable<Item> AllOuter() =>
            (Inventory.Items ?? Enumerable.Empty<Item>()).Concat(Inventory.Bank.Items ?? Enumerable.Empty<Item>());
        private static Item Find(string id) => AllOuter().SingleOrDefault(i => i != null && Id(i) == id);
        private static Container Bag(string id) => Inventory.Containers?.SingleOrDefault(c => c.Identity.ToString() == id && c.IsOpen);
        private bool OnBack(Item item) => item != null && item.Slot.Type == IdentityType.ArmorPage && item.Slot.Instance == (int)_back;
        private static string Key(Item item) => item.UniqueIdentity + "/" + item.Id + "/" + item.HighId + "/" + item.Ql + "/" + StackableItems.Quantity(item);
        private static string Signature(IEnumerable<string> keys) => string.Join(";", keys.OrderBy(k => k, StringComparer.Ordinal));
        private static string Contents(Container bag) => bag == null ? null : Signature(bag.Items.Select(Key));

        public bool Tick()
        {
            if (_done) return true;
            if (_failure != null) return false;
            try
            {
                if (!Client.InPlay || !Inventory.Bank.IsOpen || Trade.IsTrading) return false;
                if (_verify != null)
                {
                    if (_verify())
                    {
                        Logger.Information("COLONIST REPAIR verified: " + _operation);
                        var after = _after; _verify = null; _after = null;
                        after?.Invoke();
                    }
                    else if (_age.ElapsedMilliseconds >= 15000)
                        throw new InvalidOperationException("Unverified operation: " + _operation + ". No automatic resend.");
                    return false;
                }
                if (_source == null)
                {
                    var candidates = AllOuter().Where(StorageBagPolicy.IsColonist).ToList();
                    if (candidates.Count == 0) { Complete("No Colonist backpack present."); return true; }
                    if (candidates.Count != 1) throw new InvalidOperationException("Multiple Colonist backpacks; cannot choose which to equip.");
                    _source = Id(candidates[0]);
                    Logger.Information("COLONIST REPAIR starting; backpack=" + _source + ". Banking stays paused until verified.");
                }
                Item source = Find(_source);
                if (source == null) throw new InvalidOperationException("Colonist backpack disappeared.");
                var occupied = Inventory.Items.FirstOrDefault(i => i != null && OnBack(i) && Id(i) != _source);
                if (occupied != null) throw new InvalidOperationException("Back slot occupied by another item; will not unequip it.");
                if (source.Slot.Type == IdentityType.BankByRef)
                {
                    if (!EnsureFreeSlot()) return false;
                    MoveBag(source, false); return false;
                }
                if (!OnBack(source) && !StorageBagPolicy.IsNormalInventory(source))
                    throw new InvalidOperationException("Colonist is in an unexpected equipped slot; will not unequip it.");
                if (!EnsureOpen(source)) return false;
                Container sourceBag = Bag(_source);
                if (sourceBag == null) throw new InvalidOperationException("Colonist container is no longer open.");
                if (sourceBag.Items.Count == 0)
                {
                    if (_targetFromBank && _target != null) { ReturnTarget(); return false; }
                    if (!OnBack(source))
                    {
                        Issue("equip empty Colonist " + _source + " on back", () => OnBack(Find(_source)) &&
                            !Inventory.Bank.Items.Any(StorageBagPolicy.IsColonist), () => source.Equip(_back));
                        return false;
                    }
                    Complete("Empty Colonist backpack verified on back."); return true;
                }
                if (_target == null)
                {
                    Item next = AllOuter().Where(StorageBagPolicy.IsSmallBackpack)
                        .Where(i => !_fullTargets.Contains(Id(i)))
                        .OrderBy(i => StorageBagPolicy.IsNormalInventory(i) ? 0 : 1).FirstOrDefault();
                    if (next == null) throw new InvalidOperationException("No Small Backpack (99228) with verified free space remains. Add storage; Colonist will not be used for storage.");
                    _target = Id(next); _targetFromBank = next.Slot.Type == IdentityType.BankByRef;
                }
                Item target = Find(_target);
                if (target == null || !StorageBagPolicy.IsSmallBackpack(target))
                    throw new InvalidOperationException("Repair destination bag disappeared or is not a Small Backpack.");
                if (target.Slot.Type == IdentityType.BankByRef)
                {
                    if (!EnsureFreeSlot()) return false;
                    MoveBag(target, false); return false;
                }
                if (!EnsureOpen(target)) return false;
                Container targetBag = Bag(_target);
                if (targetBag == null) throw new InvalidOperationException("Destination container is no longer open.");
                if (targetBag.NumFreeSlots <= 0)
                {
                    _fullTargets.Add(_target);
                    if (_targetFromBank) ReturnTarget();
                    else _target = null;
                    return false;
                }
                Item moving = sourceBag.Items[0];
                if (moving.UniqueIdentity.Type == IdentityType.Container)
                    throw new InvalidOperationException("Nested container in Colonist backpack; refusing automatic relocation.");
                string sourceId = _source, targetId = _target;
                var sourceKeys = sourceBag.Items.Select(Key).ToList();
                var targetKeys = targetBag.Items.Select(Key).ToList();
                string movedKey = Key(moving);
                sourceKeys.Remove(movedKey); targetKeys.Add(movedKey);
                string expectedSource = Signature(sourceKeys), expectedTarget = Signature(targetKeys);
                Issue("move item " + movedKey + " from " + sourceId + " to " + targetId,
                    () => Contents(Bag(sourceId)) == expectedSource && Contents(Bag(targetId)) == expectedTarget,
                    () => moving.MoveToContainer(targetBag), null,
                    new { SourceBefore = Contents(sourceBag), TargetBefore = Contents(targetBag), expectedSource, expectedTarget });
                return false;
            }
            catch (Exception ex)
            {
                _failure = ex.Message;
                Logger.Error("COLONIST REPAIR BLOCKED: " + _failure + " Leave items in place and report this log; startup census remains held.");
                return false;
            }
        }

        private bool EnsureFreeSlot()
        {
            if (Inventory.NumFreeSlots > 0) return true;
            Item spare = Inventory.Items.FirstOrDefault(i => StorageBagPolicy.IsSmallBackpack(i) &&
                StorageBagPolicy.IsNormalInventory(i) && Id(i) != _source && Id(i) != _target);
            if (spare == null || Inventory.Bank.NumFreeSlots <= 0)
                throw new InvalidOperationException("No inventory staging slot or bank space. Will not move equipped items.");
            MoveBag(spare, true); return false;
        }
        private void MoveBag(Item item, bool toBank, Action after = null)
        {
            string id = Id(item);
            _opened.Remove(id);
            Issue("stage bag " + id + (toBank ? " to bank" : " to inventory"), () => {
                Item current = Find(id);
                return current != null && (toBank ? current.Slot.Type == IdentityType.BankByRef : StorageBagPolicy.IsNormalInventory(current));
            }, () => { if (toBank) item.MoveToBank(); else item.MoveToInventory(); }, after);
        }
        private void ReturnTarget()
        {
            if (Inventory.Bank.NumFreeSlots <= 0) throw new InvalidOperationException("No bank slot to return the repair destination.");
            MoveBag(Find(_target), true, () => { _target = null; _targetFromBank = false; });
        }
        private bool EnsureOpen(Item item)
        {
            string id = Id(item);
            if (_opened.Contains(id) && Bag(id) != null) return true;
            _opened.Remove(id);
            Issue("open backpack " + id, () => _opened.Contains(id) && Bag(id) != null, () => item.Use());
            return false;
        }
        private void Issue(string operation, Func<bool> verify, Action send, Action after = null, object evidence = null)
        {
            RuntimeStateStore.WriteJsonAtomic(_progressPath, new { Format = Version, Character = Client.CharacterName,
                Operation = operation, Source = _source, Target = _target, Evidence = evidence, IssuedUtc = DateTime.UtcNow });
            _operation = operation; _verify = verify; _after = after; _age.Restart();
            Logger.Information("COLONIST REPAIR " + operation);
            send();
        }
        private void Complete(string result)
        {
            RuntimeStateStore.WriteJsonAtomic(_donePath, new { Format = Version, Character = Client.CharacterName,
                Completed = true, CompletedUtc = DateTime.UtcNow, Backpack = _source, Result = result });
            _done = true;
            Logger.Information("COLONIST REPAIR COMPLETE: " + result + " One-run marker saved; normal census follows.");
        }
    }
}
