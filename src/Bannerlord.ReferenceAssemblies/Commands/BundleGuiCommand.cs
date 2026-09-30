using NuGet.Packaging;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// Packs GUI.v3.All from the per-build GUI packages already on the feed: the newest build of each release
/// version, with its DLC packages. Every run rebuilds it whole from its only inputs, the feed and the registry.
/// When the result matches the newest package on the feed, the run packs nothing.
/// See gui-packages-v2-all.md.
/// </summary>
internal static class BundleGuiCommand
{
    private static readonly Regex RxContentHashTag = new(@"contentHash:([0-9a-f]+)", RegexOptions.CultureInvariant);

    public static async Task RunAsync(BundleGuiOptions options, CancellationToken ct)
    {
        var app = options.App;
        var paths = options.Paths;
        if (!app.PacksGui)
        {
            Log.Info($"The {app.Name} app has no GUI packages; nothing to bundle.");
            return;
        }

        var registry = BuildRegistry.Load(options.RegistryPath, app);
        var chosen = Choose(registry.Builds, Log.Info);
        Log.Info($"{chosen.Count} release version(s) to bundle, {chosen.Count(x => x.IsBeta)} of them a beta");
        if (chosen.Count == 0)
        {
            WriteOutput(false, null);
            return;
        }

        var feed = new NuGetFeed(options.FeedUrl);
        var basePackageId = app.PackageId(GuiPackager.BaseModule, "");
        var sources = new List<GuiBundleSource>();
        var known = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chosenBuild in chosen)
        {
            ct.ThrowIfCancellationRequested();
            var version = chosenBuild.PublishedGuiVersion!;
            var baseSource = await SourceAsync(chosenBuild, basePackageId, version);
            sources.Add(baseSource);
            // The build the package was packed from, which its DLC packages were packed from too.
            var build = baseSource.Build;

            // A DLC package is the base id and the module; the registry names the modules but not which are DLC.
            var dlc = 0;
            foreach (var module in (build.ModuleVersions?.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal))
            {
                var id = app.PackageId($"{GuiPackager.BaseModule}.{module}", "");
                if (!File.Exists(LocalPath(paths.Final, id, version)))
                {
                    if (!known.TryGetValue(id, out var versions))
                        known[id] = versions = await feed.GetVersionsAsync(id, ct);
                    if (!versions.Contains(version))
                        continue;
                }
                sources.Add(await SourceAsync(build, id, version));
                dlc++;
            }
            if (dlc == 0 && build.Manifests.Keys.Any(x => app.DlcAppIds.Contains(uint.Parse(x, CultureInfo.InvariantCulture))))
                throw new InvalidDataException($"Build {build} has a DLC's depot, but no DLC GUI package at {version} was found.");
        }
        Log.Info($"{sources.Count} package(s): {sources.Count(x => x.PackageId == basePackageId)} base, {sources.Count(x => x.PackageId != basePackageId)} DLC");

        var clock = Stopwatch.StartNew();
        var content = GuiBundle.Build(sources, basePackageId);
        Log.Info($"Built the bundle in {clock.Elapsed.TotalSeconds:F1}s: content hash {content.ContentHash}");
        GuiBundleChecks.Run(content, sources, basePackageId);
        Report(content);

        var bundleId = app.PackageId(GuiBundle.Module, "");
        if (options.Prerelease is null)
        {
            if (await feed.GetNewestAsync(bundleId, ct) is { } newest && RxContentHashTag.Match(newest.Tags) is { Success: true } tag && tag.Groups[1].Value == content.ContentHash)
            {
                Log.Info($"{bundleId} {newest.Version} on the feed holds the same content; nothing to pack.");
                WriteOutput(false, null);
                return;
            }
        }
        else
        {
            Log.Info($"A prerelease for local testing: not compared with the feed.");
        }

        var packageVersion = Version(DateTimeOffset.UtcNow, options.Revision, options.Prerelease);
        if (options.Revision is null)
            Log.Info("No --revision: packing with 0, for inspection only. Do not push this package.");
        var nupkg = Pack(paths, app, bundleId, packageVersion, content);
        try
        {
            GuiBundleChecks.CheckSize(nupkg);
        }
        catch
        {
            File.Delete(nupkg);
            throw;
        }
        Log.Info($"{Path.GetFileName(nupkg)} ({new FileInfo(nupkg).Length / 1024} KiB) in {paths.FinalBundle}");
        WriteOutput(true, packageVersion);

