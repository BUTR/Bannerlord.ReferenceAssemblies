using Bannerlord.ModuleManager;

using PCLExt.FileStorage;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies;

internal partial class Tool
{
    private static FieldInfo Steam3Field { get; } = typeof(DepotDownloader.ContentDownloader).GetField("steam3", BindingFlags.Static | BindingFlags.NonPublic)!;

    private SteamAppBranch ConvertVersion(string name, string buildId, bool isBeta) => new()
    {
        Name = name,
        AppId = _options.SteamAppId,
        BuildId = uint.TryParse(buildId, out var r) ? r : 0,
        IsBeta = isBeta,
    };

    private async Task<IEnumerable<SteamAppBranch>> GetAllBranches()
    {
        DepotDownloader.AccountSettingsStore.LoadFromFile("account.config");
        DepotDownloader.ContentDownloader.InitializeSteam3(_options.SteamLogin, _options.SteamPassword);
        var steam3 = (DepotDownloader.Steam3Session) Steam3Field.GetValue(null)!;
        await steam3.RequestAppInfo(_options.SteamAppId, false);
        var depots = DepotDownloader.ContentDownloader.GetSteam3AppSection(_options.SteamAppId, SteamKit2.EAppInfoSection.Depots);
        var branches = depots["branches"];
        return branches.Children
            .Where(x => x["pwdrequired"].Value != "1" && x["lcsrequired"].Value != "1")
            .Where(x => (ApplicationVersion.TryParse(x.Name, out var version) && version.ApplicationVersionType == ApplicationVersionType.Release) || x.Name == "public")
            .Select(c => ConvertVersion(c.Name!, c["buildid"].Value!, c["description"].Value == "beta"));
    }

    private async Task DownloadBranchesAsync(IEnumerable<SteamAppBranch> toDownload, CancellationToken ct)
    {
        foreach (var branch in toDownload)
            await DownloadBranchAsync(branch, ct);
        DepotDownloader.ContentDownloader.ShutdownSteam3();
    }
    private async Task DownloadBranchAsync(SteamAppBranch steamAppBranch, CancellationToken ct)
    {
        var folder = await (await ExecutableFolder
                .CreateFolderAsync("depots", CreationCollisionOption.OpenIfExists, ct))
            .CreateFolderAsync(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists, ct);

        DepotDownloader.ContentDownloader.Config.MaxDownloads = 4;
        DepotDownloader.ContentDownloader.Config.InstallDirectory = folder.Path;

        try
        {
            var fileListData = Resourcer.Resource.AsString("Resources/FileFilters.regexp");
            var files = fileListData.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

            DepotDownloader.ContentDownloader.Config.UsingFileList = true;
            DepotDownloader.ContentDownloader.Config.FilesToDownload = [];
            DepotDownloader.ContentDownloader.Config.FilesToDownloadRegex = [];
            var filesToDownload = DepotDownloader.ContentDownloader.Config.FilesToDownload;
            filesToDownload.Clear();
            var filesToDownloadRegex = DepotDownloader.ContentDownloader.Config.FilesToDownloadRegex;
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

        await DepotDownloader.ContentDownloader.DownloadAppAsync(
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