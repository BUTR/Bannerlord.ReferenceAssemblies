using DepotDownloader;

using SteamKit2;

using System.Reflection;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>Steam would not hand over a build's content. Distinct from a network failure, which is worth retrying.</summary>
internal sealed class SteamContentException(string message) : Exception(message);

/// <summary>A branch as Steam reports it right now. Only current branches are visible; history lives in the registry.</summary>
internal sealed record SteamBranch(string Name, uint BuildId, DateTimeOffset TimeUpdated, string? Description, Dictionary<string, string> Manifests);

/// <summary>
/// The little of Steam this tool needs, on top of DepotDownloader: the current branches of the app, whether a
/// manifest is still the tip of a branch, and downloading a build by the manifest ids the registry recorded.
/// </summary>
internal sealed class SteamClient(App app, string login, string password) : IDisposable
{
    // DepotDownloader keeps its session in a private static; it is compiled into this assembly, so reflection is the only way in.
    private static readonly FieldInfo SessionField = typeof(ContentDownloader).GetField("steam3", BindingFlags.Static | BindingFlags.NonPublic)!;

    private bool _connected;

    private static Steam3Session Session => (Steam3Session) SessionField.GetValue(null)!;

    public async Task ConnectAsync()
    {
        if (_connected)
            return;

        AccountSettingsStore.LoadFromFile("account.config");
        if (!ContentDownloader.InitializeSteam3(login, password))
            throw new InvalidOperationException("Unable to log into Steam.");
        _connected = true;

        await Session.RequestAppInfo(app.AppId, true);
        foreach (var dlcAppId in app.DlcAppIds)
            await Session.RequestAppInfo(dlcAppId, true);
    }

    public void Dispose()
    {
        if (_connected)
            ContentDownloader.ShutdownSteam3();
        _connected = false;
    }

    /// <summary>
    /// Every branch of the app that is published right now, with the manifest of each tracked depot.
    /// Password and LCS protected branches are skipped: they cannot be downloaded.
    /// </summary>
    public IEnumerable<SteamBranch> GetBranches()
    {
        var depots = Depots(app.AppId) ?? throw new InvalidOperationException($"Steam returned no depot information for app {app.AppId}.");
        var dlcDepots = app.DlcAppIds.Select(Depots).OfType<KeyValue>().ToList();

        foreach (var branch in depots["branches"].Children)
        {
            if (branch.Name is not { } name || IsProtected(branch))
                continue;
            if (!uint.TryParse(branch["buildid"].Value, out var buildId))
                continue;

            var manifests = new Dictionary<string, string>();
            foreach (var depotId in app.DepotIds)
                if (Manifest(depots, depotId, name) is { } gid)
                    manifests[depotId.ToString()] = gid;
            foreach (var dlc in dlcDepots)
                foreach (var depot in dlc.Children.Where(x => uint.TryParse(x.Name, out _)))
                    if (Manifest(dlc, uint.Parse(depot.Name!), name) is { } gid)
                        manifests[depot.Name!] = gid;

            if (manifests.Count == 0)
                continue;

            yield return new SteamBranch(name, buildId, DateTimeOffset.FromUnixTimeSeconds(branch["timeupdated"].AsLong()), branch["description"].Value, manifests);
        }
    }

    /// <summary>
    /// Whether some branch still points at this build. Those are the perishable ones: a manifest that was
    /// never public stops being served once its branch moves on.
    /// </summary>
    public bool IsLive(BuildEntry build) =>
        PrimaryManifest(build) is { } manifestId && FindLiveBranch(OwningApp(app.PrimaryDepotId), [(app.PrimaryDepotId, manifestId)]) is not null;

