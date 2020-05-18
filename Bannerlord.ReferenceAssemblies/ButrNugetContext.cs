using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

        private readonly string _githubToken;

        public ButrNugetContext(string githubToken)
            => _githubToken = githubToken;

        public void Publish()
            => ExecutableFolder.GetFolder("final").GetFiles("*.nupkg")
                .AsParallel()
                .WithDegreeOfParallelism(8)
                .Select(file => Process.Start("gpr", $"push {file.Path} -k {_githubToken}"))
                .ForAll(proc => proc.WaitForExit());

        public IReadOnlyDictionary<string, IReadOnlyList<ButrNuGetPackage>> GetVersions(string userOrOrg)
        {
            var github = "https://nuget.pkg.github.com/Bannerlord-Unofficial-Tools-Resources/index.json";
            var packageSource = new PackageSource(github)
            {
                Credentials = new PackageSourceCredential(github, userOrOrg, _githubToken, true, "basic")
            };
            var sourceRepository = Repository.Factory.GetCoreV2(packageSource);
            var sourceCacheContext = new SourceCacheContext();
            var packageLister = sourceRepository.GetResource<PackageSearchResource>();
            var packageFinder = sourceRepository.GetResource<FindPackageByIdResource>();
            var allpackages = packageLister.SearchAsync("", new SearchFilter(true), 0, 20, NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult();

            foreach (var package in allpackages)
            {
                var versions1 = package.GetVersionsAsync().GetAwaiter().GetResult();
                var versions = packageFinder.GetAllVersionsAsync(package.Identity.Id, sourceCacheContext, NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult();
                ;
            }
 
            if (ProcessHelpers.Run("dotnet", $"gpr list {userOrOrg} -k {_githubToken}", out var output) != 0)
                Console.WriteLine();
            var lines = output.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);
            var returnVal = new Dictionary<string, IReadOnlyList<ButrNuGetPackage>>();
            /*
            foreach (var line in lines)
            {
                if (line.StartsWith("http") || line.StartsWith("[PRIVATE REPOSITORIES]"))
                    continue;

                var line1 = line.Trim().Split('(', ')');
                var versions = line1[2].Trim().TrimStart('[').TrimEnd(']').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                //returnVal.Add(line1[0].Trim(), versions.Select(v => v.Trim(',')).ToList());
            }
            */

            return returnVal;
        }

    }

}