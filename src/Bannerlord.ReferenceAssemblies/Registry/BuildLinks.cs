using System.Text.Json;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// builds/links.json: which Steam and GOG builds of the game are the same build. Two builds are the same when
/// they report the same version, letter aside, and the same changeset; the changeset alone is not enough,
/// as TaleWorlds restarted its count in 2022. Several Steam builds can share one, such as a public build
/// and the perf_test build made from it. A link says whether the GOG side was read from the builds' files
/// or taken from their labels. Only builds on both stores are listed, and the file is rebuilt from the two
/// registries every time, so it never says more than they do.
/// </summary>
internal sealed class BuildLinks
{
    public uint SteamAppId { get; set; }

    public ulong GogProductId { get; set; }

    public List<BuildLink> Builds { get; set; } = [];

    /// <summary>Pairs the builds of the two registries, and logs any changeset the two report different versions for.</summary>
    public static BuildLinks Compute(BuildRegistry steam, GogRegistry gog)
    {
        var steamBuilds = steam.Builds
            .Where(x => x is { HasVersion: true, ChangeSet: not null })
            .ToLookup(x => (Version: GogBuildEntry.StripLetter(x.Version!), ChangeSet: x.ChangeSet!.Value));

        var links = new List<BuildLink>();
        foreach (var group in gog.Builds.Where(x => x.HasVersion).GroupBy(x => (Version: GogBuildEntry.StripLetter(x.Version!), ChangeSet: x.ChangeSet!.Value)))
        {
            var onSteam = steamBuilds[group.Key].OrderBy(x => x.Date).ThenBy(x => x.BuildId).ToList();
            if (onSteam.Count == 0)
            {
                if (steam.Builds.Where(x => x.ChangeSet == group.Key.ChangeSet && x.HasVersion).Select(x => x.Version!).Distinct().ToList() is { Count: > 0 } others)
                    Log.Info($"GOG {group.Key.Version}.{group.Key.ChangeSet} shares its changeset with Steam {string.Join(", ", others)}; not linked");
                continue;
            }

            links.Add(new BuildLink(
                onSteam[0].Version!,
                group.Key.ChangeSet,
                onSteam.Select(x => x.BuildId).ToList(),
                group.OrderBy(x => x.Date).ThenBy(x => x.BuildId, StringComparer.Ordinal).Select(x => x.BuildId).ToList(),
                group.All(x => x.Verified)));
        }

        return new BuildLinks
        {
            SteamAppId = steam.AppId,
            GogProductId = gog.ProductId,
            Builds = links
                .OrderBy(x => steam.Find(x.Steam[0])!.Date)
                .ThenBy(x => x.Steam[0])
                .ToList(),
        };
    }

    public void Save(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, BuildRegistry.JsonOptions) + "\n");
    }
}

/// <param name="Version">The version as Steam reports it, with its letter.</param>
/// <param name="ChangeSet">The changeset both builds report.</param>
/// <param name="Steam">The Steam build ids, oldest first.</param>
/// <param name="Gog">The GOG build ids, oldest first.</param>
/// <param name="Verified">
/// Whether the link rests on files alone. Steam versions are always read from the builds' files; this
/// says the GOG builds' were too, every one of them, and not only taken from their labels.
/// </param>
internal sealed record BuildLink(string Version, int ChangeSet, List<uint> Steam, List<string> Gog, bool Verified);
