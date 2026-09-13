namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// The two Steam apps this tool packages. Everything that differs between them lives here, so a command
/// only ever asks for <c>--app game</c> or <c>--app server</c>.
/// </summary>
internal sealed record App(
    string Name,
    uint AppId,
    IReadOnlyList<uint> DepotIds,
    IReadOnlyList<uint> DlcAppIds,
    IReadOnlyList<string> BinFolders,
    string PackagePrefix,
    string Title,
    string Description,
    string Tags)
{
    public static readonly App Game = new(
        Name: "game",
        AppId: 261550,
        DepotIds: [261551, 261552],
        DlcAppIds: [2927200],
        BinFolders: ["Win64_Shipping_Client"],
        PackagePrefix: "Bannerlord.ReferenceAssemblies",
        Title: "Bannerlord Game Reference Assemblies",
        Description: "Contains stripped metadata-only libraries for building against Mount & Blade II: Bannerlord.",
        Tags: "bannerlord game reference assemblies");

    public static readonly App Server = new(
        Name: "server",
        AppId: 1863440,
        DepotIds: [1863441],
        DlcAppIds: [],
        BinFolders: ["Win64_Shipping_Server", "Linux64_Shipping_Server"],
        PackagePrefix: "Bannerlord.ReferenceAssemblies.Server",
        Title: "Bannerlord Dedicated Server Reference Assemblies",
        Description: "Contains stripped metadata-only libraries for building against the Mount & Blade II: Bannerlord dedicated server. The Windows and Linux server builds share these assemblies.",
        Tags: "bannerlord dedicated server windows linux reference assemblies");

    public static readonly App ModdingKit = new(
        Name: "moddingkit",
        AppId: 1393600,
        DepotIds: [1393601],
        DlcAppIds: [],
        BinFolders: ["Win64_Shipping_wEditor"],
        PackagePrefix: "Bannerlord.ReferenceAssemblies.ModdingKit",
        Title: "Bannerlord Modding Kit Reference Assemblies",
        Description: "Contains stripped metadata-only libraries for building against the Mount & Blade II: Bannerlord Modding Kit (editor build).",
        Tags: "bannerlord modding kit editor reference assemblies");

    public static readonly IReadOnlyList<App> All = [Game, Server, ModdingKit];

    /// <summary>The depot that holds the binaries; the game version is read from it.</summary>
    public uint PrimaryDepotId => DepotIds[0];

    /// <summary>
    /// The platform folders under bin/ that hold assemblies. The dedicated server ships Windows and Linux
    /// builds side by side; their managed assemblies are the same bytes, so one package covers both and
    /// the packager only checks that this stays true. The first folder is the one the version is read from.
    /// </summary>
    private string BinFolderPattern => BinFolders.Count == 1 ? BinFolders[0] : $"({string.Join("|", BinFolders)})";

    public static App Parse(string name) =>
        All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown app '{name}'. Use one of: {string.Join(", ", All.Select(x => x.Name))}.");

    /// <summary>Package id of the meta package (module null), of Core, or of a module, with the release-kind suffix.</summary>
    public string PackageId(string? module, string suffix) =>
        module is null ? $"{PackagePrefix}{suffix}" : $"{PackagePrefix}.{module}{suffix}";

    /// <summary>The packages a Steam build of this app becomes.</summary>
    public PackageSpec ForBuild(BuildEntry build) => new(
        PackagePrefix,
        build.PackageSuffix ?? throw new InvalidOperationException($"Build {build.BuildId} cannot be packaged: {build.Version ?? "no version"}"),
        build.PackageVersion,
        Title,
        Description,
        [.. Tags.Split(' '), $"buildId:{build.BuildId}", $"appId:{AppId}"],
        BinFolders,
        build.Version!,
        build.ModuleVersions ?? new Dictionary<string, string>());

    /// <summary>
    /// What to download for packaging: the engine assemblies, every module's assemblies, and the files
    /// that name the build. The dedicated server keeps all of its assemblies in the engine folder and has
    /// no Version.xml, so for it the module manifests are the only place the version can be read from.
    /// </summary>
    public IReadOnlyList<Regex> PackageFileFilters =>
    [
        Filter($@"^bin/{BinFolderPattern}/Version\.xml$"),
        Filter($@"^bin/{BinFolderPattern}/[^/]*TaleWorlds[^/]*$"),
        Filter($@"^Modules/[^/]+/bin/{BinFolderPattern}/[^/]+\.dll$"),
        Filter(@"^Modules/[^/]+/SubModule\.xml$"),
    ];

    /// <summary>
    /// The few small files that carry a version: the library assembly, the Version.xml beside it where the
    /// build has one, and the module manifests. All three are needed, because the assembly went unbumped
    /// through whole runs of patches and the dedicated server ships no Version.xml.
    /// </summary>
    public IReadOnlyList<Regex> VersionFileFilters =>
    [
        Filter($@"^bin/{BinFolders[0]}/(TaleWorlds\.Library\.dll|Version\.xml)$"),
        Filter(@"^Modules/[^/]+/SubModule\.xml$"),
    ];

    private static Regex Filter(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