        async Task<GuiBundleSource> SourceAsync(BuildEntry build, string id, string version)
        {
            var path = LocalPath(paths.Final, id, version);
            if (!File.Exists(path))
            {
                path = LocalPath(Path.Combine(paths.GuiBundle, "packages"), id, version);
                if (!File.Exists(path))
                {
                    Log.Info($"  downloading {id} {version}");
                    await feed.DownloadAsync(id, version, path, ct);
                }
            }
            var source = new GuiBundleSource(build, id, version, () => ReadGuiFiles(path));
            var packedFrom = PackedFrom(registry.Builds, build, CheckManifest(source), id, version);
            if (packedFrom == build)
                return source;
            Log.Info($"  {id} {version} was packed from build {packedFrom.BuildId}, which shares its package version with {build.BuildId}; bundled as {packedFrom.BuildId}");
            return source with { Build = packedFrom };
        }
    }

    /// <summary>
    /// The registry's entry for the build a package was packed from. Builds with one version and changeset share one
    /// package, packed from one of them, and mark-published --fromFeed marks every one of them published, so the
    /// build chosen can be another of them. The package's manifest says which one it was; any build outside that
    /// package version fails.
    /// </summary>
    internal static BuildEntry PackedFrom(IReadOnlyList<BuildEntry> builds, BuildEntry chosen, uint packedFrom, string id, string version)
    {
        if (packedFrom == chosen.BuildId)
            return chosen;
        return builds.FirstOrDefault(x => x.BuildId == packedFrom) is { } actual && string.Equals(actual.PackageVersion, chosen.PackageVersion, StringComparison.OrdinalIgnoreCase)
            ? actual
            : throw new InvalidDataException($"{id} {version} was packed from build {packedFrom}, which is not a build of package version {chosen.PackageVersion} in the registry; expected build {chosen.BuildId}.");
    }

    /// <summary>
    /// The build of each release version whose GUI packages go into the bundle, oldest version first: the newest
    /// with a GUI package, by changeset, then date, then build id. A beta counts. Early access builds are left
    /// out: no mod targets them any more.
    /// </summary>
    internal static List<BuildEntry> Choose(IReadOnlyList<BuildEntry> builds, Action<string>? log = null)
    {
        // Two builds with one version and changeset pack to one package version, and the feed takes it once: the
        // later build can never have a GUI package of its own, so it is not waiting for one.
        var published = builds.Where(x => x.IsPublishedAs(PackageKind.Gui)).Select(x => x.PackageVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var chosen = new List<BuildEntry>();
        foreach (var version in builds.Where(x => x.Version is ['v', ..] && x.IsReleaseLike).GroupBy(x => x.Version!))
        {
            var ordered = version
                .Where(x => x.IsPublishedAs(PackageKind.Gui) || !published.Contains(x.PackageVersion))
                .OrderByDescending(x => x.ChangeSet ?? -1).ThenByDescending(x => x.Date).ThenByDescending(x => x.BuildId)
                .ToList();
            var pick = ordered.FirstOrDefault(x => x.IsPublishedAs(PackageKind.Gui));
            // A beta Steam no longer serves will not get one, unless update --retryUnavailable finds it served again.
            foreach (var waiting in ordered.TakeWhile(x => x != pick))
                log?.Invoke($"  {version.Key}: {waiting} {(waiting.ContentUnavailable ? "has no GUI package, and Steam no longer serves it" : "has no GUI package yet")}; "
                            + (pick is null ? "no build of the version has one, so the version is left out" : $"{pick.BuildId} stands in"));
            if (pick is not null)
                chosen.Add(pick);
        }
        return chosen.OrderBy(x => x.Version!, GameVersionComparer.Instance).ToList();
    }

    /// <summary>YYYY.M.D.R: the UTC date of the run and the workflow's run number, 0 without one.</summary>
    internal static string Version(DateTimeOffset now, int? revision, string? prerelease)
    {
        var utc = now.ToUniversalTime();
        var version = string.Create(CultureInfo.InvariantCulture, $"{utc.Year}.{utc.Month}.{utc.Day}.{revision ?? 0}");
        return prerelease is { Length: > 0 } ? $"{version}-{prerelease}" : version;
    }

    /// <summary>The file names NuGetPackages.Save gives, and the download cache uses.</summary>
    private static string LocalPath(string folder, string id, string version) => Path.Combine(folder, $"{id}.{version}.nupkg");

    /// <summary>A package's files under gui/, by path relative to gui/.</summary>
    internal static IReadOnlyDictionary<string, byte[]> ReadGuiFiles(string nupkg)
    {
        using var reader = new PackageArchiveReader(nupkg);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in reader.GetFiles().Where(x => x.StartsWith("gui/", StringComparison.Ordinal)))
        {
            using var stream = reader.GetStream(path);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files[path["gui/".Length..]] = buffer.ToArray();
        }
        return files;
    }

    /// <summary>
    /// The package is the one asked for: format 3, under its own id. Returns the build its manifest says it was
    /// packed from, which the caller matches against the registry.
    /// </summary>
    private static uint CheckManifest(GuiBundleSource source)
    {
        var files = source.ReadFiles();
        if (!files.TryGetValue("manifest.json", out var bytes))
            throw new InvalidDataException($"{source.PackageId} {source.PackageVersion} has no gui/manifest.json.");
        var manifest = JsonDocument.Parse(bytes).RootElement;
        var format = manifest.GetProperty("formatVersion").GetInt32();
        var package = manifest.GetProperty("package").GetString();
        if (format != GuiPackager.FormatVersion || !string.Equals(package, source.PackageId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{source.PackageId} {source.PackageVersion} says it is {package}, format {format}; expected {source.PackageId}, format {GuiPackager.FormatVersion}.");
        return manifest.GetProperty("buildId").GetUInt32();
    }

    private static string Pack(Paths paths, App app, string bundleId, string version, GuiBundleContent content)
    {
        var staging = Path.Combine(paths.GuiBundle, "staging");
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);

        var files = new List<(string Source, string Target)>();
        foreach (var (path, bytes) in content.Files)
        {
            var target = $"gui/{path}";
            var source = Path.Combine(staging, target);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, bytes);
            files.Add((source, target));
        }
        var props = Path.Combine(staging, "build", $"{bundleId}.props");
        Directory.CreateDirectory(Path.GetDirectoryName(props)!);
        File.WriteAllText(props, GuiBundle.Props(bundleId), new UTF8Encoding(false));
        files.Add((props, $"build/{bundleId}.props"));

        var builder = NuGetPackages.New(
            bundleId,
            version,
            "Bannerlord Game GUI: every version",
            "The UI of every release version of Mount & Blade II: Bannerlord and its DLC, in one package, for analyzers that check UI patches against all the versions a mod supports. "
            + "The newest build of each version, as the GUI.v3 packages carry it, with each record stored once. Carries none of the game's files, and adds nothing to compilation.",
            ["bannerlord", "gui", "prefabs", $"appId:{app.AppId}", $"contentHash:{content.ContentHash}"]);
        builder.DevelopmentDependency = true;
        foreach (var (source, target) in files.OrderBy(x => x.Target, StringComparer.Ordinal))
            builder.Files.Add(new PhysicalPackageFile { SourcePath = source, TargetPath = target });

        Directory.CreateDirectory(paths.FinalBundle);
        return NuGetPackages.Save(builder, paths.FinalBundle);
    }

    /// <summary>The figures the plan's acceptance list asks for: sizes, lines per table, and what a plain parse of it all costs.</summary>
    private static void Report(GuiBundleContent content)
    {
        var reader = new GuiBundleReader(content.Files);
        Log.Info($"  {content.Files.Count} file(s), {content.Files.Values.Sum(x => (long) x.Length) / 1024} KiB unpacked");
        foreach (var table in reader.Tables)
            Log.Info($"    {table}: {reader.Lines(table).Length} line(s), {content.Files[table].Length / 1024} KiB");

        var clock = Stopwatch.StartNew();
        JsonDocument.Parse(content.Files[GuiBundle.IndexFile]).Dispose();
        foreach (var table in reader.Tables)
        foreach (var line in reader.Lines(table))
            JsonDocument.Parse(line).Dispose();
        Log.Info($"  a plain parse of every line takes {clock.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static void WriteOutput(bool packed, string? version)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } output)
            File.AppendAllText(output, $"packed={(packed ? "true" : "false")}\nversion={version}\n");
    }
}
