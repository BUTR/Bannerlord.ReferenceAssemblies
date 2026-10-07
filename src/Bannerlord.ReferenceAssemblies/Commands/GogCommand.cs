namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// Records GOG's public builds of the game in builds/gog/&lt;productId&gt;.json and rebuilds builds/links.json,
/// which pairs them with the Steam builds they are the same as. Needs no login: GOG lists its newest public
/// builds to anyone, and a daily run catches each one while it is listed. --fromGogDb adds the builds GOG no
/// longer lists, from GOGDB, once.
///
/// With a GOG refresh token it also checks builds' labels against their files: it downloads the few small
/// files a version is read from, as the Steam registry does, and reads them the same way.
/// </summary>
internal static class GogCommand
{
    public static async Task RunAsync(GogOptions options, CancellationToken ct)
    {
        var registry = GogRegistry.Load(options.RegistryPath, GogRegistry.GameProductId);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Bannerlord.ReferenceAssemblies (+https://github.com/BUTR/Bannerlord.ReferenceAssemblies)");
        var gog = new GogClient(http);

        if (options.FromGogDb)
        {
            var history = await gog.GetGogDbBuildsAsync(registry.ProductId, options.GogDbUrl, ct);
            Log.Info($"GOGDB knows {history.Count} public builds");
            Merge(registry, history);
        }

        var listed = await gog.GetListedBuildsAsync(registry.ProductId, ct);
        Log.Info($"GOG lists {listed.Count} public builds: {string.Join(", ", listed.Select(x => x.Label))}");
        Merge(registry, listed);
        registry.Current = listed
            .GroupBy(x => x.Branch ?? "public")
            .ToDictionary(x => x.Key, x => x.MaxBy(y => y.Date)!.BuildId);

        foreach (var build in registry.Builds.Where(x => x.Depots is null).OrderByDescending(x => x.Date))
        {
            try
            {
                build.Depots = await gog.GetDepotsAsync(build.MetaId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                Log.Info($"  {build}: could not read the build manifest, will try again next run ({ex.Message})");
            }
        }

        registry.Save();

        // Checking needs a token GOG accepts, of an account that owns the game. Without one the builds keep
        // the version their label gives, and the run goes on.
        if (!registry.Builds.Any(x => x is { Verified: false, Unverifiable: false }))
            Log.Info("Every GOG build is checked against its files already");
        else if (options.RefreshToken is not { Length: > 0 } refreshToken)
            Log.Info("GOG_REFRESH_TOKEN is not set; labels are not checked against the files");
        else if (new GogContent(http, refreshToken) is var content && await content.CheckAccessAsync(registry.ProductId, ct) is { } reason)
            Log.Info($"Not checking labels against the files: {reason}");
        else
            await VerifyAsync(registry, content, options.Verify, ct);

        Log.Info($"GOG registry: {registry.Builds.Count} builds, {registry.Builds.Count(x => x.Verified)} verified against their files" +
                 $" ({registry.Builds.Count(x => x.LabelMismatch)} disagreeing with their label), {registry.Builds.Count(x => x.Unverifiable)} unverifiable," +
                 $" {registry.Builds.Count(x => !x.HasVersion)} without a version");

        var steam = BuildRegistry.Load(options.SteamRegistryPath, App.Game);
        var links = BuildLinks.Compute(steam, registry);
        links.Save(options.LinksPath);
        var linked = links.Builds.Sum(x => x.Gog.Count);
        Log.Info($"Links: {linked} GOG builds are also on Steam; {registry.Builds.Count(x => x.HasVersion) - linked} are GOG's alone");
    }

    /// <summary>
    /// Reads the version of up to <paramref name="limit"/> builds not checked yet, newest first, from their
    /// files. A build whose files GOG no longer has is marked unverifiable; one that merely failed to
    /// download is tried again next run. The registry is saved after each build.
    /// </summary>
    private static async Task VerifyAsync(GogRegistry registry, GogContent content, int limit, CancellationToken ct)
    {
        var pending = registry.Builds.Where(x => x is { Verified: false, Unverifiable: false }).OrderByDescending(x => x.Date).Take(limit).ToList();
        Log.Info($"Checking {pending.Count} GOG builds against their files");
        foreach (var build in pending)
        {
            var folder = Path.Combine(Path.GetTempPath(), $"gog-{build.BuildId}-{Guid.NewGuid():N}");
            try
            {
                var files = await content.DownloadAsync(registry.ProductId, build.MetaId, App.Game.VersionFileFilters, folder, ct);
                if (VersionReader.Read(folder) is not { } read)
                {
                    build.Unverifiable = true;
                    Log.Info($"  {build}: no version in {(files.Count == 0 ? "the build, which has none of the files" : string.Join(", ", files))}; unverifiable");
                }
                else
                {
                    build.ApplyRead(read.Version, read.ChangeSet);
                    Log.Info($"  {build}: files report {read.Version}.{read.ChangeSet?.ToString() ?? "no-changeset"}{(build.LabelMismatch ? ", NOT what the label says" : "")}");
                }
                registry.Save();
            }
            catch (GogContentMissingException ex)
            {
                build.Unverifiable = true;
                registry.Save();
                Log.Info($"  {build}: {ex.Message} Unverifiable");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                Log.Info($"  {build}: download failed, will try again next run ({ex.Message})");
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>
    /// Adds builds not seen before. A build already recorded keeps its entry, unless GOG has since renamed
    /// it, which also forgets any check of its files.
    /// </summary>
    internal static void Merge(GogRegistry registry, IEnumerable<GogBuildEntry> builds)
    {
        foreach (var build in builds)
        {
            if (registry.Find(build.BuildId) is not { } known)
            {
                registry.Builds.Add(build);
                Log.Info($"  new: {build}");
                continue;
            }

            if (known.Label != build.Label || known.Branch != build.Branch)
            {
                Log.Info($"  {known}: GOG now calls it {build.Label}{(build.Branch is null ? "" : $" [{build.Branch}]")}");
                (known.Label, known.Branch) = (build.Label, build.Branch);
                known.ParseLabel();
            }
        }
    }
}
