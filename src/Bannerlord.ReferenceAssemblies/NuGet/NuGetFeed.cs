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
        private static readonly int MaxConcurrentOperations = 5;

        private static Regex RxPackageName;

        private readonly string _packageBaseName;

        private readonly SourceRepository _sourceRepository;

        public NuGetFeed(string feedUrl, string? feedUser, string? feedPassword, string packageBaseName)
        {
            _packageBaseName = packageBaseName;

            RxPackageName = new Regex($"{_packageBaseName}*", RegexOptions.Compiled);

            var packageSource = new PackageSource(feedUrl, "Feed1", true, false, false)
            {
                Credentials = new PackageSourceCredential(feedUrl, feedUser ?? "", feedPassword?? "", true, "basic"),
                ProtocolVersion = 3,
                MaxHttpRequestsPerSource = 8
            };

            _sourceRepository = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<NuGetPackage>>> GetVersionsAsync(CancellationToken ct)
        {
            var packageLister = _sourceRepository.GetResource<PackageSearchResource>(ct);
            var packages = (await packageLister.SearchAsync("ReferenceAssemblies", new SearchFilter(true), 0, 100, NullLogger.Instance, ct))
                .Where(p => RxPackageName.IsMatch(p.Identity.Id));

            var sourceCacheContext = new SourceCacheContext();
            var finderPackageByIdResource = _sourceRepository.GetResource<FindPackageByIdResource>(ct);
            var metadataResource = _sourceRepository.GetResource<PackageMetadataResource>(ct);

            return await packages.ToAsyncEnumerable().SelectParallel(MaxConcurrentOperations, async package =>
            {
                if (!package.Identity.Id.StartsWith(_packageBaseName))
                    return default;

                var versions = finderPackageByIdResource.GetAllVersionsAsync(package.Identity.Id, sourceCacheContext, NullLogger.Instance, ct).GetAwaiter().GetResult().ToList();
                var metadatas = GetMetadataAsync(versions.ToAsyncEnumerable(), version => metadataResource.GetMetadataAsync(new PackageIdentity(package.Identity.Id, version), sourceCacheContext, NullLogger.Instance, ct), ct);
                return (package.Identity.Id, (await GetPackageVersionsAsync(metadatas, ct).ToListAsync(ct)) as IReadOnlyList<NuGetPackage>);
            }, ct).ToDictionaryAsync(x => x.Item1, x => x.Item2, ct);
        }

        private static IAsyncEnumerable<IPackageSearchMetadata> GetMetadataAsync(IAsyncEnumerable<NuGetVersion> versions, Func<NuGetVersion, Task<IPackageSearchMetadata>> getMeta, CancellationToken cancellationToken = default) =>
            versions.SelectParallel(MaxConcurrentOperations, getMeta, cancellationToken);

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