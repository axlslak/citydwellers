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
            Reply(target, Usage(target, "buff list | buff mine | buff add <tag, name or ID> | buff remove <tag, name or ID> | buff redo | buff macro"));
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
            var advertised = memory.BufferBotInfos().Where(b => !memory.IsPaidBuffer(b.Character))
                .SelectMany(b => b.AdvertisedBuffs ?? new BufferAdvertisedBuff[0]).ToArray();
            var available = new HashSet<int>(advertised.SelectMany(b => b.NanoIds ?? new int[0]));
            var result = new List<PersonalBuffChoice>();
            // Immutable deployed catalogue supplies Mali aliases and nano families.
            // Live memory decides which of those the configured providers offer.
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Buffers", "JSON", "BuffsDb.json");
                var root = JObject.Parse(System.IO.File.ReadAllText(path));
                foreach (var property in root.Properties())
                foreach (var entry in (JArray)property.Value)
                {
                    var levels = (entry["LevelToId"] ?? new JArray()).Select(n =>
                        new KeyValuePair<int, int>((int)n["Id"], (int?)n["Level"] ?? 0)).ToArray();
                    var ids = levels.Select(n => n.Key).Concat((entry["RemoveNanoIdUponCast"] ?? new JArray())
                        .Select(n => (int)n)).Where(id => id > 0).Distinct().ToArray();
                    result.Add(new PersonalBuffChoice {
                        Name = (string)entry["Name"] ?? "Unknown buff",
                        Tags = (entry["Tags"] ?? new JArray()).Select(t => (string)t).ToArray(),
                        Ids = ids, Casts = levels.Where(n => available.Contains(n.Key)).ToArray() });
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Buff command aliases unavailable; using memory catalogue: " + ex.Message);
            }
            foreach (var buff in advertised)
                if (!result.Any(c => string.Equals(c.Name, buff.Name, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new PersonalBuffChoice { Name = buff.Name, Tags = new[] { buff.Tag },
                        Ids = buff.NanoIds ?? new int[0], Casts = (buff.NanoIds ?? new int[0])
                            .Select(id => new KeyValuePair<int, int>(id, 0)).ToArray() });
            foreach (var provider in memory.ReadPaidBuffers().Select(p => p.Profession).Distinct())
            foreach (var nano in PaidBufferCatalogue.ForProfession(provider))
            {
                // Replace the deployed definition with the paid profile's exact cast/effect IDs.
                result.RemoveAll(c => c.Ids.Contains(nano.Id));
                result.Add(new PersonalBuffChoice { Name = nano.Name, Tags = nano.Tags,
                    Ids = new[] { nano.Id }.Concat(nano.EffectIds ?? new int[0]).Distinct().ToArray(),
                    Casts = new[] { new KeyValuePair<int, int>(nano.Id, nano.Level) }, Paid = true });
            }
            return result;
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
                if (exact && !choice.Paid && !choice.Casts.Any(n => n.Key == numeric))
                {
                    Reply(target, "Can't cast nano " + numeric + ": no configured buffer advertises that version.");
                    continue;
                }
                var cast = choice.Casts.FirstOrDefault(n => (!exact || n.Key == numeric || choice.Paid) &&
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
            var body = new StringBuilder();
            body.Append(CommandLink(target, "buff list", "Available buffs")).Append("\n")
                .Append("These are the buffs remembered for your character. Remove turns off automatic refresh; it does not cancel your current NCU effect.\n\n");
            var ids = (profile?.NanoIds ?? new List<int>()).Where(id => !BufferRefreshPolicy.IsRequestOnlyNano(id) &&
                !ManagerMemory.Current.IsBuffExcluded(unchecked((int)target.SenderId), id))
                .Select(id => PaidBufferCatalogue.FindEffect(id)?.Id ?? id).Distinct()
                .OrderBy(id => PersonalBuffName(id, choices), StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length == 0) body.Append("No buffs selected yet.\n");
            foreach (int id in ids)
            {
                body.Append(CommandLink(target, "buff remove " + id, "Remove")).Append(" ")
                    .Append(EscapeBlobText(PersonalBuffName(id, choices))).Append(" (").Append(id).Append(")");
                if (!choices.Any(c => c.Casts.Length != 0 && c.Ids.Contains(id))) body.Append(" [No configured provider]");
                body.Append("\n");
            }
            ReplyBufferCatalogue(target, BuildBlobLinks(target, "My buffs", "My buffs", body.ToString()));
        }
    }
}
