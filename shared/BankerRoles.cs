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

        public static JObject EnabledOnly(JObject roles) => roles == null ? null :
            new JObject(roles.Properties().Where(p => IsEnabled(p.Value))
                .Select(p => new JProperty(p.Name, p.Value.DeepClone())));

        public static string[] ActiveNames(JObject roles, bool storageOnly = false) =>
            (storageOnly ? Storage : All).Where(name =>
                roles?.GetValue(name, StringComparison.OrdinalIgnoreCase) is JObject role && IsEnabled(role)).ToArray();
    }
}
