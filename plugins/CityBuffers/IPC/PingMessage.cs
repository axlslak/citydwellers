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
    [AoContract((int)IPCOpcode.Ping)]
    public class PingMessage : IPCMessage
    {
        public override short Opcode => (int)IPCOpcode.Ping;

        [AoMember(0)]
        public int Requester { get; set; }
    }
}
