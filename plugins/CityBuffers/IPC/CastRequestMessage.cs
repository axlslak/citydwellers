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
    [AoContract((int)IPCOpcode.CastRequest)]
    public class CastRequestMessage : IPCMessage
    {
        public override short Opcode => (int)IPCOpcode.CastRequest;

        [AoMember(0)]
        public Profession Caster { get; set; }

        [AoMember(1)]
        public int Requester { get; set; }

        [AoMember(2, SerializeSize = ArraySizeType.Int32)]
        public NanoEntry[] Entries { get; set; }
    }
}
