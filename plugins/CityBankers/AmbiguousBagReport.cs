using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;

namespace CityBankers
{
    // A clientless banker cannot be inspected with the ordinary game client:
    // its character is already logged in by this process. When a storage layout
    // hold cannot be resolved, the banker therefore records its own evidence
    // instead of asking an operator to look.
    //
    // [INVARIANT] This is read-only. It moves nothing, opens no container,
    // sends no packet, and never throws into its caller. A failed report must
    // not turn a diagnosable hold into a crash.
    internal static class AmbiguousBagReport
    {
        public static void Write(string settingsDir, string character, string role, string reason)
        {
            try
            {
                List<Item> inventory = Inventory.Items == null
                    ? new List<Item>()
                    : Inventory.Items.Where(i => i != null).ToList();
                List<Item> bank = Inventory.Bank == null || Inventory.Bank.Items == null
                    ? new List<Item>()
                    : Inventory.Bank.Items.Where(i => i != null).ToList();

                var entries = inventory.Select(i => Describe(i, "inventory"))
                    .Concat(bank.Select(i => Describe(i, "bank")))
                    .ToList();

                var storage = inventory.Where(StorageBagPolicy.IsStorageBag)
                        .Select(i => new { Location = "inventory", Item = i })
                    .Concat(bank.Where(StorageBagPolicy.IsStorageBag)
                        .Select(i => new { Location = "bank", Item = i }))
                    .ToList();

                var duplicates = storage
                    .GroupBy(x => x.Item.UniqueIdentity.ToString())
                    .Where(g => g.Count() != 1)
                    .Select(g => new
                    {
                        identity = g.Key,
                        count = g.Count(),
                        occurrences = g.Select(x => new
                        {
                            location = x.Location,
                            slot = x.Item.Slot.Instance & 65535,
                            slotInstance = x.Item.Slot.Instance,
                            slotType = x.Item.Slot.Type.ToString(),
                            id = x.Item.Id,
                            highId = x.Item.HighId,
                            ql = x.Item.Ql,
                            name = x.Item.Name
                        }).ToList()
                    })
                    .ToList();

                // SDK containers are keyed by identity, so this view cannot
                // distinguish which outer slot is physical. Preserve it as
                // context, not proof that a repeated identity is a phantom.
                var containers = Inventory.Containers == null
                    ? new List<object>()
                    : Inventory.Containers.Where(c => c != null).Select(c => (object)new
                    {
                        identity = c.Identity.ToString(),
                        handle = c.Handle,
                        isOpen = c.IsOpen,
                        itemCount = c.Items == null ? -1 : c.Items.Count()
                    }).ToList();

                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string path = Path.Combine(
                    RuntimeStateStore.GetDataDirectory(settingsDir),
                    "diagnostic-dumps",
                    "ambiguous-bags-" + character.ToLowerInvariant() + "-" + stamp + ".json");

                RuntimeStateStore.WriteJsonAtomic(path, new
                {
                    format = "citybankers-ambiguous-bag-report-v1",
                    character,
                    role,
                    observedUtc = DateTime.UtcNow,
                    reason,
                    summary = new
                    {
                        inventoryItems = inventory.Count,
                        bankItems = bank.Count,
                        storageBagEntries = storage.Count,
                        distinctStorageIdentities = storage
                            .Select(x => x.Item.UniqueIdentity.ToString()).Distinct().Count(),
                        knownContainers = containers.Count,
                        duplicateIdentities = duplicates.Count
                    },
                    duplicates,
                    containers,
                    entries
                });

                Logger.Information("[CityBankers] AMBIGUOUS BAG REPORT " + character + " -> " + path);
            }
            catch (Exception ex)
            {
                Logger.Warning("[CityBankers] Ambiguous bag report unavailable for " +
                    character + ": " + ex.Message);
            }
        }

        private static object Describe(Item item, string location)
        {
            return new
            {
                location,
                slot = item.Slot.Instance & 65535,
                slotInstance = item.Slot.Instance,
                slotType = item.Slot.Type.ToString(),
                identity = item.UniqueIdentity.ToString(),
                identityType = item.UniqueIdentity.Type.ToString(),
                identityInstance = item.UniqueIdentity.Instance,
                id = item.Id,
                highId = item.HighId,
                ql = item.Ql,
                name = item.Name,
                normalInventory = StorageBagPolicy.IsNormalInventory(item),
                storageBag = StorageBagPolicy.IsStorageBag(item)
            };
        }
    }
}
