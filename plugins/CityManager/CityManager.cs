using System;
using System.Collections.Generic;
using System.Linq;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityManager
{
    public class CityManager : ClientlessPluginEntry
    {
        int Remaining;
        double TimeStamp;
        CloakStatus Status = new CloakStatus();
        ControllerState CurrentControllerState = new ControllerState();
        ControllerState ChatControllerState = new ControllerState();

        public override void Init(string pluginDir)
        {
            Logger.Information("CloakBot Init");
            CurrentControllerState = ControllerState.Move;
            Client.MessageReceived += MessageReceived;
        }

        private void MessageReceived(object sender, Message e)
        {
            //if (e.Header.PacketType != PacketType.N3Message) { return; }
            if (e.Body.PacketType != PacketType.N3Message) { return; }

            var n3Message = (N3Message)e.Body;
            //if (n3Message.Identity.Instance != Client.LocalDynelId) { return; }

            Logger.Debug($"N3MessageType = {n3Message.N3MessageType}");

            switch (n3Message.N3MessageType)
            {
                case N3MessageType.AOTransportSignal:
                    var sigMsg = (AOTransportSignalMessage)e.Body;
                    switch (sigMsg.Action)
                    {
                        case AOSignalAction.CityInfo:
                            Logger.Information("AOSignalAction.CityInfo");
                            var cityInfo = (CityInfo)sigMsg.TransportSignalMessage;
                            if (cityInfo.Unknown1 == 0) { return; }
                            Logger.Information("Controller opened");
                            CurrentControllerState = ControllerState.Opened;
                            break;
                        case AOSignalAction.CloakInfo:
                            Logger.Information("AOSignalAction.CloakInfo");
                            var cloakInfo = (CloakInfo)sigMsg.TransportSignalMessage;
                            Remaining = cloakInfo.ShieldTimerInSeconds;
                            Status = cloakInfo.CloakState;
                            TimeStamp = (int)(DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds; ;
                            break;
                    }

                    break;
                case N3MessageType.GenericCmd:
                    break;
                case N3MessageType.LookAt:
                    break;
                case N3MessageType.CharInPlay:
                    var charInPlayMsg = (CharInPlayMessage)e.Body;
                    if (charInPlayMsg?.Identity.Instance != Client.LocalDynelId) { return; }
                    Logger.Information("In play");
                    Client.Chat.PrivateMessageReceived += HandlePrivateMessage;
                    Client.OnUpdate += Tick;
                    break;
            }
        }

        private void HandlePrivateMessage(object sender, PrivateMessage msg)
        {
            var stringIgnores = new List<string> { "You have been auto-invited to the private channel.",
                "Unknown", "AnarchyOnline", "Reconnecting you to", "Darknet", "<" };
            if (stringIgnores.Any(i => msg.Message.Contains(i))) { return; }
            Logger.Information($"{msg.SenderName} sent {msg.Message}");
            string[] commandParts = msg.Message.Split(' ');
            string command = commandParts.Length > 0 ? commandParts[0].ToLower() : string.Empty;
            switch (command)
            {
                case "help":
                case "Help":
                    SendHelpMessage(msg.SenderId);
                    break;
                case "cloak":
                    Client.SendPrivateMessage(msg.SenderId, $"Cloak = {Status}, " +
                            $"Time remaining = {Math.Round(((TimeStamp + Remaining) - (int)(DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds) / 60)} minutes");
                    break;
                case "lower":
                    if (Status != CloakStatus.Enabled) { return; }
                    CurrentControllerState = ControllerState.CloakLower;
                    break;
                case "stand":
                    DynelManager.LocalPlayer.MovementComponent.ChangeMovement(MovementAction.LeaveSit);
                    break;
                case "sit":
                    DynelManager.LocalPlayer.MovementComponent.ChangeMovement(MovementAction.SwitchToSit);
                    break;
            }
        }

        private void SendHelpMessage(uint senderId)
        {
            string helpMessage = "Available commands:\n" +
                                 "help: Display this help message.\n" +
                                 "cloak: prints cloak status.\n" +
                                 "lower: Lowers the cloak.\n";

            try
            {
                Client.SendPrivateMessage(senderId, helpMessage);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending help message to {senderId}: {ex.Message}");
            }
        }

        private void Tick(object sender, double e)
        {
            var citycontroller = DynelManager.AllDynels.FirstOrDefault(c => c.Name == "City Controller");

            if (citycontroller == null) { return; }

            var cru = ControllerRecompilerUnit.Crus.Select(id => Inventory.Find(id, out var item) ? item : null).FirstOrDefault(item => item != null);

            //if (Remaining == 0 || (int)(DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds > TimeStamp + Remaining || CurrentControllerState== ControllerState.None)
            //{
            //    CurrentControllerState = ControllerState.Move;
            //}

            if (CurrentControllerState != ChatControllerState)
            {
                Logger.Information($"Controller State = {CurrentControllerState}");
                ChatControllerState = CurrentControllerState;
            }

            switch (CurrentControllerState)
            {
                case ControllerState.Move:
                    if (DynelManager.LocalPlayer.DistanceFrom(citycontroller) > 10f) { return; }
                    ;
                    CurrentControllerState = ControllerState.Open;
                    break;
                case ControllerState.Open:
                    if (citycontroller == null) { Logger.Information("cc null"); return; }
                    //Logger.Information($"Try opening cc, distance = {DynelManager.LocalPlayer.DistanceFrom(citycontroller)}");
                    Use(citycontroller);
                    Use(citycontroller);
                    Use(citycontroller);
                    CurrentControllerState = ControllerState.Waiting;
                    break;
                case ControllerState.Opened:
                    //if (!CityController.CanToggleCloak()) { return; }
                    //switch (CityController.CloakState)
                    //{
                    //    case CloakStatus.Disabled:
                    //        if (CityController.Charge <= 0.75f)
                    //        {
                    //            CurrentControllerState = ControllerState.Charge;
                    //        }
                    //        else
                    //        {
                    //            CurrentControllerState = ControllerState.CloakRaise;
                    //        }
                    //        break;
                    //    case CloakStatus.Enabled:
                    //        break;
                    //}
                    break;
                case ControllerState.Charge:
                    if (cru != null)
                    {
                        Client.Send(new GenericCmdMessage
                        {
                            Action = GenericCmdAction.UseItemOnItem,
                            User = citycontroller.Identity,
                            Target = cru.Slot,
                            Count = 1
                        });
                        CurrentControllerState = ControllerState.CloakRaise;
                    }
                    else { CurrentControllerState = ControllerState.CloakRaise; }

                    break;
                case ControllerState.CloakRaise:
                    Client.Send(new ToggleCloakMessage
                    {
                        Unknown1 = 49152
                    });
                    CurrentControllerState = ControllerState.Done;
                    break;
                case ControllerState.CloakLower:
                    Client.Send(new ToggleCloakMessage
                    {
                        Unknown1 = 49152
                    });
                    CurrentControllerState = ControllerState.Done;
                    break;
                case ControllerState.Done:
                    citycontroller?.Use();
                    break;
            }
        }

        class ControllerRecompilerUnit
        {
            public static readonly int[] Crus = {
                257110, 254364, 305225, 254367, 254359, 258522, 254350, 254329, 254328, 254327, 254326
            };
        }

        enum ControllerState { None, Waiting, Move, Open, Opened, Charge, CloakLower, CloakRaise, Done }
        //public enum CloakStatus { Unknown = 0, Disabled = -1, Enabled = 1 }

        void Use(Dynel target)
        {
            Client.Send(new LookAtMessage
            {
                Target = target.Identity
            });

            //Targeting.SetTarget(target.Identity);

            Client.Send(new GenericCmdMessage
            {
                Temp1 = 0,
                Action = GenericCmdAction.Use,
                Temp4 = 1,
                User = DynelManager.LocalPlayer.Identity,
                Target = target.Identity,
                Unknown = 1,
            });
        }
    }
}
