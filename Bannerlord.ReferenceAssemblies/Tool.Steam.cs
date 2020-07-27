using DepotDownloader;

using PCLExt.FileStorage;

using SteamKit2;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    internal partial class Tool
    {
        private SteamAppBranch ConvertVersion(string version, string buildId) => new SteamAppBranch
        {
            Name = version,
            AppId = _options.SteamAppId,
            DepotId = _options.SteamDepotId,
            BuildId = uint.TryParse(buildId, out var r) ? r : 0
        };

        private IEnumerable<SteamAppBranch> GetAllBranches()
        {
            AccountSettingsStore.LoadFromFile("account.config");
            DepotDownloader.Program.InitializeSteam(_options.SteamLogin, _options.SteamPassword);
            ContentDownloader.steam3.RequestAppInfo(_options.SteamAppId);
            var depots = ContentDownloader.GetSteam3AppSection(_options.SteamAppId, EAppInfoSection.Depots);
            var branches = depots["branches"];
            return branches.Children.Select(c => ConvertVersion(c.Name!, c["buildid"].Value!));
        }

        private async Task DownloadBranchesAsync(IEnumerable<SteamAppBranch> toDownload, CancellationToken ct)
        {
            foreach (var branch in toDownload)
                await DownloadBranchAsync(branch, ct);
            ContentDownloader.ShutdownSteam3();
        }
        private async Task DownloadBranchAsync(SteamAppBranch steamAppBranch, CancellationToken ct)
        {
            var folder = await (await (await ExecutableFolder
                        .CreateFolderAsync("depots", CreationCollisionOption.OpenIfExists, ct))
                    .CreateFolderAsync(_options.SteamDepotId.ToString(), CreationCollisionOption.OpenIfExists, ct))
                .CreateFolderAsync(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists, ct);

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

                Trace.WriteLine("Using file filters:");
                
                ++Trace.IndentLevel;
                foreach (var file in fileRxs)
                    Trace.WriteLine(file);
                --Trace.IndentLevel;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Warning: Unable to load file filters: {ex}");
            }

            await ContentDownloader.DownloadAppAsync(_options.SteamAppId, _options.SteamDepotId, ContentDownloader.INVALID_MANIFEST_ID,
                steamAppBranch.Name, _options.SteamOS, _options.SteamOSArch, null, false, true, ct);
        }
    }
}