using DepotDownloader;
using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;
using SteamKit2;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static Bannerlord.ReferenceAssemblies.ProcessHelpers;

namespace Bannerlord.ReferenceAssemblies
{

    /// <summary>
    /// 1. Check NuGet feed to get all generated versions
    /// 2. Check Steam for game branches
    /// 3. Compare NuGet list with the current branches
    /// 4. Download all new versions from Steam with out file filter
    /// 5. Generate reference assemblies from th downloaded files
    /// 6. Generate nuspec and csproj files
    /// 7. Use dotnet pack to generate the nupgk
    /// 8. Upload packages to feed
    ///
    /// PCLExt was used because it's good for prototyping IMO
    /// Ideally, should be replaced by scripts in the future
    /// </summary>
    public static partial class Program
    {

        private static readonly string PackageName = "Bannerlord.ReferenceAssemblies";

        private static readonly uint steamAppId = 261550;

        private static readonly string os = "windows";

        private static readonly string osArch = "64";

        private static readonly uint steamDepotId = 261551;

        private static string _login;

        private static string _pass;

        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

        private static ButrNugetContext _butrNuget;

        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static readonly string OrgName = "bannerlord-unofficial-tools-resources";

        public static void Main(string[] args)
        {
            _login = args[1];
            _pass = args[3];
            _butrNuget = new ButrNugetContext(args[5]);

            Console.WriteLine("Checking NuGet...");
            var packages = _butrNuget.GetVersions(OrgName);

            foreach (var pkg in packages)
                Console.WriteLine($"{pkg.Key}: [{string.Join(", ", pkg.Value)}]");

            Console.WriteLine("Checking branches...");
            var branches = GetAllBranches();

            Console.WriteLine("Getting new versions...");
            var coreNugetVersions
                = packages.TryGetValue("Bannerlord.ReferenceAssemblies.Core", out var v)
                    ? v
                    : Array.Empty<string>();

            var toDownload
                = branches.Where(branch => !string.IsNullOrEmpty(branch.Version)
                    && !coreNugetVersions.Contains(branch.Version)).ToList();

            if (toDownload.Count == 0)
            {
                Console.WriteLine("No new version detected! Exiting...");
                ContentDownloader.ShutdownSteam3();
                return;
            }

            Console.WriteLine("New versions:");
            foreach (var br in toDownload)
                Console.WriteLine($"{br.Name}: {br.Version} ({br.BuildId})");

            Console.WriteLine("Checking downloading...");
            DownloadBranches(toDownload);

            Console.WriteLine("Generating references...");
            GenerateReferences(toDownload);

            Console.WriteLine("Generating packages...");
            GeneratePackages(toDownload);

            Console.WriteLine("Publishing...");
            _butrNuget.Publish();
        }

        private static void DownloadBranches(IEnumerable<Branch> toDownload)
        {
            foreach (var branch in toDownload)
                DownloadBranch(branch);
            ContentDownloader.ShutdownSteam3();
        }

        private static string GetAssembliesVersion(string path)
        {
            if (Run("dotnet", $"getblver \"{path}\"", out var versionStr) != 0)
                throw new NotImplementedException("Resolving assemblies version failed.");

            return versionStr;
        }

        private static void GenerateReferences(IEnumerable<Branch> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var rootFolder = ExecutableFolder
                    .GetFolder("depots")
                    .GetFolder(steamDepotId.ToString())
                    .GetFolder(branch.BuildId.ToString());

                GenerateReference(branch, "", rootFolder);
                foreach (var module in rootFolder.GetFolder("Modules").GetFolders())
                    GenerateReference(branch, module.Name, module);
            }
        }

        private static Branch ConvertVersion(string version, string buildId)
        {
            var letter = version[0];
            if (char.IsDigit(version[1]))
                return new Branch()
                {
                    Name = version,
                    Version = $"{version[1..]}.{buildId}-{letter}",
                    BuildId = uint.TryParse(buildId, out var r) ? r : 0
                };
            else
                return new Branch()
                {
                    Name = version,
                    Version = "",
                    BuildId = uint.TryParse(buildId, out var r) ? r : 0
                };
        }

        private static IEnumerable<Branch> GetAllBranches()
        {
            AccountSettingsStore.LoadFromFile("account.config");
            DepotDownloader.Program.InitializeSteam(_login, _pass);
            ContentDownloader.steam3.RequestAppInfo(steamAppId);
            var depots = ContentDownloader.GetSteam3AppSection(steamAppId, EAppInfoSection.Depots);
            var branches = depots["branches"];
            return branches.Children.Select(c => ConvertVersion(c.Name, c["buildid"].Value));
        }

        private static void DownloadBranch(Branch branch)
        {
            var folder = ExecutableFolder
                .CreateFolder("depots", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists);

            ContentDownloader.Config.MaxDownloads = 4;
            ContentDownloader.Config.InstallDirectory = folder.Path;

            try
            {
                var fileListData = Resourcer.Resource.AsString("FileFilters.regexp");
                var fileRxs = fileListData.Split(new[] {'\n', '\r'}, StringSplitOptions.RemoveEmptyEntries);

                ContentDownloader.Config.UsingFileList = true;
                ContentDownloader.Config.FilesToDownload = new List<string>();
                ContentDownloader.Config.FilesToDownloadRegex = new List<Regex>();

                foreach (var fileRx in fileRxs)
                {
                    // require all expressions to be valid and with proper slashes
                    var rx = new Regex(fileRx, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    ContentDownloader.Config.FilesToDownloadRegex.Add(rx);
                }

                Console.WriteLine("Using file filters:");
                using var tw = new IndentedTextWriter(Console.Out);
                ++tw.Indent;
                foreach (var file in fileRxs)
                    tw.WriteLine(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Unable to load file filters: {ex}");
            }

            ContentDownloader.DownloadAppAsync(steamAppId, steamDepotId, ContentDownloader.INVALID_MANIFEST_ID, branch.Name, os,
                osArch, null, false, true).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static void GenerateReference(Branch branch, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);

            var outputFolder = ExecutableFolder
                .CreateFolder("ref", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .GetModuleFolder(moduleName, isCore)
                .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
            {
                if (Run("dotnet", $"refgen -f -o \"{Path.Combine(outputFolder.Path, file.Name)}\" \"{file.Path}\"") != 0)
                    throw new NotImplementedException("Generating reference assemblies failed.");
            }
        }

    }

}