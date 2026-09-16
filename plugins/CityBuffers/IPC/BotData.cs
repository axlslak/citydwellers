using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.Serialization;
using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

namespace MalisBuffBots
{
    public class BotData
    {
        public Identity Identity;
        public long LastUpdateInTicks;
        public int[] SpellData;
        public BuffEntry[] Queue;
        public int TeamMemberId;
        public int TeamTrackerId;
    }
}
