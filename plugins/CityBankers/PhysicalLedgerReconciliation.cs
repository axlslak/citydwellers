using System;
using System.Collections.Generic;
using System.Linq;
using CityBankers.Shared;

namespace CityBankers
{
    // Pure reconciliation: no AO calls, file writes, or guesses from event logs.
    // The caller must commit the plan together with its census generation before
    // enabling inventory movement. A partial census cannot establish absence.
    internal static class PhysicalLedgerReconciliation
    {
        internal sealed class Observation
        {
            public string Character;
            public string PhysicalRole;
            public string Location;
            public int? Bag;
            public int Slot;
            public string BagIdentity;
            public BagAuditAgent.BagInnerItem Item;
            public string DestinationRole;
            public DateTime ObservedUtc;
        }

        internal sealed class Difference
        {
            public string Kind;
            public ActiveLedgerItem Previous;
            public ActiveLedgerItem Current;
            public Observation Physical;
        }

        internal sealed class Plan
        {
            public string Format = "citybankers-physical-reconciliation-v1";
            public List<ActiveLedgerItem> Items = new List<ActiveLedgerItem>();
            public List<Difference> Differences = new List<Difference>();
            public List<Observation> Routing = new List<Observation>();
        }

        internal static List<Observation> ReadCensus(string settings,
            BagAuditAgent.BagAuditResult census)
        {
            if (census == null || string.IsNullOrWhiteSpace(census.Character) ||
                string.IsNullOrWhiteSpace(census.Role) ||
                string.IsNullOrWhiteSpace(census.RunId) || !census.BankOpened ||
                census.FatalError != null || census.FailedCount != 0 ||
                census.BankReturnFailureCount != 0 || census.Bags == null ||
                census.Bags.Count != census.TotalBagCount ||
                census.BankBagCount + census.InventoryBagCount != census.TotalBagCount ||
                census.Bags.Count(bag => bag != null && bag.Source == "bank") != census.BankBagCount ||
                census.Bags.Count(bag => bag != null && bag.Source == "inventory") != census.InventoryBagCount ||
                census.OpenedCount != census.TotalBagCount ||
                census.BankReturnedCount != census.BankBagCount ||
                census.LooseInventoryItems == null || census.LooseBankItems == null)
                throw new InvalidOperationException("Complete physical census required.");

            var observed = new List<Observation>();
            Action<BagAuditAgent.BagInnerItem, string, int?, string> add = (item, location, bag, identity) =>
            {
                if (item == null || item.LowId == 0 || item.SlotInstance < 0)
                    throw new InvalidOperationException("Census contains an unreadable item.");
                SymbiantCatalog.AcceptanceRule rule;
                string destination = SymbiantCatalog.TryGetRule(settings, item.LowId, out rule)
                    ? rule.Role : "central";
                observed.Add(new Observation
                {
                    Character = census.Character, PhysicalRole = census.Role,
                    Location = location, Bag = bag, Slot = item.SlotInstance & 65535,
                    BagIdentity = identity, Item = item, DestinationRole = destination,
                    ObservedUtc = census.ObservedUtc
                });
            };
            foreach (var bag in census.Bags)
            {
                if (bag == null || !bag.Opened || bag.Items == null ||
                    bag.ItemCount != bag.Items.Count || bag.Error != null ||
                    (bag.Source != "bank" && bag.Source != "inventory") ||
                    (bag.Source == "bank" && !bag.ReturnedToBank))
                    throw new InvalidOperationException("Census contains an incomplete bag.");
                int outer = bag.Source == "bank" ? bag.ReturnedOuterSlotInstance : bag.OuterSlotInstance;
                if (outer < 0) throw new InvalidOperationException("Census bag has no final physical slot.");
                foreach (var item in bag.Items) add(item, bag.Source, outer & 65535, bag.UniqueIdentity);
            }
            foreach (var item in census.LooseInventoryItems) add(item, "inventory", null, null);
            foreach (var item in census.LooseBankItems) add(item, "bank", null, null);
            if (observed.GroupBy(Address).Any(group => group.Count() != 1))
                throw new InvalidOperationException("Census reports more than one item at a physical slot.");
            return observed;
        }

