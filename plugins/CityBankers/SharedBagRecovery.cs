using File = CityDwellers.Shared.DiskFiles;
using Directory = System.IO.Directory;
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
    // Only StartupCensusGate may drive this actor, after quiescing operational
    // actors and before issuing a census. The journal is written BEFORE an AO
    // mutation. Restart observes the outstanding action; it never blindly replays it.
    internal static class SharedBagRecovery
    {
        internal sealed class Address
        {
            public int Type, Slot, Bag;
            public string Key => Type + "/" + Slot + "/" + Bag;
        }
        internal sealed class Content
        {
            public int Slot, Low, High, Ql, Quantity;
            public string Identity;
            public string Key => Low + "/" + High + "/" + Ql + "/" + Identity;
        }
        private sealed class View
        {
            public long Sequence;
            public int Handle;
            public List<Content> Items;
        }
        internal sealed class Transfer
        {
            public string Id, AnchorId, OriginalLocation, AnchorTransaction;
            public DateTime AnchorReceivedUtc;
            public int? OriginalBag, OriginalSlot;
            public int SourceBag, TargetBag, TargetSlot;
            public Content Item;
            public DateTime VerifiedUtc;
        }
        internal sealed class Pending
        {
            public string Id, Kind, Epoch;
            public Address Source;
            public int TargetBag;
            public List<Address> Before;
            public List<Content> SourceBefore, TargetBefore;
            public Transfer Transfer;
        }
        internal sealed class State
        {
            public string Format = "citybankers-shared-bag-recovery-v1";
            public string Run, Character, Phase, Epoch;
            public string FirstReadRefreshEpoch;
            public int Bag, Experiment;
            public List<Address> Original;
            public List<int> Destinations = new List<int>();
            public List<int> FullTargets = new List<int>();
            public List<Transfer> Transfers = new List<Transfer>();
            public List<Transfer> Disposals = new List<Transfer>();
            public List<Content> Baseline;
            public Dictionary<int, List<Content>> TargetBaselines = new Dictionary<int, List<Content>>();
            public List<string> Events = new List<string>();
            public Dictionary<int, int> ReadFailures = new Dictionary<int, int>();
            public Pending Pending;
            public DateTime UpdatedUtc, RetryAfterUtc, ReconciledUtc;
            public int UnappliedAttempts;
            public bool EmptyShellsDisappeared;
        }
        private static string settings, path, history, epoch, bankEpoch, lastError, requestedOpen;
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
            history = Path.Combine(root, "history");
            // Unreadable persistence is a hold, never a fresh recovery plan.
            retained = CityDwellers.Shared.ManagerMemory.Current.ReadBagHistory(
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
                return Inventory.Items.Concat(Inventory.Bank.Items).Where(i => i != null &&
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
            var s = CensusApplication.ReadExisting<State>(path);
            if (s != null && (s.Format != "citybankers-shared-bag-recovery-v1" ||
                s.Character != Client.CharacterName || s.Transfers == null || s.Destinations == null ||
                s.Events == null || s.FullTargets == null || s.Disposals == null || s.TargetBaselines == null ||
                s.Original == null || s.ReadFailures == null || string.IsNullOrEmpty(s.Run) ||
                (s.Phase != "confirm" && s.Phase != "evacuate" && s.Phase != "complete")))
                throw new InvalidOperationException("Invalid shared-bag recovery journal.");
            return s;
        }
        private static void Save(State s)
        {
            s.UpdatedUtc = DateTime.UtcNow;
            RuntimeStateStore.WriteJsonAtomic(path, s);
            retained = true;
        }
        private static void Event(State s, string text)
        {
            if (s.Events.Count >= 256) s.Events.RemoveAt(0);
            s.Events.Add(DateTime.UtcNow.ToString("O") + " " + text);
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
        // True means this banker remains held. Called only with census ownership.
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
                if (s.Phase == "complete")
                {
                    RuntimeStateStore.WriteJsonAtomic(Path.Combine(history, s.Run + ".json"), s);
                    File.Delete(path); retained = false; return true;
                }
                if (Outer().GroupBy(a => a.Type + "/" + a.Slot).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("Server snapshot repeats an outer slot; no unique action address.");
                if (s.Phase == "confirm")
                {
                    if (!SnapshotLayoutMatches()) { Reconnect("Resume confirmation requires consistent outer snapshots."); return true; }
                    if (Aliases(s.Bag).Count <= 1) { Finish(s, "Repeated reference cleared on reconnect."); return true; }
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
                if (DateTime.UtcNow < s.RetryAfterUtc) return true;
                var aliases = Aliases(s.Bag);
                if (aliases.Count == 0 && !s.EmptyShellsDisappeared)
                    throw new InvalidOperationException("Source disappeared without a verified empty-shell action.");
                if (aliases.Any(a => !Normal(a) && a.Type != (int)IdentityType.BankByRef &&
                    !(a.Type == (int)IdentityType.SocialPage && a.Slot == (int)EquipSlot.Social_Back)))
                    throw new InvalidOperationException("Suspect bag occupies protected equipment; refusing to replace equipment.");

                // Stage one reference at a time. Reconnect after every outer move
                // removes reliance on the SDK's inferred Bank.NextAvailableSlot.
                var inBank = aliases.FirstOrDefault(a => a.Type == (int)IdentityType.BankByRef);
                if (inBank != null)
                {
                    if (!Headroom(s)) return true;
                    IssueOuter(s, "stage", inBank, 0); return true;
                }
                var social = aliases.FirstOrDefault(a => a.Type == (int)IdentityType.SocialPage);
                if (social != null)
                {
                    if (!Headroom(s)) return true;
                    IssueOuter(s, "restore-social", social, 0); return true;
                }
                if (s.Baseline == null)
                {
                    if (aliases.Count == 0) throw new InvalidOperationException("Missing source baseline.");
                    // InventoryUpdate identifies the shared container, not the
                    // outer icon used. A second icon does not promise another reply.
                    var initial = Open(s, aliases[0]); if (initial == null) return true;
                    s.Baseline = initial.Items;
                    Event(s, "Recorded one fresh shared-container contents budget; aliases are outer references, not independent inventories.");
                    Save(s); return true;
                }
                foreach (var alias in aliases)
                {
                    View source = Open(s, alias);
                    if (source == null) return true;
                    if (source.Items.Count == 0) continue;
                    if (s.Phase != "evacuate")
                    { s.Phase = "evacuate"; Save(s); return true; }
                    Evacuate(s, alias, source); return true;
                }
                if (!Equal(s.Baseline, s.Transfers.Select(t => t.Item)))
                    throw new InvalidOperationException("Source is empty but the initial contents are not fully accounted for in destinations.");
                // The shared identity has an incoming empty snapshot newer than the
                // last mutation. Native cached contents alone are insufficient.
                foreach (int id in s.TargetBaselines.Keys)
                {
                    var target = Aliases(id).SingleOrDefault();
                    if (target == null) throw new InvalidOperationException("Evacuated stock destination disappeared.");
                    // Bank destinations were verified before their journaled return;
                    // they will be independently read again by the following census.
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
                if (aliases.Count <= 1) { Finish(s, "Empty-shell recovery completed; evacuated quantities conserved; normal census will verify final stock."); return true; }
                if (s.Experiment == 0)
                {
                    if (Inventory.Bank.NumFreeSlots <= 0) { s.Experiment = 1; Save(s); return true; }
                    IssueOuter(s, "bank-test", aliases[0], 0); return true;
                }
                if (s.Experiment == 1)
                {
                    bool occupied = Inventory.Items.Any(i => i != null && i.Slot.Instance == (int)EquipSlot.Social_Back);
                    if (occupied) { s.Experiment = 2; Save(s); return true; }
                    IssueOuter(s, "social-test", aliases[0], 0); return true;
                }
                // Owner-authorized last resort. Only empty shells are deleted.
                // Items are NEVER classified as dupes by template/QL equality.
                IssueOuter(s, "delete", aliases[0], 0); return true;
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
            if (clean == null || Inventory.Bank.NumFreeSlots <= 0)
                throw new InvalidOperationException("No safe inventory staging capacity; retaining stock and retrying.");
            // Ordinary healthy outer-bag move, with full reconnect verification.
            // The following census records its new location and preserves anchors.
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
                if (s.ReadFailures.Remove(address.Bag)) { s.FirstReadRefreshEpoch = null; Save(s); }
                return fresh;
            }
            if (DateTime.UtcNow < s.RetryAfterUtc) return null;
            if (requestedOpen == address.Key)
            {
                if (openAge.ElapsedMilliseconds < 15000) return null;
                int failures; s.ReadFailures.TryGetValue(address.Bag, out failures);
                failures = Math.Min(6, failures + 1); s.ReadFailures[address.Bag] = failures;
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
        private static bool TargetConserved(State s, int id, List<Content> observed)
        {
            return Equal(s.TargetBaselines[id].Concat(s.Transfers.Where(t => t.TargetBag == id).Select(t => t.Item)), observed);
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
                DisposeExcess(s, source, content, item, budget, secured); return;
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
            if (!s.TargetBaselines.ContainsKey(target.Bag)) s.TargetBaselines[target.Bag] = destination.Items;
            s.Pending = new Pending { Id = receipt.Id, Kind = "transfer", Epoch = epoch,
                Source = source, TargetBag = target.Bag, SourceBefore = content.Items,
                TargetBefore = destination.Items, Transfer = receipt };
            Save(s); InvalidateViews();
            BagOriginTrace.MoveToContainer(moving, liveTarget);
        }
        private static void DisposeExcess(State s, Address source, View content, Content item, int budget, int secured)
        {
            // Extra means the SAME initially observed shared-container record
            // reappeared after its entire one-container budget was safely secured.
            // Neither template equality nor an old ledger count authorizes deletion.
            if (budget <= 0 || secured != budget || !s.Baseline.Any(i =>
                i.Slot == item.Slot && i.Key == item.Key && i.Quantity == item.Quantity) ||
                s.Disposals.Count >= Math.Max(1, s.Original.Count) * Math.Max(1, s.Baseline.Count))
                throw new InvalidOperationException("Unexpected excess is not proven against the recovery baseline; retaining it.");
            var receipt = s.Transfers.FirstOrDefault(t => t.Item.Key == item.Key && t.TargetSlot >= 0);
            if (receipt == null) throw new InvalidOperationException("No independently located original backs excess disposal.");
            foreach (int id in s.Transfers.Where(t => t.Item.Key == item.Key).Select(t => t.TargetBag).Distinct())
            {
                var address = Aliases(id).SingleOrDefault();
                if (address == null) throw new InvalidOperationException("Secured original destination disappeared.");
                if (!Normal(address))
                {
                    if (!Headroom(s)) return;
                    if (!s.Destinations.Contains(id)) s.Destinations.Add(id);
                    IssueOuter(s, "stage-target", address, 0); return;
                }
                var fresh = Open(s, address); if (fresh == null) return;
                if (!TargetConserved(s, id, fresh.Items))
                    throw new InvalidOperationException("Secured originals changed; excess disposal prohibited.");
            }
            var target = Aliases(receipt.TargetBag).Single();
            var targetView = Open(s, target); if (targetView == null) return;
            var live = Inventory.Containers.SingleOrDefault(c => c.Identity.Instance == source.Bag);
            var extra = live?.Items.SingleOrDefault(i => (i.Slot.Instance & 65535) == item.Slot &&
                i.Id == item.Low && i.HighId == item.High && i.Ql == item.Ql);
            if (extra == null || live.Handle != content.Handle)
            { InvalidateViews(); throw new InvalidOperationException("Excess source handle changed."); }
            s.Pending = new Pending { Id = Guid.NewGuid().ToString("N"), Kind = "dispose", Epoch = epoch,
                Source = source, TargetBag = receipt.TargetBag, SourceBefore = content.Items,
                TargetBefore = targetView.Items, Transfer = new Transfer { SourceBag = s.Bag,
                    TargetBag = receipt.TargetBag, TargetSlot = receipt.TargetSlot, Item = item } };
            Save(s); InvalidateViews(); extra.Delete();
        }
        private static void IssueOuter(State s, string kind, Address source, int target)
        {
            Item item = Live(source);
            if (kind == "delete" || kind == "bank-test" || kind == "social-test")
            {
                if (Aliases(s.Bag).Any(a => !opened.ContainsKey(a.Key) || opened[a.Key].Items.Count != 0))
                    throw new InvalidOperationException("The shared identity needs a post-mutation empty snapshot before an experiment.");
            }
            s.Pending = new Pending { Id = Guid.NewGuid().ToString("N"), Kind = kind,
                Epoch = epoch, Source = source, TargetBag = target, Before = Outer() };
            Event(s, "Issuing " + kind + " at " + source.Key + "; outer result requires verification.");
            Save(s); InvalidateViews();
            switch (kind)
            {
                case "stage": case "stage-target": case "restore-social": BagOriginTrace.MoveToInventory(item); break;
                case "park": case "return-target": case "bank-test": BagOriginTrace.MoveToBank(item); break;
                case "social-test": item.Equip(EquipSlot.Social_Back); break;
                case "delete": item.Delete(); break;
                default: throw new InvalidOperationException("Unknown recovery action.");
            }
        }
        private static void Resolve(State s)
        {
            Pending p = s.Pending;
            if (pendingId != p.Id) { pendingId = p.Id; pendingAge.Restart(); }
            if (p.Kind == "transfer" || p.Kind == "dispose")
            {
                // Reopen BOTH ends; local OnContainerAction's slot inference is
                // not completion evidence. Reconnect automatically on uncertainty.
                var source = Aliases(p.Source.Bag).SingleOrDefault(address => address.Key == p.Source.Key);
                var target = Aliases(p.TargetBag).SingleOrDefault();
                if (source == null || target == null || !Normal(target))
                    throw new InvalidOperationException("Pending transfer endpoints are not observable in inventory.");
                View sourceView = Open(s, source); if (sourceView == null) return;
                View targetView = Open(s, target); if (targetView == null) return;
                if (Delta(p.SourceBefore, sourceView.Items, p.Transfer.Item, -1) &&
                    (p.Kind == "dispose" ? Exact(p.TargetBefore, targetView.Items) : Delta(p.TargetBefore, targetView.Items, p.Transfer.Item, 1)))
                {
                    var arrivals = targetView.Items.Where(i => i.Key == p.Transfer.Item.Key &&
                        !p.TargetBefore.Any(old => old.Slot == i.Slot)).ToList();
                    if (arrivals.Count == 1) p.Transfer.TargetSlot = arrivals[0].Slot;
                    p.Transfer.VerifiedUtc = DateTime.UtcNow;
                    if (p.Kind == "dispose") s.Disposals.Add(p.Transfer); else s.Transfers.Add(p.Transfer);
                    Event(s, p.Kind == "dispose" ? "Removed proven excess; secured original destination unchanged." :
                        "Evacuated one observed record with exact source/destination quantity deltas.");
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
            else if (p.Kind == "bank-test") s.Experiment = 1;
            else if (p.Kind == "social-test") s.Experiment = 2;
            else if (p.Kind == "delete" && same)
            {
                // A confirmed empty shell survived. Retire only this intent and
                // re-observe emptiness before any subsequent deletion attempt.
                retryAge.Restart();
            }
            if (same) Backoff(s); else { s.UnappliedAttempts = 0; s.RetryAfterUtc = default(DateTime); }
            if ((p.Kind == "delete" || p.Kind == "bank-test" || p.Kind == "social-test") && remaining.Count == 0)
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
            Event(s, reason); s.Phase = "complete"; Save(s);
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
            // Preserve an anchor only when its ownership is unambiguous. Retain all
            // original ledger records for the ordinary census history otherwise.
            if (candidates.Count != 1) return;
            var anchor = candidates[0]; transfer.AnchorId = anchor.Id;
            transfer.AnchorTransaction = anchor.TransactionId; transfer.AnchorReceivedUtc = anchor.ReceivedUtc;
            transfer.OriginalLocation = anchor.Location; transfer.OriginalBag = anchor.Bag; transfer.OriginalSlot = anchor.Slot;
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
