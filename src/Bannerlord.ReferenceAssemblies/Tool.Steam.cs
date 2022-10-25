using PCLExt.FileStorage;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    internal partial class Tool
    {
        private SteamAppBranch ConvertVersion(string name, string buildId) => new()
        {
            Name = name,
            AppId = _options.SteamAppId,
            BuildId = uint.TryParse(buildId, out var r) ? r : 0
        };

        private IEnumerable<SteamAppBranch> GetAllBranches()
        {
            DepotDownloaderExt.AccountSettingsStoreLoadFromFile("account.config");
            DepotDownloaderExt.DepotDownloaderProgramInitializeSteam(_options.SteamLogin, _options.SteamPassword);
            DepotDownloaderExt.ContentDownloadersteam3RequestAppInfo(_options.SteamAppId);
            var depots = DepotDownloaderExt.ContentDownloaderGetSteam3AppSection(_options.SteamAppId);
            var branches = depots["branches"];
            return branches.Children.Where(x => x["pwdrequired"].Value != "1" && x["lcsrequired"].Value != "1").Select(c => ConvertVersion(c.Name!, c["buildid"].Value!));
        }

        private async Task DownloadBranchesAsync(IEnumerable<SteamAppBranch> toDownload, CancellationToken ct)
        {
            foreach (var branch in toDownload)
                await DownloadBranchAsync(branch, ct);
            DepotDownloaderExt.ContentDownloaderShutdownSteam3();
        }
        private async Task DownloadBranchAsync(SteamAppBranch steamAppBranch, CancellationToken ct)
        {
            var folder = await (await ExecutableFolder
                        .CreateFolderAsync("depots", CreationCollisionOption.OpenIfExists, ct))
                .CreateFolderAsync(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists, ct);

            DepotDownloaderExt.ContentDownloaderConfigSetMaxDownloads(4);
            DepotDownloaderExt.ContentDownloaderConfigSetInstallDirectory(folder.Path);

            try
            {
                var fileListData = Resourcer.Resource.AsString("Resources/FileFilters.regexp");
                var files = fileListData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                DepotDownloaderExt.ContentDownloaderConfigSetUsingFileList(true);
                var filesToDownload = DepotDownloaderExt.ContentDownloaderConfigGetFilesToDownload();
                filesToDownload.Clear();
                var filesToDownloadRegex = DepotDownloaderExt.ContentDownloaderConfigGetFilesToDownloadRegex();
                filesToDownloadRegex.Clear();

                foreach (var fileEntry in files)
                {
                    if (fileEntry.StartsWith("regex:"))
                    {
                        var rgx = new Regex(fileEntry.Substring(6), RegexOptions.Compiled | RegexOptions.IgnoreCase);
                        filesToDownloadRegex.Add(rgx);
                    }
                    else
                    {
                        filesToDownload.Add(fileEntry);
                    }
                    // require all expressions to be valid and with proper slashes
                }

                Trace.WriteLine("Using file filters:");

                ++Trace.IndentLevel;
                foreach (var file in files)
                    Trace.WriteLine(file);
                --Trace.IndentLevel;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Warning: Unable to load file filters: {ex}");
            }

            await DepotDownloaderExt.ContentDownloaderDownloadAppAsync(
                _options.SteamAppId,
                _options.SteamDepotId.Select(x => (x, ulong.MaxValue)).ToList(),
                steamAppBranch.Name,
                _options.SteamOS,
                _options.SteamOSArch,
                null!,
                false,
                false).ConfigureAwait(false);
        }
    }
}