        internal static Plan Build(IEnumerable<ActiveLedgerItem> previous,
            IEnumerable<Observation> physical, IEnumerable<string> completelyObservedCharacters)
        {
            var covered = new HashSet<string>(completelyObservedCharacters, StringComparer.OrdinalIgnoreCase);
            var remaining = previous.ToList();
            var allPhysical = physical.ToList();
            var pending = new List<Observation>(allPhysical);
            if (pending.Any(item => !covered.Contains(item.Character)) ||
                pending.GroupBy(Address).Any(group => group.Count() != 1) ||
                remaining.Any(item => string.IsNullOrWhiteSpace(item.Id)) ||
                remaining.GroupBy(item => item.Id).Any(group => group.Count() != 1))
                throw new InvalidOperationException("Ambiguous census scope or ledger identity.");
            var plan = new Plan();

            // Reserve every exact anchor first. Never let an earlier unmatched copy
            // steal the donor/transaction of a later copy still in its known slot.
            foreach (var item in pending.ToList())
            {
                var matches = remaining.Where(entry => Compatible(entry, item) &&
                    Same(entry.Character, item.Character) && Same(entry.Location, item.Location) &&
                    Normalize(entry.Bag) == item.Bag && Normalize(entry.Slot) == item.Slot).ToList();
                if (matches.Count != 1) continue;
                Match(plan, remaining, pending, matches[0], item, "location-confirmed");
            }

            // A single unmatched occurrence on each side can be reconnected within
            // the completely observed scope. Multiple indistinguishable copies are
            // retained as unknown origin, never assigned a donor by list order.
            foreach (var group in pending.GroupBy(item => item.Item.LowId).ToList())
            {
                var matches = remaining.Where(entry => covered.Contains(entry.Character) &&
                    entry.AoId == group.Key).ToList();
                if (matches.Count == 1 && group.Count() == 1 && Compatible(matches[0], group.Single()))
                    Match(plan, remaining, pending, matches[0], group.Single(), "location-recovered");
            }
            foreach (var item in pending)
            {
                var entry = Materialize(null, item);
                plan.Items.Add(entry);
                plan.Differences.Add(new Difference { Kind = "found-unknown-origin", Current = entry, Physical = item });
            }
            foreach (var entry in remaining)
            {
                if (!covered.Contains(entry.Character)) { plan.Items.Add(entry); continue; }
                // This is an unmatched CLAIM, not proof that a physical copy was
                // destroyed. Preserve it for history/investigation with this label.
                plan.Differences.Add(new Difference { Kind = "claim-not-matched", Previous = entry });
            }
            foreach (var item in allPhysical)
                if (!Same(item.PhysicalRole, item.DestinationRole) ||
                    (item.Bag == null && !Same(item.PhysicalRole, "central")))
                    plan.Routing.Add(item);
            return plan;
        }

        private static void Match(Plan plan, List<ActiveLedgerItem> remaining,
            List<Observation> pending, ActiveLedgerItem previous, Observation item, string kind)
        {
            remaining.Remove(previous);
            pending.Remove(item);
            var entry = Materialize(previous, item);
            plan.Items.Add(entry);
            if (kind != "location-confirmed")
                plan.Differences.Add(new Difference { Kind = kind, Previous = previous, Current = entry, Physical = item });
        }

        private static ActiveLedgerItem Materialize(ActiveLedgerItem previous, Observation item)
        {
            return new ActiveLedgerItem
            {
                Id = previous?.Id ?? "cb-" + Guid.NewGuid().ToString("N"),
                AoId = item.Item.LowId, HighId = item.Item.HighId, Ql = item.Item.Ql,
                TransactionId = previous?.TransactionId,
                From = previous?.From, ReceivedUtc = previous?.ReceivedUtc ?? item.ObservedUtc,
                Family = item.DestinationRole, Character = item.Character,
                Location = item.Location, Bag = item.Bag, Slot = item.Slot
            };
        }

        private static int? Normalize(int? value) => value.HasValue ? value.Value & 65535 : (int?)null;
        private static bool Compatible(ActiveLedgerItem entry, Observation item) =>
            entry.AoId == item.Item.LowId && (!entry.HighId.HasValue || entry.HighId == item.Item.HighId) &&
            (!entry.Ql.HasValue || entry.Ql == item.Item.Ql);
        private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        private static string Address(Observation item) => item.Character.ToLowerInvariant() + "/" +
            item.Location + "/" + (item.Bag.HasValue ? item.Bag.Value.ToString() : "loose") + "/" + item.Slot;
    }
}
