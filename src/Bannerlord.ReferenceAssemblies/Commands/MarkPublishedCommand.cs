namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// Writes into the registry what the feed carries, so that deciding what is missing needs only the file.
/// Run it after a push with the build ids that were pushed, or with --fromFeed to rewrite every marker
/// from NuGet, which is also how an existing feed is recorded for the first time.
/// </summary>
internal static class MarkPublishedCommand
{
    public static async Task RunAsync(MarkPublishedOptions options, CancellationToken ct)
    {
        var app = options.App;
        var registry = BuildRegistry.Load(options.RegistryPath, app);

        if (options.FromFeed)
            await ReconcileAsync(registry, app, new NuGetFeed(options.FeedUrl), ct);

        var marked = 0;
        foreach (var buildId in options.BuildId)
        {
            if (registry.Find(buildId) is not { } build)
            {
                Log.Info($"Build {buildId} is not in the registry; skipping.");
                continue;
            }
            if (!build.CanBeGenerated)
            {
                Log.Info($"Build {buildId} has no packageable version, so nothing could have been published for it; skipping.");
                continue;
            }
            if (build.IsPublished)
                continue;

            build.PublishedVersion = build.PackageVersion;
            marked++;
            Log.Info($"  {build} published as {build.PackageVersion}");
        }

        registry.Save();
        Log.Info($"Marked {marked} build(s) as published; {registry.Builds.Count(x => x.IsPublished)} of {registry.Builds.Count(x => x.CanBeGenerated)} packageable build(s) are on the feed.");
    }

    /// <summary>Rewrites every published marker from the feed and saves. Marks and clears alike.</summary>
    public static async Task ReconcileAsync(BuildRegistry registry, App app, NuGetFeed feed, CancellationToken ct)
    {
        Log.Info("Reading the feed...");
        var (buildIds, versions) = await feed.GetPublishedAsync(app, ct);
        Log.Info($"The feed carries {buildIds.Count} build(s) and {versions.Count} package version(s) of {app.PackagePrefix}");

        var marked = 0;
        var cleared = 0;
        foreach (var build in registry.Builds.Where(x => x.CanBeGenerated))
        {
            // The build id tag is the direct match; the version covers packages made before the tag
            // existed, and builds that share a version with one already published.
            var onFeed = buildIds.Contains(build.BuildId) || versions.Contains(build.PackageVersion);
            var value = onFeed ? build.PackageVersion : null;
            if (build.PublishedVersion == value)
                continue;

            if (value is null)
                cleared++;
            else
                marked++;
            build.PublishedVersion = value;
        }
        registry.Save();
        Log.Info($"Reconciled with the feed: {marked} marked, {cleared} cleared");
    }
}
