using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// builds/gog/&lt;productId&gt;.json: every GOG build of the game we know about. Nothing is downloaded from
/// GOG; this is a second record of what was released and when, beside the Steam registry. GOG only shows
/// its few newest public builds without a login, so like the Steam registry this file is what keeps the
/// older ones known.
/// </summary>
internal sealed class GogRegistry
{
    /// <summary>Mount &amp; Blade II: Bannerlord on GOG.</summary>
    public const ulong GameProductId = 1564781494;

    public ulong ProductId { get; set; }

    /// <summary>
    /// The newest build of each branch GOG lists right now, by branch name; the default branch is
    /// recorded as public.
    /// </summary>
    public Dictionary<string, string> Current { get; set; } = [];

    public List<GogBuildEntry> Builds { get; set; } = [];

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    public GogBuildEntry? Find(string buildId) => Builds.FirstOrDefault(x => x.BuildId == buildId);

    public static GogRegistry Load(string path, ulong productId)
    {
        var registry = File.Exists(path)
            ? JsonSerializer.Deserialize<GogRegistry>(File.ReadAllText(path), BuildRegistry.JsonOptions) ?? throw new InvalidDataException($"Registry {path} is empty")
            : new GogRegistry { ProductId = productId };
        if (registry.ProductId != productId)
            throw new InvalidDataException($"Registry {path} belongs to GOG product {registry.ProductId}, expected {productId}");
        registry.FilePath = path;
        Log.Info($"GOG registry {path}: {registry.Builds.Count} builds");
        return registry;
    }

    public void Save()
    {
        Builds = Builds.OrderBy(x => x.Date).ThenBy(x => x.BuildId, StringComparer.Ordinal).ToList();
        if (Path.GetDirectoryName(FilePath) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, BuildRegistry.JsonOptions) + "\n");
    }
}

/// <summary>One GOG build, as GOG's build list and the build's own manifest describe it.</summary>
internal sealed class GogBuildEntry
{
    private static readonly Regex RxLabel = new(@"^[a-z]?(?<version>\d+\.\d+\.\d+)\.(?<changeSet>\d+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>GOG's build id. Kept as a string: it does not fit the integers JavaScript reads exactly.</summary>
    public string BuildId { get; set; } = "";

    public DateTimeOffset Date { get; set; }

    /// <summary>The branch the build was published to; null for GOG's default branch.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Branch { get; set; }

    /// <summary>
    /// The version name TaleWorlds gave the build on GOG, as given: the game version and then the
    /// changeset, such as 1.4.8.119303, or now and then a test name.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// The game version. Once <see cref="Verified"/>, as the build's own files report it, letter included,
    /// like the Steam registry; until then, as the label gives it, mostly without one. Null when the label
    /// is not a version and the files have not been read.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>The changeset, from the same source as <see cref="Version"/>. It is what pairs a GOG build with a Steam one.</summary>
    public int? ChangeSet { get; set; }

    /// <summary>
    /// <see cref="Version"/> and <see cref="ChangeSet"/> were read from the build's own files rather than taken
    /// from the label.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Verified { get; set; }

    /// <summary>
    /// The build's files report another version or changeset than the label says. <see cref="Version"/> and
    /// <see cref="ChangeSet"/> hold what the files report; the label stays as GOG gave it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LabelMismatch { get; set; }

    /// <summary>
    /// GOG does not serve the files the version is read from, or they name no version, so the label cannot
    /// be checked. Not retried.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unverifiable { get; set; }

    /// <summary>The build manifest, at https://cdn.gog.com/content-system/v2/meta/aa/bb/&lt;metaId&gt;.</summary>
    public string MetaId { get; set; } = "";

    /// <summary>
    /// The content depots of the build: the game's and its DLCs', by GOG product id. Null until the build
    /// manifest has been read.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GogDepot>? Depots { get; set; }

    [JsonIgnore]
    public bool HasVersion => Version is not null && ChangeSet is not null;

    /// <summary>Sets <see cref="Version"/> and <see cref="ChangeSet"/> from <see cref="Label"/>, and forgets any check of the files.</summary>
    public void ParseLabel()
    {
        var match = RxLabel.Match(Label.Trim());
        Version = match.Success ? match.Groups["version"].Value : null;
        ChangeSet = match.Success && int.TryParse(match.Groups["changeSet"].Value, out var changeSet) ? changeSet : null;
        (Verified, LabelMismatch, Unverifiable) = (false, false, false);
    }

    /// <summary>
    /// Takes what the build's files report. The letter is not compared: the labels mostly leave it out,
    /// so only a different number or changeset counts as a mismatch.
    /// </summary>
    public void ApplyRead(string version, int? changeSet)
    {
        LabelMismatch = Version is null || StripLetter(version) != StripLetter(Version) || changeSet != ChangeSet;
        (Version, ChangeSet, Verified, Unverifiable) = (version, changeSet, true, false);
    }

    public static string StripLetter(string version) => version is [>= 'a' and <= 'z', ..] ? version[1..] : version;

    public override string ToString() => $"{BuildId} {Label}{(Branch is null ? "" : $" [{Branch}]")}";
}

internal sealed record GogDepot(string ProductId, string Manifest, long Size);
