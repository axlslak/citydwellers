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
    [AoContract((int)IPCOpcode.BanRemove)]
    public class BanRemoveMessage : IPCMessage
    {
        public override short Opcode => (int)IPCOpcode.BanRemove;

        [AoMember(0, SerializeSize = ArraySizeType.Int32)]
        public string Name { get; set; }
    }
}
