using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>
/// How a build becomes package ids and a package version. The game line keeps the ids it has always had;
/// the dedicated server is a sibling family under .Server, with the same release-kind suffix at the end.
/// </summary>
public sealed class PackageNamingTests
{
    [Theory]
    [InlineData("game", null, "", "Bannerlord.ReferenceAssemblies")]
    [InlineData("game", "Core", "", "Bannerlord.ReferenceAssemblies.Core")]
    [InlineData("game", "Native", ".EarlyAccess", "Bannerlord.ReferenceAssemblies.Native.EarlyAccess")]
    [InlineData("server", null, "", "Bannerlord.ReferenceAssemblies.Server")]
    [InlineData("server", "Core", "", "Bannerlord.ReferenceAssemblies.Server.Core")]
    [InlineData("server", "Multiplayer", ".EarlyAccess", "Bannerlord.ReferenceAssemblies.Server.Multiplayer.EarlyAccess")]
    [InlineData("moddingkit", "Core", "", "Bannerlord.ReferenceAssemblies.ModdingKit.Core")]
    [InlineData("moddingkit", "SandBox", ".EarlyAccess", "Bannerlord.ReferenceAssemblies.ModdingKit.SandBox.EarlyAccess")]
    public void PackageId_places_the_module_before_the_release_kind(string app, string? module, string suffix, string expected) =>
        Assert.Equal(expected, App.Parse(app).PackageId(module, suffix));

    [Theory]
    [InlineData("v1.3.7", "")]
    [InlineData("e1.7.2", ".EarlyAccess")]
    public void PackageSuffix_follows_the_version_letter(string version, string expected) =>
        Assert.Equal(expected, new BuildEntry { Version = version }.PackageSuffix);

    [Theory]
    [InlineData(null)]
    [InlineData("b0.8.7")]   // the pre-launch baseline build
    [InlineData("i1.0.0")]
    public void Other_version_letters_are_not_packaged(string? version) =>
        Assert.False(new BuildEntry { Version = version }.CanBeGenerated);

    [Fact]
    public void PackageVersion_drops_the_letter_and_appends_the_changeset() =>
        Assert.Equal("1.3.7.102919", new BuildEntry { Version = "v1.3.7", ChangeSet = 102919, Branches = ["public", "v1.3.7"] }.PackageVersion);

    [Fact]
    public void PackageVersion_uses_the_build_id_when_the_binaries_carry_no_changeset() =>
        Assert.Equal("1.0.1.4842596", new BuildEntry { BuildId = 4842596, Version = "e1.0.1", Branches = ["public"] }.PackageVersion);

    [Fact]
    public void A_build_only_ever_seen_in_beta_is_a_prerelease() =>
        Assert.Equal("1.5.2.121216-beta", new BuildEntry { Version = "v1.5.2", ChangeSet = 121216, Branches = ["beta"] }.PackageVersion);

    [Fact]
    public void A_beta_build_that_later_went_public_is_not() =>
        Assert.Equal("1.5.2.121216", new BuildEntry { Version = "v1.5.2", ChangeSet = 121216, Branches = ["beta", "public"] }.PackageVersion);

    [Fact]
    public void IsPublished_compares_the_recorded_version_with_the_current_one()
    {
        var build = new BuildEntry { Version = "v1.3.7", ChangeSet = 102919, Branches = ["public"], PublishedVersion = "1.3.7.102919" };
        Assert.True(build.IsPublished);

        // A corrected version means the package on the feed is mislabelled, so the build counts as missing again.
        build.Version = "v1.3.8";
        Assert.False(build.IsPublished);
    }

    [Fact]
    public void Each_package_is_tagged_with_its_module_own_version_and_Core_and_meta_with_the_game_version()
    {
        // Game build 24573425: the official modules say v1.4.8 like the game, War Sails says v1.2.8.
        var build = new BuildEntry { BuildId = 24573425, Version = "v1.4.8", ChangeSet = 119303, Branches = ["public"], ModuleVersions = new() { ["SandBox"] = "v1.4.8", ["NavalDLC"] = "v1.2.8" } };
        var spec = App.Game.ForBuild(build);
        Assert.Equal("moduleVersion:v1.2.8", spec.ModuleVersionTag("NavalDLC"));
        Assert.Equal("moduleVersion:v1.4.8", spec.ModuleVersionTag("SandBox"));
        Assert.Equal("moduleVersion:v1.4.8", spec.ModuleVersionTag("Core"));
        Assert.Equal("moduleVersion:v1.4.8", spec.ModuleVersionTag(null));
        Assert.Contains("buildId:24573425", spec.Tags);
    }

