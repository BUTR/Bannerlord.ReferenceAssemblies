using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>What the versions verb tells BUTR/.github, and when it says the versions may be announced.</summary>
public sealed class VersionsCommandTests
{
    private static BuildEntry Build(uint id, string version, int changeSet, bool published) => new()
    {
        BuildId = id,
        Version = version,
        ChangeSet = changeSet,
        Branches = ["public"],
        PublishedVersion = published ? $"{version[1..]}.{changeSet}" : null,
    };

    [Fact]
    public void Stable_is_the_public_tip_and_beta_the_beta_tip()
    {
        var registry = new BuildRegistry
        {
            Current = { ["public"] = 1, ["beta"] = 2 },
            Builds = { Build(1, "v1.4.8", 119303, published: true), Build(2, "v1.5.2", 121216, published: true) },
        };

        var versions = VersionsCommand.Compute(registry);
        Assert.Equal(("v1.4.8", "v1.5.2", true), (versions.Stable, versions.Beta, versions.Published));
    }

    [Fact]
    public void Without_a_beta_branch_beta_is_stable()
    {
        var registry = new BuildRegistry
        {
            Current = { ["public"] = 1 },
            Builds = { Build(1, "v1.4.8", 119303, published: true), Build(2, "v1.5.2", 121216, published: true) },
        };

        var versions = VersionsCommand.Compute(registry);
        Assert.Equal(("v1.4.8", "v1.4.8", 1u, true), (versions.Stable, versions.Beta, versions.BetaBuildId, versions.Published));
    }

    [Fact]
    public void Not_published_until_both_tips_are_on_the_feed()
    {
        var registry = new BuildRegistry
        {
            Current = { ["public"] = 1, ["beta"] = 2 },
            Builds = { Build(1, "v1.4.8", 119303, published: true), Build(2, "v1.5.2", 121216, published: false) },
        };

        Assert.False(VersionsCommand.Compute(registry).Published);
    }

    [Fact]
    public void A_tip_whose_version_is_not_read_yet_is_not_published_either()
    {
        var registry = new BuildRegistry
        {
            Current = { ["public"] = 1 },
            Builds = { new BuildEntry { BuildId = 1, Branches = ["public"] } },
        };

        var versions = VersionsCommand.Compute(registry);
        Assert.Null(versions.Stable);
        Assert.False(versions.Published);
    }

    [Fact]
    public void An_empty_registry_reports_nothing()
    {
        var versions = VersionsCommand.Compute(new BuildRegistry());
        Assert.Equal((null, null, false), (versions.Stable, versions.Beta, versions.Published));
    }
}
