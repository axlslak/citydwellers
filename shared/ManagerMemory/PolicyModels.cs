using System;
using System.Collections.Generic;
using System.Linq;
using CityDwellers.Shared;
namespace CityBankers.Shared
{
    [Serializable]
    public sealed class PhatzPolicyItem
        {
            public int AoId;
            public int HighId;
            public int Ql;
            public string Name;
            public int MaxCopies = -1;
            public string AddedBy;
            public DateTime AddedUtc;
            internal PhatzPolicyItem Copy()
        {
            var copy = (PhatzPolicyItem)MemberwiseClone();

            return copy;
        }
    }
}

namespace CityBankers.Shared
{
    [Serializable]
    public sealed class PhatzPolicyState
        {
            public string Format = "citybankers-phatz-policy-v1";
            public DateTime UpdatedUtc;
            public List<PhatzPolicyItem> Items = new List<PhatzPolicyItem>();
            public List<int> DisabledAoIds = new List<int>();
            public List<ItemTemplatePair> KnownPairs = new List<ItemTemplatePair>();
            internal PhatzPolicyState Copy()
        {
            var copy = (PhatzPolicyState)MemberwiseClone();
            copy.Items = Items?.Select(item => item?.Copy()).ToList();
            copy.DisabledAoIds = DisabledAoIds?.ToList();
            copy.KnownPairs = KnownPairs?.Select(item => item?.Copy()).ToList();
            return copy;
        }
    }
}

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class ItemTemplatePair
    {
        public int LowId;
        public int HighId;
        internal ItemTemplatePair Copy()
        {
            var copy = (ItemTemplatePair)MemberwiseClone();

            return copy;
        }
    }
}

namespace CityBankers.Shared
{
    [Serializable]
    public sealed class BankTerminalState
    {
        public int Instance = 1477725977;
        public string Revision = "initial", ChangedBy;
        public DateTime ChangedUtc;
        internal BankTerminalState Copy() => (BankTerminalState)MemberwiseClone();
    }
}
