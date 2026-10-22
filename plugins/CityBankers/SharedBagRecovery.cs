using File = CityDwellers.Shared.DiskFiles;
using Address = CityBankers.BagRecoveryAddress;
using Content = CityBankers.BagHistoryItem;
using Transfer = CityBankers.BagHistoryTransfer;
using Pending = CityBankers.BagRecoveryIntent;
using State = CityBankers.BagRecoveryState;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBankers
{
    // Only StartupCensusGate may drive this actor, after quiescing this banker.
    // No census is launched. The journal is written BEFORE an AO
    // mutation. Restart observes the outstanding action; it never blindly replays it.
    internal static class SharedBagRecovery
    {
        private sealed class View
        {
            public long Sequence;
            public int Handle;
            public List<Content> Items;
        }
        private static string settings, path, epoch, bankEpoch, lastError, requestedOpen;
        private static string pendingId, reconnectRequested;
        private static bool installed, retained;
        internal static bool PlannedReconnect { get; private set; }
        private static readonly Stopwatch statusAge = Stopwatch.StartNew();
        private static long sequence, observationBoundary;
        private static readonly Stopwatch openAge = new Stopwatch();
        private static readonly Stopwatch pendingAge = new Stopwatch();
        private static readonly Stopwatch retryAge = Stopwatch.StartNew();
        private static readonly Dictionary<int, View> views = new Dictionary<int, View>();
        private static readonly Dictionary<string, View> opened = new Dictionary<string, View>();
        private static List<Address> inventory = new List<Address>(), bank = new List<Address>();
        private static int inventoryCount;

        // Read-only evidence for normal reserve handling. A caller takes the
        // boundary BEFORE a trade/move, so an automatic contents response is not
        // discarded just because it arrived before that actor's next tick.
        internal static long ContainerObservationSequence => sequence;
        internal static bool TryObserveContainer(Identity identity, long after, out int handle, out int count)
        {
            handle = count = 0;
            View view;
            if (!installed || epoch == null || bankEpoch != epoch || identity.Type != IdentityType.Container ||
                !views.TryGetValue(identity.Instance, out view) || view.Sequence <= after) return false;
            handle = view.Handle; count = view.Items.Count;
            return true;
        }

        internal static void Install(string directory)
        {
            if (installed) return;
            settings = directory;
            string root = Path.Combine(RuntimeStateStore.GetDataDirectory(settings), "bag-recovery",
                Client.CharacterName.ToLowerInvariant());
            path = Path.Combine(root, "active.json");
            // Unreadable persistence is a hold, never a fresh recovery plan.
            retained = ReadState() != null || CityDwellers.Shared.ManagerMemory.Current.ReadBagHistory(
                CityDwellers.Shared.ManagerAccounting.TransactionId, Client.CharacterName).Any(row => row.Phase != "complete");
            epoch = bankEpoch = requestedOpen = reconnectRequested = pendingId = lastError = null;
            inventory.Clear(); bank.Clear(); views.Clear(); opened.Clear();
            PlannedReconnect = false;
            Client.MessageReceived += Receive;
            installed = true;
        }
        internal static void Stop()
        {
            Client.MessageReceived -= Receive;
            installed = false;
            views.Clear(); opened.Clear();
        }
        internal static bool RequiresHold
        {
            get
            {
                if (!installed) return false;
                if (retained) return true;
                return (Inventory.Items ?? new List<Item>()).Concat(Inventory.Bank.Items ?? new List<Item>()).Where(i => i != null &&
                    i.Id == StorageBagPolicy.SmallBackpackId && i.UniqueIdentity.Type == IdentityType.Container)
                    .GroupBy(i => i.UniqueIdentity).Any(g => g.Count() > 1);
            }
        }
        private static List<Address> Outer() => inventory.Concat(bank).ToList();
        private static List<Address> Aliases(int id) => Outer().Where(a => a.Bag == id).ToList();
        private static bool Normal(Address a) => a.Type == (int)IdentityType.Inventory;
        private static Item Live(Address a)
        {
            IEnumerable<Item> items = a.Type == (int)IdentityType.BankByRef
                ? Inventory.Bank.Items : Inventory.Items;
            var matches = items.Where(i => i != null && (int)i.Slot.Type == a.Type &&
                i.Slot.Instance == a.Slot && i.UniqueIdentity.Type == IdentityType.Container &&
                i.UniqueIdentity.Instance == a.Bag && i.Id == StorageBagPolicy.SmallBackpackId).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Fresh outer address and SDK listing disagree.");
            return matches[0];
        }
        private static List<Address> ReadOuter(IEnumerable<InventorySlot> slots, bool fromBank)
        {
            return slots.Where(s => s.Identity.Type == IdentityType.Container &&
                s.ItemLowId == StorageBagPolicy.SmallBackpackId).Select(s => new Address
                {
                    Bag = s.Identity.Instance, Slot = s.Placement,
                    Type = fromBank ? (int)IdentityType.BankByRef :
                        s.Placement >= 0x31 && s.Placement <= 0x3F ? (int)IdentityType.SocialPage :
                        s.Placement >= Inventory.INVENTORY_START && s.Placement < Inventory.INVENTORY_END
                            ? (int)IdentityType.Inventory : (int)IdentityType.ArmorPage
                }).ToList();
        }
        private static void Receive(object sender, Message message)
        {
            try
            {
                if (message?.Body is FullCharacterMessage full)
                {
                    epoch = Guid.NewGuid().ToString("N"); bankEpoch = null; PlannedReconnect = false;
                    observationBoundary = sequence;
                    views.Clear(); opened.Clear(); requestedOpen = null; reconnectRequested = null;
                    inventory = ReadOuter(full.InventorySlots, false);
                    inventoryCount = full.InventorySlots.Count(s => s.Placement >= Inventory.INVENTORY_START &&
                        s.Placement < Inventory.INVENTORY_END);
                }
                else if (message?.Body is BankMessage snapshot && DynelManager.LocalPlayer != null &&
                    snapshot.Identity == DynelManager.LocalPlayer.Identity)
                {
                    bank = ReadOuter(snapshot.BankSlots, true); bankEpoch = epoch;
                }
                else if (message?.Body is InventoryUpdateMessage update && update.InventoryIdentity.Type == IdentityType.Container)
                {
                    views[update.InventoryIdentity.Instance] = new View { Sequence = ++sequence, Handle = update.Handle,
                        Items = update.Items.Select(i => new Content { Slot = i.Placement & 65535,
                            Low = i.ItemLowId, High = i.ItemHighId, Ql = i.Quality,
                            Identity = i.Identity.ToString(), Quantity = Math.Max(1, (int)(ushort)i.Count) }).ToList() };
                }
            }
            catch (Exception ex)
            {
                // Unknown wire state must inhibit recovery, not fall back to cached contents.
                bankEpoch = null;
                Warn("Incoming recovery evidence unavailable: " + ex.GetType().Name);
            }
        }
        private static State Load()
        {
            var s = ReadState();
            if (s == null && File.Exists(path) &&
                (string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path))["Phase"] != "complete")
                throw new InvalidOperationException("An unfinished older on-disk repair is present; preserving it for administrator review.");
            if (s != null && (s.Format != "citybankers-shared-bag-recovery-v2" ||
                s.Character != Client.CharacterName || s.Transfers == null || s.Destinations == null ||
                s.FullTargets == null || s.TargetBaselines == null || s.Baseline == null ||
                s.Original == null || s.ReadFailures == null || string.IsNullOrEmpty(s.Run) ||
                (s.Phase != "confirm" && s.Phase != "evacuate" && s.Phase != "remove-empty") ||
                (s.Pending != null && (s.Pending.Source == null ||
                    !new[] { "transfer", "stage", "stage-target", "restore-social", "park", "return-target", "delete" }.Contains(s.Pending.Kind) ||
                    (s.Pending.Kind == "transfer" && s.Pending.Transfer?.Item == null)))))
                throw new InvalidOperationException("Invalid shared-bag recovery journal.");
            return s;
        }
        private static State ReadState() => CityDwellers.Shared.ManagerMemory.Current.ReadBagRecovery(
            CityDwellers.Shared.ManagerAccounting.TransactionId, Client.CharacterName);
        private static void Save(State s)
        {
            s.UpdatedUtc = DateTime.UtcNow;
            CityDwellers.Shared.ManagerAccounting.Transaction("Bag repair intent", () =>
                CityDwellers.Shared.ManagerMemory.Current.ChangeBagRecovery(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, s));
            retained = s.Phase != "complete";
        }
        private static void Event(State s, string text)
        {
            Logger.Warning("[CityBankers] BAG RECOVERY " + Client.CharacterName + ": " + text);
        }
        private static void Warn(string text)
        {
            if (lastError == text) return;
            lastError = text;
            Logger.Warning("[CityBankers] BAG RECOVERY waiting: " + text);
        }
        private static void Reconnect(string reason)
        {
            if (reconnectRequested == epoch) return;
            Logger.Warning("[CityBankers] BAG RECOVERY intentional verification reconnect: " + reason);
            PlannedReconnect = true; // Must precede the synchronous Disconnected event.
            try
            {
                ClientlessSessionGuard.ReconnectForBagRecovery();
                reconnectRequested = epoch;
            }
            catch { PlannedReconnect = false; throw; }
        }
        private static bool SnapshotLayoutMatches()
        {
            var native = Inventory.Items.Concat(Inventory.Bank.Items).Where(i => i != null &&
                i.Id == StorageBagPolicy.SmallBackpackId && i.UniqueIdentity.Type == IdentityType.Container)
                .Select(i => (int)i.Slot.Type + "/" + i.Slot.Instance + "/" + i.UniqueIdentity.Instance).OrderBy(k => k);
            return Outer().Select(a => a.Key).OrderBy(k => k).SequenceEqual(native);
        }
        // True means this banker remains held under its own recovery reservation.
        internal static bool Tick()
        {
            if (!RequiresHold) return false;
            if (!Client.InPlay || Trade.IsTrading || !Inventory.Bank.IsOpen || epoch == null)
                return true;
            if (retryAge.IsRunning && retryAge.ElapsedMilliseconds < 3000) return true;
            try
            {
                if (bankEpoch != epoch) { Reconnect("Current bank snapshot unavailable."); return true; }
                State s = Load(); // Every tick resumes durable truth, including failed writes.
                if (s == null)
                {
                    if (retained) throw new InvalidOperationException("Active recovery journal is missing; preserving the hold.");
                    var duplicate = Outer().GroupBy(a => a.Bag).FirstOrDefault(g => g.Count() > 1);
                    if (duplicate == null || !SnapshotLayoutMatches())
                    { Reconnect("Incoming outer snapshots disagree with the current SDK layout."); return true; }
                    s = new State { Run = Guid.NewGuid().ToString("N"), Character = Client.CharacterName,
                        Bag = duplicate.Key, Original = duplicate.ToList(), Phase = "evacuate", Epoch = epoch };
                    Event(s, "Detected repeated bag identity " + s.Bag + "; incoming login/bank snapshots agree with current addresses; beginning evacuation without reconnect.");
                    Save(s); return true;
                }
                // The first failed refresh often just means this connection has
                // already supplied that container once. A new login + bank snapshot
                // completes that refresh; do not also idle until the old timer ends.
                // Repeated failures still retain their escalating backoff.
                if (s.FirstReadRefreshEpoch != null && s.FirstReadRefreshEpoch != epoch && SnapshotLayoutMatches())
                {
                    s.FirstReadRefreshEpoch = null; s.RetryAfterUtc = default(DateTime);
                    Event(s, "First contents refresh reconnected with consistent outer snapshots; retry fresh contents now.");
                    Save(s);
                }
                if (Outer().GroupBy(a => a.Type + "/" + a.Slot).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("Server snapshot repeats an outer slot; no unique action address.");
                if (s.Phase == "confirm")
                {
                    if (!SnapshotLayoutMatches()) { Reconnect("Resume confirmation requires consistent outer snapshots."); return true; }
                    s.Phase = "evacuate"; Save(s); return true;
                }
                if (statusAge.ElapsedMilliseconds >= 30000)
                {
                    Logger.Information("[CityBankers] BAG RECOVERY progress phase=" + s.Phase +
                        "; pending=" + (s.Pending?.Kind ?? "none") + "; open=" + (requestedOpen ?? "none") +
                        "; verified records=" + s.Transfers.Count + "; aliases=" + Aliases(s.Bag).Count);
                    statusAge.Restart();
                }
                if (s.Pending != null) { Resolve(s); return true; }
                if (s.Phase != "remove-empty" && ContentsSecured(s))
                {
                    // Each receipt required an exact source decrement and target
                    // increment. The final receipt therefore proved the source
                    // empty. Preserve that fact across removal/reconnects: deleting
                    // one outer reference must not send the remaining empty bank
                    // reference back through the inventory-staging prerequisite.
                    // This also resumes schema13 repairs already fully evacuated.
                    s.Phase = "remove-empty";
                    s.LastUnappliedAddress = s.FirstReadRefreshEpoch = null;
                    s.UnappliedAttempts = 0; s.RetryAfterUtc = default(DateTime);
                    s.ReadFailures.RemoveAll(f => f.Bag == s.Bag);
                    Event(s, "All original contents secured; removing the empty damaged bag at its remaining addresses.");
                    Save(s); return true;
                }
                if (DateTime.UtcNow < s.RetryAfterUtc) return true;
                var aliases = Aliases(s.Bag);
                if (aliases.Count == 0 && !s.EmptyShellsDisappeared)
                    throw new InvalidOperationException("Source disappeared without a verified empty-shell action.");
                if (aliases.Any(a => !Normal(a) && a.Type != (int)IdentityType.BankByRef &&
                    !(a.Type == (int)IdentityType.SocialPage && a.Slot == (int)EquipSlot.Social_Back)))
                    throw new InvalidOperationException("Suspect bag occupies protected equipment; refusing to replace equipment.");

                // Stage one reference at a time. Reconnect after every outer move
                // removes reliance on the SDK's inferred Bank.NextAvailableSlot.
                var inBank = aliases.Where(a => a.Type == (int)IdentityType.BankByRef)
                    .OrderBy(a => a.Key == s.LastUnappliedAddress ? 1 : 0).FirstOrDefault();
                if (s.Phase != "remove-empty" && inBank != null && !aliases.Any(Normal))
                {
                    if (!Headroom(s)) return true;
                    IssueOuter(s, "stage", inBank, 0); return true;
                }
                var social = aliases.FirstOrDefault(a => a.Type == (int)IdentityType.SocialPage);
                if (s.Phase != "remove-empty" && social != null && !aliases.Any(Normal))
                {
                    if (!Headroom(s)) return true;
                    IssueOuter(s, "restore-social", social, 0); return true;
                }
                if (!s.BaselineRecorded)
                {
                    if (aliases.Count == 0) throw new InvalidOperationException("Missing source baseline.");
                    // InventoryUpdate identifies the shared container, not the
                    // outer icon used. A second icon does not promise another reply.
                    var initial = Open(s, aliases.First(Normal)); if (initial == null) return true;
                    ValidateStoredContents(s.Bag, initial.Items);
                    foreach (var item in initial.Items)
                        FindAnchor(s, new Transfer { Item = item });
                    s.Baseline = initial.Items;
                    s.BaselineRecorded = true;
                    Event(s, "Recorded one fresh shared-container contents budget; aliases are outer references, not independent inventories.");
                    Save(s); return true;
                }
                if (s.Phase != "remove-empty")
                {
                    // All references have one identity and one contents budget.
                    // While evacuating, use the accessible inventory reference;
                    // a failed refresh does not make its bank alias another bag.
                    var alias = aliases.First(Normal);
                    View source = Open(s, alias);
                    if (source == null) return true;
                    if (source.Items.Count == 0)
                        throw new InvalidOperationException("Source is empty but its original contents are not fully secured.");
                    Evacuate(s, alias, source); return true;
                }
                if (!ContentsSecured(s))
                    throw new InvalidOperationException("Empty-bag removal lacks verified evacuation receipts.");
                View unexpected;
                if (views.TryGetValue(s.Bag, out unexpected) && unexpected.Sequence > observationBoundary &&
                    unexpected.Items.Count != 0)
                    throw new InvalidOperationException("New contents appeared in the evacuated bag; retaining every reference.");
                // Recheck destinations before each empty-reference removal. The
                // source's empty proof is the durable exact-delta receipt (or its
                // original empty snapshot), not another Use on a deleted shell.
                foreach (int id in s.TargetBaselines.Select(t => t.Bag))
                {
                    var target = Aliases(id).SingleOrDefault();
                    if (target == null) throw new InvalidOperationException("Evacuated stock destination disappeared.");
                    // A bank destination was verified before its journaled return;
                    // its outer location was then confirmed on a fresh connection.
                    if (!Normal(target)) continue;
                    var view = Open(s, target); if (view == null) return true;
                    if (!TargetConserved(s, id, view.Items))
                        throw new InvalidOperationException("Evacuated stock no longer matches its verified receipts.");
                }
                if (aliases.Count <= 1 && s.Destinations.Count > 0)
                {
                    int id = s.Destinations[0];
                    var target = Aliases(id).SingleOrDefault();
                    if (target == null) throw new InvalidOperationException("Evacuation destination disappeared.");
                    if (Normal(target)) { IssueOuter(s, "return-target", target, 0); return true; }
                    s.Destinations.RemoveAt(0); Save(s); return true;
                }
                if (aliases.Count == 0)
                { Finish(s, "Damaged bag removed; evacuated items conserved and their accounting locations updated."); return true; }
                // The owner requested removal, not experiments with the empty bag.
                IssueOuter(s, "delete", aliases.OrderBy(a => a.Key == s.LastUnappliedAddress ? 1 : 0)
                    .ThenBy(a => Normal(a) ? 0 : 1).First(), 0); return true;
            }
            catch (Exception ex)
            {
                Warn(ex.Message); retryAge.Restart();
                // Durable pending actions survive and will be observed, not resent.
                return true;
            }
        }
        private static bool Headroom(State s)
        {
            if (inventoryCount < 30) return true;
            var clean = inventory.Where(Normal).FirstOrDefault(a => a.Bag != s.Bag && Aliases(a.Bag).Count == 1);
            if (clean == null || Math.Min(Inventory.Bank.NumFreeSlots, 102 - Inventory.Bank.Items.Count) <= 0)
                throw new InvalidOperationException("No safe inventory staging capacity; retaining stock and retrying.");
            // Ordinary healthy outer-bag move, with full reconnect verification.
            // Completion rebinds its outer location without scanning its contents.
            IssueOuter(s, "park", clean, 0); return false;
        }
        private static View Open(State s, Address address)
        {
            View fresh;
            // Only wire snapshots, never Inventory.Containers' inferred contents.
            // All outer references to this identity share this observation.
            if (views.TryGetValue(address.Bag, out fresh) && fresh.Sequence > observationBoundary)
            {
                if (!opened.Values.Any(v => v.Sequence == fresh.Sequence))
                    Logger.Information("[CityBankers] BAG RECOVERY observed shared container " + address.Bag +
                        "; records=" + fresh.Items.Count + "; incoming sequence=" + fresh.Sequence);
                opened[address.Key] = fresh;
                if (requestedOpen == address.Key) requestedOpen = null;
                if (s.ReadFailures.RemoveAll(f => f.Bag == address.Bag) != 0) { s.FirstReadRefreshEpoch = null; Save(s); }
                return fresh;
            }
            if (DateTime.UtcNow < s.RetryAfterUtc) return null;
            if (views.ContainsKey(address.Bag))
            {
                // This connection already supplied this container. Repeating Use
                // after a move does not promise another contents packet. Refresh
                // now and verify the retained intent from the new snapshots.
                Reconnect("Fresh contents required after a mutation of container " + address.Bag);
                return null;
            }
            if (requestedOpen == address.Key)
            {
                if (openAge.ElapsedMilliseconds < 15000) return null;
                s.LastUnappliedAddress = address.Key;
                var failed = s.ReadFailures.SingleOrDefault(f => f.Bag == address.Bag);
                if (failed == null) { failed = new BagRecoveryReadFailure { Bag = address.Bag }; s.ReadFailures.Add(failed); }
                int failures = failed.Count = Math.Min(6, failed.Count + 1);
                s.FirstReadRefreshEpoch = failures == 1 ? epoch : null;
                s.RetryAfterUtc = DateTime.UtcNow.AddSeconds(Math.Min(960, 15 * (1 << failures)));
                Event(s, "No fresh shared-container snapshot for " + address.Key +
                    "; stop Use replay and refresh connection; retry after " + s.RetryAfterUtc.ToString("O"));
                Save(s);
                Reconnect("Contents unavailable after one Use at " + address.Key);
                return null;
            }
            if (requestedOpen != null) throw new InvalidOperationException("Another container observation is pending.");
            requestedOpen = address.Key; openAge.Restart();
            Logger.Information("[CityBankers] BAG RECOVERY opening shared container " + address.Bag + " through " + address.Key);
            Live(address).Use();
            return null;
        }
        private static void InvalidateViews(bool mutation = true)
        {
            opened.Clear(); requestedOpen = null;
            // Retiring a verified intent is not an inventory mutation: preserve
            // its actual after-snapshots until the next action is about to be sent.
            if (mutation) observationBoundary = sequence;
        }
        private static Dictionary<string, int> Quantities(IEnumerable<Content> items) => items
            .GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        private static bool Equal(IEnumerable<Content> a, IEnumerable<Content> b)
        {
            var x = Quantities(a); var y = Quantities(b);
            return x.Count == y.Count && x.All(p => y.ContainsKey(p.Key) && y[p.Key] == p.Value);
        }
        private static bool Exact(IEnumerable<Content> a, IEnumerable<Content> b) =>
            a.Select(i => i.Slot + "/" + i.Key + "/" + i.Quantity).OrderBy(k => k)
                .SequenceEqual(b.Select(i => i.Slot + "/" + i.Key + "/" + i.Quantity).OrderBy(k => k));
        private static bool ContentsSecured(State s) => s.BaselineRecorded &&
            s.Baseline.Count == s.Transfers.Count &&
            s.Transfers.All(t => t.Item != null && t.SourceBag == s.Bag &&
                t.VerifiedUtc != default(DateTime) && t.TargetSlot >= 0 && !string.IsNullOrEmpty(t.AnchorId) &&
                s.TargetBaselines.Any(target => target.Bag == t.TargetBag)) &&
            s.Transfers.Select(t => t.AnchorId).Distinct().Count() == s.Transfers.Count &&
            s.Transfers.Select(t => t.TargetBag + "/" + t.TargetSlot).Distinct().Count() == s.Transfers.Count &&
            Equal(s.Baseline, s.Transfers.Select(t => t.Item));
        private static bool TargetConserved(State s, int id, List<Content> observed)
        {
            return Exact(s.TargetBaselines.Single(t => t.Bag == id).Items.Concat(
                s.Transfers.Where(t => t.TargetBag == id).Select(t => new Content
                {
                    Slot = t.TargetSlot, Low = t.Item.Low, High = t.Item.High,
                    Ql = t.Item.Ql, Identity = t.Item.Identity, Quantity = t.Item.Quantity
                })), observed);
        }
        private static bool Delta(List<Content> before, List<Content> after, Content item, int sign)
        {
            var x = Quantities(before); var y = Quantities(after);
            int amount; x.TryGetValue(item.Key, out amount); amount += sign * item.Quantity;
            if (amount < 0) return false;
            if (amount == 0) x.Remove(item.Key); else x[item.Key] = amount;
            return x.Count == y.Count && x.All(p => y.ContainsKey(p.Key) && y[p.Key] == p.Value);
        }
        private static void Evacuate(State s, Address source, View content)
        {
            Content item = content.Items.OrderBy(i => i.Slot).First();
            int budget = s.Baseline.Where(i => i.Key == item.Key).Sum(i => i.Quantity);
            int secured = s.Transfers.Where(t => t.Item.Key == item.Key).Sum(t => t.Item.Quantity);
            if (secured + item.Quantity > budget)
            {
                throw new InvalidOperationException("Source exceeds its recorded contents budget; retaining every item for administrator review.");
            }
            var target = Outer().GroupBy(a => a.Bag).Where(g => g.Count() == 1 && g.Key != s.Bag &&
                !s.FullTargets.Contains(g.Key)).Select(g => g.Single())
                .Where(a => Normal(a) || a.Type == (int)IdentityType.BankByRef)
                .OrderBy(a => Normal(a) ? 0 : 1).ThenBy(a => a.Slot).FirstOrDefault();
            if (target == null) throw new InvalidOperationException("No distinct destination bag with available capacity.");
            if (!Normal(target))
            {
                if (!Headroom(s)) return;
                if (!s.Destinations.Contains(target.Bag)) s.Destinations.Add(target.Bag);
                IssueOuter(s, "stage-target", target, 0); return;
            }
            View destination = Open(s, target);
            if (destination == null) return;
            if (destination.Items.Count >= 21)
            {
                s.FullTargets.Add(target.Bag);
                if (s.Destinations.Contains(target.Bag)) IssueOuter(s, "return-target", target, 0);
                else Save(s);
                return;
            }
            if (item.Identity.StartsWith("(Container:", StringComparison.Ordinal))
                throw new InvalidOperationException("Nested container in source; automatic item relocation is not safe.");
            // Whole-record moves; no split or merge request is fabricated here.
            var liveContainer = Inventory.Containers.SingleOrDefault(c => c.Identity.Instance == source.Bag);
            var liveTarget = Inventory.Containers.SingleOrDefault(c => c.Identity.Instance == target.Bag);
            var moving = liveContainer?.Items.SingleOrDefault(i => (i.Slot.Instance & 65535) == item.Slot &&
                i.Id == item.Low && i.HighId == item.High && i.Ql == item.Ql);
            if (moving == null || liveTarget == null || liveContainer.Handle != content.Handle ||
                liveTarget.Handle != destination.Handle)
            {
                InvalidateViews();
                throw new InvalidOperationException("Container handle changed before evacuation; await fresh observation.");
            }
            var receipt = new Transfer { Id = Guid.NewGuid().ToString("N"), SourceBag = s.Bag,
                TargetBag = target.Bag, TargetSlot = -1, Item = item };
            FindAnchor(s, receipt);
            if (!s.TargetBaselines.Any(t => t.Bag == target.Bag))
            {
                ValidateStoredContents(target.Bag, destination.Items);
                s.TargetBaselines.Add(new BagRecoveryTarget { Bag = target.Bag, Items = destination.Items });
            }
            else if (!TargetConserved(s, target.Bag, destination.Items))
                throw new InvalidOperationException("Evacuation destination no longer matches its verified receipts; retaining both bags.");
            s.Pending = new Pending { Id = receipt.Id, Kind = "transfer", Epoch = epoch,
                Source = source, TargetBag = target.Bag, SourceBefore = content.Items,
                TargetBefore = destination.Items, Transfer = receipt };
            Save(s); InvalidateViews();
            BagOriginTrace.MoveToContainer(moving, liveTarget);
        }
        private static void IssueOuter(State s, string kind, Address source, int target)
        {
            Item item = Live(source);
            if (kind == "delete")
            {
                if (source.Bag != s.Bag || s.Phase != "remove-empty" || !ContentsSecured(s))
                    throw new InvalidOperationException("Only the verified-empty damaged bag may be removed.");
            }
            s.Pending = new Pending { Id = Guid.NewGuid().ToString("N"), Kind = kind,
                Epoch = epoch, Source = source, TargetBag = target, Before = Outer() };
            Event(s, "Issuing " + kind + " at " + source.Key + "; outer result requires verification.");
            Save(s); InvalidateViews();
            switch (kind)
            {
                case "stage": case "stage-target": case "restore-social": BagOriginTrace.MoveToInventory(item); break;
                case "park": case "return-target": BagOriginTrace.MoveToBank(item); break;
                case "delete": item.Delete(); break;
                default: throw new InvalidOperationException("Unknown recovery action.");
            }
        }
        private static void Resolve(State s)
        {
            Pending p = s.Pending;
            if (pendingId != p.Id) { pendingId = p.Id; pendingAge.Restart(); }
            if (p.Kind == "transfer")
            {
                // Reopen BOTH ends; local OnContainerAction's slot inference is
                // not completion evidence. Reconnect automatically on uncertainty.
                // Contents belong to the container identity. A reconnect can
                // retire one outer reference without changing that identity.
                var source = Aliases(p.Source.Bag).Where(Normal)
                    .OrderBy(address => address.Key == p.Source.Key ? 0 : 1).FirstOrDefault();
                var target = Aliases(p.TargetBag).SingleOrDefault();
                if (source == null || target == null || !Normal(target))
                    throw new InvalidOperationException("Pending transfer endpoints are not observable in inventory.");
                View sourceView = Open(s, source); if (sourceView == null) return;
                View targetView = Open(s, target); if (targetView == null) return;
                if (Delta(p.SourceBefore, sourceView.Items, p.Transfer.Item, -1) &&
                    Delta(p.TargetBefore, targetView.Items, p.Transfer.Item, 1))
                {
                    var arrivals = targetView.Items.Where(i => i.Key == p.Transfer.Item.Key &&
                        !p.TargetBefore.Any(old => old.Slot == i.Slot)).ToList();
                    if (arrivals.Count != 1)
                        throw new InvalidOperationException("Evacuation destination slot is not uniquely observed; retaining the pending intent.");
                    p.Transfer.TargetSlot = arrivals[0].Slot;
                    p.Transfer.VerifiedUtc = DateTime.UtcNow;
                    s.Transfers.Add(p.Transfer);
                    Event(s, "Evacuated one observed record with exact source/destination quantity deltas.");
                    s.Pending = null; s.UnappliedAttempts = 0; s.RetryAfterUtc = default(DateTime);
                    Save(s); InvalidateViews(false); return;
                }
                if (p.Epoch == epoch)
                {
                    if (pendingAge.ElapsedMilliseconds >= 3000) Reconnect("Uncertain " + p.Kind + " outcome at " + p.Source.Key);
                    return;
                }
                if (Exact(p.SourceBefore, sourceView.Items) && Exact(p.TargetBefore, targetView.Items))
                {
                    // A new connection confirms that neither endpoint changed.
                    // Retire the old intent; next tick selects a fresh live record.
                    Event(s, "Unapplied item intent reconciled after reconnect; retry from fresh contents.");
                    s.Pending = null; Backoff(s); Save(s); InvalidateViews(false); return;
                }
                InvalidateViews();
                throw new InvalidOperationException("Evacuation delta is uncertain; retaining both endpoints and retrying observations.");
            }
            if (p.Epoch == epoch)
            {
                if (pendingAge.ElapsedMilliseconds >= 3000) Reconnect("Verify " + p.Kind + " outer addresses after action at " + p.Source.Key);
                return;
            }
            var beforeOther = p.Before.Where(a => a.Bag != p.Source.Bag).Select(a => a.Bag).OrderBy(i => i);
            var afterOther = Outer().Where(a => a.Bag != p.Source.Bag).Select(a => a.Bag).OrderBy(i => i);
            if (!beforeOther.SequenceEqual(afterOther))
                throw new InvalidOperationException("Unrelated bag population changed during recovery action.");
            var remaining = Aliases(p.Source.Bag);
            bool same = p.Before.Where(a => a.Bag == p.Source.Bag).Select(a => a.Key).OrderBy(k => k)
                .SequenceEqual(remaining.Select(a => a.Key).OrderBy(k => k));
            var beforeSource = p.Before.Where(a => a.Bag == p.Source.Bag).ToList();
            bool staged = remaining.Count(Normal) > beforeSource.Count(Normal) &&
                remaining.Count <= beforeSource.Count;
            if (p.Kind == "stage" || p.Kind == "stage-target" || p.Kind == "restore-social")
            {
                if (!staged && !same) throw new InvalidOperationException("Outer staging not verified; retaining intent without resend.");
                if (same) retryAge.Restart();
            }
            else if (p.Kind == "return-target" || p.Kind == "park")
            {
                if (!same && (remaining.Count != 1 || remaining[0].Type != (int)IdentityType.BankByRef))
                    throw new InvalidOperationException("Destination return not verified.");
                if (!same && p.Kind == "return-target") s.Destinations.Remove(p.Source.Bag);
                if (same) retryAge.Restart();
            }
            else if (p.Kind == "delete" && same)
            {
                // A confirmed empty shell survived. Retire only this intent and
                // re-observe emptiness before any subsequent deletion attempt.
                retryAge.Restart();
            }
            if (same) { s.LastUnappliedAddress = p.Source.Key; Backoff(s); }
            else { s.LastUnappliedAddress = null; s.UnappliedAttempts = 0; s.RetryAfterUtc = default(DateTime); }
            if (p.Kind == "delete" && remaining.Count == 0)
                s.EmptyShellsDisappeared = true;
            Event(s, p.Kind + " observed after reconnect: before=" +
                string.Join(",", beforeSource.Select(a => a.Key)) + "; after=" +
                string.Join(",", remaining.Select(a => a.Key)) + (same ? "; no outer change" : "; outer layout changed"));
            s.Pending = null; Save(s); InvalidateViews(false);
        }
        private static void Backoff(State s)
        {
            s.FirstReadRefreshEpoch = null; // Never waive a mutation's no-effect backoff.
            s.UnappliedAttempts = Math.Min(6, s.UnappliedAttempts + 1);
            s.RetryAfterUtc = DateTime.UtcNow.AddSeconds(Math.Min(960, 15 * (1 << s.UnappliedAttempts)));
        }
        private static void Finish(State s, string reason)
        {
            if (!s.BaselineRecorded || !s.EmptyShellsDisappeared || Aliases(s.Bag).Count != 0 ||
                !Equal(s.Baseline, s.Transfers.Select(t => t.Item)))
                throw new InvalidOperationException("Repair completion lacks conserved contents or confirmed removal of the damaged bag.");
            CommitRepair(s);
            retained = false;
            Event(s, reason);
        }
        private static void FindAnchor(State s, Transfer transfer)
        {
            var ledger = CityDwellers.Shared.BankerState.ReadLedger<ActiveLedgerState>();
            var storage = RuntimeStateStore.LoadStorageState(settings);
            var bags = (storage?.Workers ?? new List<StorageWorkerState>()).Where(w =>
                string.Equals(w.Character, s.Character, StringComparison.OrdinalIgnoreCase))
                .SelectMany(w => w.Bags ?? new List<StorageBagState>())
                .Where(b => b.LastUniqueIdentity == new Identity(IdentityType.Container, s.Bag).ToString()).ToList();
            var candidates = (ledger?.Items ?? new List<ActiveLedgerItem>()).Where(i =>
                i.Character == s.Character && i.AoId == transfer.Item.Low && i.HighId == transfer.Item.High &&
                i.Ql == transfer.Item.Ql && i.Slot == transfer.Item.Slot &&
                bags.Any(b => i.Location == b.Source && i.Bag == (b.OuterSlotInstance & 65535)) &&
                !s.Transfers.Any(t => t.AnchorId == i.Id)).ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException("Evacuation requires one existing ledger owner for each item; no item was moved for this intent.");
            var anchor = candidates[0]; transfer.AnchorId = anchor.Id;
            transfer.AnchorTransaction = anchor.TransactionId; transfer.AnchorReceivedUtc = anchor.ReceivedUtc;
            transfer.OriginalLocation = anchor.Location; transfer.OriginalBag = anchor.Bag; transfer.OriginalSlot = anchor.Slot;
        }

        private static StorageWorkerState StoredWorker(StorageState storage) => storage?.Workers?.SingleOrDefault(w =>
            string.Equals(w.Character, Client.CharacterName, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidOperationException("Repair storage owner is unavailable.");

        private static void ValidateStoredContents(int bag, List<Content> contents)
        {
            var worker = StoredWorker(RuntimeStateStore.LoadStorageState(settings));
            string identity = new Identity(IdentityType.Container, bag).ToString();
            var stored = worker.Bags.SingleOrDefault(b => b.LastUniqueIdentity == identity);
            if (stored?.Items == null || stored.Items.Count != contents.Count ||
                contents.Select(i => i.Slot).Distinct().Count() != contents.Count ||
                contents.Any(i => i.Quantity != 1 || !stored.Items.Any(old =>
                    old.InnerSlot == i.Slot && old.AoId == i.Low && old.HighId == i.High && old.Ql == i.Ql)))
                throw new InvalidOperationException("Fresh repair contents differ from recorded stock; retaining the banker and all items.");
            var anchors = CityDwellers.Shared.ManagerMemory.Current.ReadLedger(
                CityDwellers.Shared.ManagerAccounting.TransactionId).Items.Where(i =>
                    string.Equals(i.Character, worker.Character, StringComparison.OrdinalIgnoreCase) &&
                    i.Location == stored.Source && i.Bag == (stored.OuterSlotInstance & 65535)).ToList();
            if (anchors.Count != contents.Count || contents.Any(i => anchors.Count(a =>
                a.Slot == i.Slot && a.AoId == i.Low && a.HighId == i.High && a.Ql == i.Ql) != 1))
                throw new InvalidOperationException("Repair contents and ledger ownership disagree; retaining every item.");
        }

        private static void CommitRepair(State s)
        {
            if (!SnapshotLayoutMatches())
                throw new InvalidOperationException("Repair final outer layout is not confirmed.");
            CityDwellers.Shared.ManagerAccounting.Transaction("Complete affected banker bag repair", () =>
            {
                var manager = CityDwellers.Shared.ManagerMemory.Current;
                string transaction = CityDwellers.Shared.ManagerAccounting.TransactionId;
                var storage = RuntimeStateStore.LoadStorageState(settings);
                var worker = StoredWorker(storage);
                var ledger = manager.ReadLedger(transaction);
                string damaged = new Identity(IdentityType.Container, s.Bag).ToString();
                var source = worker.Bags.SingleOrDefault(b => b.LastUniqueIdentity == damaged);
                if (source == null || source.Items.Count != s.Transfers.Count ||
                    s.Transfers.Select(t => t.AnchorId).Distinct().Count() != s.Transfers.Count ||
                    s.Transfers.Any(t => t.AnchorId == null || t.TargetSlot < 0))
                    throw new InvalidOperationException("Repair cannot map every stored item to one verified destination.");
                var outer = Outer().GroupBy(a => new Identity(IdentityType.Container, a.Bag).ToString())
                    .ToDictionary(g => g.Key, g => g.ToList());
                var expectedIds = worker.Bags.Where(b => b.LastUniqueIdentity != damaged).Select(b => b.LastUniqueIdentity).ToList();
                if (expectedIds.Distinct().Count() != expectedIds.Count ||
                    !expectedIds.OrderBy(i => i).SequenceEqual(outer.Keys.OrderBy(i => i)))
                    throw new InvalidOperationException("Unrelated storage bags changed during repair.");
                int originalCount = worker.Bags.Select(b => b.LastUniqueIdentity).Distinct().Count();
                var original = worker.Bags.ToList();
                var unchangedAnchors = ledger.Items.Where(i => i.Character == s.Character &&
                    !s.Transfers.Any(t => t.AnchorId == i.Id)).Select(i => new { Item = i,
                        Bag = original.SingleOrDefault(b => b != source && i.Location == b.Source &&
                            i.Bag == (b.OuterSlotInstance & 65535)) }).Where(p => p.Bag != null).ToList();
                foreach (var t in s.Transfers)
                {
                    var item = source.Items.SingleOrDefault(i => i.InnerSlot == t.Item.Slot &&
                        i.AoId == t.Item.Low && i.HighId == t.Item.High && i.Ql == t.Item.Ql);
                    var anchor = ledger.Items.SingleOrDefault(i => i.Id == t.AnchorId && i.Character == s.Character &&
                        i.TransactionId == t.AnchorTransaction && i.ReceivedUtc == t.AnchorReceivedUtc &&
                        i.Location == t.OriginalLocation && i.Bag == t.OriginalBag && i.Slot == t.OriginalSlot);
                    var target = worker.Bags.SingleOrDefault(b => b.LastUniqueIdentity ==
                        new Identity(IdentityType.Container, t.TargetBag).ToString());
                    if (item == null || anchor == null || target == null || target.Items.Any(i => i.InnerSlot == t.TargetSlot))
                        throw new InvalidOperationException("Repair accounting conflicts with a retained item or destination slot.");
                    // These objects are Manager read copies. Their donor, receipt
                    // time, transaction and ledger ID stay unchanged.
                    source.Items.Remove(item);
                    item.InnerSlot = t.TargetSlot;
                    item.ObservedUtc = DateTime.UtcNow;
                    target.Items.Add(item);
                    var address = outer[target.LastUniqueIdentity].Single();
                    anchor.Location = Normal(address) ? "inventory" : "bank";
                    anchor.Bag = address.Slot & 65535;
                    anchor.Slot = t.TargetSlot;
                }
                if (source.Items.Count != 0)
                    throw new InvalidOperationException("The damaged bag still owns recorded items.");
                // Rebind other bags moved for staging by identity, using fresh
                // outer snapshots. Their contents are neither scanned nor rebuilt.
                foreach (var binding in unchangedAnchors)
                {
                    var addresses = outer[binding.Bag.LastUniqueIdentity];
                    // Another damaged bag is repaired on the next pass. None of
                    // its references was selected or moved by this operation.
                    if (addresses.Count != 1) continue;
                    var address = addresses[0];
                    binding.Item.Location = Normal(address) ? "inventory" : "bank";
                    binding.Item.Bag = address.Slot & 65535;
                }
                foreach (var bag in original.Where(b => b != source))
                {
                    var addresses = outer[bag.LastUniqueIdentity];
                    if (addresses.Count != 1) continue;
                    var address = addresses[0];
                    string location = Normal(address) ? "inventory" : "bank";
                    bag.Source = location;
                    bag.OuterSlotType = ((IdentityType)address.Type).ToString();
                    bag.OuterSlotInstance = address.Slot & 65535;
                    bag.LastHandle = 0;
                }
                worker.Bags.Remove(source);
                worker.ObservedUtc = storage.UpdatedUtc = ledger.UpdatedUtc = DateTime.UtcNow;
                var reserve = manager.ReadReserve(transaction);
                int previousTarget;
                if (!reserve.TryGetTarget(s.Character, out previousTarget) || previousTarget < originalCount)
                    reserve.SetTarget(s.Character, originalCount);
                manager.ChangeReserve(transaction, reserve);
                manager.ChangeLedger(transaction, ledger);
                manager.ChangeStorage(transaction, storage);
                RuntimeStateStore.SaveCurrentStock(RuntimeStateStore.BuildCurrentStock(settings, storage));
                manager.ChangeBagHistory(transaction, new BagHistoryRecord { Run = s.Run, Character = s.Character,
                    Bag = s.Bag, Phase = "complete", EmptyShellsDisappeared = true, UpdatedUtc = DateTime.UtcNow,
                    ReconciledUtc = DateTime.UtcNow, Transfers = s.Transfers });
                s.Phase = "complete";
                manager.ChangeBagRecovery(transaction, s);
            });
        }
        internal static void MarkReconciled(string directory, IEnumerable<string> characters)
        {
            foreach (string character in characters.Distinct(StringComparer.OrdinalIgnoreCase))
                CityDwellers.Shared.ManagerMemory.Current.ReconcileBagHistory(CityDwellers.Shared.ManagerAccounting.TransactionId, character);
        }
        internal static void ApplyProvenance(string directory, IEnumerable<PhysicalLedgerReconciliation.Observation> observations,
            IEnumerable<ActiveLedgerItem> anchors)
        {
            var all = observations.ToList(); var previous = anchors.ToList();
            foreach (string character in all.Select(o => o.Character).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var s in CityDwellers.Shared.ManagerMemory.Current.ReadBagHistory(
                    CityDwellers.Shared.ManagerAccounting.TransactionId, character))
                {
                    if (s == null || s.Character != character || s.Phase != "complete" || s.ReconciledUtc != default(DateTime)) continue;
                    foreach (var t in s.Transfers.Where(t => t.AnchorId != null && t.TargetSlot >= 0))
                    {
                        var a = previous.SingleOrDefault(i => i.Id == t.AnchorId && i.Character == character &&
                            i.Location == t.OriginalLocation && i.Bag == t.OriginalBag && i.Slot == t.OriginalSlot &&
                            i.TransactionId == t.AnchorTransaction && i.ReceivedUtc == t.AnchorReceivedUtc);
                        var matches = all.Where(o => o.Character == character && o.BagIdentity ==
                            new Identity(IdentityType.Container, t.TargetBag).ToString() && o.Slot == t.TargetSlot &&
                            o.Item.LowId == t.Item.Low && o.Item.HighId == t.Item.High && o.Item.Ql == t.Item.Ql).ToList();
                        if (a != null && matches.Count == 1) matches[0].RecoveryLedgerId = a.Id;
                    }
                }
            }
        }
    }
}
