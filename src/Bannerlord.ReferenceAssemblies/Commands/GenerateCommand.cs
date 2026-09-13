namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// Packs the builds the registry says the feed is missing. Downloads by the recorded manifest ids, so it
/// is not limited to whatever branches Steam exposes today.
/// </summary>
internal static class GenerateCommand
{
    /// <summary>How long a young build waits for its DLC before it is packed as recorded. See <see cref="DlcMayBeTrailing"/>.</summary>
    internal static readonly TimeSpan DlcGracePeriod = TimeSpan.FromHours(2);

    public static async Task RunAsync(GenerateOptions options, CancellationToken ct)
    {
        var app = options.App;
        var paths = options.Paths;
        var registry = BuildRegistry.Load(options.RegistryPath, app);

        var candidates = registry.Builds.Where(x => x.CanBeGenerated).ToList();
        if (registry.Builds.Count(x => !x.HasVersion) is > 0 and var unnamed)
            Log.Info($"{unnamed} build(s) have no version yet; the update verb reads them.");
        if (candidates.Count(x => x.ChangeSet is null) is > 0 and var noChangeSet)
            Log.Info($"{noChangeSet} build(s) carry no changeset; their package version uses the Steam build id instead.");

        SteamClient? steam = null;
        try
        {
            List<BuildEntry> toGenerate;
            if (options.BuildId.Any())
            {
                var wanted = options.BuildId.ToHashSet();
                toGenerate = candidates.Where(x => wanted.Contains(x.BuildId)).ToList();
                foreach (var missing in wanted.Where(id => toGenerate.All(x => x.BuildId != id)))
                    Log.Info($"Build {missing} is not in the registry or cannot be packaged; skipping.");
            }
            else
            {
                // The registry records what the feed carries, so the usual run asks NuGet nothing at all.
                if (options.CheckFeed)
                    await MarkPublishedCommand.ReconcileAsync(registry, app, new NuGetFeed(options.FeedUrl), ct);

                Log.Info($"{candidates.Count(x => x.IsPublished)} build(s) already published according to the registry");

                // The current tip of a branch is perishable: once the branch moves on, Steam stops serving
                // it unless it was public. Public history can be fetched any time, so tips go first and
                // the backlog follows newest first. A dry run only reports, so it stays offline.
                var live = new HashSet<uint>();
                if (!options.DryRun)
                {
                    steam = options.CreateSteam();
                    await steam.ConnectAsync();
                    live = candidates.Where(steam.IsLive).Select(x => x.BuildId).ToHashSet();
                    Log.Info($"{live.Count} candidate(s) are the current tip of a branch");
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var held in candidates.Where(x => !x.IsPublished && DlcMayBeTrailing(x, registry.Builds, app, now)))
                    Log.Info($"Build {held} is {(now - held.Date).TotalMinutes:F0} minutes old and still carries the previous build's DLC manifest; left for a later run.");
                if (candidates.Count(x => !x.IsPublished && x.IsReleaseLike && x.ContentUnavailable) is > 0 and var refused)
                    Log.Info($"{refused} build(s) Steam refused before are left out; update --retryUnavailable or generate --buildId asks again.");

                toGenerate = Choose(registry.Builds, app, live, options.IncludeBeta, options.MaxBuilds, now);
            }

            if (options.DryRun)
            {
                paths.WriteBuildList("pending-builds.txt", toGenerate);
                Log.Info($"{toGenerate.Count} build(s) outstanding:");
                foreach (var build in toGenerate)
                    Log.Info($"  {build}");
                return;
            }

            if (toGenerate.Count == 0)
            {
                Log.Info("Nothing to generate.");
                return;
            }

            Log.Info($"Generating {toGenerate.Count} build(s):");
            foreach (var build in toGenerate)
                Log.Info($"  {build}");

            steam ??= options.CreateSteam();
            var packager = new ReferencePackager(paths);
            var generated = new List<BuildEntry>();
            var failed = 0;
            foreach (var build in toGenerate)
            {
                ct.ThrowIfCancellationRequested();

                // One build that Steam will not serve must not cost the run the builds that follow it.
                try
                {
                    var depot = paths.Depot(build.BuildId);
                    Log.Info($"Downloading {build}...");
                    await steam.DownloadAsync(build, depot, app.PackageFileFilters, primaryDepotOnly: false, ct);
                    build.ContentUnavailable = false;

                    // The binaries are authoritative: a registry version that disagrees would mislabel the package.
                    if (VersionReader.Read(depot) is not { } actual)
                    {
                        Log.Info($"Build {build.BuildId} has no readable version in its binaries; skipping.");
                        continue;
                    }
                    if (actual.Version != build.Version || actual.ChangeSet != build.ChangeSet)
                    {
                        Log.Info($"Build {build.BuildId} reports {actual.Version}.{actual.ChangeSet?.ToString() ?? "no-changeset"} but the registry says {build.Version}.{build.ChangeSet?.ToString() ?? "no-changeset"}; correcting the registry.");
                        (build.Version, build.ChangeSet) = actual;
                    }
                    build.ModuleVersions = VersionReader.ReadModuleVersions(depot);
                    registry.Save();

                    Log.Info($"Packing {build} as {build.PackageVersion}...");
                    packager.Pack(depot, paths.Ref(build.BuildId), app.ForBuild(build));
                    generated.Add(build);
                }
                catch (SteamContentException ex)
                {
                    // Recorded, so that the next scheduled run does not spend a slot on the same refusal.
                    failed++;
                    build.ContentUnavailable = true;
                    registry.Save();
                    Log.Info($"Build {build.BuildId} failed: {ex.Message} Marked unavailable; update --retryUnavailable or generate --buildId asks again.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    Log.Info($"Build {build.BuildId} failed: {ex.Message}");
                }
            }
            if (failed > 0)
                Log.Info($"{failed} build(s) failed; the rest were generated.");

            // The workflow feeds these ids to mark-published once the push has actually succeeded, so
            // nothing is recorded as published that never reached the feed.
            var list = paths.WriteBuildList("generated-builds.txt", generated);
            Log.Info($"Generated {generated.Count} build(s); ids written to {list}");
        }
        finally
        {
            steam?.Dispose();
        }
    }

    /// <summary>
    /// The builds a scheduled run packs, in order: what the feed lacks, of the builds modders build against,
    /// one per package version, the current branch tips first and then the backlog newest first.
    /// </summary>
    internal static List<BuildEntry> Choose(IReadOnlyList<BuildEntry> builds, App app, IReadOnlySet<uint> live, bool includeBeta, int maxBuilds, DateTimeOffset now)
    {
        var candidates = builds.Where(x => x.CanBeGenerated).ToList();
        var published = candidates.Where(x => x.IsPublished).Select(x => x.PackageVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(x => !x.IsPublished && !published.Contains(x.PackageVersion))
            .Where(x => x.IsReleaseLike)
            .Where(x => includeBeta || !x.IsBeta)
            // A build Steam refused last time would fail again and take the slot of one it would serve.
            .Where(x => !x.ContentUnavailable)
            .Where(x => !DlcMayBeTrailing(x, builds, app, now))
            // Several builds can report one game version and changeset, and they would all pack to the
            // same package version. Keep the public one, else the earliest.
            .GroupBy(x => x.PackageVersion, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Branches.Contains("public")).ThenBy(x => x.Date).First())
            .OrderByDescending(x => live.Contains(x.BuildId))
            .ThenByDescending(x => x.Date)
            .Take(Math.Max(0, maxBuilds))
            .ToList();
    }

    /// <summary>
    /// Whether the build was probably recorded before its DLC caught up. The DLC build follows the game
    /// build by up to an hour, and a registry run in that gap writes the previous DLC manifest against the
    /// new game build; packing it then would ship the previous DLC's assemblies under the new version.
    /// Every game build so far has come with a DLC manifest of its own, so a young build whose DLC
    /// manifest an older build already carries is left for a later run. After the grace period it is
    /// taken as recorded: by then the update verb has either replaced the manifest or the DLC is not coming.
    /// </summary>
    internal static bool DlcMayBeTrailing(BuildEntry build, IReadOnlyList<BuildEntry> all, App app, DateTimeOffset now)
    {
        if (app.DlcAppIds.Count == 0 || now - build.Date > DlcGracePeriod)
            return false;

        return build.Manifests
            .Where(x => !app.DepotIds.Contains(uint.Parse(x.Key)))
            .Any(dlc => all.Any(other => other.BuildId != build.BuildId
                                         && other.Date < build.Date
                                         && other.Manifests.TryGetValue(dlc.Key, out var manifest)
                                         && manifest == dlc.Value));
    }
}
