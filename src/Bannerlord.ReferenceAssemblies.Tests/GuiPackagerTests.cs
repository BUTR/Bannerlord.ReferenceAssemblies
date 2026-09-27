using NuGet.Packaging;

using System.IO.Compression;
using System.Text.Json;

using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>
/// The GUI packages of a fake build: two game modules, and a DLC module that ships a prefab with a base
/// prefab's file name under another subfolder, downloaded to a folder of its own and copied over the game.
/// </summary>
public sealed class GuiPackagerTests : IDisposable
{
    private const string BasePackage = "Bannerlord.ReferenceAssemblies.GUI.v1";
    private const string DlcPackage = "Bannerlord.ReferenceAssemblies.GUI.v1.NavalDLC";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gui-packager-{Guid.NewGuid():N}");
    private string Game => Path.Combine(_root, "depots", "24573425");
    private string Dlc => Path.Combine(_root, "depots", "24573425.dlc", "2927200");

    public GuiPackagerTests()
    {
        Module(Game, "Native", """<Module><Id value="Native"/><Version value="v1.4.8"/><ModuleType value="Official"/><DependedModules/></Module>""");
        File(Game, "Modules/Native/GUI/Prefabs/Options/ExposureOptionsList.xml", "<Prefab>base</Prefab>");
        File(Game, "Modules/Native/GUI/Brushes/Native.xml", "<Brushes/>");
        File(Game, "Modules/Native/GUI/NativeSpriteData.xml", SpriteData("ui_fonts", alwaysLoad: true, "ui_order"));
        File(Game, "Modules/Native/GUI/Fonts/NativeLanguages.xml", "<Languages DefaultLanguage=\"English\"/>");
        File(Game, "Modules/Native/GUI/Fonts/Nested/Other.xml", "<Other/>"); // not packed: only the Fonts folder's own XML is
        File(Game, "GUI/GauntletUI/Fonts/Galahad/Galahad.fnt", "info face=Galahad");
        File(Game, "GUI/GauntletUI/Fonts/FiraSans/FiraSans.fnt", "info face=FiraSans");
        File(Game, "Modules/Native/ModuleData/sound_event_data.gen.xml",
            "<base><events><event path=\"event:/ui/tab\" /><event path=\"event:/Extra/intro\" /><event path=\"event:/ui/checkbox\" />"
            + "<event path=\"event:/ui/panels/panel_clan_open\" /><event path=\"event:/mission/combat/hit\" /></events></base>");

        Module(Game, "SandBox", """
            <Module><Id value="Sandbox"/><Version value="v1.4.8"/><ModuleType value="Official"/>
              <DependedModules><DependedModule Id="Native" DependentVersion="v1.4.8" Optional="false"/></DependedModules>
            </Module>
            """);
        File(Game, "Modules/SandBox/GUI/Prefabs/Clan/ClanScreen.xml", "<Prefab>clan</Prefab>");

        Module(Dlc, "NavalDLC", """
            <Module><Id value="NavalDLC"/><Version value="v1.2.8"/><RequiredBaseVersion value="v1.4.8"/><ModuleType value="OfficialOptional"/>
              <DependedModules><DependedModule Id="Sandbox" DependentVersion="v1.4.8" Optional="false"/></DependedModules>
            </Module>
            """);
        File(Dlc, "Modules/NavalDLC/GUI/Prefabs/Options/Naval/ExposureOptionsList.xml", "<Prefab>naval</Prefab>");
        File(Dlc, "Modules/NavalDLC/GUI/NavalDLCSpriteData.xml", SpriteData("ui_naval_common", alwaysLoad: true));
        File(Dlc, $"{SteamClient.ContentDownloaderConfigDir}/depot.config", "state");
        SteamClient.InstallDlc(Dlc, Game);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private static void Module(string folder, string name, string subModule) => File(folder, $"Modules/{name}/SubModule.xml", subModule);

    /// <summary>Sprite data in the game's shape: the first category may be marked AlwaysLoad.</summary>
    private static string SpriteData(string first, bool alwaysLoad, params string[] others) =>
        $"<SpriteData><SpriteCategories><SpriteCategory><Name>{first}</Name>{(alwaysLoad ? "<AlwaysLoad />" : "")}</SpriteCategory>"
        + string.Concat(others.Select(x => $"<SpriteCategory><Name>{x}</Name></SpriteCategory>"))
        + "</SpriteCategories><SpriteParts/></SpriteData>";

    private static void File(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    private static PackageSpec Spec(string version = "v1.4.8") => App.Game.ForBuild(new BuildEntry
    {
        BuildId = 24573425,
        Version = version,
        ChangeSet = 119303,
        Branches = ["public"],
        ModuleVersions = new Dictionary<string, string> { ["Native"] = "v1.4.8", ["SandBox"] = "v1.4.8", ["NavalDLC"] = "v1.2.8" },
    });

    private IReadOnlyList<string> Pack(string work = "work", string version = "v1.4.8") =>
        new GuiPackager(new Paths(Path.Combine(_root, work))).Pack(Game, [Dlc], Spec(version));

    private static Dictionary<string, byte[]> Entries(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        return zip.Entries.Where(x => !x.FullName.EndsWith('/')).ToDictionary(x => x.FullName, x =>
        {
            using var stream = x.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        });
    }

    private static string PackageOf(IReadOnlyList<string> packages, string id) =>
        Assert.Single(packages, x => Path.GetFileName(x) == $"{id}.1.4.8.119303.nupkg");

    [Fact]
    public void The_base_package_carries_the_game_modules_XML_byte_for_byte_and_nothing_of_the_DLC()
    {
        var entries = Entries(PackageOf(Pack(), BasePackage));
        var gui = entries.Keys.Where(x => x.StartsWith("gui/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        Assert.Equal(
        [
            "gui/Native/GUI/Brushes/Native.xml",
            "gui/Native/GUI/Fonts/NativeLanguages.xml",
            "gui/Native/GUI/NativeSpriteData.xml",
            "gui/Native/GUI/Prefabs/Options/ExposureOptionsList.xml",
            "gui/SandBox/GUI/Prefabs/Clan/ClanScreen.xml",
            "gui/fonts.json",
            "gui/manifest.json",
            "gui/movies.json",
            "gui/spriteCategories.json",
            "gui/types.json",
            "gui/uiSounds.json",
        ], gui);
        Assert.Equal(System.IO.File.ReadAllBytes(Path.Combine(Game, "Modules/Native/GUI/Prefabs/Options/ExposureOptionsList.xml")), entries["gui/Native/GUI/Prefabs/Options/ExposureOptionsList.xml"]);
        Assert.Equal(System.IO.File.ReadAllBytes(Path.Combine(Game, "Modules/Native/GUI/NativeSpriteData.xml")), entries["gui/Native/GUI/NativeSpriteData.xml"]);
        Assert.DoesNotContain(entries.Keys, x => x.StartsWith("lib/", StringComparison.Ordinal) || x.StartsWith("ref/", StringComparison.Ordinal));
        Assert.Equal(GuiPackager.Props(BasePackage), System.Text.Encoding.UTF8.GetString(entries[$"build/{BasePackage}.props"]));
    }

    [Fact]
    public void The_DLC_package_carries_the_DLC_module_only()
    {
        var entries = Entries(PackageOf(Pack(), DlcPackage));
        Assert.Equal("<Prefab>naval</Prefab>", System.Text.Encoding.UTF8.GetString(entries["gui/NavalDLC/GUI/Prefabs/Options/Naval/ExposureOptionsList.xml"]));
        Assert.DoesNotContain(entries.Keys, x => x.StartsWith("gui/Native/", StringComparison.Ordinal) || x.StartsWith("gui/SandBox/", StringComparison.Ordinal));
        Assert.Contains($"build/{DlcPackage}.props", entries.Keys);
        Assert.Equal(System.IO.File.ReadAllBytes(Path.Combine(Dlc, "Modules/NavalDLC/GUI/NavalDLCSpriteData.xml")), entries["gui/NavalDLC/GUI/NavalDLCSpriteData.xml"]);

        using var manifest = JsonDocument.Parse(entries["gui/manifest.json"]);
        var module = Assert.Single(manifest.RootElement.GetProperty("modules").EnumerateArray());
        Assert.Equal("NavalDLC", module.GetProperty("folder").GetString());
        Assert.Equal("v1.2.8", module.GetProperty("version").GetString());
        Assert.True(module.GetProperty("dlc").GetBoolean());
    }

    [Fact]
    public void The_base_manifest_lists_the_modules_in_load_order_as_their_manifests_write_them()
    {
        using var manifest = JsonDocument.Parse(Entries(PackageOf(Pack(), BasePackage))["gui/manifest.json"]);
        var root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(BasePackage, root.GetProperty("package").GetString());
        Assert.Equal("v1.4.8", root.GetProperty("gameVersion").GetString());
        Assert.Equal(119303, root.GetProperty("changeSet").GetInt32());
        Assert.Equal(24573425u, root.GetProperty("buildId").GetUInt32());

        var modules = root.GetProperty("modules").EnumerateArray().ToList();
        Assert.Equal(["Native", "SandBox"], modules.Select(x => x.GetProperty("folder").GetString()));
        Assert.Equal("Sandbox", modules[1].GetProperty("id").GetString());
        Assert.All(modules, x => Assert.False(x.GetProperty("dlc").GetBoolean()));

        var dependency = Assert.Single(modules[1].GetProperty("dependedModules").EnumerateArray());
        Assert.Equal(["Id", "DependentVersion", "Optional"], dependency.EnumerateObject().Select(x => x.Name));
        Assert.Equal("false", dependency.GetProperty("Optional").GetString());
    }

    [Fact]
    public void Both_packages_take_the_game_build_version_and_the_DLC_its_own_version_only_in_a_tag()
    {
        var packages = Pack();
        using var baseReader = new PackageArchiveReader(PackageOf(packages, BasePackage));
        using var dlcReader = new PackageArchiveReader(PackageOf(packages, DlcPackage));

        Assert.Equal("1.4.8.119303", baseReader.NuspecReader.GetVersion().ToNormalizedString());
        Assert.Equal("1.4.8.119303", dlcReader.NuspecReader.GetVersion().ToNormalizedString());
        Assert.Contains("moduleVersion:v1.4.8", baseReader.NuspecReader.GetTags().Split(' '));
        Assert.Contains("moduleVersion:v1.2.8", dlcReader.NuspecReader.GetTags().Split(' '));
        foreach (var reader in new[] { baseReader, dlcReader })
        {
            var tags = reader.NuspecReader.GetTags().Split(' ');
            Assert.Contains("buildId:24573425", tags);
            Assert.Contains("appId:261550", tags);
            Assert.True(reader.NuspecReader.GetDevelopmentDependency());
            Assert.Equal("MIT", reader.NuspecReader.GetLicenseMetadata()?.License);
            Assert.Empty(reader.NuspecReader.GetDependencyGroups());
        }
    }

    [Fact]
    public void An_early_access_build_packs_under_the_EarlyAccess_ids()
    {
        var packages = Pack(version: "e1.4.8");
        Assert.Equal(
            ["Bannerlord.ReferenceAssemblies.GUI.v1.EarlyAccess.1.4.8.119303.nupkg", "Bannerlord.ReferenceAssemblies.GUI.v1.NavalDLC.EarlyAccess.1.4.8.119303.nupkg"],
            packages.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var entries = Entries(packages[0]);
        Assert.Contains("build/Bannerlord.ReferenceAssemblies.GUI.v1.EarlyAccess.props", entries.Keys);
    }

    [Fact]
    public void Packing_the_same_build_twice_gives_the_same_gui_content()
    {
        var first = Pack("first");
        var second = Pack("second");
        foreach (var id in new[] { BasePackage, DlcPackage })
        {
            var a = Entries(PackageOf(first, id)).Where(x => x.Key.StartsWith("gui/", StringComparison.Ordinal)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            var b = Entries(PackageOf(second, id)).Where(x => x.Key.StartsWith("gui/", StringComparison.Ordinal)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            Assert.Equal(a.Select(x => x.Key), b.Select(x => x.Key));
            Assert.All(a.Zip(b), x => Assert.Equal(x.First.Value, x.Second.Value));
        }
    }

    [Fact]
    public void A_DLC_file_under_a_base_module_stops_the_packing()
    {
        File(Dlc, "Modules/Native/GUI/Prefabs/Options/ExposureOptionsList.xml", "<Prefab>patched</Prefab>");
        var error = Assert.Throws<InvalidDataException>(() => GuiPackager.FindDlcModules([Dlc]));
        Assert.Contains("Modules/Native/GUI/Prefabs/Options/ExposureOptionsList.xml", error.Message);
    }

    [Fact]
    public void Installing_a_DLC_copies_its_files_over_the_game_but_not_DepotDownloaders_state()
    {
        Assert.True(System.IO.File.Exists(Path.Combine(Game, "Modules/NavalDLC/SubModule.xml")));
        Assert.True(System.IO.File.Exists(Path.Combine(Game, "Modules/NavalDLC/GUI/Prefabs/Options/Naval/ExposureOptionsList.xml")));
        Assert.False(Directory.Exists(Path.Combine(Game, SteamClient.ContentDownloaderConfigDir)));
    }

    [Fact]
    public void Every_JSON_file_is_format_version_1()
    {
        foreach (var package in Pack())
        foreach (var (name, bytes) in Entries(package).Where(x => x.Key.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var json = JsonDocument.Parse(bytes);
            Assert.True(json.RootElement.GetProperty("formatVersion").GetInt32() == 1, $"{Path.GetFileName(package)}: {name}");
        }
    }

    [Fact]
    public void Categories_the_sprite_data_always_loads_are_listed_in_the_package_of_their_module()
    {
        var packages = Pack();
        string Always(string id)
        {
            using var json = JsonDocument.Parse(Entries(PackageOf(packages, id))["gui/spriteCategories.json"]);
            return string.Join(",", json.RootElement.GetProperty("always").EnumerateArray().Select(x => $"{x.GetProperty("category").GetString()}@{x.GetProperty("module").GetString()}"));
        }
        Assert.Equal("ui_fonts@Native", Always(BasePackage));
        Assert.Equal("ui_naval_common@NavalDLC", Always(DlcPackage));
    }

    [Fact]
    public void The_fonts_are_the_font_files_the_game_loads_and_the_languages_XML_is_packed_as_is()
    {
        var packages = Pack();
        var entries = Entries(PackageOf(packages, BasePackage));
        Assert.Equal(System.IO.File.ReadAllBytes(Path.Combine(Game, "Modules/Native/GUI/Fonts/NativeLanguages.xml")), entries["gui/Native/GUI/Fonts/NativeLanguages.xml"]);
        Assert.DoesNotContain(entries.Keys, x => x.EndsWith(".fnt", StringComparison.Ordinal));

        using var fonts = JsonDocument.Parse(entries["gui/fonts.json"]);
        Assert.Equal(["FiraSans", "Galahad"], fonts.RootElement.GetProperty("fonts").EnumerateArray().Select(x => x.GetString()));

        // The engine's fonts are the base package's; the DLC has none of its own.
        using var dlcFonts = JsonDocument.Parse(Entries(PackageOf(packages, DlcPackage))["gui/fonts.json"]);
        Assert.Empty(dlcFonts.RootElement.GetProperty("fonts").EnumerateArray());
    }

    [Fact]
    public void The_UI_sounds_are_the_ui_events_without_their_prefix_and_the_sound_data_is_not_packed()
    {
        var entries = Entries(PackageOf(Pack(), BasePackage));
        Assert.DoesNotContain(entries.Keys, x => x.Contains("sound_event_data", StringComparison.Ordinal));

        using var sounds = JsonDocument.Parse(entries["gui/uiSounds.json"]);
        Assert.Equal("event:/ui/", sounds.RootElement.GetProperty("prefix").GetString());
        Assert.Equal(["checkbox", "panels/panel_clan_open", "tab"], sounds.RootElement.GetProperty("sounds").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["Native/ModuleData/sound_event_data.gen.xml"], sounds.RootElement.GetProperty("sources").EnumerateArray().Select(x => x.GetString()));
    }
}
