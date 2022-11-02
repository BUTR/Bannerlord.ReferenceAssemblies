using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    internal class NuGetFeed
    {
        private readonly Regex RxPackageName;

        private readonly string _packageBaseName;

        private readonly SourceRepository _sourceRepository;

        public NuGetFeed(string feedUrl, string? feedUser, string? feedPassword, string packageBaseName)
        {
            _packageBaseName = packageBaseName;

            RxPackageName = new Regex($"{_packageBaseName}*", RegexOptions.Compiled);

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
            var foundPackages = await packageLister.SearchAsync(_packageBaseName, new SearchFilter(true), 0, 10, NullLogger.Instance, ct);

            var sourceCacheContext = new SourceCacheContext();
            var finderPackageByIdResource = await _sourceRepository.GetResourceAsync<FindPackageByIdResource>(ct);
            var metadataResource = await _sourceRepository.GetResourceAsync<PackageMetadataResource>(ct);

            return await foundPackages.Where(p => RxPackageName.IsMatch(p.Identity.Id)).ToAsyncEnumerable().SelectAwait(async package =>
            {
                if (!package.Identity.Id.StartsWith(_packageBaseName))
                    return default;

                var versions = MaxVersions(finderPackageByIdResource.GetAllVersionsAsync(package.Identity.Id, sourceCacheContext, NullLogger.Instance, ct));
                var metadatas = GetMetadataAsync(versions, version => metadataResource.GetMetadataAsync(new PackageIdentity(package.Identity.Id, version), sourceCacheContext, NullLogger.Instance, ct), ct);
                return (package.Identity.Id, (IReadOnlyList<NuGetPackage>) await GetPackageVersionsAsync(metadatas, ct).ToListAsync(ct));
            }).ToDictionaryAsync(x => x.Item1, x => x.Item2, ct);
        }

        private static async IAsyncEnumerable<NuGetVersion> MaxVersions(Task<IEnumerable<NuGetVersion>> source)
        {
            var dict = new Dictionary<string, NuGetVersion>();
            foreach (var version in await source)
            {
                var v = version.Version.ToString(3);
                var currentMax = dict.TryGetValue(v, out var c) ? c : null;
                if (currentMax is null) dict[v] = version;
                // Release reset their build index. For now everything that is higher than 200000 is considered EA
                // TODO: better fix?
                else if (version.Version.Build < 100000 && currentMax.Version < version.Version) dict[v] = version;
                else if (currentMax.Version < version.Version) dict[v] = version;
            }
            foreach (var value in dict.Values)
                yield return value;
        }

        private static async IAsyncEnumerable<IPackageSearchMetadata> GetMetadataAsync(IAsyncEnumerable<NuGetVersion> versions, Func<NuGetVersion, Task<IPackageSearchMetadata>> getMeta, CancellationToken cancellationToken = default)
        {
            await foreach (var version in versions.WithCancellation(cancellationToken))
            {
                yield return await getMeta(version);
            }
        }

        private static async IAsyncEnumerable<NuGetPackage> GetPackageVersionsAsync(IAsyncEnumerable<IPackageSearchMetadata> metadatas, [EnumeratorCancellation] CancellationToken cancellation = default)
        {
            await foreach (var metadata in metadatas.WithCancellation(cancellation))
            {
                var package = NuGetPackage.Get(metadata.Identity.Id, metadata.Identity.Version, metadata.Tags);
                if (package != null)
                    yield return package.Value;
            }
        }
    }
}