using System;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class LandControlSite
    {
        public int PlayfieldId, Number, MinQl, MaxQl, X, Y;
        public int? FixedCloseSeconds, CloseSeconds, ControllerQl, OrgId, Conductors, Turrets;
        public string Playfield, ShortName, Name, Organization, Faction, Evidence;
        public string State = "unknown";
        public bool Enabled;
        public DateTime? ObservedUtc, PlantedUtc, PenaltyUntilUtc, LastAttackUtc;
        internal LandControlSite Copy() => (LandControlSite)MemberwiseClone();
        public string Key => PlayfieldId + ":" + Number;
    }

    [Serializable]
    public sealed class LandControlReport
    {
        public string Id, Action, Reporter, Organization, Faction, Details;
        public int PlayfieldId, SiteNumber;
        public int? ControllerQl, OrgId, X, Y, Conductors, Turrets, CloseSeconds;
        public DateTime ObservedUtc;
        public DateTime? PlantedUtc, PenaltyUntilUtc;
        internal LandControlReport Copy() => (LandControlReport)MemberwiseClone();
    }

    [Serializable]
    public sealed class LandControlAttack
    {
        public string EventId, Name, Organization, Faction;
        public DateTime ObservedUtc;
        public bool Winner;
        public uint CharacterId;
        public int Level = -1, AlienLevel = -1, Profession;
        public DateTime? ProfileUtc;
        internal LandControlAttack Copy() => (LandControlAttack)MemberwiseClone();
    }

    [Serializable]
    public sealed class LandControlBattle
    {
        public long Id;
        public int? PlayfieldId, SiteNumber;
        public string Playfield, DefenderOrg, DefenderFaction, WinnerOrg, WinnerFaction, Outcome, Resolution;
        public int? X, Y;
        public DateTime FirstUtc, LastUtc;
        public bool Finished;
        public LandControlAttack[] Attackers;
        public int AttackCount;
        public int AttackerTotal, AttackerPage, AttackerPages;
        internal LandControlBattle Copy() => (LandControlBattle)MemberwiseClone();
    }

    [Serializable]
    public sealed class LandControlBattleQuery
    {
        public int? PlayfieldId, SiteNumber;
        public string DefenderOrg, AttackerOrg, Attacker, EitherOrg;
        public bool? Finished;
        public int Page = 1;
        public long? BattleId;
    }

    [Serializable]
    public sealed class LandControlBattlePage
    {
        public int Total, Page, Pages;
        public LandControlBattle[] Battles;
    }

    [Serializable]
    public sealed class LandControlStatistics
    {
        public int Attacks, Victories, BaseLosses, Damage, Destroyed, Unresolved;
        public int ClanAttacks, OmniAttacks, NeutralAttacks, UnknownAttacks;
        public int ClanLosses, OmniLosses, NeutralLosses, UnknownLosses;
        public DateTime? FirstUtc;
    }
}
