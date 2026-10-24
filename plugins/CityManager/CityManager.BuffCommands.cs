using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private sealed class PersonalBuffChoice
        {
            public string Name;
            public string[] Tags;
            public int[] Ids;
            public KeyValuePair<int, int>[] Casts; // nano id, recipient level
            public bool Paid;
        }

        private sealed class ServiceBuffRow
        {
            public BufferBotInfo Provider;
            public BufferAdvertisedBuff Buff;
            public bool Available;
        }

        private List<ServiceBuffRow> ServiceBuffRows()
        {
            var memory = ManagerMemory.Current;
            var now = DateTime.UtcNow;
            var paid = memory.ReadPaidBuffers();
            var rows = memory.BufferBotInfos().SelectMany(provider =>
                (provider.AdvertisedBuffs ?? new BufferAdvertisedBuff[0]).Select(buff => new ServiceBuffRow {
                    Provider = provider, Buff = buff,
                    Available = paid.Any(p => string.Equals(p.Character, provider.Character, StringComparison.OrdinalIgnoreCase) && !p.Blocked) ||
                        IsFreshReadyBuffer(provider, now) })).ToList();
            // Profiles bootstrap an as-yet unaudited on-demand character. Once an
            // audit exists, its uploaded capability list decides what is offered.
            foreach (var provider in paid)
            foreach (var nano in PaidBufferCatalogue.ForProfession(provider.Profession)
                .Where(n => memory.PaidBufferOffers(provider.Character, n.Id)))
            {
                var existing = rows.FirstOrDefault(r => string.Equals(r.Provider.Character, provider.Character, StringComparison.OrdinalIgnoreCase) &&
                    (r.Buff.NanoIds ?? new int[0]).Contains(nano.Id));
                if (existing != null)
                {
                    // Catalogues saved before schema 12 have no effect-ID rows.
                    // Preserve their uploaded capabilities while filling known
                    // cast/effect relationships; no new audit is required.
                    existing.Buff.EffectIds = (existing.Buff.EffectIds ?? new int[0])
                        .Concat(nano.EffectIds ?? new int[0]).Distinct().ToArray();
                    continue;
                }
                rows.Add(new ServiceBuffRow { Provider = new BufferBotInfo { Character = provider.Character,
                    Profession = provider.Profession }, Available = !provider.Blocked,
                    Buff = new BufferAdvertisedBuff { Profession = provider.Profession, Name = nano.Name,
                        Description = nano.Description, Tag = nano.Tags[0], Tags = nano.Tags,
                        Type = nano.IsTeam ? "Team" : "Single", Ncu = nano.Ncu,
                        NanoIds = new[] { nano.Id }, EffectIds = nano.EffectIds ?? new int[0],
                        Nanos = new[] { new BufferNanoMetadata { Id = nano.Id, Level = nano.Level, Ncu = nano.Ncu } } } });
            }
            return rows;
        }

        private void ProcessBuffCommand(string senderName, string[] parts, ReplyTarget target)
        {
            string action = parts.Length == 1 ? "list" : parts[1].ToLowerInvariant();
            if (action == "list" && parts.Length <= 2)
            {
                ProcessBufferBuffListCommand(new[] { "bufflist" }, target);
                return;
            }
            if ((action == "redo" || action == "rebuff" || action == "macro") && parts.Length == 2)
            {
                string command = action == "macro" ? "buffmacro" : "rebuff";
                BeginBufferPublicCommand(senderName, command, new[] { command }, target);
                return;
            }
            if (action == "mine" && parts.Length == 2)
            {
                if (!RequirePersonalBuffIdentity(target)) return;
                var privateTarget = ReplyTarget.ForTell(target.SenderId, senderName);
                QueuePublicWork(privateTarget, () => ShowPersonalBuffs(privateTarget));
                return;
            }
            if ((action == "add" || action == "cast") && parts.Length >= 3)
            {
                BeginBufferPublicCommand(senderName, "cast", new[] { "cast" }.Concat(parts.Skip(2)).ToArray(), target);
                return;
            }
            if ((action == "del" || action == "rem" || action == "remove") && parts.Length >= 3)
            {
                if (!RequirePersonalBuffIdentity(target)) return;
                var privateTarget = ReplyTarget.ForTell(target.SenderId, senderName);
                string[] arguments = parts.Skip(2).ToArray();
                QueuePublicWork(privateTarget, () => RemovePersonalBuffSelection(senderName, arguments, privateTarget));
                return;
            }
            if (TryProcessBuffHistoryCommand(parts, target)) return;
            Reply(target, Usage(target, "buff list | buff mine | buff add <tag, name or ID> | buff remove <tag, name or ID> | buff redo | buff macro | buff last [page] | buff top | buff <person> [page]"));
        }

        private bool RequirePersonalBuffIdentity(ReplyTarget target)
        {
            if (target != null && target.SenderId != 0) return true;
            Reply(target, "I could not resolve your AO character identity for this buff request.");
            return false;
        }

        private List<PersonalBuffChoice> PersonalBuffChoices()
        {
            var memory = ManagerMemory.Current;
            var result = new List<PersonalBuffChoice>();
            foreach (var row in ServiceBuffRows())
            {
                var buff = row.Buff;
                result.Add(new PersonalBuffChoice { Name = buff.Name,
                    Tags = (buff.Tags ?? new string[0]).Concat(new[] { buff.Tag }).Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Ids = (buff.NanoIds ?? new int[0]).Concat(buff.EffectIds ?? new int[0]).Distinct().ToArray(),
                    Casts = (buff.NanoIds ?? new int[0]).Select(id => new KeyValuePair<int, int>(id,
                        (buff.Nanos ?? new BufferNanoMetadata[0]).FirstOrDefault(n => n.Id == id)?.Level ?? 0))
                        .OrderByDescending(n => n.Value).ToArray(), Paid = memory.IsPaidBuffer(row.Provider.Character) });
            }
            return result;
        }

        private bool TryServeObservedBuffCommand(string senderName, string command, ReplyTarget target)
        {
            if (command != "rebuff" && command != "buffmacro") return false;
            var player = ManagerMemory.Current.ReadNearbyPlayers()
                .FirstOrDefault(p => p.CharacterId == unchecked((int)target.SenderId));
            if (player == null) return false; // Retain the existing nearby-caster read fallback.
            var choices = PersonalBuffChoices();
            var ids = new HashSet<int>();
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int effect in player.VisibleNanoIds.Where(id => !BufferRefreshPolicy.IsRequestOnlyNano(id)))
            {
                int canonicalCast = PaidBufferCatalogue.FindEffect(effect)?.Id ?? effect;
                var choice = choices.FirstOrDefault(c => c.Casts.Length != 0 &&
                    (c.Ids.Contains(effect) || c.Casts.Any(n => n.Key == canonicalCast)));
                if (choice == null) continue;
                int cast = choice.Casts.Any(n => n.Key == effect) ? effect : canonicalCast;
                if (cast == 0 || !choice.Casts.Any(n => n.Key == cast)) continue;
                ids.Add(cast);
                tags.Add(choice.Tags.FirstOrDefault() ?? cast.ToString());
            }
            DevTrace("BUFFER " + command + " requester=" + senderName + " matched=" + ids.Count +
                " ncuCast=" + (ids.Contains(275043) ? "275043" : "none") + ".");
            if (ids.Count == 0)
                Reply(target, "No supported active buffs were visible.");
            else if (command == "buffmacro")
                Reply(target, "<font color='" + ColorCommand + "'>/macro buffpreset /tell " +
                    Client.CharacterName + " buff add " + EscapeBlobText(string.Join(" ", tags.OrderBy(t => t))) + "</font>");
            else
                ServeBufferMacro(senderName, ids.Select(id => id.ToString()).ToArray(),
                    player.Level > 0 ? (int?)player.Level : null, target);
            return true;
        }

        private static bool MatchesBuffChoice(PersonalBuffChoice choice, string query)
        {
            int id;
            return (int.TryParse(query, out id) && choice.Ids.Contains(id)) ||
                string.Equals(choice.Name, query, StringComparison.OrdinalIgnoreCase) ||
                (choice.Tags ?? new string[0]).Contains(query, StringComparer.OrdinalIgnoreCase);
        }

        private string[] SelectAndResolveBuffArguments(string senderName, string[] arguments, int? level, ReplyTarget target)
        {
            var choices = PersonalBuffChoices();
            string fullName = string.Join(" ", arguments).Trim('"');
            var named = choices.Where(c => string.Equals(c.Name, fullName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var queries = named.Length != 0 ? new[] { fullName } : arguments;
            var requests = new List<string>();
            var selected = new List<int>();
            var related = new List<int>();
            foreach (string query in queries)
            {
                var matches = choices.Where(c => MatchesBuffChoice(c, query)).ToArray();
                // Retain paid/free shared-tag context, particularly low-level NCU.
                var choice = matches.Where(c => c.Casts.Length != 0)
                    .OrderByDescending(c => c.Paid && !(query == "ncu" && level.HasValue && level.Value < c.Casts[0].Value))
                    .FirstOrDefault();
                if (choice == null)
                {
                    if (matches.Length != 0)
                        Reply(target, "Can't cast " + matches[0].Name + ": no configured buffer advertises it.");
                    else requests.Add(query); // Existing engine reports unknown tags without rejecting the other work.
                    continue;
                }
                int numeric;
                bool exact = int.TryParse(query, out numeric);
                int exactCast = exact ? numeric : 0;
                if (exact && !choice.Casts.Any(n => n.Key == exactCast))
                {
                    exactCast = PaidBufferCatalogue.FindEffect(numeric)?.Id ?? 0;
                    if (!choice.Casts.Any(n => n.Key == exactCast))
                    {
                        Reply(target, "Can't cast nano " + numeric + ": no configured buffer advertises that version.");
                        continue;
                    }
                }
                var cast = choice.Casts.FirstOrDefault(n => (!exact || n.Key == exactCast) &&
                    (!level.HasValue || n.Value <= level.Value));
                if (cast.Key != 0)
                {
                    related.AddRange(choice.Ids);
                    if (level.HasValue || choice.Casts.All(n => n.Value == 0)) selected.Add(cast.Key);
                    bool isTag = choice.Tags.Contains(query, StringComparer.OrdinalIgnoreCase);
                    requests.Add(isTag ? query : cast.Key.ToString());
                }
                else
                {
                    // Let the usual cast service explain recipient requirements.
                    requests.Add(choice.Casts[0].Key.ToString());
                }
            }
            if (related.Count != 0)
                ManagerMemory.Current.SelectPersonalBuffs(unchecked((int)target.SenderId), senderName,
                    selected.Distinct().ToArray(), related.Distinct().ToArray());
            return requests.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private string PersonalBuffName(int id, List<PersonalBuffChoice> choices)
        {
            try
            {
                NanoItem nano;
                if (ItemData.Find(id, out nano)) return nano.Name;
            }
            catch { }
            return choices.FirstOrDefault(c => c.Ids.Contains(id))?.Name ?? "Nano " + id;
        }

        private void RemovePersonalBuffSelection(string senderName, string[] arguments, ReplyTarget target)
        {
            int characterId = unchecked((int)target.SenderId);
            var choices = PersonalBuffChoices();
            var profile = ManagerMemory.Current.ReadLearnedBuffProfile(characterId);
            var remembered = (profile?.NanoIds ?? new List<int>()).Concat(profile?.ExcludedNanoIds ?? new List<int>()).Distinct().ToArray();
            string fullName = string.Join(" ", arguments).Trim('"');
            bool isName = choices.Any(c => string.Equals(c.Name, fullName, StringComparison.OrdinalIgnoreCase)) ||
                remembered.Any(id => string.Equals(PersonalBuffName(id, choices), fullName, StringComparison.OrdinalIgnoreCase));
            var queries = isName ? new[] { fullName } : arguments;
            var remove = new HashSet<int>();
            foreach (string query in queries)
            {
                int id;
                var matches = choices.Where(c => MatchesBuffChoice(c, query)).ToArray();
                foreach (var match in matches) foreach (int nano in match.Ids) remove.Add(nano);
                if (int.TryParse(query, out id) && id > 0) remove.Add(id);
                foreach (int nano in remembered.Where(n => string.Equals(PersonalBuffName(n, choices), query, StringComparison.OrdinalIgnoreCase))) remove.Add(nano);
                if (matches.Length == 0 && !int.TryParse(query, out id) &&
                    !remembered.Any(n => string.Equals(PersonalBuffName(n, choices), query, StringComparison.OrdinalIgnoreCase)))
                    Reply(target, "Unknown buff '" + EscapeBlobText(query) + "'. Use buff mine for remove buttons.");
            }
            if (remove.Count == 0) return;
            ManagerMemory.Current.RemovePersonalBuffs(characterId, senderName, remove.ToArray());
            Reply(target, "Removed from your remembered buffs; automatic refresh is off. Use buff add to select it again.");
            ShowPersonalBuffs(target);
        }

        private void ShowPersonalBuffs(ReplyTarget target)
        {
            var profile = ManagerMemory.Current.ReadLearnedBuffProfile(unchecked((int)target.SenderId));
            var choices = PersonalBuffChoices();
            var offeredIds = new HashSet<int>(choices.SelectMany(choice => choice.Casts)
                .Select(cast => cast.Key));
            var body = new StringBuilder();
            body.Append(CommandLink(target, "buff list", "Available buffs")).Append("\n")
                .Append("These are your selected buffs from our available catalogue. Remove turns off automatic refresh; it does not cancel your current NCU effect.\n\n");
            var ids = (profile?.NanoIds ?? new List<int>()).Where(id => !BufferRefreshPolicy.IsRequestOnlyNano(id) &&
                !ManagerMemory.Current.IsBuffExcluded(unchecked((int)target.SenderId), id))
                .Select(id => PaidBufferCatalogue.FindEffect(id)?.Id ?? id).Distinct()
                .Where(offeredIds.Contains)
                .OrderBy(id => PersonalBuffName(id, choices), StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length == 0) body.Append("No supported buffs selected.\n");
            foreach (int id in ids)
            {
                body.Append(CommandLink(target, "buff remove " + id, "Remove")).Append(" ")
                    .Append(EscapeBlobText(PersonalBuffName(id, choices))).Append(" (").Append(id).Append(")");
                body.Append("\n");
            }
            ReplyBufferCatalogue(target, BuildBlobLinks(target, "My buffs", "My buffs", body.ToString()));
        }
    }
}
