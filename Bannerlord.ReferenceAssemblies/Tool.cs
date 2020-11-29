using Bannerlord.ReferenceAssemblies.Options;

using DepotDownloader;

using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SteamKit2;

namespace Bannerlord.ReferenceAssemblies
{
    internal partial class Tool
    {
        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

        private readonly GenerateOptions _options;

        public Tool(GenerateOptions options)
        {
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
            var branches = GetAllBranches().ToList();

            Trace.WriteLine("Getting new versions...");
            var prefixes = new HashSet<BranchType>(branches.Select(branch => branch.Prefix).Where(b => b != BranchType.Unknown));

            var coreVersions = prefixes.ToDictionary(branchType => branchType,
                branchType => packages.TryGetValue(GetPackageName("Core", branchType), out var pkg)
                    ? pkg.Select(x => x.BuildId)
                    : Array.Empty<uint>());

            var publicBranch = branches.First(branch => branch.Name == "public");
            var otherBranches = branches.Where(branch => branch.Prefix != BranchType.Unknown);
            var matchedPublicBranch = otherBranches.FirstOrDefault(branch => branch.BuildId == publicBranch.BuildId);
            if (matchedPublicBranch.BuildId == 0)
            {
                Trace.WriteLine("Public Branch does not match any version branch! Report to TaleWorlds!");
                return;
            }

            Trace.WriteLine($"Public Branch Matches: {matchedPublicBranch.Name}");
            var toDownload = branches.Where(branch =>
                branch.Prefix != BranchType.Unknown
                && !coreVersions[branch.Prefix].Contains(branch.BuildId)
                // TODO: Fix parsing meta from older versions
                && Version.TryParse(branch.Name.Remove(0, 1), out var v) && v >= new Version("1.1.0")
            ).ToList();

            if (toDownload.Count == 0)
            {
                Trace.WriteLine("No new version detected! Exiting...");
                DepotDownloaderExt.ContentDownloaderShutdownSteam3();
                return;
            }

            Trace.WriteLine("New versions:");
            foreach (var br in toDownload)
                Trace.WriteLine($"{br.Name}: ({br.AppId} {br.DepotId} {br.BuildId})");

            Trace.WriteLine("Checking downloading...");
            await DownloadBranchesAsync(toDownload, ct);

            Trace.WriteLine("Generating references...");
            GenerateReferences(toDownload);

            Trace.WriteLine("Generating packages...");

            GeneratePackages(toDownload);

            Trace.WriteLine("Publishing...");
            await nugetFeed.PublishAsync();
        }


        private static string? GetAssembliesVersion(string path) => ProcessHelpers.Run("dotnet", $"getblmeta getchangeset -f {path}", out var versionStr) != 0
            ? null
            : versionStr.Replace("\r", "").Replace("\n", "");

        private void GenerateReferences(IEnumerable<SteamAppBranch> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var depotsFolder = ExecutableFolder
                    .GetFolder("depots")
                    .GetFolder(_options.SteamDepotId.ToString())
                    .GetFolder(branch.BuildId.ToString());

                var refFolder = ExecutableFolder
                    .CreateFolder("ref", CreationCollisionOption.OpenIfExists)
                    .CreateFolder(_options.SteamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
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
                var args = $"-f|-o|{Path.Combine(outputFolder.Path, file.Name)}|{file.Path}".Split('|');
                ReferenceAssemblyGenerator.Program.Main(args);
            }
        }
    }
}