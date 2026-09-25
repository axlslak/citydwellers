using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MalisBuffBots
{
    public class CommandProcessor
    {
        private static readonly Dictionary<Command, CommandInfo> _commandActions = new Dictionary<Command, CommandInfo>
        {
            { Command.Stand, new CommandInfo(Rank.Moderator, StandAction) },
            { Command.Sit, new CommandInfo(Rank.Moderator, SitAction) },
            { Command.Cast, new CommandInfo(Rank.Unranked, CastRequest) },
            { Command.Rebuff, new CommandInfo(Rank.Unranked, RebuffRequest) },
            { Command.Buffmacro, new CommandInfo(Rank.Unranked, BuffMacroRequest) },
            { Command.Help, new CommandInfo(Rank.Unranked, HelpRequest) },
            { Command.Debug, new CommandInfo(Rank.Admin, Debug) },
            { Command.Clear, new CommandInfo(Rank.Admin, ClearRequest) },
        };


        public bool TryProcess(PrivateMessage msg, out Command command, out string[] commandParts, out int requesterId)
        {
            commandParts = msg.Message.ToLower().Split(' ');
            requesterId = (int)msg.SenderId;
            command = new Command();

            if (Main.Ipc.BotCache.ContainsIdentity(requesterId))
                return false;

            if (!Enum.TryParse(commandParts[0].ToTitleCase(), out command))
            {
                if (!Main.SettingsJson.Data.PrivateChannelMode)
                    CityBufferBridge.SendPrivateMessage(msg.SenderId, ScriptTemplate.CommandNotFound());

                return false;
            }

            commandParts = commandParts.Length > 1 ? commandParts.Skip(1).ToArray() : null;

            if (!_commandActions.TryGetValue(command, out CommandInfo action))
            {
                if (!Main.SettingsJson.Data.PrivateChannelMode)
                    CityBufferBridge.SendPrivateMessage(msg.SenderId, ScriptTemplate.CommandNotFound());

                return false;
            }

            if (!Main.UserRank.MeetsRank(action.Rank, msg.SenderName))
            {
                if (!Main.SettingsJson.Data.PrivateChannelMode)
                    CityBufferBridge.SendPrivateMessage(msg.SenderId, ScriptTemplate.PermissionError(command.ToString()));

                return false;
            }

            return _commandActions[command].Action.Invoke(msg);
        }


        private static bool CastRequest(PrivateMessage msg)
        {
            if (msg.Message.Split(' ').Length < 2)
            {
                CityBufferBridge.SendPrivateMessage(msg.SenderId, ScriptTemplate.InvalidParams());
                return false;
            }

            return true;
        }

        private static bool SitAction(PrivateMessage msg)
        {
            Logger.Information($"Received sit request from {msg.SenderName}");
            return true;
        }

        private static bool StandAction(PrivateMessage msg)
        {
            Logger.Information($"Received stand request from {msg.SenderName}");
            return true;
        }

        private static bool RebuffRequest(PrivateMessage msg)
        {
            Logger.Information($"Received rebuff request from {msg.SenderName}");
            return true;
        }

        private static bool BuffMacroRequest(PrivateMessage msg)
        {
            Logger.Information($"Received ncu scan request from {msg.SenderName}");
            return true;
        }

        private static bool HelpRequest(PrivateMessage msg)
        {
            Logger.Information($"Received help request from {msg.SenderName}");
            return true;
        }

        private static bool ClearRequest(PrivateMessage msg)
        {
            Logger.Information($"Received clear request from {msg.SenderName}");
            return true;
        }

        private static bool Debug(PrivateMessage msg)
        {
            Logger.Information($"Received debug request from {msg.SenderName}");
            return true;
        }

    }

    public class CommandInfo
    {
        public Rank Rank { get; }
        public Func<PrivateMessage, bool> Action { get; }

        public CommandInfo(Rank rank, Func<PrivateMessage, bool> action)
        {
            Rank = rank;
            Action = action;
        }
    }

    public enum Command
    {
        Stand,
        Sit,
        Cast,
        Rebuff,
        Buffmacro,
        Help,
        Reload,
        Debug,
        Clear
    }

    public enum Rank
    {
        Unranked,
        Warper,
        Moderator,
        Admin,
    }
}
