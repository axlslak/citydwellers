using System;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBankers
{
    public partial class BankingServiceAgent
    {
        private const float BagShopUseDistance = 2f;
        private sealed class BagShopApproach
        {
            public readonly object Sync = new object();
            public Vector3 Observed, Command;
            public long Sequence, StopAfter;
            public MovementAction LastAction;
            public DateTime ObservedUtc;
            public bool Moving;
            public DateTime NextUpdate;
        }

        private void BeginBagShopApproach(Dynel dealer)
        {
            var work = _bagShop;
            work.Dealer = dealer.Identity;
            work.Phase = "approaching"; work.Age.Restart();
            lock (work.Approach.Sync)
            {
                work.Approach.Observed = DynelManager.LocalPlayer.Transform.Position;
                work.Approach.Command = work.Approach.Observed;
            }
            Logger.Information("[CityBankers] BAG SHOP approaching " + dealer.Identity +
                "; distance=" + dealer.DistanceFrom(DynelManager.LocalPlayer).ToString("F2") + "m.");
        }

        private void ObserveBagShopMovement(BagShopWork work, CharDCMoveMessage movement)
        {
            if (movement == null || movement.Identity.Instance != Client.LocalDynelId ||
                (work.Phase != "approaching" && work.Phase != "stopping")) return;
            lock (work.Approach.Sync)
            {
                work.Approach.Observed = movement.Position;
                work.Approach.LastAction = movement.MoveType;
                work.Approach.ObservedUtc = DateTime.UtcNow;
                work.Approach.Sequence++;
            }
            BankerActivityGovernor.Wake();
        }

        private void SendBagShopMovement(Vector3 position, Vector3 target, MovementAction action)
        {
            var player = DynelManager.LocalPlayer;
            player.MovementComponent.Position = position;
            player.MovementComponent.Heading = position.Distance2DFrom(target) > 0.01f
                ? Quaternion.FromTo(new Vector3(position.X, 0, position.Z), new Vector3(target.X, 0, target.Z))
                : player.Transform.Heading;
            player.MovementComponent.ChangeMovement(action);
        }

        private void StopBagShopApproach(BagShopWork work)
        {
            if (work == null) return;
            lock (work.Approach.Sync)
            {
                if (!work.Approach.Moving) return;
                // Use the latest actual position on cancellation, not an
                // unacknowledged intended endpoint.
                try
                {
                    if (Client.InPlay)
                        SendBagShopMovement(work.Approach.Observed, work.Approach.Observed, MovementAction.FullStop);
                }
                catch (Exception error) { Logger.Warning("[CityBankers] BAG SHOP movement stop unavailable: " + error.Message); }
                finally { work.Approach.Moving = false; }
            }
        }

        private void TickBagShopApproach(BagShopWork work)
        {
            var dealer = DynelManager.Npcs.Cast<Dynel>().Concat(DynelManager.VendingMachines.Cast<Dynel>())
                .FirstOrDefault(d => d.Identity == work.Dealer);
            if (dealer?.Transform == null) { StopBagShop("The dealer disappeared before opening."); return; }
            Vector3 target = dealer.Transform.Position;
            var movement = work.Approach;
            lock (movement.Sync)
            {
                if (work.Phase == "stopping")
                {
                    // The stop command alone is not evidence of arrival.
                    if (movement.Sequence <= movement.StopAfter) return;
                    bool stopped = movement.LastAction == MovementAction.FullStop || movement.LastAction == MovementAction.ForwardStop;
                    if (!stopped && DateTime.UtcNow - movement.ObservedUtc < TimeSpan.FromMilliseconds(300)) return;
                    movement.Moving = false;
                    if (movement.Observed.DistanceFrom(target) <= BagShopUseDistance)
                    {
                        work.Phase = "opening"; work.Age.Restart();
                        dealer.Use();
                        Logger.Information("[CityBankers] BAG SHOP opening " + dealer.Identity +
                            "; observed distance=" + movement.Observed.DistanceFrom(target).ToString("F2") + "m.");
                        return;
                    }
                    movement.Command = movement.Observed;
                    work.Phase = "approaching";
                }
                if (movement.Observed.Distance2DFrom(target) + 0.1f < movement.Command.Distance2DFrom(target))
                    movement.Command = movement.Observed;
                if (movement.Command.DistanceFrom(target) <= BagShopUseDistance)
                {
                    movement.StopAfter = movement.Sequence;
                    work.Phase = "stopping";
                    SendBagShopMovement(movement.Command, target, MovementAction.FullStop);
                    return;
                }
                if (DateTime.UtcNow < movement.NextUpdate) return;
                if (!movement.Moving)
                {
                    movement.Command = movement.Observed;
                    SendBagShopMovement(movement.Command, target, MovementAction.ForwardStart);
                    movement.Moving = true;
                }
                else
                {
                    // Same conservative walking cadence as CityBuddies. Never
                    // let asserted movement run far ahead of native positions.
                    if (movement.Command.Distance2DFrom(movement.Observed) > 1f) return;
                    float distance = movement.Command.Distance2DFrom(target);
                    if (distance < 0.01f) return;
                    float step = Math.Min(1.6667f * 0.2f, Math.Max(0, distance - 1.5f));
                    movement.Command = new Vector3(
                        movement.Command.X + (target.X - movement.Command.X) / distance * step,
                        movement.Observed.Y,
                        movement.Command.Z + (target.Z - movement.Command.Z) / distance * step);
                    SendBagShopMovement(movement.Command, target, MovementAction.Update);
                }
                movement.NextUpdate = DateTime.UtcNow.AddMilliseconds(200);
            }
        }
    }
}
