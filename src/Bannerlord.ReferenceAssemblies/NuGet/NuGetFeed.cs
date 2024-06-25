using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies;

internal class NuGetFeed
{
    private readonly SourceRepository _sourceRepository;

    public NuGetFeed(string feedUrl, string? feedUser, string? feedPassword)
    {
        var packageSource = new PackageSource(feedUrl, "Feed1", true, false, false)
        {
            Credentials = new PackageSourceCredential(feedUrl, feedUser ?? "", feedPassword ?? "", true, string.Empty),
            MaxHttpRequestsPerSource = 8,
        };

        _sourceRepository = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<NuGetPackage>>> GetVersionsAsync(CancellationToken ct)
    {
        var packageLister = await _sourceRepository.GetResourceAsync<PackageSearchResource>(ct);
        var foundPackages = (await packageLister.SearchAsync("Bannerlord.ReferenceAssemblies", new SearchFilter(true), 0, 50, NullLogger.Instance, ct))
            .Where(x => !x.Identity.Id.Contains("EarlyAccess", StringComparison.OrdinalIgnoreCase));

        var sourceCacheContext = new SourceCacheContext();
        var finderPackageByIdResource = await _sourceRepository.GetResourceAsync<FindPackageByIdResource>(ct);
        var metadataResource = await _sourceRepository.GetResourceAsync<PackageMetadataResource>(ct);

        return await foundPackages.ToAsyncEnumerable().SelectAwait(async package =>
        {
            var versions = MaxVersions(finderPackageByIdResource.GetAllVersionsAsync(package.Identity.Id, sourceCacheContext, NullLogger.Instance, ct));
            var metadatas = GetMetadataAsync(versions, version => metadataResource.GetMetadataAsync(new PackageIdentity(package.Identity.Id, version), sourceCacheContext, NullLogger.Instance, ct), ct);
            return (package.Identity.Id, (IReadOnlyList<NuGetPackage>) await GetPackageVersionsAsync(metadatas, ct).ToListAsync(ct));
        }).ToDictionaryAsync(x => x.Item1, x => x.Item2, ct);
    }

    private static async IAsyncEnumerable<NuGetVersion> MaxVersions(Task<IEnumerable<NuGetVersion>> source)
    {
        var data = (await source).ToList();
        var dict = new Dictionary<string, NuGetVersion>();
        var dictBeta = new Dictionary<string, NuGetVersion>();
        foreach (var version in data.Where(x => !x.IsPrerelease))
        {
            var v = version.Version.ToString(3);
            var currentMax = dict.GetValueOrDefault(v);
            if (currentMax is null) dict[v] = version;
            // Release reset their build index. For now everything that is higher than 200000 is considered EA
            // TODO: better fix?
            else if (version.Version.Build < 100000 && currentMax.Version < version.Version) dict[v] = version;
            else if (currentMax.Version < version.Version) dict[v] = version;
        }
        foreach (var version in data.Where(x => x.IsPrerelease))
        {
            var v = version.Version.ToString(3);
            var currentMax = dictBeta.GetValueOrDefault(v);
            if (currentMax is null) dictBeta[v] = version;
            // Release reset their build index. For now everything that is higher than 200000 is considered EA
            // TODO: better fix?
            else if (version.Version.Build < 100000 && currentMax.Version < version.Version) dictBeta[v] = version;
            else if (currentMax.Version < version.Version) dictBeta[v] = version;
        }
        foreach (var value in dict.Values)
            yield return value;
        foreach (var value in dictBeta.Values)
            yield return value;
    }

    private static async IAsyncEnumerable<IPackageSearchMetadata> GetMetadataAsync(IAsyncEnumerable<NuGetVersion> versions, Func<NuGetVersion, Task<IPackageSearchMetadata>> getMeta, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var version in versions.WithCancellation(ct))
        {
            yield return await getMeta(version);
        }
    }

    private static async IAsyncEnumerable<NuGetPackage> GetPackageVersionsAsync(IAsyncEnumerable<IPackageSearchMetadata> metadatas, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var metadata in metadatas.WithCancellation(ct))
        {
            var package = NuGetPackage.Get(metadata.Identity.Id, metadata.Identity.Version, metadata.Tags);
            if (package != null)
                yield return package.Value;
        }
    }
}