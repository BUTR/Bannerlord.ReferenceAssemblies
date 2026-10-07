using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>How GOG's builds are read and which Steam builds they are paired with.</summary>
public sealed class GogRegistryTests
{
    private static GogBuildEntry Gog(string id, string label, string date = "2026-08-05T14:14:38Z")
    {
        var entry = new GogBuildEntry { BuildId = id, Label = label, Date = DateTimeOffset.Parse(date), MetaId = "00000000" };
        entry.ParseLabel();
        return entry;
    }

    private static BuildEntry Steam(uint id, string? version, int? changeSet, string branch = "public") => new()
    {
        BuildId = id,
        Date = DateTimeOffset.Parse("2026-08-05T10:00:00Z").AddSeconds(id),
        Version = version,
        ChangeSet = changeSet,
        Branches = [branch],
    };

    [Theory]
    [InlineData("1.4.8.119303", "1.4.8", 119303)]
    [InlineData("e1.6.2.284832", "1.6.2", 284832)]
    [InlineData("1.8.1.1942", "1.8.1", 1942)]
    [InlineData("test", null, null)]
    [InlineData("DLC.Test.02", null, null)]
    [InlineData("1.4.8", null, null)]
    public void The_label_carries_the_version_and_the_changeset(string label, string? version, int? changeSet)
    {
        var entry = Gog("1", label);
        Assert.Equal((version, changeSet), (entry.Version, entry.ChangeSet));
    }

