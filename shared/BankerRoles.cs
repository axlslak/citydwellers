using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    public static class BankerRoles
    {
        public static readonly IReadOnlyList<string> Storage = Array.AsReadOnly(new[] {
            "artillery", "infantry", "control", "support", "extermination",
            "spirit", "dyna", "phatz", "types"
        });
        public static readonly IReadOnlyList<string> All =
            Array.AsReadOnly(new[] { "central" }.Concat(Storage).ToArray());

        // Omitted Enabled preserves existing configurations. Disabled entries
        // remain bootstrap data, but are not members of the running roster.
        public static bool IsEnabled(JToken role) =>
            (role as JObject)?.GetValue("Enabled", StringComparison.OrdinalIgnoreCase)?.Value<bool?>() != false;

        // Configuration member keys identify accounts; persisted Role continues
        // to identify the shared item category and its retention policy.
        public static string Canonical(string member) => member?.Split('#')[0].ToLowerInvariant();

        // A single account remains valid. Accounts lets a category grow without
        // inventing another category, stock command, or set of copy limits.
        public static JObject NormalizeAliases(JObject roles)
        {
            if (roles == null) return null;
            var result = (JObject)roles.DeepClone();
            JProperty alias = result.Properties().FirstOrDefault(p =>
                string.Equals(p.Name, "typesetter", StringComparison.OrdinalIgnoreCase));
            if (alias != null)
            {
                if (result.GetValue("types", StringComparison.OrdinalIgnoreCase) == null)
                    result["types"] = alias.Value.DeepClone();
                alias.Remove();
            }
            var expanded = new JObject();
            foreach (var property in result.Properties())
            {
                string category = Canonical(property.Name);
                if (!All.Contains(category, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unknown banker role: " + property.Name);
                var group = property.Value as JObject;
                var accounts = group?.GetValue("Accounts", StringComparison.OrdinalIgnoreCase) as JArray;
                if (accounts == null) { expanded.Add(property.Name, property.Value.DeepClone()); continue; }
                if (category == "central" && accounts.Count != 1)
                    throw new InvalidOperationException("Bankers requires exactly one Central account.");
                foreach (var token in accounts)
                {
                    var account = token as JObject;
                    string character = (string)account?.GetValue("Character", StringComparison.OrdinalIgnoreCase);
                    if (account == null || string.IsNullOrWhiteSpace(character))
                        throw new InvalidOperationException("Bankers.Roles." + category + ".Accounts requires a Character for each member.");
                    account = (JObject)account.DeepClone();
                    if (!IsEnabled(group)) account["Enabled"] = false;
                    if (account.GetValue("Password", StringComparison.OrdinalIgnoreCase) == null &&
                        group.GetValue("Password", StringComparison.OrdinalIgnoreCase) != null)
                        account["Password"] = group.GetValue("Password", StringComparison.OrdinalIgnoreCase).DeepClone();
                    string key = expanded.GetValue(category, StringComparison.OrdinalIgnoreCase) == null
                        ? category : category + "#" + character.Trim().ToLowerInvariant();
                    if (expanded.GetValue(key, StringComparison.OrdinalIgnoreCase) != null)
                        throw new InvalidOperationException("Duplicate banker account: " + character);
                    expanded.Add(key, account);
                }
            }
            return expanded;
        }

        public static JObject EnabledOnly(JObject roles) => roles == null ? null :
            new JObject(NormalizeAliases(roles).Properties().Where(p => IsEnabled(p.Value))
                .Select(p => new JProperty(p.Name, p.Value.DeepClone())));

        public static string[] ActiveNames(JObject roles, bool storageOnly = false)
        {
            roles = NormalizeAliases(roles);
            return roles?.Properties().Where(p => (storageOnly ? Storage : All)
                .Contains(Canonical(p.Name), StringComparer.OrdinalIgnoreCase) && p.Value is JObject && IsEnabled(p.Value))
                .Select(p => p.Name).ToArray() ?? new string[0];
        }
    }
}
