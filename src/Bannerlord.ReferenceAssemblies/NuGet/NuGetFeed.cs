using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

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
    /// </summary>
    public async Task<(IReadOnlySet<uint> BuildIds, IReadOnlySet<string> Versions)> GetPublishedAsync(App app, CancellationToken ct)
    {
        var repository = Repository.Factory.GetCoreV3(url);
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(ct)
                               ?? throw new InvalidOperationException($"{url} offers no package metadata resource.");
        using var cache = new SourceCacheContext();

        var packageIds =
            from suffix in new[] { "", ".EarlyAccess" }
            from module in new string?[] { null, "Core" }
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
}
