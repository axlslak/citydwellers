using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    public sealed partial class ManagerMemory
    {
        private readonly List<LandControlReport> _landControlReports = new List<LandControlReport>();
        private LandControlProjection _landControlProjection;

        private LandControlProjection LandControlView()
        {
            if (_landControlProjection != null) return _landControlProjection;
            var projection = new LandControlProjection();
            var timeline = _landControlEvents.Select(e => new LandControlTimeline { Event = e, Time = e.ObservedUtc, Id = e.Id })
                .Concat(_landControlReports.Select(r => new LandControlTimeline { Report = r, Time = r.ObservedUtc, Id = r.Id }));
            foreach (var row in timeline.OrderBy(r => r.Time).ThenBy(r => r.Event == null ? 1 : 0)
                .ThenBy(r => r.Event?.Sequence ?? 0).ThenBy(r => r.Id, StringComparer.Ordinal))
                if (row.Event != null) projection.Observe(row.Event); else projection.Report(row.Report);
            return _landControlProjection = projection;
        }

        public LandControlSite[] ReadLandControlSites()
        { lock (_accountingSync) return LandControlView().Sites.Values.Select(s => s.Copy()).ToArray(); }

        public void RecordLandControlReport(LandControlReport report)
        {
            Guid id;
            if (report == null || !Guid.TryParseExact(report.Id, "N", out id) || string.IsNullOrWhiteSpace(report.Reporter) ||
                report.ObservedUtc == default(DateTime) || report.ObservedUtc > DateTime.UtcNow.AddMinutes(1) ||
                !LandControlCatalog.ReadSites().Any(s => s.PlayfieldId == report.PlayfieldId && s.Number == report.SiteNumber))
                throw new ArgumentException("A known site, reporter and receipt time are required.");
            if (report.Action != "scout" && report.Action != "empty" && report.Action != "remove") throw new ArgumentException("Unknown scouting action.");
            var site = LandControlCatalog.ReadSites().Single(s => s.PlayfieldId == report.PlayfieldId && s.Number == report.SiteNumber);
            if (report.Action == "scout" && (string.IsNullOrWhiteSpace(report.Organization) ||
                (report.Faction != "Clan" && report.Faction != "Omni" && report.Faction != "Neutral") ||
                !report.ControllerQl.HasValue || report.ControllerQl < site.MinQl || report.ControllerQl > site.MaxQl))
                throw new ArgumentException("Scouting requires organization, faction and a controller QL allowed at this site.");
            if (report.PlantedUtc > report.ObservedUtc || report.CloseSeconds < 0 || report.CloseSeconds >= 86400 ||
                report.Conductors < 0 || report.Turrets < 0 || report.X < 0 || report.Y < 0 || report.OrgId <= 0)
                throw new ArgumentException("Invalid scouting value or future plant time.");
            lock (_accountingSync)
            {
                if (_landControlPersistence == null) throw new InvalidOperationException("Land control is not initialized.");
                if (_landControlReports.Any(r => r.Id == report.Id)) return;
                var copy = report.Copy();
                copy.ObservedUtc = LandControlMicroseconds(copy.ObservedUtc);
                if (copy.PlantedUtc.HasValue) copy.PlantedUtc = LandControlMicroseconds(copy.PlantedUtc.Value);
                if (copy.PenaltyUntilUtc.HasValue) copy.PenaltyUntilUtc = LandControlMicroseconds(copy.PenaltyUntilUtc.Value);
                _landControlPersistence.AppendLandControlReport(copy);
                _landControlReports.Add(copy);
                if (_landControlProjection != null && copy.ObservedUtc > _landControlProjection.LastUtc) _landControlProjection.Report(copy);
                else _landControlProjection = null;
            }
        }

        public static bool LandControlMatches(string value, string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            if (value == null) return false;
            return search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(word => LandControlWildcard(value, "*" + word + "*"));
        }

        private static DateTime LandControlMicroseconds(DateTime time) => new DateTime(time.Ticks - time.Ticks % 10, DateTimeKind.Utc);

        private static bool LandControlWildcard(string value, string pattern)
        {
            // Greedy wildcard matching has no regular-expression backtracking
            // tree and cannot monopolize the accounting lock with a public filter.
            value = value.ToUpperInvariant(); pattern = pattern.ToUpperInvariant();
            int v = 0, p = 0, star = -1, start = 0;
            while (v < value.Length)
            {
                if (p < pattern.Length && (pattern[p] == '*' || pattern[p] == '%')) { star = p++; start = v; }
                else if (p < pattern.Length && (pattern[p] == '_' || pattern[p] == value[v])) { v++; p++; }
                else if (star >= 0) { p = star + 1; v = ++start; }
                else return false;
            }
            while (p < pattern.Length && (pattern[p] == '*' || pattern[p] == '%')) p++;
            return p == pattern.Length;
        }

        private LandControlAttack ProfileAttack(LandControlAttack original)
        {
            var row = original.Copy();
            PlayerProfile profile;
            if (row.Name != null && _playerProfiles.TryGetValue(row.Name, out profile))
            { row.Level = profile.Level; row.AlienLevel = profile.AlienLevel; row.Profession = profile.Profession; row.ProfileUtc = profile.UpdatedUtc; }
            PlayerSeenRecord seen;
            if (row.Name != null && _seen.TryGetValue(row.Name, out seen)) row.CharacterId = seen.CharacterId;
            return row;
        }

        public LandControlBattlePage ReadLandControlBattles(LandControlBattleQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            lock (_accountingSync)
            {
                var view = LandControlView();
                Func<LandControlAttack, bool> matches = a => LandControlMatches(a.Organization, query.AttackerOrg) &&
                    (LandControlMatches(a.Name, query.Attacker) || !string.IsNullOrEmpty(query.Attacker) &&
                        ProfileAttack(a).CharacterId != 0 && ProfileAttack(a).CharacterId.ToString() == query.Attacker);
                var rows = view.Battles.Values.Where(b =>
                    (!query.BattleId.HasValue || b.Id == query.BattleId) &&
                    (!query.PlayfieldId.HasValue || b.PlayfieldId == query.PlayfieldId) &&
                    (!query.SiteNumber.HasValue || b.SiteNumber == query.SiteNumber) &&
                    (!query.Finished.HasValue || b.Finished == query.Finished) && LandControlMatches(b.DefenderOrg, query.DefenderOrg) &&
                    (string.IsNullOrWhiteSpace(query.EitherOrg) || LandControlMatches(b.DefenderOrg, query.EitherOrg) ||
                        LandControlMatches(b.WinnerOrg, query.EitherOrg) || view.Attacks[b.Id].Any(a => LandControlMatches(a.Organization, query.EitherOrg))) &&
                    ((string.IsNullOrWhiteSpace(query.AttackerOrg) && string.IsNullOrWhiteSpace(query.Attacker)) || view.Attacks[b.Id].Any(matches)))
                    .OrderByDescending(b => b.LastUtc).ThenByDescending(b => b.Id).ToArray();
                int pages = Math.Max(1, (rows.Length + 9) / 10), page = Math.Max(1, Math.Min(pages, query.Page));
                var result = rows.Skip((page - 1) * 10).Take(10).Select(b =>
                {
                    var copy = b.Copy(); var attacks = view.Attacks[b.Id];
                    copy.AttackCount = attacks.Count; copy.AttackerTotal = attacks.Count;
                    copy.AttackerPages = Math.Max(1, (attacks.Count + 49) / 50);
                    copy.AttackerPage = query.BattleId.HasValue ? Math.Max(1, Math.Min(copy.AttackerPages, query.Page)) : 1;
                    copy.Attackers = attacks.AsEnumerable().Reverse().Skip((copy.AttackerPage - 1) * 50)
                        .Take(query.BattleId.HasValue ? 50 : 5).Select(ProfileAttack).ToArray();
                    return copy;
                }).ToArray();
                return new LandControlBattlePage { Total = rows.Length, Page = page, Pages = pages, Battles = result };
            }
        }

        public LandControlStatistics ReadLandControlStatistics(DateTime since)
        {
            lock (_accountingSync)
            {
                var result = new LandControlStatistics { FirstUtc = _landControlEvents.Count == 0 ? (DateTime?)null : _landControlEvents[0].ObservedUtc };
                foreach (var row in _landControlEvents.Where(e => e.ObservedUtc >= since))
                {
                    if (row.Kind == "attack")
                    {
                        result.Attacks++;
                        switch ((row.AttackerSide ?? "").ToLowerInvariant()) { case "clan": result.ClanAttacks++; break; case "omni": result.OmniAttacks++; break; case "neutral": result.NeutralAttacks++; break; default: result.UnknownAttacks++; break; }
                        var pf = LandControlCatalog.FindPlayfield(row.Playfield);
                        if (!pf.HasValue || !row.X.HasValue || !row.Y.HasValue || !LandControlCatalog.FindSite(pf.Value, row.X.Value, row.Y.Value).HasValue) result.Unresolved++;
                    }
                    if (row.Kind == "victory") result.Victories++;
                    if (row.Kind == "base-lost") result.BaseLosses++;
                    if (row.Kind == "tower-damage") result.Damage++;
                    if (row.Kind == "tower-destroyed") result.Destroyed++;
                    if (row.Kind == "victory" || row.Kind == "base-lost")
                        switch ((row.DefenderSide ?? "").ToLowerInvariant()) { case "clan": result.ClanLosses++; break; case "omni": result.OmniLosses++; break; case "neutral": result.NeutralLosses++; break; default: result.UnknownLosses++; break; }
                }
                return result;
            }
        }
    }

    internal sealed class LandControlTimeline
    { internal LandControlEvent Event; internal LandControlReport Report; internal DateTime Time; internal string Id; }

    internal sealed class LandControlProjection
    {
        internal readonly Dictionary<string, LandControlSite> Sites = LandControlCatalog.ReadSites().ToDictionary(s => s.Key);
        internal readonly Dictionary<long, LandControlBattle> Battles = new Dictionary<long, LandControlBattle>();
        internal readonly Dictionary<long, List<LandControlAttack>> Attacks = new Dictionary<long, List<LandControlAttack>>();
        internal DateTime LastUtc;
        private static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        private static bool CompatibleFaction(string a, string b) => string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || Same(a, b);

        internal void Report(LandControlReport report)
        {
            LastUtc = report.ObservedUtc;
            var site = Sites[report.PlayfieldId + ":" + report.SiteNumber];
            DateTime? previousPenalty = report.Action == "scout" && site.State == "occupied" && Same(site.Organization, report.Organization) &&
                Same(site.Faction, report.Faction) && site.PlantedUtc == report.PlantedUtc ? site.PenaltyUntilUtc : null;
            Reset(site);
            site.ObservedUtc = report.ObservedUtc; site.Evidence = report.Action + " reported by " + report.Reporter;
            if (report.Action == "empty") { site.State = "unplanted"; return; }
            if (report.Action == "remove") return;
            site.State = "occupied"; site.Organization = report.Organization; site.Faction = report.Faction; site.OrgId = report.OrgId;
            site.ControllerQl = report.ControllerQl; site.Conductors = report.Conductors; site.Turrets = report.Turrets;
            site.PlantedUtc = report.PlantedUtc; site.PenaltyUntilUtc = report.PenaltyUntilUtc;
            if (previousPenalty.HasValue && (!site.PenaltyUntilUtc.HasValue || previousPenalty > site.PenaltyUntilUtc)) site.PenaltyUntilUtc = previousPenalty;
            if (report.X.HasValue && report.Y.HasValue) { site.X = report.X.Value; site.Y = report.Y.Value; }
            site.CloseSeconds = report.CloseSeconds;
            if (!site.CloseSeconds.HasValue && site.PlantedUtc.HasValue)
            {
                int time = (int)site.PlantedUtc.Value.TimeOfDay.TotalSeconds;
                site.CloseSeconds = site.FixedCloseSeconds.HasValue ? site.FixedCloseSeconds.Value + time % 3600 : time;
            }
            if (site.PlantedUtc.HasValue && report.ObservedUtc - site.PlantedUtc.Value <= TimeSpan.FromHours(2))
                Penalty(site.PlantedUtc.Value, site.Organization, site.Faction);
        }

        private static void Reset(LandControlSite site)
        {
            site.State = "unknown"; site.Organization = site.Faction = null; site.OrgId = site.ControllerQl = site.CloseSeconds = site.Conductors = site.Turrets = null;
            site.PlantedUtc = site.PenaltyUntilUtc = null;
            var fact = LandControlCatalog.ReadSites().Single(s => s.Key == site.Key); site.X = fact.X; site.Y = fact.Y;
        }

        private void Penalty(DateTime at, string organization, string faction)
        {
            if (string.IsNullOrWhiteSpace(organization) || string.IsNullOrWhiteSpace(faction)) return;
            foreach (var site in Sites.Values.Where(s => s.State == "occupied" && Same(s.Organization, organization) && Same(s.Faction, faction) && s.PlantedUtc <= at))
            {
                double seconds = LandControlCatalog.Mod((site.PlantedUtc.Value - at).TotalSeconds, 3600) + 3600;
                DateTime until = at.AddSeconds(seconds);
                if (!site.PenaltyUntilUtc.HasValue || until > site.PenaltyUntilUtc.Value) site.PenaltyUntilUtc = until;
            }
        }

        private LandControlBattle NewBattle(LandControlEvent e, int? pf, int? site)
        {
            if (e.Sequence <= 0 || Battles.ContainsKey(e.Sequence)) throw new InvalidOperationException("Land control event sequence is not unique.");
            var battle = new LandControlBattle { Id = e.Sequence, PlayfieldId = pf, SiteNumber = site, Playfield = e.Playfield,
                X = e.X, Y = e.Y, DefenderOrg = e.DefenderOrg, DefenderFaction = e.DefenderSide, FirstUtc = e.ObservedUtc, LastUtc = e.ObservedUtc,
                Resolution = site.HasValue ? "Site matched by recorded coordinate bounds" : "Exact site unresolved" };
            Battles.Add(battle.Id, battle); Attacks.Add(battle.Id, new List<LandControlAttack>()); return battle;
        }

        internal void Observe(LandControlEvent e)
        {
            LastUtc = e.ObservedUtc;
            int? pf = LandControlCatalog.FindPlayfield(e.Playfield);
            int? number = pf.HasValue && e.X.HasValue && e.Y.HasValue ? LandControlCatalog.FindSite(pf.Value, e.X.Value, e.Y.Value) : null;
            LandControlSite site = number.HasValue ? Sites[pf + ":" + number] : null;
            if (e.Kind == "attack")
            {
                // Unknown coordinates must not merge separate lands into site zero.
                var battle = number.HasValue ? Battles.Values.Where(b => !b.Finished && b.PlayfieldId == pf && b.SiteNumber == number &&
                    Same(b.DefenderOrg, e.DefenderOrg) && (Same(b.DefenderFaction, e.DefenderSide) || string.IsNullOrEmpty(b.DefenderFaction) || string.IsNullOrEmpty(e.DefenderSide)) &&
                    e.ObservedUtc - b.LastUtc <= TimeSpan.FromHours(8)).OrderByDescending(b => b.LastUtc).FirstOrDefault() : null;
                battle = battle ?? NewBattle(e, pf, number); battle.LastUtc = e.ObservedUtc;
                if (e.Sequence < battle.Id)
                {
                    var attacks = Attacks[battle.Id]; Battles.Remove(battle.Id); Attacks.Remove(battle.Id);
                    battle.Id = e.Sequence; Battles.Add(battle.Id, battle); Attacks.Add(battle.Id, attacks);
                }
                if (string.IsNullOrEmpty(battle.DefenderFaction)) battle.DefenderFaction = e.DefenderSide;
                Attacks[battle.Id].Add(new LandControlAttack { EventId = e.Id, Name = e.Attacker, Organization = e.AttackerOrg, Faction = e.AttackerSide, ObservedUtc = e.ObservedUtc });
                if (site != null)
                {
                    if (site.State != "occupied" || !Same(site.Organization, e.DefenderOrg)) Reset(site);
                    site.State = "occupied"; site.Organization = e.DefenderOrg; site.Faction = e.DefenderSide ?? site.Faction;
                    site.ObservedUtc = e.ObservedUtc; site.LastAttackUtc = e.ObservedUtc; site.Evidence = "Native attack announcement";
                }
                Penalty(e.ObservedUtc, e.AttackerOrg, e.AttackerSide);
            }
            else if (e.Kind == "victory" || e.Kind == "base-lost")
            {
                var candidates = Battles.Values.Where(b => !b.Finished && b.PlayfieldId == pf && pf.HasValue &&
                    Same(b.DefenderOrg, e.DefenderOrg) && CompatibleFaction(b.DefenderFaction, e.DefenderSide) &&
                    e.ObservedUtc - b.LastUtc <= TimeSpan.FromHours(6) &&
                    (e.Kind == "base-lost" || Attacks[b.Id].Any(a => Same(a.Organization, e.AttackerOrg) && CompatibleFaction(a.Faction, e.AttackerSide)))).ToArray();
                var battle = candidates.Length == 1 ? candidates[0] : NewBattle(e, pf, null);
                battle.Finished = true; battle.Outcome = e.Kind; battle.WinnerOrg = e.AttackerOrg; battle.WinnerFaction = e.AttackerSide; battle.LastUtc = e.ObservedUtc;
                if (candidates.Length == 1) battle.Resolution += "; outcome correlated by parties/playfield within six hours, not a direct site receipt";
                else battle.Resolution = "Outcome site unresolved: " + candidates.Length + " matching battles";
                foreach (var a in Attacks[battle.Id]) a.Winner = e.Kind == "victory" && Same(a.Organization, e.AttackerOrg) && CompatibleFaction(a.Faction, e.AttackerSide);
                // A playfield-only loss invalidates old ownership claims; it does
                // not prove every matching land empty or the winner planted it.
                foreach (var s in Sites.Values.Where(s => s.PlayfieldId == pf && Same(s.Organization, e.DefenderOrg) && CompatibleFaction(s.Faction, e.DefenderSide)))
                { Reset(s); s.ObservedUtc = e.ObservedUtc; s.Evidence = "Playfield loss notice; rescout required"; }
                if (e.Kind == "victory") Penalty(e.ObservedUtc, e.AttackerOrg, e.AttackerSide);
            }
        }
    }
}
