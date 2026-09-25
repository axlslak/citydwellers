using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using MalisBuffBots;
using Scriban;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MalisBuffBots
{
    public class ScriptTemplate
    {
        private class Colors
        {
            public const string Red = "ED5A73";
            public const string Yellow = "DEDE42";
            public const string Green = "B9FF00";
        }

        private static object _helpMenuPreset = null;

        private static Dictionary<LdbFeedback, string> _ldbFeedback = new Dictionary<LdbFeedback, string>
        {
            { LdbFeedback.NotEnoughNcu , "Not enough (NCU) left."},
            { LdbFeedback.NotInLineOfSight , "Not in line of sight."},
            { LdbFeedback.OutOfRange , "Out of range."},
            { LdbFeedback.UnableToUseNano , "I was unable to use this nano."},
            { LdbFeedback.NotEnoughNano, "Not enough nano energy left and my sit kit is on cooldown."},
            { LdbFeedback.BetterNanoInNcu, "Better nano already running."}
        };

        public static string HelpMenu()
        {
            if (_helpMenuPreset == null)
                LoadHelpMenu();

            var template = GetTemplate("BuffMenuTemplate").Render(_helpMenuPreset);

            return template;
        }

        private static void LoadHelpMenu()
        {
            _helpMenuPreset = new
            {
                Botname = DynelManager.LocalPlayer.Name,
                Db = Main.BuffsJson.Entries.Where(x => x.Key == Profession.Generic || Main.Ipc.BotCache.ContainsKey(x.Key)).Select(kv => new
                {
                    Prof = kv.Key,
                    Id = (int)kv.Key,
                    Entries = kv.Value.Where(x => Main.Ipc.BotCache.Entries.Values.Where(c => c.SpellData != null && c.SpellData.Count() > 0).SelectMany(y => y.SpellData).Any(y => x.ContainsId(y))).Select(entry => new
                    {
                        Tag = entry.Tags[0],
                        Nanoname = entry.Name,
                        Description = entry.Description,
                        Ncu = GetNcuCost(entry.RemoveNanoIdUponCast[0] != 0 ? entry.RemoveNanoIdUponCast[0] : entry.LevelToId[0].Id),
                        Type = entry.Type.ToString()
                    }).ToList()
                }).ToList()
            };
        }

        private static int GetNcuCost(int nanoId)
        {
            if (!ItemData.Find(nanoId, out NanoItem nanoItem))
                return 0;

            return nanoItem.NCU;
        }

        public static string CreateFeedbackReply(BuffEntry buffEntry, LdbFeedback? feedback)
        {
            return CreateFeedbackReply(buffEntry, _ldbFeedback[(LdbFeedback)feedback]);
        }

        public static string CreateFailTeamInviteReply()
        {
            return GetTemplate("FailedToTeamInviteTemplate").Render(new
            {
                Red = Colors.Red,
            });
        }

        public static string CreateFeedbackReply(BuffEntry buffEntry, string feedback)
        {
            return GetTemplate("FeedbackReplyTemplate").Render(new
            {
                Nanoname = buffEntry.NanoEntry.Name,
                Errormsg = feedback,
                Yellow = Colors.Yellow,
                Red = Colors.Red,
                Green = Colors.Green
            });
        }





        public static string AlreadyInQueue(string nanoName)
        {
            return GetTemplate("AlreadyInQueueTemplate").Render(new
            {
                Nanoname = nanoName,
                Red = Colors.Red,
                Green = Colors.Green
            });
        }

        public static string Buffmacro(string botName, List<string> tags)
        {
            return GetTemplate("BuffmacroReplyTemplate").Render(new
            {
                Name = botName,
                Tags = string.Join(" ", tags),
                Yellow = Colors.Yellow
            });
        }

        public static string CommandNotFound()
        {
            return GetTemplate("CommandNotFoundTemplate").Render(new
            {
                Red = Colors.Red,
                Yellow = Colors.Yellow
            });
        }

        public static string PermissionError(string command)
        {
            return GetTemplate("PermissionErrorTemplate").Render(new
            {
                Red = Colors.Red,
                Green = Colors.Green,
                Command = command
            });
        }

        public static string InvalidParams()
        {
            return GetTemplate("InvalidParamsTemplate").Render(new
            {
                Red = Colors.Red,
            });
        }


        public static string TeamBuffTimeout(string nanoName)
        {
            return GetTemplate("TeamBuffTimeoutTemplate").Render(new
            {
                Red = Colors.Red,
                Green = Colors.Green,
                Nanoname = nanoName
            });
        }

        public static string TeamInvite()
        {
            return GetTemplate("TeamInviteTemplate").Render(new
            {
                Yellow = Colors.Yellow
            });
        }

        public static string TeamBotsBusy()
        {
            return GetTemplate("TeamBotsBusyTemplate").Render(new
            {
                Red = Colors.Red
            });
        }

        public static string RequestRejected()
        {
            return GetTemplate("RequestRejectedTemplate").Render(new
            {
                Red = Colors.Red
            });
        }

        public static string RetrievingBuffs()
        {
            return GetTemplate("RetrievingBuffsTemplate").Render(new
            {
                Yellow = Colors.Yellow
            });
        }

        private static Template GetTemplate(string templateFile)
        {
            return Template.Parse(File.ReadAllText(System.IO.Path.Combine(Path.PLUGIN_DIR, "Templates", templateFile + ".txt")));
        }
    }
}
