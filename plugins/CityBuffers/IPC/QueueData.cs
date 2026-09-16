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
    public class QueueData
    {

        [AoMember(0)]
        public Profession Profession { get; set; }

        [AoMember(1, SerializeSize = ArraySizeType.Int32)]
        public BuffEntry[] Entries { get; set; }
    }
}
