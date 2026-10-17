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

        // Accept the original configuration spelling without changing the file
        // or creating a second banker. An explicit canonical entry takes priority.
        public static JObject NormalizeAliases(JObject roles)
        {
            if (roles == null) return null;
            JProperty alias = roles.Properties().FirstOrDefault(p =>
                string.Equals(p.Name, "typesetter", StringComparison.OrdinalIgnoreCase));
            if (alias == null) return roles;
            var result = (JObject)roles.DeepClone();
            if (result.GetValue("types", StringComparison.OrdinalIgnoreCase) == null)
                result["types"] = alias.Value.DeepClone();
            result.Property(alias.Name).Remove();
            return result;
        }

        public static JObject EnabledOnly(JObject roles) => roles == null ? null :
            new JObject(NormalizeAliases(roles).Properties().Where(p => IsEnabled(p.Value))
                .Select(p => new JProperty(p.Name, p.Value.DeepClone())));

        public static string[] ActiveNames(JObject roles, bool storageOnly = false)
        {
            roles = NormalizeAliases(roles);
            return (storageOnly ? Storage : All).Where(name =>
                roles?.GetValue(name, StringComparison.OrdinalIgnoreCase) is JObject role && IsEnabled(role)).ToArray();
        }
    }
}
