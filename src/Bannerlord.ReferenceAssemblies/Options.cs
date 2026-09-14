using CommandLine;

namespace Bannerlord.ReferenceAssemblies;

internal abstract class CommonOptions
{
    [Option("app", Required = true, HelpText = "Which app to work on: game or server.")]
    public string AppName { get; set; } = default!;

    [Option("registry", HelpText = "Path to the build registry. Default: builds/<appId>.json in the current directory.")]
    public string? Registry { get; set; }

    [Option("workDir", HelpText = "Where downloads, stripped assemblies and packages go. Default: next to the executable.")]
    public string? WorkDir { get; set; }

    public App App => App.Parse(AppName);

    public string RegistryPath => Path.GetFullPath(Registry ?? Path.Combine("builds", $"{App.AppId}.json"));

    public Paths Paths => new(Path.GetFullPath(WorkDir ?? AppContext.BaseDirectory));
}

internal abstract class SteamOptions : CommonOptions
{
    [Option("steamLogin", HelpText = "Steam account that owns the app. Defaults to the STEAM_LOGIN environment variable.")]
    public string? SteamLogin { get; set; }

    [Option("steamPassword", HelpText = "Defaults to the STEAM_PASSWORD environment variable.")]
    public string? SteamPassword { get; set; }

    public SteamClient CreateSteam() => new(
        App,
        SteamLogin ?? Environment.GetEnvironmentVariable("STEAM_LOGIN") ?? throw new ArgumentException("Pass --steamLogin or set STEAM_LOGIN."),
        SteamPassword ?? Environment.GetEnvironmentVariable("STEAM_PASSWORD") ?? throw new ArgumentException("Pass --steamPassword or set STEAM_PASSWORD."));
}

[Verb("update", HelpText = "Records the current Steam branches in the registry and reads the version of builds that have none yet.")]
internal sealed class UpdateOptions : SteamOptions
{
    [Option("fillVersions", Default = 25, HelpText = "How many builds may have their version read in this run. Each one downloads a few small files.")]
    public int FillVersions { get; set; } = 25;

    [Option("buildId", HelpText = "Read only these build ids, however many, whatever their flags say, instead of the newest builds that still need a version.")]
    public IEnumerable<uint> BuildId { get; set; } = [];

    [Option("retryUnavailable", Default = false, HelpText = "Also retry builds whose manifest Steam refused before.")]
    public bool RetryUnavailable { get; set; }
}

[Verb("generate", isDefault: true, HelpText = "Downloads the builds the registry says are not on the feed yet, strips their assemblies and packs them.")]
internal sealed class GenerateOptions : SteamOptions
{
    [Option("dryRun", Default = false, HelpText = "Only work out what is outstanding and write the ids to final/pending-builds.txt. Contacts nothing.")]
    public bool DryRun { get; set; }

    [Option("checkFeed", Default = false, HelpText = "Reconcile the registry's published markers with NuGet before choosing.")]
    public bool CheckFeed { get; set; }

    [Option("feedUrl", Default = NuGetFeed.DefaultUrl)]
    public string FeedUrl { get; set; } = NuGetFeed.DefaultUrl;

    [Option("maxBuilds", Default = 4, HelpText = "Maximum number of builds to generate in one run.")]
    public int MaxBuilds { get; set; } = 4;

    [Option("buildId", HelpText = "Generate these build ids only, whether or not they are on the feed.")]
    public IEnumerable<uint> BuildId { get; set; } = [];

    [Option("includeBeta", Default = true, HelpText = "Also generate builds that only ever appeared in the beta branch.")]
    public bool IncludeBeta { get; set; } = true;
}

[Verb("mark-published", HelpText = "Records in the registry which builds the feed carries, so the other verbs never have to ask NuGet.")]
internal sealed class MarkPublishedOptions : CommonOptions
{
    [Option("buildId", HelpText = "Mark these build ids as published at the version they currently report.")]
    public IEnumerable<uint> BuildId { get; set; } = [];

    [Option("fromFeed", Default = false, HelpText = "Read the feed and rewrite every marker from it.")]
    public bool FromFeed { get; set; }

    [Option("feedUrl", Default = NuGetFeed.DefaultUrl)]
    public string FeedUrl { get; set; } = NuGetFeed.DefaultUrl;
}

[Verb("versions", HelpText = "Reports the current stable and beta versions of the app from the registry, and whether both are on the feed. Contacts nothing.")]
internal sealed class VersionsOptions : CommonOptions
{
}

/// <summary>Where the work lands. Only <c>final</c> is read by anything else: the workflows pick the packages up there.</summary>
internal sealed record Paths(string Root)
{
    public string Depot(uint buildId) => Path.Combine(Root, "depots", buildId.ToString());
    public string Ref(uint buildId) => Path.Combine(Root, "ref", buildId.ToString());
    public string Final => Path.Combine(Root, "final");

    public string WriteFinal(string fileName, string content)
    {
        Directory.CreateDirectory(Final);
        var path = Path.Combine(Final, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteBuildList(string fileName, IEnumerable<BuildEntry> builds) =>
        WriteFinal(fileName, string.Join(" ", builds.Select(x => x.BuildId)));
}
