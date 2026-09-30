using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.IO;
using System.Linq;
using CityDwellers.Shared;

namespace MalisBuffBots
{
    public class N3MessageProcessor
    {
        private QueueProcessor _queueProcessor;
        public int LastLdbMessage = 0;
        private readonly int[] _meepNanos = new int[13] { 142707, 142708, 142710, 142712, 142714, 142734, 142736, 142735, 142737, 142740, 150334, 154914, 154913 };

        public N3MessageProcessor(QueueProcessor queueProcessor)
        {
            _queueProcessor = queueProcessor;
            Client.MessageReceived += OnMessageReceived;
        }

        public void OnMessageReceived(object _, Message msg)
        {
            if (msg.Header.PacketType != PacketType.N3Message)
                return;

            try
            {
                N3Message n3Msg = (N3Message)msg.Body;

                switch (n3Msg.N3MessageType)
                {
                    case N3MessageType.CharacterAction:
                        ProcessCharacterActionMessage((CharacterActionMessage)n3Msg);
                        break;
                    case N3MessageType.Feedback:
                        ProcessFeedbackMessage((FeedbackMessage)n3Msg);
                        break;
                    case N3MessageType.TeamMember:
                        OnTeamMemberMessage((TeamMemberMessage)n3Msg);
                        break;
                    case N3MessageType.CastNanoSpell:
                        OnCastNanoSpellMessage((CastNanoSpellMessage)n3Msg);
                        break;
                    case N3MessageType.CharInPlay:
                        OnCharInPlayMessage((CharInPlayMessage)n3Msg);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                Logger.Error("N3MessageProcessorOnMessageReceived");
            }
        }

        private void OnCharInPlayMessage(CharInPlayMessage n3Msg)
        {
            if (DynelManager.LocalPlayer == null || n3Msg.Identity != DynelManager.LocalPlayer.Identity)
                return;

            if (Main.SettingsJson.Data.InitConnectionDelay > 0)
                return;

            Logger.Information("I am in play, resetting queue data!");
            Main.QueueProcessor.ResetBotQueue();
        }

        private void OnCastNanoSpellMessage(CastNanoSpellMessage n3Msg)
        {
            if (!_meepNanos.Contains(n3Msg.NanoId))
                return;

            var teamMember = Team.Members.FirstOrDefault(x => x.Identity == n3Msg.Identity);

            if (teamMember == null)
                return;

            try
            {
                Logger.Information($"MEEP {teamMember.Name} | {teamMember.Identity} - {n3Msg.NanoId}");
            }
            catch (Exception ex)
            {
                Logger.Information(ex.Message);
            }

            Logger.Information($"Detected meeper '{teamMember.Name}'");

            if (!Main.SettingsJson.Data.AutoBanMeepers)
                return;

            if (Main.QueueProcessor.TeamTrackerId != teamMember.Identity.Instance)
                return;

            var moveComponent = DynelManager.LocalPlayer.MovementComponent;
            moveComponent.ChangeMovement(MovementAction.SwitchToSit);
            moveComponent.ChangeMovement(MovementAction.LeaveSit);

            var formattedName = teamMember.Name.ToLower();

            if (!ManagerMemory.Current.RequestBufferBan(formattedName, Client.CharacterName))
                Logger.Warning($"Unable to queue City Dwellers auto-ban request for '{formattedName}'.");
            Main.QueueProcessor.ResetBotQueue();
        }

        private void ProcessCharacterActionMessage(CharacterActionMessage actionMsg)
        {
            switch (actionMsg.Action)
            {
                case CharacterActionType.AcceptTeamRequest:
                    Logger.Information($"Team acceptance received: actor={actionMsg.Identity}, target={actionMsg.Target}.");
                    break;
                case CharacterActionType.TeamRequest:
                    OnTeamRequestAction(actionMsg.Identity, actionMsg.Target);
                    break;
                case CharacterActionType.FinishNanoCasting:
                    OnFinishNanoCastingAction(actionMsg.Identity, actionMsg.Target, actionMsg.Parameter2);
                    break;
                case (CharacterActionType)21:
                    // This unnamed action also precedes successful team acceptance.
                    // Do not infer failure or requester identity from its target.
                    Logger.Debug($"Character action 21: actor={actionMsg.Identity}, target={actionMsg.Target}, parameter1={actionMsg.Parameter1}, parameter2={actionMsg.Parameter2}.");
                    break;
                case CharacterActionType.TeamMemberLeft:
                    if (actionMsg.Target == DynelManager.LocalPlayer.Identity)
                        Main.Ipc.BotCache.BroadcastTeamInfoMessage();
                    break;
                case CharacterActionType.SetNanoDuration:
                    OnSetNanoDurationAction(actionMsg.Identity,actionMsg.Target.Instance);
                    break;
            }
        }

        private void OnSetNanoDurationAction(Identity identity, int nanoId)
        {
            if (Main.PaidPilot) return; // Preserve the owner's existing nanos and FSC self effect.
            if (identity != DynelManager.LocalPlayer.Identity)
                return;

            if (Main.RebuffProcessor == null || Main.RebuffProcessor.Contains(nanoId, out _))
                return;

            DynelManager.LocalPlayer.ForceRemoveBuff(nanoId);
        }

        internal static void OnTeamMemberMessage(TeamMemberMessage teamMsg)
        {
            Main.Ipc.BotCache.BroadcastTeamInfoMessage(teamMsg.Character);
        }

        private void OnTeamRequestAction(Identity identity, Identity target)
        {
            if (Main.PaidPilot) return; // This pilot issues its own requester invitations.
            Logger.Information($"Team request received from '{identity.Instance}'");

            if (Main.Ipc.BotCache.Entries.Values.Any(x => x.Identity == target))
            {
                Logger.Information("Accepting team invite");
                Team.Accept(target);
                _queueProcessor.TeamTimeout.Reset();
            }
        }

        private void OnFinishNanoCastingAction(Identity identity, Identity target, int param2)
        {
            if (identity != DynelManager.LocalPlayer.Identity)
                return;

            if (_queueProcessor.Queue.Current == null)
                return;

            if (!_queueProcessor.Queue.Current.NanoEntry.ContainsId(param2))
                return;

            var buffTarget = DynelManager.Players.FirstOrDefault(x => x.Identity == _queueProcessor.Queue.Current.Requester);
            var buffTargetName = buffTarget != null ? buffTarget.Name : target.Instance.ToString();

            Logger.Information($"Finished casting '{_queueProcessor.Queue.Current.NanoEntry.Name}' on '{buffTargetName}'");
            if (Main.PaidPilot) CityBufferBridge.PaidResult(_queueProcessor.Queue.Current.Requester.Instance,
                Client.CharacterName + " finished casting " + _queueProcessor.Queue.Current.NanoEntry.Name + ".");
            _queueProcessor.ResetCurrentBuffEntry(completed: true);
        }

        private void ProcessFeedbackMessage(FeedbackMessage feedbackMsg)
        {
            if (feedbackMsg.Identity != DynelManager.LocalPlayer.Identity)
                return;

            if (feedbackMsg.CategoryId != 110)
                return;

            LastLdbMessage = feedbackMsg.MessageId;
            if (!_queueProcessor.HasOutstandingCast) return;
            _queueProcessor.RecordCastFeedback(feedbackMsg.MessageId);
            switch ((LdbFeedback)feedbackMsg.MessageId)
            {
                case LdbFeedback.NotEnoughNcu:
                case LdbFeedback.NotInLineOfSight:
                case LdbFeedback.OutOfRange:
                case LdbFeedback.UnableToUseNano:
                case LdbFeedback.WaitForNanoToFinish:
                    _queueProcessor.RetryCurrentBuffEntry(QueueProcessor.FeedbackReason((LdbFeedback)feedbackMsg.MessageId));
                    break;
                case LdbFeedback.BetterNanoInNcu:
                    _queueProcessor.ResetCurrentBuffEntry((LdbFeedback)feedbackMsg.MessageId);
                    break;
                case LdbFeedback.NotEnoughNano:
                    OnNotEnoughNanoFeedback((LdbFeedback)feedbackMsg.MessageId);
                    break;
                case LdbFeedback.MustStandToCast:
                    var moveComponent = DynelManager.LocalPlayer.MovementComponent;
                    moveComponent.ChangeMovement(MovementAction.LeaveSit);
                    _queueProcessor.RetryCurrentBuffEntry("standing up to cast");
                    break;
                case LdbFeedback.SuccessfulCast:
                    break;
                default:
                    Logger.Information($"Unregistered ldbfeedback msg:{feedbackMsg.MessageId}; currentNano={_queueProcessor.Queue.Current?.NanoEntry?.Name ?? "none"}; requester={_queueProcessor.Queue.Current?.Requester.Instance ?? 0}");
                    break;
            }

        }

        private void OnNotEnoughNanoFeedback(LdbFeedback messageId)
        {
            if (DynelManager.LocalPlayer.CanUseSitKit(out Item item))
            {
                var moveComponent = DynelManager.LocalPlayer.MovementComponent;
                moveComponent.ChangeMovement(MovementAction.SwitchToSit);
                item.Use();
                moveComponent.ChangeMovement(MovementAction.LeaveSit);
                _queueProcessor.RetryCurrentBuffEntry("restoring nano");
                return;
            }

            _queueProcessor.RetryCurrentBuffEntry(QueueProcessor.FeedbackReason(messageId));
        }

    }
}
