using System.Text.Json.Serialization;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// One Steam build of the app. Manifest ids are strings so that JSON readers without 64-bit integers
/// keep them intact.
/// </summary>
internal sealed class BuildEntry
{
    private static readonly Regex RxVersionBranch = new(@"^[a-z]\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    public uint BuildId { get; set; }

    /// <summary>When the build reached the branch it was first seen in.</summary>
    public DateTimeOffset Date { get; set; }

    /// <summary>Patch title from SteamDB or the branch description from Steam. Informational.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>Every branch the build was seen in: public, beta, or a version branch such as v1.2.9.</summary>
    public List<string> Branches { get; set; } = [];

    /// <summary>Game version such as v1.3.7 or e1.7.2. Null until read from the binaries.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Source revision behind the build. Null when the binaries carry none: the launch-era builds predate
    /// ApplicationVersion.DefaultChangeSet and nothing else in them records a revision. That is a fact
    /// about the build, not a failed read, so it is kept as null rather than written as zero, which would
    /// make twenty different builds look like one. See <see cref="PackageVersion"/>.
    /// </summary>
    public int? ChangeSet { get; set; }

    /// <summary>
    /// The package version last pushed to the feed for this build. A scheduled run decides what is
    /// missing from this alone, without asking NuGet. It is the version rather than a flag so that a
    /// build whose version is later corrected is packaged again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublishedVersion { get; set; }

    /// <summary>The files came down but carried no version. Not retried.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool VersionUnreadable { get; set; }

    /// <summary>
    /// Steam refused the manifest of this build the last time it was asked. A manifest that was never
    /// public stops being served once its branch moves on, so this is mostly permanent, but refusals
    /// have also been seen to lift; update --retryUnavailable asks again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ContentUnavailable { get; set; }

    /// <summary>
    /// The version each module's own manifest declares, by module folder: the official modules follow the
    /// game, a DLC such as War Sails has a line of its own (NavalDLC: v1.2.8). Packed as a moduleVersion
    /// tag on the module's package; the package version itself is always the game's. Null until read.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ModuleVersions { get; set; }

    /// <summary>Depot id to manifest id. Downloading needs access to these manifests and the depot chunks.</summary>
    public Dictionary<string, string> Manifests { get; set; } = [];

    [JsonIgnore]
    public bool HasVersion => !string.IsNullOrEmpty(Version);

    /// <summary>
    /// Package id suffix for the release kind: e-versions pack as .EarlyAccess, v-versions unsuffixed.
    /// Anything else, such as the pre-launch b0.8.7, is not something the feed should carry.
    /// </summary>
    [JsonIgnore]
    public string? PackageSuffix => Version is [var kind, ..] ? kind switch { 'e' => ".EarlyAccess", 'v' => "", _ => null } : null;

    [JsonIgnore]
    public bool CanBeGenerated => PackageSuffix is not null;

    /// <summary>Whether the feed already carries this build at the version it currently reports.</summary>
    [JsonIgnore]
    public bool IsPublished => CanBeGenerated && PublishedVersion == PackageVersion;

    /// <summary>
    /// Whether the build is one modders build against. Branches like perf_test or a one-off launcher
    /// fix are internal experiments.
    /// </summary>
    [JsonIgnore]
    public bool IsReleaseLike => Branches.Any(x => x is "public" or "beta" || IsVersionBranch(x));

    /// <summary>A build is a beta only while it has never been in public or in a version branch.</summary>
    [JsonIgnore]
    public bool IsBeta => Branches.Contains("beta") && !Branches.Any(x => x == "public" || IsVersionBranch(x));

    /// <summary>
    /// NuGet version: the game version without its letter, then the changeset, e.g. 1.3.7.102919 or
    /// 1.5.2.121216-beta. The launch-era builds carry no changeset and twenty of them all call themselves
    /// e1.0.0, so for those the Steam build id stands in as the fourth part: it is the only value that
    /// tells them apart, it rises over time like a changeset does, and the package tags carry it anyway.
    /// </summary>
    [JsonIgnore]
    public string PackageVersion => $"{Version![1..]}.{ChangeSet?.ToString() ?? BuildId.ToString()}{(IsBeta ? "-beta" : "")}";

    public static bool IsVersionBranch(string name) => RxVersionBranch.IsMatch(name);

    public override string ToString() =>
        $"{BuildId} {(HasVersion ? $"{Version}.{ChangeSet?.ToString() ?? "no-changeset"}" : "?")} [{string.Join(", ", Branches)}]";
}
