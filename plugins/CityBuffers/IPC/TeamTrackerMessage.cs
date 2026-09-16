using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using AOSharp.Core.IPC;
using SmokeLounge.AOtomation.Messaging.Serialization;
using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

namespace MalisBuffBots
{
    [AoContract((int)IPCOpcode.RegisterTeamTracker)]
    public class TeamTrackerMessage : IPCMessage
    {
        public override short Opcode => (int)IPCOpcode.RegisterTeamTracker;

        [AoMember(0)]
        public Profession Profession { get; set; }

        [AoMember(1)]
        public int TeamTrackerId { get; set; }
    }
}
