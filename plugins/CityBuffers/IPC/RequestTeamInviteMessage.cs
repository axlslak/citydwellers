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
    [AoContract((int)IPCOpcode.RequestTeamInvite)]
    public class RequestTeamInviteMessage : IPCMessage
    {
        public override short Opcode => (int)IPCOpcode.RequestTeamInvite;

        [AoMember(0)]
        public bool IsTeamTracker { get; set; }

        [AoMember(1)]
        public int Requester { get; set; }

        [AoMember(2)]
        public int Bot { get; set; }
    }
}