    [Theory]
    [InlineData("2026-08-05T14:14:38+0000")]
    [InlineData("2026-08-05T14:14:38+00:00")]
    [InlineData("2026-08-05T16:14:38+0200")]
    public void Both_date_styles_read_as_the_same_moment(string value) =>
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 14, 14, 38, TimeSpan.Zero), GogClient.ParseDate(value));

    [Fact]
    public void The_manifest_is_asked_of_cdn_gog_com_whatever_host_the_link_names()
    {
        var metaId = GogClient.MetaIdOf("https://gog-cdn-lumen.secure2.footprint.net/content-system/v2/meta/62/32/6232995efcc40503f2b5faffad93a352");
        Assert.Equal("https://cdn.gog.com/content-system/v2/meta/62/32/6232995efcc40503f2b5faffad93a352", GogClient.MetaUrl(metaId));
    }

    [Fact]
    public void Builds_with_the_same_version_and_changeset_are_linked_and_every_Steam_copy_is_listed()
    {
        var steam = new BuildRegistry { AppId = 261550, Builds = { Steam(1, "v1.4.7", 117484), Steam(2, "v1.4.7", 117484, "perf_test"), Steam(3, "v1.4.8", 119303) } };
        var gog = new GogRegistry { ProductId = GogRegistry.GameProductId, Builds = { Gog("a", "1.4.7.117484"), Gog("b", "1.4.8.119303") } };

        var links = BuildLinks.Compute(steam, gog);

        Assert.Equal(2, links.Builds.Count);
        Assert.Equal(new BuildLink("v1.4.7", 117484, [1, 2], ["a"], Verified: false), links.Builds[0], LinkComparer);
        Assert.Equal(new BuildLink("v1.4.8", 119303, [3], ["b"], Verified: false), links.Builds[1], LinkComparer);
    }

    [Fact]
    public void A_link_is_verified_only_when_every_GOG_build_in_it_was_read_from_its_files()
    {
        var steam = new BuildRegistry { AppId = 261550, Builds = { Steam(1, "v1.4.8", 119303), Steam(2, "v1.4.9", 120000) } };
        var gog = new GogRegistry
        {
            ProductId = GogRegistry.GameProductId,
            Builds = { Gog("a", "1.4.8.119303"), Gog("b", "1.4.9.120000"), Gog("c", "1.4.9.120000") },
        };
        gog.Builds[0].ApplyRead("v1.4.8", 119303);
        gog.Builds[1].ApplyRead("v1.4.9", 120000);

        var links = BuildLinks.Compute(steam, gog);
        Assert.Equal([true, false], links.Builds.Select(x => x.Verified));
    }

    [Fact]
    public void Files_that_agree_with_the_label_verify_it_and_bring_the_letter()
    {
        var entry = Gog("a", "1.4.8.119303");
        entry.ApplyRead("v1.4.8", 119303);
        Assert.Equal(("v1.4.8", 119303, true, false), (entry.Version, entry.ChangeSet, entry.Verified, entry.LabelMismatch));
    }

    [Theory]
    [InlineData("1.3.4.101915", "v1.3.4", 101916)]
    [InlineData("1.3.4.101915", "v1.3.5", 101915)]
    [InlineData("test", "v1.0.0", 3539)]
    public void Files_that_disagree_with_the_label_win_and_are_flagged(string label, string version, int changeSet)
    {
        var entry = Gog("a", label);
        entry.ApplyRead(version, changeSet);
        Assert.Equal((version, changeSet, true, true), (entry.Version, entry.ChangeSet, entry.Verified, entry.LabelMismatch));
        Assert.Equal(label, entry.Label);
    }

    [Fact]
    public void A_renamed_build_forgets_its_check()
    {
        var registry = new GogRegistry { ProductId = GogRegistry.GameProductId, Builds = { Gog("a", "1.4.8.119303") } };
        registry.Builds[0].ApplyRead("v1.4.8", 119303);

        GogCommand.Merge(registry, [Gog("a", "1.4.8.119304")]);
        Assert.Equal(("1.4.8", 119304, false), (registry.Builds[0].Version, registry.Builds[0].ChangeSet, registry.Builds[0].Verified));
    }

    [Fact]
    public void A_shared_changeset_with_another_version_is_not_a_link()
    {
        // TaleWorlds restarted its changeset count in 2022, so a changeset alone can name two builds.
        var steam = new BuildRegistry { AppId = 261550, Builds = { Steam(1, "e1.6.0", 1942) } };
        var gog = new GogRegistry { ProductId = GogRegistry.GameProductId, Builds = { Gog("a", "1.8.1.1942") } };

        Assert.Empty(BuildLinks.Compute(steam, gog).Builds);
    }

    [Fact]
    public void Builds_on_one_store_only_and_labels_without_a_version_are_left_out()
    {
        var steam = new BuildRegistry { AppId = 261550, Builds = { Steam(1, "v1.3.15", 110062), Steam(2, null, null, "beta") } };
        var gog = new GogRegistry { ProductId = GogRegistry.GameProductId, Builds = { Gog("a", "1.3.15.109797"), Gog("b", "test") } };

        Assert.Empty(BuildLinks.Compute(steam, gog).Builds);
    }

    [Fact]
    public void Merging_adds_new_builds_and_keeps_known_ones_unless_renamed()
    {
        var registry = new GogRegistry { ProductId = GogRegistry.GameProductId, Builds = { Gog("a", "1.4.8.119303") } };
        registry.Builds[0].Depots = [new GogDepot("1564781494", "abc", 1)];

        GogCommand.Merge(registry, [Gog("a", "1.4.8.119303"), Gog("b", "1.4.9.120000")]);
        Assert.Equal(["a", "b"], registry.Builds.Select(x => x.BuildId));
        Assert.NotNull(registry.Builds[0].Depots);

        GogCommand.Merge(registry, [Gog("b", "1.4.9.120001")]);
        Assert.Equal(120001, registry.Find("b")!.ChangeSet);
    }

    private static readonly IEqualityComparer<BuildLink> LinkComparer = EqualityComparer<BuildLink>.Create(
        (x, y) => x!.Version == y!.Version && x.ChangeSet == y.ChangeSet && x.Steam.SequenceEqual(y.Steam) && x.Gog.SequenceEqual(y.Gog) && x.Verified == y.Verified,
        x => x.ChangeSet);
}
