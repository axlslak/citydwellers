using System;
using System.Collections.Generic;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using TradeTrace = CityDwellers.Shared.TradeTrace;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private sealed class StorageContent
        {
            public int Slot, Low, High, Ql;
            public string Identity;
        }

        private sealed class StorageMoveEvidence
        {
            public Identity Source, Target;
            public List<StorageContent> Before, Contents;
            public bool Acknowledged, Invalidated;
            public int WireSlot, Retries, Handle;
            public long Observation, AckAt, ContentsAt;
            public string CaptureError;
        }

        private static List<StorageContent> StorageContents(Container bag) => bag.Items.Select(item =>
            new StorageContent { Slot = item.Slot.Instance & 65535, Low = item.Id,
                High = item.HighId, Ql = item.Ql, Identity = item.UniqueIdentity.ToString() }).ToList();

        private void InvalidateStorageMove()
        {
            if (_storageJob?.Move != null) _storageJob.Move.Invalidated = true;
        }

        private void ObserveStorageMove(object sender, Message message)
        {
            // A plugin observer exception would prevent the SDK's own handler
            // from running for this packet. Never interrupt native processing.
            try { CaptureStorageMove(message); }
            catch (Exception error)
            {
                if (_storageJob?.Move != null)
                    _storageJob.Move.CaptureError = error.GetType().Name;
                BankerActivityGovernor.Wake();
            }
        }

        private void CaptureStorageMove(Message message)
        {
            var job = _storageJob;
            var move = job?.Move;
            if (move == null || job.Failure != null) return;
            if (message?.Body is FullCharacterMessage) { move.Invalidated = true; return; }
            if (move.Invalidated) return;
            long sequence = ++move.Observation;
            // MessageReceived precedes SDK mutation. Capture primitives here;
            // consume them on the AO update thread after native handlers finish.
            if (message?.Body is ContainerAddItem added &&
                added.Identity == DynelManager.LocalPlayer.Identity &&
                added.Source == move.Source && added.Target == move.Target && !move.Acknowledged)
            {
                move.Acknowledged = true;
                move.WireSlot = added.Slot;
                move.AckAt = sequence;
                BankerActivityGovernor.Wake();
            }
            else if (message?.Body is InventoryUpdateMessage update && update.InventoryIdentity == move.Target)
            {
                move.Handle = update.Handle;
                move.ContentsAt = sequence;
                move.Contents = update.Items?.Select(item => new StorageContent
                {
                    Slot = item.Placement & 65535, Low = item.ItemLowId, High = item.ItemHighId,
                    Ql = item.Quality, Identity = item.Identity.ToString()
                }).ToList();
                BankerActivityGovernor.Wake();
            }
        }

        private void SendStorageItemMove(Item item, Container container, int retries = 0)
        {
            var before = StorageContents(container);
            if (!ValidStorageContents(before))
                throw new InvalidOperationException("Destination bag cache has invalid or duplicate inner slots; no insertion sent.");
            _storageJob.Move = new StorageMoveEvidence
            {
                Source = item.Slot, Target = container.Identity,
                Before = before, Retries = retries
            };
            _storageJob.Phase = StoragePhase.MovingItemIntoBag;
            SetStorageDeadline(ServicePolicy.ItemMoveTimeoutMs);
            TradeTrace.Mark(_storageSpan, "item.move.requested",
                detail: "source=" + item.Slot + "; target=" + container.Identity + "; retry=" + retries,
                aoId: _storageJob.Expected.AoId, ql: _storageJob.Expected.Ql,
                occurrence: _storageJob.ActualItemIdentity);
            TradeTrace.Count(_storageSpan, TradeTrace.AoOperations);
            BagOriginTrace.MoveToContainer(item, container);
        }

        private Item StorageMoveSource()
        {
            var move = _storageJob.Move;
            return Inventory.Items.FirstOrDefault(item => item != null && item.Slot == move.Source);
        }

        private bool MatchesStorageContent(StorageContent item)
        {
            var expected = _storageJob.Expected;
            return item.Low == expected.AoId && item.High == expected.HighId && item.Ql == expected.Ql &&
                (!IsUsableIdentity(item.Identity) || !IsUsableIdentity(_storageJob.ActualItemIdentity) ||
                 item.Identity == _storageJob.ActualItemIdentity);
        }

        private Item FindStoragePlacement(Container container)
        {
            if (container?.Items == null) return null;
            var occupied = new HashSet<int>(_storageJob.Move.Before.Select(item => item.Slot));
            occupied.UnionWith((_storageJob.Bag.Items ?? new List<StoredItemState>())
                .Where(item => item != null).Select(item => item.InnerSlot));
            var matches = container.Items.Where(item => item != null &&
                (item.Slot.Instance & 65535) < 21 &&
                !occupied.Contains(item.Slot.Instance & 65535) &&
                MatchesStorageContent(new StorageContent { Low = item.Id, High = item.HighId,
                    Ql = item.Ql, Identity = item.UniqueIdentity.ToString() })).ToList();
            return matches.Count == 1 && SameStorageContents(_storageJob.Move.Before,
                StorageContents(container).Where(item => item.Slot != (matches[0].Slot.Instance & 65535)).ToList())
                ? matches[0] : null;
        }

        private bool TryAcknowledgedStoragePlacement(Container container, out Item observed)
        {
            observed = null;
            var move = _storageJob.Move;
            // A contents response older than the move receipt is its baseline.
            // A later response must agree; never prefer the cache over that wire view.
            if (!move.Acknowledged || (move.Contents != null && move.ContentsAt > move.AckAt)) return false;
            observed = FindStoragePlacement(container);
            return observed != null && StorageMoveSource() == null;
        }

        private static bool ValidStorageContents(List<StorageContent> contents) => contents != null &&
            contents.Count <= 21 && contents.All(item => item != null && item.Slot >= 0 && item.Slot < 21) &&
            contents.Select(item => item.Slot).Distinct().Count() == contents.Count;

        private static bool SameStorageContents(List<StorageContent> before, List<StorageContent> after) =>
            ValidStorageContents(before) && ValidStorageContents(after) &&
            before.Count == after.Count && before.All(old => after.Count(item => item.Slot == old.Slot &&
                item.Low == old.Low && item.High == old.High && item.Ql == old.Ql &&
                (!IsUsableIdentity(old.Identity) || !IsUsableIdentity(item.Identity) || old.Identity == item.Identity)) == 1);

        private bool TryStorageSnapshotPlacement(Container container, out Item observed)
        {
            observed = null;
            var move = _storageJob.Move;
            if (!ValidStorageContents(move.Contents) || container == null || !container.IsOpen ||
                container.Handle != move.Handle)
                return false;
            var arrivals = move.Contents.Where(item => MatchesStorageContent(item) &&
                move.Before.All(old => old.Slot != item.Slot)).ToList();
            if (arrivals.Count != 1 || !SameStorageContents(move.Before,
                move.Contents.Where(item => item != arrivals[0]).ToList())) return false;
            observed = FindStoragePlacement(container);
            // Use the received contents placement, never reinterpret the move
            // packet's Slot field or invent a different destination slot.
            return observed != null && (observed.Slot.Instance & 65535) == arrivals[0].Slot &&
                StorageMoveSource() == null;
        }

        private void BeginPlacementRefresh(string reason)
        {
            var move = _storageJob.Move;
            Item bag = FindInventoryBagByIdentity(_storageJob.BagLiveIdentity);
            if (bag == null) { FailStorageJob("Cannot refresh placement: destination bag is not in inventory. " + StorageMoveDetail()); return; }
            move.Contents = null; // Only a response following this request may justify a retry.
            _storageJob.Phase = StoragePhase.RefreshingPlacement;
            SetStorageDeadline(ServicePolicy.BagOpenTimeoutMs);
            Logger.Warning("[CityBankers] STORAGE placement refresh: " + reason + "; " + StorageMoveDetail());
            TradeTrace.Mark(_storageSpan, "item.move.refresh", detail: StorageMoveDetail());
            TradeTrace.Count(_storageSpan, TradeTrace.BagOpens);
            TradeTrace.Count(_storageSpan, TradeTrace.AoOperations);
            bag.Use();
        }

        private void ProcessPlacementRefresh()
        {
            var move = _storageJob.Move;
            if (move == null || move.Invalidated || move.CaptureError != null)
            {
                FailStorageJob("Placement refresh evidence unavailable; " + (move == null ? "no pending move" : StorageMoveDetail()));
                return;
            }
            Container container = FindContainerByIdentity(_storageJob.BagLiveIdentity);
            Item observed;
            if (TryStorageSnapshotPlacement(container, out observed))
            {
                Logger.Information("[CityBankers] STORAGE placement confirmed by fresh bag contents; " + StorageMoveDetail());
                CompleteStoragePlacement(container, observed);
                return;
            }
            if (TryAcknowledgedStoragePlacement(container, out observed))
            {
                CompleteStoragePlacement(container, observed);
                return;
            }
            if (move.Contents != null)
            {
                Item source = StorageMoveSource();
                bool exactSource = source != null && source.Id == _storageJob.Expected.AoId &&
                    source.HighId == _storageJob.Expected.HighId && source.Ql == _storageJob.Expected.Ql &&
                    (!IsUsableIdentity(_storageJob.ActualItemIdentity) ||
                     source.UniqueIdentity.ToString() == _storageJob.ActualItemIdentity);
                // No acknowledged move is ever replayed. Retry once only after
                // a fresh destination response and the exact unchanged source.
                if (!move.Acknowledged && move.Retries == 0 && exactSource &&
                    container != null && container.IsOpen && container.Handle == move.Handle &&
                    SameStorageContents(move.Before, move.Contents))
                {
                    Logger.Warning("[CityBankers] STORAGE retrying unacknowledged insertion after fresh unchanged bag contents; " + StorageMoveDetail());
                    SendStorageItemMove(source, container, move.Retries + 1);
                    return;
                }
                FailStorageJob("Fresh destination contents do not prove a consistent placement or safe retry. " + StorageMoveDetail());
                return;
            }
            if (DateTime.UtcNow >= _storageJob.DeadlineUtc)
                FailStorageJob("No fresh destination contents received for placement refresh. " + StorageMoveDetail());
            else
                TradeTrace.Wait(_storageSpan, "item-move-fresh-contents");
        }

        private string StorageMoveDetail()
        {
            var move = _storageJob.Move;
            return "batch=" + _storageJob.Command.BatchId + "; AOID=" + _storageJob.Expected.AoId +
                "; source=" + move.Source + "; target=" + move.Target +
                "; ack=" + move.Acknowledged + "; wireSlot=" + (move.Acknowledged ? move.WireSlot.ToString() : "none") +
                "; contents=" + (move.Contents == null ? "none" : move.Contents.Count.ToString()) +
                "; ackAt=" + move.AckAt + "; contentsAt=" + move.ContentsAt + "; retry=" + move.Retries +
                "; captureError=" + (move.CaptureError ?? "none");
        }
    }
}