    [Fact]
    public void App_names_are_case_insensitive_and_unknown_ones_are_rejected()
    {
        Assert.Same(App.Server, App.Parse("Server"));
        Assert.Throws<ArgumentException>(() => App.Parse("launcher"));
    }

    [Theory]
    [InlineData("", "Bannerlord.ReferenceAssemblies.GUI.v3", "Bannerlord.ReferenceAssemblies.GUI.v3.NavalDLC")]
    [InlineData(".EarlyAccess", "Bannerlord.ReferenceAssemblies.GUI.v3.EarlyAccess", "Bannerlord.ReferenceAssemblies.GUI.v3.NavalDLC.EarlyAccess")]
    public void GUI_package_ids_carry_the_format_version_and_put_a_DLC_module_after_it(string suffix, string expectedBase, string expectedDlc)
    {
        Assert.Equal(expectedBase, App.Game.PackageId(GuiPackager.BaseModule, suffix));
        Assert.Equal(expectedDlc, App.Game.PackageId($"{GuiPackager.BaseModule}.NavalDLC", suffix));
    }

    [Fact]
    public void Only_the_game_packs_GUI_packages()
    {
        Assert.True(App.Game.PacksGui);
        Assert.False(App.Server.PacksGui);
        Assert.False(App.ModdingKit.PacksGui);
        Assert.Contains(App.Game.PackageFileFilters, x => x.IsMatch("Modules/NavalDLC/GUI/Prefabs/Clan/ClanScreen.xml"));
        Assert.DoesNotContain(App.Server.PackageFileFilters, x => x.IsMatch("Modules/Native/GUI/Prefabs/Clan/ClanScreen.xml"));
    }

    [Theory]
    [InlineData("Modules/Native/GUI/Prefabs/Options/ExposureOptionsList.xml", true)]
    [InlineData("Modules/Native/GUI/Brushes/Native.xml", true)]
    [InlineData("Modules/Native/GUI/NativeSpriteData.xml", true)]
    [InlineData("Modules/Native/GUI/Prefabs/NativeSpriteData.xml", true)]
    [InlineData("Modules/Native/GUI/Fonts/NativeLanguages.xml", false)]
    [InlineData("Modules/Native/GUI/Fonts/Galahad/Galahad.xml", false)]
    [InlineData("Modules/Native/ModuleData/items.xml", false)]
    public void The_GUI_packages_are_written_from_prefab_brush_and_sprite_data_XML_only(string path, bool packed) =>
        Assert.Equal(packed, App.GuiFileFilter.IsMatch(path));

    [Fact]
    public void The_GUI_marker_is_stored_under_the_name_of_the_current_format()
    {
        // A new format starts every build over, so a bump of FormatVersion needs a new marker name.
        Assert.Equal($"publishedGuiV{GuiPackager.FormatVersion}Version", BuildEntry.PublishedGuiVersionName);

        var registry = Path.Combine(Path.GetTempPath(), $"registry-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(registry, """
                { "appId": 261550, "builds": [ { "buildId": 1, "version": "v1.4.8", "changeSet": 119303, "branches": ["public"], "publishedGuiVersion": "1.4.8.119303" } ] }
                """);
            var loaded = BuildRegistry.Load(registry, App.Game);
            Assert.Null(loaded.Find(1)!.PublishedGuiVersion);

            loaded.Find(1)!.PublishedGuiVersion = "1.4.8.119303";
            loaded.Save();
            var saved = File.ReadAllText(registry);
            Assert.Contains("\"publishedGuiV3Version\": \"1.4.8.119303\"", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("\"publishedGuiVersion\"", saved, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(registry);
        }
    }

    [Fact]
    public void The_GUI_marker_is_kept_apart_from_the_reference_one()
    {
        var build = new BuildEntry { Version = "v1.4.8", ChangeSet = 119303, Branches = ["public"], PublishedVersion = "1.4.8.119303" };
        Assert.True(build.IsPublishedAs(PackageKind.Reference));
        Assert.False(build.IsPublishedAs(PackageKind.Gui));

        build.PublishedGuiVersion = "1.4.8.119303";
        Assert.True(build.IsPublishedAs(PackageKind.Gui));
    }
}
