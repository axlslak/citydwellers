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
    [AoContract((int)IPCOpcode.UpdateBotInfo)]
    public class BotInfoMessage : IPCMessage
    {
        public override short Opcode => (int)IPCOpcode.UpdateBotInfo;

        [AoMember(0)]
        public Profession Profession { get; set; }

        [AoMember(1)]
        public Identity Identity { get; set; }

        [AoMember(2, SerializeSize = ArraySizeType.Int32)]
        public int[] SpellData { get; set; }
    }
}
