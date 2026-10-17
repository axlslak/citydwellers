using System;
using System.Collections.Generic;
using System.Linq;

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
    }
}
