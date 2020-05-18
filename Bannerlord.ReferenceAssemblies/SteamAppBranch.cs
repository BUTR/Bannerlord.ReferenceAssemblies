using System;
using System.Collections.Generic;

namespace Bannerlord.ReferenceAssemblies
{

    public enum BranchType
    {

        Unknown = 105,
        Alpha = 97,
        Beta = 98,
        EarlyAccess = 101,
        Release = 118,
        Development = 100
    }

    internal struct SteamAppBranch
    {

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

        public string Version { get; set; }

        public uint AppId { get; set; }

        public uint DepotId { get; set; }

        public uint BuildId { get; set; }
    }

}