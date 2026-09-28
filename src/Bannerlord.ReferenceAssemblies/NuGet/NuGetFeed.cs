using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Bannerlord.ReferenceAssemblies;

internal sealed class NuGetFeed(string url)
{
    public const string DefaultUrl = "https://api.nuget.org/v3/index.json";

    private static readonly Regex RxBuildIdTag = new(@"buildId:(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex RxAppIdTag = new(@"appId:(\d+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// What the feed already carries for the app: the build ids from the buildId tag every package has,
    /// and the package versions. Versions matter too, because two Steam builds can report the same game
    /// version and changeset, and the second would pack to a version that already exists.
    ///
    /// Each id is looked up exactly. Searching by name instead returns everyone else's packages that
    /// merely start the same way, such as the adwitkow.Bannerlord.ReferenceAssemblies mirror, whose tags
    /// carry the very same build ids. The meta package and Core are both read because the meta package is
    /// missing for a couple of older versions that Core has.
    ///
    /// Only packages tagged with this app count. The game and the dedicated server can report the same
    /// version, and the tag is what keeps one from marking the other's builds as published.
    ///
    /// The GUI packages are read apart, from the base GUI package: they are published on a schedule of
    /// their own, so one kind being on the feed says nothing about the other.
    /// </summary>
    public async Task<(IReadOnlySet<uint> BuildIds, IReadOnlySet<string> Versions)> GetPublishedAsync(App app, PackageKind kind, CancellationToken ct)
    {
        var repository = Repository.Factory.GetCoreV3(url);
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(ct)
                               ?? throw new InvalidOperationException($"{url} offers no package metadata resource.");
        using var cache = new SourceCacheContext();

        var modules = kind == PackageKind.Gui ? new string?[] { GuiPackager.BaseModule } : new string?[] { null, "Core" };
        var packageIds =
            from suffix in new[] { "", ".EarlyAccess" }
            from module in modules
            select app.PackageId(module, suffix);

        var buildIds = new HashSet<uint>();
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in packageIds)
        {
            foreach (var metadata in await metadataResource.GetMetadataAsync(packageId, true, true, cache, NullLogger.Instance, ct))
            {
                if (metadata.Identity is not { } identity || !string.Equals(identity.Id, packageId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // An untagged package cannot be attributed, and counting it could skip a build that was
                // never published. Leaving it out only risks building something twice.
                var tags = metadata.Tags ?? "";
                if (RxAppIdTag.Match(tags) is not { Success: true } appTag || uint.Parse(appTag.Groups[1].Value) != app.AppId)
                    continue;

                versions.Add(identity.Version.ToNormalizedString());
                if (RxBuildIdTag.Match(tags) is { Success: true } buildTag)
                    buildIds.Add(uint.Parse(buildTag.Groups[1].Value));
            }
        }
        return (buildIds, versions);
    }

    /// <summary>Every version the feed has of a package id, listed or not, normalized; empty when it has none.</summary>
    public async Task<IReadOnlySet<string>> GetVersionsAsync(string packageId, CancellationToken ct)
    {
        var find = await FindResourceAsync(ct);
        using var cache = new SourceCacheContext();
        var versions = await find.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, ct);
        return versions.Select(x => x.ToNormalizedString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Downloads the .nupkg of one version of a package id to the path.</summary>
    public async Task DownloadAsync(string packageId, string version, string path, CancellationToken ct)
    {
        var find = await FindResourceAsync(ct);
        using var cache = new SourceCacheContext();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        await using (var stream = File.Create(partial))
        {
            if (!await find.CopyNupkgToStreamAsync(packageId, NuGetVersion.Parse(version), stream, cache, NullLogger.Instance, ct))
                throw new InvalidOperationException($"{url} has no {packageId} {version}.");
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>The tags of the newest stable, listed version of a package id, or null when it has none.</summary>
    public async Task<(string Version, string Tags)?> GetNewestAsync(string packageId, CancellationToken ct)
    {
        var repository = Repository.Factory.GetCoreV3(url);
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(ct)
                               ?? throw new InvalidOperationException($"{url} offers no package metadata resource.");
        using var cache = new SourceCacheContext();
        var newest = (await metadataResource.GetMetadataAsync(packageId, false, false, cache, NullLogger.Instance, ct))
            .Where(x => x.Identity is { } identity && string.Equals(identity.Id, packageId, StringComparison.OrdinalIgnoreCase))
            .MaxBy(x => x.Identity.Version);
        return newest is null ? null : (newest.Identity.Version.ToNormalizedString(), newest.Tags ?? "");
    }

    private async Task<FindPackageByIdResource> FindResourceAsync(CancellationToken ct) =>
        await Repository.Factory.GetCoreV3(url).GetResourceAsync<FindPackageByIdResource>(ct)
        ?? throw new InvalidOperationException($"{url} offers no package download resource.");
}
