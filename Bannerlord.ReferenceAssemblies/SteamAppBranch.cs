using System;
using System.Collections.Generic;

namespace Bannerlord.ReferenceAssemblies
{
    internal struct SteamAppBranch
    {
        public static readonly IReadOnlyDictionary<BranchType, string?> VersionPrefixToName = new SortedList<BranchType, string?>
        {
            { BranchType.Alpha, "Alpha" },
            { BranchType.Beta, "Beta" },
            { BranchType.EarlyAccess, "EarlyAccess" },
            { BranchType.Development, "Development" },
            { BranchType.Release, null },
            { BranchType.Unknown, "Invalid" },
        };
        
        public BranchType Prefix
        {
            get
            {
                if (string.IsNullOrEmpty(Name) || Name.Length < 1 || !char.IsDigit(Name[1]) || !Enum.IsDefined(typeof(BranchType), (int) Name[0]))
                    return BranchType.Unknown;
                return (BranchType) Name[0];
            }
        }

        public string Name { get; set; } 
        public uint AppId { get; set; }
        public uint DepotId { get; set; }
        public uint BuildId { get; set; }

        public string GetVersion(string appVersion) =>
            //char.IsDigit(Name[1]) ? $"{Name[1..]}.{appVersion}-{Name[0]}" : "";
            char.IsDigit(Name[1]) ? $"{Name[1..]}.{appVersion}" : "";

        public override string ToString() => $"{Name} ({AppId} {DepotId} {BuildId})";
    }
}