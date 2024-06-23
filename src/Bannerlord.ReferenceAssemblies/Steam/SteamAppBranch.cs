using System;
using System.Collections.Generic;

namespace Bannerlord.ReferenceAssemblies;

internal record SteamAppBranch
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

    public string? Name { get; init; }
    public uint AppId { get; init; }
    public uint BuildId { get; init; }
    public bool IsBeta { get; init; }

    public override string ToString() => $"{Name} {(IsBeta ? "(beta)" : "")} ({AppId} {BuildId})";
}

internal record SteamAppBranchWithVersion : SteamAppBranch
{
    public BranchType Prefix
    {
        get
        {
            if (string.IsNullOrEmpty(Version) || Version.Length < 1 || !char.IsDigit(Version[1]) || !Enum.IsDefined(typeof(BranchType), (int) Version[0]))
                return BranchType.Unknown;
            return (BranchType) Version[0];
        }
    }

    public string Version { get; init; }
    public string ChangeSet { get; init; }

    public SteamAppBranchWithVersion(string version, string changeSet, SteamAppBranch branch) : base(branch)
    {
        Version = version;
        ChangeSet = changeSet;
    }

    public override string ToString() => $"{Name} {Version}.{ChangeSet} ({AppId} {BuildId})";
}