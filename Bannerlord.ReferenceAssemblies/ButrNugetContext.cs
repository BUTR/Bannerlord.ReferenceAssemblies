using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;

namespace Bannerlord.ReferenceAssemblies
{

    internal class ButrNugetContext
    {

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
            var allPackages = await packageLister.SearchAsync("bannerlord",
                new SearchFilter(true) {SupportedFrameworks = new[] {"net472"}}, 0, 100, NullLogger.Instance, ct);

            var returnVal = new Dictionary<string, IReadOnlyList<ButrNuGetPackage>>();

            foreach (var package in allPackages)
            {
                if (!package.Identity.Id.StartsWith(Program.PackageName))
                    continue;

                var versions = new List<ButrNuGetPackage>();
                returnVal.Add(package.Identity.Id, versions);
                var packageVersions = await package.GetVersionsAsync();
                versions.AddRange(packageVersions
                    .Where(version => !string.IsNullOrEmpty(version.PackageSearchMetadata?.Tags))
                    .Select(version => new ButrNuGetPackage(package.Identity.Id, version)));
            }

            return returnVal;
        }

    }

}