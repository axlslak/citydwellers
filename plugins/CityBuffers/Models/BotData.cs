using AOSharp.Common.GameData;

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
