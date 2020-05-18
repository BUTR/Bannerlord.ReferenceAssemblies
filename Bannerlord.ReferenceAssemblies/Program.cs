using DepotDownloader;
using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;
using SteamKit2;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

        internal static readonly string PackageName = "Bannerlord.ReferenceAssemblies";

        private static readonly uint steamAppId = 261550;

        private static readonly string steamOS = "windows";

        private static readonly string steamOSArch = "64";

        private static readonly uint steamDepotId = 261551;

        private static string _login;

        private static string _pass;

        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

        private static ButrNugetContext _butrNuget;

        private static readonly string OrgName = "Bannerlord-Unofficial-Tools-Resources";

        public static async Task<int> Main(string[] args)
        {
            var ctrlC = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                if (eventArgs.SpecialKey != ConsoleSpecialKey.ControlC)
                    return;

                ctrlC.Cancel();
                eventArgs.Cancel = true;
            };

            var ct = ctrlC.Token;
            
            _login = args[1];
            _pass = args[3];
            _butrNuget = new ButrNugetContext(args[5]);

            Console.WriteLine("Checking NuGet...");
            var packages = await _butrNuget.GetVersionsAsync(OrgName, ct);

            foreach (var (key, value) in packages)
                Console.WriteLine($"{key}: [{string.Join(", ", value)}]");

            Console.WriteLine("Checking branches...");
            var branches = GetAllBranches().ToList();

            Console.WriteLine("Getting new versions...");
            var prefixes = new HashSet<BranchType>(branches.Select(branch => branch.Prefix));
            prefixes.Remove(BranchType.Unknown);

            var coreVersions
                = prefixes.ToDictionary(branchType => branchType, branchType
                    => packages.TryGetValue(GetPackageName("Core", branchType), out var pkg)
                        ? pkg.Select(x => x.BuildId)
                        : Array.Empty<uint>());

            var publicBranch = branches.First(branch => branch.Name == "public");
            var otherBranches = branches.Where(branch => branch.Prefix != BranchType.Unknown);
            var matchedPublicBranch = otherBranches.FirstOrDefault(branch => branch.BuildId == publicBranch.BuildId);
            if (matchedPublicBranch.BuildId == 0)
            {
                // ReSharper disable once MethodHasAsyncOverload
                Console.WriteLine("Public Branch does not match any branch!");
                throw new NotImplementedException();
            }

            var toDownload
                = branches.Where(branch => branch.Prefix != BranchType.Unknown && !coreVersions[branch.Prefix].Contains(branch.BuildId)).ToList();

            if (toDownload.Count == 0)
            {
                Console.WriteLine("No new version detected! Exiting...");
                ContentDownloader.ShutdownSteam3();
                return 0;
            }

            toDownload = toDownload.Take(1).ToList();

            Console.WriteLine("New versions:");
            foreach (var br in toDownload)
                Console.WriteLine($"{br.Name} {br.Version}: ({br.BuildId})");

            Console.WriteLine("Checking downloading...");
            await DownloadBranchesAsync(toDownload, ct);

            Console.WriteLine("Generating references...");
            GenerateReferences(toDownload);

            Console.WriteLine("Generating packages...");
            GeneratePackages(toDownload);

            Console.WriteLine("Publishing...");
            _butrNuget.Publish();

            return 0;
        }

        private static async Task DownloadBranchesAsync(IEnumerable<SteamAppBranch> toDownload, CancellationToken ct)
        {
            await Task.WhenAll( toDownload.Select(branch => DownloadBranchAsync(branch, ct) ) ).ConfigureAwait(false);
            ContentDownloader.ShutdownSteam3();
        }

        private static string GetAssembliesVersion(string path)
        {
            if (Run("dotnet", $"getblver \"{path}\"", out var versionStr) != 0)
                throw new NotImplementedException("Resolving assemblies version failed.");

            return versionStr;
        }

        private static void GenerateReferences(IEnumerable<SteamAppBranch> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var depotsFolder = ExecutableFolder
                    .GetFolder("depots")
                    .GetFolder(steamDepotId.ToString())
                    .GetFolder(branch.BuildId.ToString());

                var refFolder = ExecutableFolder
                    .CreateFolder("ref", CreationCollisionOption.OpenIfExists)
                    .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                    .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists);

                // core
                var coreRefFolder = refFolder
                    .GetModuleFolder("")
                    .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                    .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);
                GenerateReference(branch, "", depotsFolder, coreRefFolder);

                // official modules
                foreach (var module in depotsFolder.GetFolder("Modules").GetFolders())
                {
                    var name = module.Name;
                    var moduleOutputFolder = refFolder
                        .GetModuleFolder(name, false)
                        .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                        .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);
                    GenerateReference(branch, name, module, moduleOutputFolder);
                }
            }
        }

        private static SteamAppBranch ConvertVersion(string version, string buildId)
            => new SteamAppBranch()
            {
                Name = version,
                Version = char.IsDigit(version[1]) ? $"{version[1..]}.{buildId}-{version[0]}" : "",
                AppId = steamAppId,
                DepotId = steamDepotId,
                BuildId = uint.TryParse(buildId, out var r) ? r : 0
            };

        private static IEnumerable<SteamAppBranch> GetAllBranches()
        {
            AccountSettingsStore.LoadFromFile("account.config");
            DepotDownloader.Program.InitializeSteam(_login, _pass);
            ContentDownloader.steam3.RequestAppInfo(steamAppId);
            var depots = ContentDownloader.GetSteam3AppSection(steamAppId, EAppInfoSection.Depots);
            var branches = depots["branches"];
            return branches.Children.Select(c => ConvertVersion(c.Name, c["buildid"].Value));
        }

        private static async Task DownloadBranchAsync(SteamAppBranch steamAppBranch, CancellationToken ct)
        {
            var folder = ExecutableFolder
                .CreateFolder("depots", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists);

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
                await using var tw = new IndentedTextWriter(Console.Out);
                ++tw.Indent;
                foreach (var file in fileRxs)
                    tw.WriteLine(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Unable to load file filters: {ex}");
            }

            await ContentDownloader.DownloadAppAsync(steamAppId, steamDepotId, ContentDownloader.INVALID_MANIFEST_ID, steamAppBranch.Name,
                steamOS, steamOSArch, null, false, true, ct);
        }

        private static void GenerateReference(SteamAppBranch steamAppBranch, string moduleName, IFolder rootFolder, IFolder outputFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
            {
                if (Run("dotnet", $"refgen -f -o \"{Path.Combine(outputFolder.Path, file.Name)}\" \"{file.Path}\"") != 0)
                    throw new NotImplementedException("Generating reference assemblies failed.");
            }
        }

    }

}