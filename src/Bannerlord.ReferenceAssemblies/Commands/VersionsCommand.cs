using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// The current stable and beta versions of an app as the registry records them. Beta is the stable version
/// when there is no beta branch. Published says whether both builds' packages are on the feed, which is
/// what decides whether the versions may be announced to the repositories that build against them.
/// </summary>
internal sealed record CurrentVersions(
    string? Stable,
    uint? StableBuildId,
    string? Beta,
    uint? BetaBuildId,
    bool Published);

/// <summary>
/// Reports the current versions from the registry alone: to the console as JSON, to final/versions.json,
/// and to GITHUB_OUTPUT when running in a workflow. BUTR/.github turns them into the GAME_VERSION_STABLE
/// and GAME_VERSION_BETA variables once told.
/// </summary>
internal static class VersionsCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Task RunAsync(VersionsOptions options)
    {
        var registry = BuildRegistry.Load(options.RegistryPath, options.App);
        var versions = Compute(registry);

        Log.Info($"Stable: {Describe(versions.Stable, versions.StableBuildId, registry)}");
        Log.Info($"Beta:   {Describe(versions.Beta, versions.BetaBuildId, registry)}{(versions.BetaBuildId == versions.StableBuildId ? " (no beta branch; same as stable)" : "")}");
        Log.Info(versions.Published ? "Both are on the feed." : "Not both on the feed yet; not to be announced.");

        var json = JsonSerializer.Serialize(versions, JsonOptions);
        options.Paths.WriteFinal("versions.json", json);
        if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } output)
            File.AppendAllText(output, $"stable={versions.Stable}\nbeta={versions.Beta}\npublished={(versions.Published ? "true" : "false")}\n");

        Console.WriteLine(json);
        return Task.CompletedTask;
    }

    /// <summary>Stable is the tip of public; beta the tip of beta, or stable when Steam has no beta branch.</summary>
    internal static CurrentVersions Compute(BuildRegistry registry)
    {
        var stable = Tip(registry, "public");
        var beta = Tip(registry, "beta") ?? stable;
        var published = stable is { IsPublished: true } && beta is { IsPublished: true };
        return new CurrentVersions(stable?.Version, stable?.BuildId, beta?.Version, beta?.BuildId, published);
    }

    private static BuildEntry? Tip(BuildRegistry registry, string branch) =>
        registry.Current.TryGetValue(branch, out var buildId) ? registry.Find(buildId) : null;

    private static string Describe(string? version, uint? buildId, BuildRegistry registry) =>
        buildId is null
            ? "no such branch"
            : $"{version ?? "version not read yet"} (build {buildId}{(registry.Find(buildId.Value) is { IsPublished: true } ? ", published" : ", not on the feed")})";
}
