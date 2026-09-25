using CityDwellers.Shared;

namespace MalisBuffBots
{
    // Keep Mali's rank seam intact. City Dwellers owns the authority source;
    // no per-buffer UserRanks.json is loaded or written.
    public class UserRank
    {
        public bool MeetsRank(Rank rank, string name)
        {
            switch (rank)
            {
                case Rank.Unranked:
                    return ManagerMemory.Current.BufferIsMember(name);
                case Rank.Warper:
                    return ManagerMemory.Current.BufferIsWarper(name);
                case Rank.Moderator:
                    return ManagerMemory.Current.BufferIsRanked(name) ||
                           ManagerMemory.Current.BufferIsAdmin(name);
                case Rank.Admin:
                    return ManagerMemory.Current.BufferIsAdmin(name);
            }

            return false;
        }
    }
}