    /// <summary>
    /// Downloads the files of a build that match the filters, by the manifest ids the registry recorded.
    /// The game's depots go to <paramref name="directory"/>, each DLC app's to a folder of its own, which is
    /// then copied over the game folder the way Steam installs a DLC. So the game folder holds the whole
    /// build, and the DLC folder still says which of its files the DLC brought.
    /// Throws <see cref="SteamContentException"/> when Steam refuses to serve a manifest.
    /// </summary>
    public async Task DownloadAsync(BuildEntry build, string directory, Func<uint, string> dlcDirectory, IReadOnlyList<Regex> files, bool primaryDepotOnly, CancellationToken ct)
    {
        await ConnectAsync();
        Directory.CreateDirectory(directory);

        var config = ContentDownloader.Config;
        config.MaxDownloads = 4;
        config.UsingFileList = true;
        config.FilesToDownload = [];
        config.FilesToDownloadRegex = files.ToList();

        // A DLC depot has to be requested through the DLC's own app id, so the depots go app by app.
        var byApp = build.Manifests
            .Select(x => (DepotId: uint.Parse(x.Key), ManifestId: ulong.Parse(x.Value)))
            .Where(x => !primaryDepotOnly || x.DepotId == app.PrimaryDepotId)
            .GroupBy(x => OwningApp(x.DepotId));

        var dlcFolders = new List<string>();
        foreach (var group in byApp)
        {
            var manifests = group.Select(x => (x.DepotId, x.ManifestId)).ToList();
            var folder = group.Key == app.AppId ? directory : dlcDirectory(group.Key);
            if (group.Key != app.AppId)
                dlcFolders.Add(folder);
            Directory.CreateDirectory(folder);
            config.InstallDirectory = folder;

            // A manifest must be requested under the branch that points at it now; a retired manifest is
            // served under public only if it was public once. Ask for the request code first so a refusal
            // is told apart from a download that merely failed.
            var branch = FindLiveBranch(group.Key, manifests) ?? ContentDownloader.DEFAULT_BRANCH;
            foreach (var (depotId, manifestId) in manifests)
                if (await Session.GetDepotManifestRequestCodeAsync(depotId, group.Key, manifestId, branch) == 0)
                    throw new SteamContentException($"Steam refused manifest {manifestId} of depot {depotId} under branch '{branch}'.");

            DepotConfigStore.Instance = null!;
            try
            {
                await ContentDownloader.DownloadAppAsync(group.Key, manifests, branch, "windows", "64", null!, false, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // DepotDownloader cancels its own token when a manifest cannot be fetched.
                throw new SteamContentException($"Steam did not serve the manifests of build {build.BuildId} for app {group.Key}.");
            }

            foreach (var (depotId, manifestId) in manifests)
                if (!DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depotId, out var installed) || installed != manifestId)
                    throw new IOException($"Depot {depotId}, manifest {manifestId} did not finish downloading.");
        }

        foreach (var folder in dlcFolders)
            InstallDlc(folder, directory);
    }

    /// <summary>
    /// Copies a DLC's downloaded files over the game folder, overwriting as Steam does, so that everything
    /// reading a build keeps reading one folder. DepotDownloader's own bookkeeping stays behind.
    /// </summary>
    internal static void InstallDlc(string dlcFolder, string gameFolder)
    {
        foreach (var file in Directory.EnumerateFiles(dlcFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dlcFolder, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] == ContentDownloaderConfigDir)
                continue;

            var target = Path.Combine(gameFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    /// <summary>The folder DepotDownloader keeps its state in, inside every install directory.</summary>
    internal const string ContentDownloaderConfigDir = ".DepotDownloader";

    private static KeyValue? Depots(uint appId) => ContentDownloader.GetSteam3AppSection(appId, EAppInfoSection.Depots);

    private static bool IsProtected(KeyValue branch) => branch["pwdrequired"].AsBoolean() || branch["lcsrequired"].AsBoolean();

    private static string? Manifest(KeyValue depots, uint depotId, string branch) => depots[depotId.ToString()]["manifests"][branch]["gid"].Value;

    private ulong? PrimaryManifest(BuildEntry build) =>
        build.Manifests.TryGetValue(app.PrimaryDepotId.ToString(), out var gid) && ulong.TryParse(gid, out var manifestId) ? manifestId : null;

    /// <summary>The branch that currently points at exactly these manifests, or null.</summary>
    private static string? FindLiveBranch(uint appId, IReadOnlyList<(uint DepotId, ulong ManifestId)> manifests)
    {
        if (Depots(appId) is not { } depots)
            return null;

        foreach (var branch in depots["branches"].Children)
        {
            if (branch.Name is not { } name || IsProtected(branch))
                continue;
            if (manifests.All(x => Manifest(depots, x.DepotId, name) == x.ManifestId.ToString()))
                return name;
        }
        return null;
    }

    /// <summary>Which app a depot must be requested through. DLC depots belong to the DLC app, not the base game.</summary>
    private uint OwningApp(uint depotId)
    {
        if (app.DepotIds.Contains(depotId))
            return app.AppId;
        return app.DlcAppIds.FirstOrDefault(dlc => Depots(dlc)?.Children.Any(x => x.Name == depotId.ToString()) == true, app.AppId);
    }
}
