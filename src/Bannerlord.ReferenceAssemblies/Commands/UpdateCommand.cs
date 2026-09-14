namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// Writes what Steam shows right now into the registry, then reads the version of builds that have none
/// by downloading a few small files. Steam forgets a manifest once its branch moves on; the registry is
/// what remembers it, which is why this runs often.
/// </summary>
internal static class UpdateCommand
{
    public static async Task RunAsync(UpdateOptions options, CancellationToken ct)
    {
        var app = options.App;
        var registry = BuildRegistry.Load(options.RegistryPath, app);

        using var steam = options.CreateSteam();
        await steam.ConnectAsync();

        Log.Info("Reading Steam branches...");
        var branches = steam.GetBranches().ToList();
        registry.Current = branches.ToDictionary(x => x.Name, x => x.BuildId);
        var added = 0;
        var updated = 0;
        foreach (var branch in branches)
        {
            Log.Info($"  {branch.Name}: build {branch.BuildId} ({branch.TimeUpdated:yyyy-MM-dd}) {string.Join(" ", branch.Manifests.Select(x => $"{x.Key}={x.Value}"))}");

            if (registry.Find(branch.BuildId) is not { } entry)
            {
                registry.Builds.Add(new BuildEntry
                {
                    BuildId = branch.BuildId,
                    Date = branch.TimeUpdated,
                    Title = string.IsNullOrWhiteSpace(branch.Description) ? null : branch.Description,
                    Branches = [branch.Name],
                    Manifests = branch.Manifests,
                });
                added++;
                continue;
            }

            var changed = false;
            if (!entry.Branches.Contains(branch.Name))
            {
                entry.Branches.Add(branch.Name);
                changed = true;
            }
            foreach (var (depot, gid) in branch.Manifests)
            {
                if (entry.Manifests.TryAdd(depot, gid))
                {
                    changed = true;
                    continue;
                }
                if (entry.Manifests[depot] == gid)
                    continue;

                // The DLC build can trail the game build by up to an hour, so a run in that gap records
                // the previous DLC manifest against the new game build. While the build is unpublished
                // the newer manifest simply replaces it and the build is read again. Once published,
                // the packages on the feed already carry the old assemblies and repacking cannot fix it.
                if (entry.IsPublished)
                {
                    Log.Info($"  WARNING: depot {depot} of published build {entry.BuildId} moved from manifest {entry.Manifests[depot]} to {gid}; the registry keeps what was packaged.");
                    continue;
                }
                Log.Info($"  depot {depot} of build {entry.BuildId} moved from manifest {entry.Manifests[depot]} to {gid}; will read it again");
                entry.Manifests[depot] = gid;
                entry.Version = null;
                entry.ChangeSet = null;
                entry.ModuleVersions = null;
                entry.VersionUnreadable = false;
                changed = true;
            }
            if (changed)
                updated++;
        }
        registry.Save();
        Log.Info($"Branches: {added} new build(s), {updated} updated");

        // Newest first: a build that a branch points at now is the one that might stop being served.
        // Refused builds are left alone unless asked for; --buildId takes exactly what it names.
        var wanted = options.BuildId.ToHashSet();
        // A build needs reading when it has no version yet, or when its module versions were never read.
        var pending = registry.Builds
            .Where(x => wanted.Count > 0
                ? wanted.Contains(x.BuildId)
                : (!x.HasVersion && !x.VersionUnreadable || x.HasVersion && x.ModuleVersions is null) && (options.RetryUnavailable || !x.ContentUnavailable))
            .OrderByDescending(x => x.Date)
            .Take(wanted.Count > 0 ? int.MaxValue : Math.Max(0, options.FillVersions))
            .ToList();

        Log.Info($"Reading the version of {pending.Count} build(s)...");
        var filled = 0;
        foreach (var build in pending)
        {
            ct.ThrowIfCancellationRequested();
            var folder = options.Paths.Depot(build.BuildId);
            try
            {
                // Every depot, so that a DLC's manifest comes down with the game's version files.
                await steam.DownloadAsync(build, folder, app.VersionFileFilters, primaryDepotOnly: false, ct);
                build.ContentUnavailable = false;

                if (VersionReader.Read(folder) is { } read)
                {
                    // A build read again, for its module versions, may turn out to have been misread. The
                    // package version follows the correction, and a published build is then packed again.
                    if (build.HasVersion && (build.Version, build.ChangeSet) != read)
                        Log.Info($"  {build.BuildId}: the binaries report {read.Version}.{read.ChangeSet?.ToString() ?? "no-changeset"} but the registry said {build.Version}.{build.ChangeSet?.ToString() ?? "no-changeset"}; correcting{(build.PublishedVersion is not null ? ", and the feed carries the old version, so the build will be packaged again" : "")}");
                    (build.Version, build.ChangeSet) = read;
                    build.ModuleVersions = VersionReader.ReadModuleVersions(folder);
                    filled++;
                    // Only the modules that do not simply repeat the game version are worth a line.
                    var own = build.ModuleVersions.Where(x => x.Value != build.Version).Select(x => $"{x.Key} {x.Value}").ToList();
                    Log.Info($"  {build}{(own.Count > 0 ? $" ({string.Join(", ", own)})" : "")}{(build.ChangeSet is null ? " (no changeset in the binaries; the package version will use the build id)" : "")}");
                }
                else
                {
                    // The download is verified complete, so what is missing is missing from the manifest:
                    // either the files carry no version, or the build has no library under the expected
                    // folder at all (one 2020 Modding Kit build). Asking again would not change that.
                    build.VersionUnreadable = true;
                    Log.Info(VersionReader.FindFile(folder, "TaleWorlds.Library.dll") is not null
                        ? $"  {build.BuildId}: the binaries carry no version; marked unreadable"
                        : $"  {build.BuildId}: the manifest holds no library assembly under bin/{app.BinFolders[0]}; marked unreadable. update --buildId asks again.");
                }
            }
            catch (SteamContentException ex)
            {
                build.ContentUnavailable = true;
                Log.Info($"  {build.BuildId}: {ex.Message} Marked unavailable; update --retryUnavailable asks again.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"  {build.BuildId}: download failed: {ex.Message}; will retry");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }

            // Saved after every build so an interrupted run keeps its progress.
            registry.Save();
        }

        Log.Info($"Versions: {filled} filled, " +
                 $"{registry.Builds.Count(x => !x.HasVersion && !x.VersionUnreadable && !x.ContentUnavailable)} still missing, " +
                 $"{registry.Builds.Count(x => x.ContentUnavailable)} refused by Steam, " +
                 $"{registry.Builds.Count(x => x.VersionUnreadable)} unreadable, " +
                 $"{registry.Builds.Count(x => x.HasVersion && x.ChangeSet is null)} without a changeset");
    }
}
