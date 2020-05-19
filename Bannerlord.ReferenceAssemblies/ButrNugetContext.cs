using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;

namespace Bannerlord.ReferenceAssemblies
{

    internal class ButrNugetContext
    {
        private static readonly int MaxConcurrentOperations = 5;

        private static Regex RxPackageName = new Regex($"{Program.PackageName}*", RegexOptions.Compiled);

        private static ISettings NugetConfig = Settings.LoadDefaultSettings(Environment.CurrentDirectory);

        private static readonly PackageSourceProvider NugetPackageSourceProvider = new PackageSourceProvider(NugetConfig);

        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

        private readonly string _githubToken;

        public ButrNugetContext(string githubToken)
            => _githubToken = githubToken;

        public void Publish()
            => ExecutableFolder.GetFolder("final").GetFiles("*.nupkg")
                .AsParallel()
                .WithDegreeOfParallelism(8)
                .Select(file => Process.Start("dotnet", $"gpr push {file.Path} -k {_githubToken}"))
                .ForAll(proc => proc.WaitForExit());

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<ButrNuGetPackage>>> GetVersionsAsync(string userOrOrg, CancellationToken ct)
        {
            var sources = NugetPackageSourceProvider.LoadPackageSources().ToList();
            var packageSource = sources.FirstOrDefault(x => x.Name == "Source");

            if (packageSource == null)
            {
                // must be local
                var butrSource = sources.First(x => x.Name == "BUTR");
                packageSource = new PackageSource(butrSource.Source, butrSource.Name, butrSource.IsEnabled, butrSource.IsOfficial, butrSource.IsPersistable)
                {
                    Credentials = new PackageSourceCredential(butrSource.Source, userOrOrg, _githubToken, true, "basic"),
                    ProtocolVersion = 3,
                    MaxHttpRequestsPerSource = 8
                };
                NugetPackageSourceProvider.UpdatePackageSource(packageSource, true, true);
            }

            var sourceRepository = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());
            var packageLister = sourceRepository.GetResource<PackageSearchResource>();
            var packages = (await packageLister.SearchAsync("bannerlord",
                new SearchFilter(true) {SupportedFrameworks = new[] {"net472"}}, 0, 100, NullLogger.Instance, ct))
                .Where(p => RxPackageName.IsMatch(p.Identity.Id));

            var sourceCacheContext = new SourceCacheContext();
            var finderPackageByIdResource = sourceRepository.GetResource<FindPackageByIdResource>();
            var metadataResource = sourceRepository.GetResource<PackageMetadataResource>();

            return await packages.ToAsyncEnumerable().SelectParallel(MaxConcurrentOperations, async package =>
            {
                if (!package.Identity.Id.StartsWith(Program.PackageName))
                    return default;

                var versions = GetVersionsAsync(finderPackageByIdResource.GetAllVersionsAsync(package.Identity.Id, sourceCacheContext, NullLogger.Instance, ct));
                var metadatas = GetMetadataAsync(versions, version => metadataResource.GetMetadataAsync(new PackageIdentity(package.Identity.Id, version), sourceCacheContext, NullLogger.Instance, ct), ct);
                return new KeyValuePair<string, IReadOnlyList<ButrNuGetPackage>>(package.Identity.Id, await GetPackageVersionsAsync(metadatas, ct).ToListAsync(ct));
            }, ct).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        }

        private static async IAsyncEnumerable<NuGetVersion> GetVersionsAsync(Task<IEnumerable<NuGetVersion>> versions)
        {
            foreach (var version in await versions)
            {
                yield return version;
            }
        }
        private static IAsyncEnumerable<IPackageSearchMetadata> GetMetadataAsync(IAsyncEnumerable<NuGetVersion> versions, Func<NuGetVersion, Task<IPackageSearchMetadata>> getMeta, CancellationToken cancellationToken = default)
            => versions.SelectParallel(MaxConcurrentOperations, getMeta, cancellationToken);

        private static async IAsyncEnumerable<ButrNuGetPackage> GetPackageVersionsAsync(IAsyncEnumerable<IPackageSearchMetadata> metadatas, [EnumeratorCancellation] CancellationToken cancellation = default)
        {
            await foreach (var metadata in metadatas.WithCancellation(cancellation))
            {
                var package = ButrNuGetPackage.Get(metadata.Identity.Id, metadata.Identity.Version, metadata.Tags);
                if (package == null)
                    continue;

                yield return package.Value;
            }
        }

    }

    internal static class IAsyncEnumerableExtensions
    {

        public static async IAsyncEnumerable<TResult> SelectParallel<TResult, TSource>(this IAsyncEnumerable<TSource> enumerable, int maxConcurrent, Func<TSource, Task<TResult>> func, [EnumeratorCancellation] CancellationToken cancellation = default)
        {
            var semaphore = new SemaphoreSlim(maxConcurrent);
            var returnVal = new List<TResult>();
            var tasks = await enumerable.Select(@enum => Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync(cancellation).ConfigureAwait(false);
                    returnVal.Add(await func(@enum));
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellation))
                .ToListAsync(cancellationToken: cancellation);
            await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var val in returnVal)
                yield return val;
        }

        public static IEnumerable<TResult> SelectParallel<TResult, TSource>(this IEnumerable<TSource> enumerable, int maxConcurrent, Func<TSource, TResult> func)
        {
            var semaphore = new SemaphoreSlim(maxConcurrent);
            var returnVal = new List<TResult>();
            var tasks = enumerable.Select(@enum => Task.Run(() =>
            {
                try
                {
                    semaphore.Wait();
                    returnVal.Add(func(@enum));
                }
                finally
                {
                    semaphore.Release();
                }
            }))
                .ToList();
            Task.WhenAll(tasks).ConfigureAwait(false).GetAwaiter().GetResult();

            foreach (var val in returnVal)
                yield return val;
        }

    }
}