using Bannerlord.ReferenceAssemblies.Options;

using NuGet.Packaging;

using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    internal partial class Tool
    {
        private static readonly Dictionary<string, string> SupportMatrix = new()
        {
            {"Bannerlord.ReferenceAssemblies.BirthAndDeath.EarlyAccess", "e1.8.0"}
        };

        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

        private readonly GenerateOptions _options;

        public Tool(GenerateOptions options)
        {
            DepotDownloaderExt.Init();

            _options = options;
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            var nugetFeed = new NuGetFeed(_options.FeedUrl, _options.FeedUser, _options.FeedPassword, _options.PackageBaseName);

            Trace.WriteLine("Checking NuGet...");
            var packages = await nugetFeed.GetVersionsAsync(ct);

            foreach (var (key, value) in packages)
                Trace.WriteLine($"{key}: [{string.Join(", ", value)}]");

            Trace.WriteLine("Checking branches...");
            var branches = GetAllBranches().Where(x => !x.Name.Contains("perf_test")).ToList();

            var packageNameWithBuildIds = new Dictionary<string, IEnumerable<uint>>();
            foreach (var (packageId, package) in packages)
                packageNameWithBuildIds[packageId] = package.Select(x => x.BuildId).ToArray();

            var toDownload = new HashSet<SteamAppBranch>();
            foreach (var (packageId, buildIds) in packageNameWithBuildIds)
            {
                toDownload.AddRange(branches.Where(branch =>
                    (!SupportMatrix.TryGetValue(packageId, out var val) || new AlphanumComparatorFast().Compare(val, branch.Name) <= 0) &&
                    !buildIds.Contains(branch.BuildId)));
            }

            if (toDownload.Count == 0)
            {
                Trace.WriteLine("No new version detected! Exiting...");
                DepotDownloaderExt.ContentDownloaderShutdownSteam3();
                return;
            }

            Trace.WriteLine("New versions:");
            foreach (var br in toDownload)
                Trace.WriteLine($"{br.Name}: ({br.AppId} {br.BuildId})");

            Trace.WriteLine("Checking downloading...");
            await DownloadBranchesAsync(toDownload, ct);

            var toDownloadWithVersion = toDownload.Select(branch =>
            {
                var depotsBinFolder = ExecutableFolder
                    .GetFolder("depots")
                    .GetFolder(branch.BuildId.ToString());

                var version = GetAssembliesVersion(depotsBinFolder.Path);
                var changeSet = GetAssembliesAppVersion(depotsBinFolder.Path);
                if (version == null)
                {
                    Trace.WriteLine($"Branch {branch.Name} ({branch.AppId} {branch.BuildId}) does not include a readable Version, skipping...");
                    return null;
                }
                if (changeSet == null)
                {
                    Trace.WriteLine($"Branch {branch.Name} ({branch.AppId} {branch.BuildId}) does not include a readable ChangeSet, skipping...");
                    return null;
                }
                return new SteamAppBranchWithVersion(version, changeSet, branch);
            }).OfType<SteamAppBranchWithVersion>().ToArray();

            Trace.WriteLine("Generating references...");
            GenerateReferences(toDownloadWithVersion);

            Trace.WriteLine("Generating packages...");

            GeneratePackages(toDownloadWithVersion);

            Trace.WriteLine("Done!");
        }


        private static string? GetAssembliesAppVersion(string path) =>
            ProcessHelpers.Run("getblmeta", $"getchangeset -f {path}", out var versionStr) != 0
                ? null
                : versionStr.Replace("\r", "").Replace("\n", "");

       private static string? GetAssembliesVersion(string path) =>
            ProcessHelpers.Run("getblmeta", $"getversion -f {path}", out var versionStr) != 0
                ? null
                : versionStr.Replace("\r", "").Replace("\n", "");

        private void GenerateReferences(IEnumerable<SteamAppBranchWithVersion> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var depotsFolder = ExecutableFolder
                    .GetFolder("depots")
                    .GetFolder(branch.BuildId.ToString());

                var refFolder = ExecutableFolder
                    .CreateFolder("ref", CreationCollisionOption.OpenIfExists)
                    .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists);

                // core
                var coreRefFolder = refFolder
                    .GetModuleFolder("")
                    .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                    .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);
                GenerateReference("", depotsFolder, coreRefFolder);

                // official modules
                foreach (var module in depotsFolder.GetFolder("Modules").GetFolders())
                {
                    var name = module.Name;
                    var moduleOutputFolder = refFolder
                        .GetModuleFolder(name, false)
                        .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                        .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);
                    GenerateReference(name, module, moduleOutputFolder);
                }
            }
        }
        private static void GenerateReference(string moduleName, IFolder rootFolder, IFolder outputFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
            {
                var outputFile = Path.Combine(outputFolder.Path, file.Name);
                ProcessHelpers.Run("refasmer", $"{file.Path} -o {outputFile} -c");
            }
        }
    }
}