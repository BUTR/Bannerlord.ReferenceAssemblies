using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>Which builds a scheduled generate run picks, and in what order.</summary>
public sealed class GenerateSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static BuildEntry Build(uint id, string version, int changeSet, DateTimeOffset date, string[]? branches = null, string? dlcManifest = null, string? published = null, bool unavailable = false) => new()
    {
        BuildId = id,
        Version = version,
        ChangeSet = changeSet,
        Date = date,
        Branches = [.. branches ?? ["public"]],
        PublishedVersion = published,
        ContentUnavailable = unavailable,
        Manifests = dlcManifest is null
            ? new Dictionary<string, string> { ["261551"] = $"m{id}" }
            : new Dictionary<string, string> { ["261551"] = $"m{id}", ["2927200"] = dlcManifest },
    };

    private static List<uint> Choose(App app, IEnumerable<BuildEntry> builds, IEnumerable<uint>? live = null, int maxBuilds = 4, bool includeBeta = true) =>
        GenerateCommand.Choose(builds.ToList(), app, (live ?? []).ToHashSet(), includeBeta, maxBuilds, Now).Select(x => x.BuildId).ToList();

    [Fact]
    public void Published_builds_and_builds_sharing_a_published_package_version_are_left_out()
    {
        var builds = new[]
        {
            Build(1, "v1.4.7", 117484, Now.AddDays(-30), published: "1.4.7.117484"),
            Build(2, "v1.4.7", 117484, Now.AddDays(-29)), // the same version and changeset re-uploaded
            Build(3, "v1.4.8", 119303, Now.AddDays(-10)),
        };
        Assert.Equal([3u], Choose(App.Game, builds));
    }

    [Fact]
    public void Builds_that_only_reached_an_internal_branch_are_not_packaged() =>
        Assert.Empty(Choose(App.Game, [Build(1, "v1.4.7", 117484, Now.AddDays(-30), branches: ["perf_test"])]));

    [Fact]
    public void A_build_Steam_refused_before_does_not_take_a_slot()
    {
        var builds = new[]
        {
            Build(1, "v1.5.0", 120240, Now.AddDays(-25), branches: ["beta"], unavailable: true),
            Build(2, "v1.4.8", 119303, Now.AddDays(-30)),
        };
        Assert.Equal([2u], Choose(App.Game, builds));
    }

    [Fact]
    public void Live_tips_go_first_then_the_backlog_newest_first_up_to_the_limit()
    {
        var builds = new[]
        {
            Build(1, "e1.5.0", 100, Now.AddDays(-400)),
            Build(2, "e1.5.1", 101, Now.AddDays(-300)),
            Build(3, "e1.5.2", 102, Now.AddDays(-200)),
            Build(4, "v1.5.3", 103, Now.AddDays(-100), branches: ["beta"]),
        };
        Assert.Equal([1u, 3u, 2u], Choose(App.Game, builds, live: [1], maxBuilds: 3, includeBeta: false));
    }

    [Fact]
    public void Of_builds_sharing_a_package_version_the_public_one_is_packed_else_the_earliest()
    {
        var builds = new[]
        {
            // A build only ever seen in beta packs as a prerelease, so it never shares a version with these.
            Build(1, "v1.7.0", 304003, Now.AddDays(-40), branches: ["v1.7.0"]),
            Build(2, "v1.7.0", 304003, Now.AddDays(-39), branches: ["public"]),
            Build(3, "v1.7.0", 304003, Now.AddDays(-38), branches: ["v1.7.0"]),
        };
        Assert.Equal([2u], Choose(App.Game, builds));

        Assert.Equal([1u], Choose(App.Game, builds.Where(x => x.BuildId != 2)));
    }

    [Fact]
    public void A_young_build_still_carrying_the_previous_build_DLC_manifest_waits()
    {
        var previous = Build(1, "v1.4.7", 117484, Now.AddDays(-20), dlcManifest: "dlc-old", published: "1.4.7.117484");
        var fresh = Build(2, "v1.4.8", 119303, Now.AddMinutes(-30), dlcManifest: "dlc-old");

        Assert.True(GenerateCommand.DlcMayBeTrailing(fresh, [previous, fresh], App.Game, Now));
        Assert.Empty(Choose(App.Game, [previous, fresh]));
    }

    [Fact]
    public void A_young_build_with_a_DLC_manifest_of_its_own_is_packed_at_once()
    {
        var previous = Build(1, "v1.4.7", 117484, Now.AddDays(-20), dlcManifest: "dlc-old", published: "1.4.7.117484");
        var fresh = Build(2, "v1.4.8", 119303, Now.AddMinutes(-30), dlcManifest: "dlc-new");

        Assert.Equal([2u], Choose(App.Game, [previous, fresh]));
    }

    [Fact]
    public void After_the_grace_period_the_build_is_packed_as_recorded()
    {
        var previous = Build(1, "v1.4.7", 117484, Now.AddDays(-20), dlcManifest: "dlc-old", published: "1.4.7.117484");
        var aged = Build(2, "v1.4.8", 119303, Now - GenerateCommand.DlcGracePeriod - TimeSpan.FromMinutes(1), dlcManifest: "dlc-old");

        Assert.Equal([2u], Choose(App.Game, [previous, aged]));
    }

    [Fact]
    public void Builds_without_a_DLC_manifest_and_apps_without_DLC_never_wait()
    {
        var old = Build(1, "e1.5.0", 100, Now.AddDays(-400), published: "1.5.0.100");
        var fresh = Build(2, "e1.5.1", 101, Now.AddMinutes(-5));
        Assert.Equal([2u], Choose(App.Game, [old, fresh]));

        // The server registry has only its own depot; identical manifest ids there would be a coincidence, not a trailing DLC.
        var serverOld = Build(3, "v1.2.11", 200, Now.AddDays(-5), dlcManifest: "same", published: "1.2.11.200");
        var serverFresh = Build(4, "v1.2.12", 201, Now.AddMinutes(-5), dlcManifest: "same");
        Assert.False(GenerateCommand.DlcMayBeTrailing(serverFresh, [serverOld, serverFresh], App.Server, Now));
    }
}
