using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MalisBuffBots
{
    public enum IPCOpcode
    {
        CastRequest = 0,
        ReceiveQueueInfo = 1,
        UpdateBotInfo = 2,
        RequestTeamInvite = 3,
        Ping = 4,
        Pong = 5,
        UpdateTeamMember = 6,
        RegisterTeamTracker = 7,
    }
}
