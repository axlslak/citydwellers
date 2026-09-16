using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MalisBuffBots
{
    public enum PvpFlagId
    {
        OneMin = 216382,
        TenMin = 214879,
        FifteenMin = 284620,
        OneHr = 202732,
    }

    public enum Profession : uint
    {
        Generic,
        Soldier,
        MartialArtist,
        Engineer,
        Fixer,
        Agent,
        Adventurer,
        Trader,
        Bureaucrat,
        Enforcer,
        Doctor,
        NanoTechnician,
        Metaphysicist,
        Monster,
        Keeper,
        Shade
    }

    public enum CastType
    {
        Single,
        Team
    }

    public enum LdbFeedback
    {
        NotInLineOfSight = 8605508,
        BetterNanoInNcu = 101968183,
        UnableToUseNano = 108119101,
        SuccessfulCast = 124550313,
        MustStandToCast = 20556461,
        NotEnoughNano = 206104233,
        WaitForNanoToFinish = 213890029,
        NotEnoughNcu = 220179189,
        OutOfRange = 32054429,
    }
}
