using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>
/// GUI.v3.All from fake format 3 packages: two versions of the base package, written indented as the real ones
/// are, and a DLC package of the second. What they share is stored once, and every file rebuilds as it was.
/// </summary>
public sealed class GuiBundleTests
{
    private const string BaseId = "Bannerlord.ReferenceAssemblies.GUI.v3";
    private const string DlcId = "Bannerlord.ReferenceAssemblies.GUI.v3.NavalDLC";

    private static readonly string SharedTree = Tree("Shared", "Native", """
        {"n":"Prefab","c":[{"n":"Window","c":[{"n":"Widget","a":{"Id":"Root","Text":"<b>&é</b>"},"c":["some text",{"n":"Children"}]}]}]}
        """);

    [Fact]
    public void Every_file_of_every_package_rebuilds_as_it_was()
    {
        var sources = Sources();
        var content = GuiBundle.Build(sources, BaseId);
        GuiBundleChecks.Run(content, sources, BaseId);

        var reader = new GuiBundleReader(content.Files);
        var packages = reader.Packages.ToList();
        Assert.Equal(["v1.2.9 " + BaseId, "v1.2.10 " + BaseId, "v1.2.10 " + DlcId], packages.Select(x => $"{x.GameVersion} {x.Package["packageId"]}"));
        foreach (var (source, (_, package)) in GuiBundle.Order(sources, BaseId).SelectMany(x => x).Zip(packages))
        {
            var rebuilt = reader.Rebuild(package);
            var original = source.ReadFiles();
            Assert.Equal(original.Keys.Order(StringComparer.Ordinal), rebuilt.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in original)
                Assert.Equal(GuiBundle.Normalize(bytes), rebuilt[path]);
        }
    }

    [Fact]
    public void A_record_both_versions_have_is_stored_once_and_a_changed_one_twice()
    {
        var content = GuiBundle.Build(Sources(), BaseId);
        var widgets = Lines(content, "records.types.widgets.jsonl");
        // TextWidget is the same in both versions; ButtonWidget gained an event in v1.2.10; the DLC's is its own.
        Assert.Equal(4, widgets.Length);
        Assert.Single(widgets, x => x.Contains("TextWidget"));
        Assert.Equal(2, widgets.Count(x => x.Contains("ButtonWidget")));
    }

    [Fact]
    public void Arrays_become_tables_and_everything_else_stays_inline_in_published_order()
    {
        var content = GuiBundle.Build(Sources(), BaseId);
        var package = new GuiBundleReader(content.Files).Packages.First().Package;
        var manifest = package["files"]!.AsArray().Single(x => x!["path"]!.GetValue<string>() == "manifest.json")!;
        var properties = manifest["properties"]!.AsArray().Select(x => x!).ToList();

        Assert.Equal(["formatVersion", "package", "gameVersion", "changeSet", "buildId", "modules"], properties.Select(x => x["name"]!.GetValue<string>()));
        Assert.Equal(GuiPackager.FormatVersion, properties[0]["value"]!.GetValue<int>());
        Assert.Null(properties[3]["value"]);   // changeSet: null stays null
        Assert.Equal("records.manifest.modules.jsonl", properties[5]["table"]!.GetValue<string>());
        Assert.Equal("[[0,0]]", properties[5]["runs"]!.ToJsonString());
    }

    [Fact]
    public void An_empty_array_is_inline_and_gets_no_table()
    {
        var content = GuiBundle.Build(Sources(), BaseId);
        Assert.DoesNotContain("records.fonts.sources.jsonl", content.Files.Keys);
        var fonts = new GuiBundleReader(content.Files).Packages.First().Package["files"]!.AsArray()
            .Single(x => x!["path"]!.GetValue<string>() == "fonts.json")!;
        Assert.Equal("[]", fonts["properties"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "sources")!["value"]!.ToJsonString());
    }

    [Fact]
    public void An_equal_subtree_is_one_node_and_a_changed_attribute_changes_only_its_node_and_parents()
    {
        var content = GuiBundle.Build(Sources(), BaseId);
        var nodes = Lines(content, GuiBundle.NodesFile);

        // Shared is in both versions and in the DLC package, stored once: Children, Widget, Window, Prefab. Options
        // is five nodes in v1.2.9. In v1.2.10 one attribute of its button differs, which makes a new button,
        // ListPanel, Window and Prefab; the unchanged TextWidget beside the button is reused.
        Assert.Equal(4 + 5 + 4, nodes.Length);
        Assert.Single(nodes, x => x == """{"n":"Children"}""");

        // Children first, text as strings, and < & é as they are.
        Assert.Equal("""{"n":"Widget","a":{"Id":"Root","Text":"<b>&é</b>"},"c":["some text",0]}""", nodes[1]);
        var reader = new GuiBundleReader(content.Files);
        Assert.All(reader.Nodes().Select((node, i) => (node, i)), x => Assert.All(x.node.Children ?? Array.Empty<object>(), child => Assert.True(child is string || (int) child < x.i)));
    }

    [Fact]
    public void Text_is_written_unescaped()
    {
        var content = GuiBundle.Build(Sources(), BaseId);
        var widgets = Encoding.UTF8.GetString(content.Files["records.types.widgets.jsonl"]);
        Assert.Contains("<Text> & ünïcode", widgets);
        Assert.DoesNotContain("\\u", widgets);
    }

    [Fact]
    public void Consecutive_lines_are_one_run()
    {
        Assert.Equal("[[0,2],[5,6],[9,9]]", GuiBundle.Runs([0, 1, 2, 5, 6, 9]).ToJsonString());
        Assert.Equal("[]", GuiBundle.Runs([]).ToJsonString());
    }

    [Fact]
    public void The_same_packages_in_any_order_give_the_same_bytes()
    {
        var first = GuiBundle.Build(Sources(), BaseId);
        var second = GuiBundle.Build(Sources().AsEnumerable().Reverse(), BaseId);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.Files.Keys, second.Files.Keys);
        foreach (var (path, bytes) in first.Files)
            Assert.Equal(bytes, second.Files[path]);
    }

    [Fact]
    public void A_changed_record_changes_the_hash()
    {
        var sources = Sources();
        var changed = sources.Select(x => x.PackageId == DlcId ? x with { ReadFiles = () => With(x.ReadFiles(), "fonts.json", """{ "formatVersion": 3, "fonts": ["Other"], "sources": [] }""") } : x);
        Assert.NotEqual(GuiBundle.Build(sources, BaseId).ContentHash, GuiBundle.Build(changed, BaseId).ContentHash);
    }

    [Fact]
    public void A_bundle_that_does_not_rebuild_its_packages_fails_the_checks()
    {
        var sources = Sources();
        var content = GuiBundle.Build(sources, BaseId);
        var files = content.Files.ToDictionary(x => x.Key, x => x.Value);
        files["records.types.widgets.jsonl"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["records.types.widgets.jsonl"]).Replace("TextWidget", "TextWidgetX"));
        Assert.Throws<InvalidDataException>(() => GuiBundleChecks.Run(content with { Files = files }, sources, BaseId));
    }

    [Fact]
    public void A_line_no_package_uses_fails_the_checks()
    {
        var sources = Sources();
        var content = GuiBundle.Build(sources, BaseId);
        var files = content.Files.ToDictionary(x => x.Key, x => x.Value);
        files["records.types.enums.jsonl"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["records.types.enums.jsonl"]) + "\"unused\"\n");
        Assert.Throws<InvalidDataException>(() => GuiBundleChecks.Run(content with { Files = files }, sources, BaseId));
    }

    [Fact]
    public void A_tree_its_prefab_entry_does_not_describe_stops_the_build() =>
        Assert.Throws<InvalidDataException>(() => GuiBundle.Build(WithInBase("Native/GUI/Prefabs/Shared.json", Tree("Renamed", "Native", """{"n":"Prefab"}""")), BaseId));

    [Fact]
    public void A_file_below_gui_that_no_prefab_entry_lists_stops_the_build() =>
        Assert.Throws<InvalidDataException>(() => GuiBundle.Build(WithInBase("Native/GUI/Prefabs/Stray.json", Tree("Stray", "Native", """{"n":"Prefab"}""")), BaseId));

    [Fact]
    public void Game_versions_sort_by_their_numbers() =>
        Assert.Equal(["v1.2.9", "v1.2.10", "v1.10.0"], new[] { "v1.10.0", "v1.2.10", "v1.2.9" }.Order(GameVersionComparer.Instance));

    [Fact]
    public void The_props_list_the_json_and_jsonl_files_at_the_top_of_gui()
    {
        var props = GuiBundle.Props("Bannerlord.ReferenceAssemblies.GUI.v3.All");
        Assert.Contains("""Include="$(MSBuildThisFileDirectory)../gui/*.json;$(MSBuildThisFileDirectory)../gui/*.jsonl" """, props);
        Assert.Contains("""Package="Bannerlord.ReferenceAssemblies.GUI.v3.All" """, props);
        Assert.Equal("Bannerlord.ReferenceAssemblies.GUI.v3.All", App.Game.PackageId(GuiBundle.Module, ""));
    }

    [Theory]
    [InlineData(57, null, "2026.9.28.57")]
    [InlineData(null, null, "2026.9.28.0")]
    [InlineData(3, "beta", "2026.9.28.3-beta")]
    public void The_version_is_the_UTC_date_and_the_run_number(int? revision, string? prerelease, string expected) =>
        Assert.Equal(expected, BundleGuiCommand.Version(new DateTimeOffset(2026, 9, 28, 22, 30, 0, TimeSpan.Zero), revision, prerelease));

    [Fact]
    public void The_date_is_the_UTC_date() =>
        // 01:30 at UTC+3 is 22:30 the day before in UTC.
        Assert.Equal("2026.9.27.1", BundleGuiCommand.Version(new DateTimeOffset(2026, 9, 28, 1, 30, 0, TimeSpan.FromHours(3)), 1, null));

    [Fact]
    public void The_newest_build_with_a_GUI_package_of_each_release_version_is_chosen_oldest_version_first()
    {
        List<BuildEntry> builds =
        [
            Build(1, "v1.2.10", 200, ["public"]),
            Build(2, "v1.2.10", 210, ["public", "v1.2.10"]),
            Build(3, "v1.2.9", 100, ["public"]),
            Build(4, "v1.3.0", 300, ["beta"]),                 // a beta-only version counts
            Build(5, "e1.8.1", 50, ["public"]),                 // early access is left out
            Build(6, "v1.3.1", 310, ["perf_test"]),             // not a build modders build against
        ];
        Assert.Equal([3u, 2u, 4u], BundleGuiCommand.Choose(builds).Select(x => x.BuildId));
    }

    [Fact]
    public void A_newer_build_without_a_GUI_package_leaves_the_older_one_in_and_says_so()
    {
        List<BuildEntry> builds = [Build(1, "v1.4.8", 100, ["public"]), Build(2, "v1.4.8", 110, ["public"], gui: false)];
        var log = new List<string>();
        Assert.Equal([1u], BundleGuiCommand.Choose(builds, log.Add).Select(x => x.BuildId));
        Assert.Contains(log, x => x.Contains("has no GUI package yet") && x.Contains("1 stands in"));
    }

    [Fact]
    public void A_build_packing_to_a_published_package_version_is_skipped_without_a_word()
    {
        // v1.2.6: two builds, one changeset, one package version; the feed took it for the first.
        List<BuildEntry> builds =
        [
            Build(12771784, "v1.2.6", 30619, ["v1.2.0"], date: new DateTimeOffset(2023, 11, 22, 0, 0, 0, TimeSpan.Zero)),
            Build(12829474, "v1.2.6", 30619, ["v1.2.0"], gui: false, date: new DateTimeOffset(2023, 11, 30, 0, 0, 0, TimeSpan.Zero)),
        ];
        var log = new List<string>();
        Assert.Equal([12771784u], BundleGuiCommand.Choose(builds, log.Add).Select(x => x.BuildId));
        Assert.Empty(log);
    }

    [Fact]
    public void A_package_shared_by_two_builds_is_bundled_as_the_build_it_was_packed_from()
    {
        // mark-published --fromFeed marks both v1.2.6 builds, so the newer is chosen; the package came from the older.
        var older = Build(12771784, "v1.2.6", 30619, ["v1.2.0"], date: new DateTimeOffset(2023, 11, 22, 0, 0, 0, TimeSpan.Zero));
        var newer = Build(12829474, "v1.2.6", 30619, ["v1.2.0"], date: new DateTimeOffset(2023, 11, 30, 0, 0, 0, TimeSpan.Zero));
        List<BuildEntry> builds = [older, newer];

        Assert.Same(newer, BundleGuiCommand.Choose(builds).Single());
        Assert.Same(older, BundleGuiCommand.PackedFrom(builds, newer, 12771784, BaseId, "1.2.6.30619"));
        Assert.Same(newer, BundleGuiCommand.PackedFrom(builds, newer, 12829474, BaseId, "1.2.6.30619"));
    }

    [Fact]
    public void A_package_packed_from_a_build_of_another_package_version_is_refused()
    {
        List<BuildEntry> builds = [Build(1, "v1.2.6", 30619, ["public"]), Build(2, "v1.2.7", 31207, ["public"])];
        Assert.Throws<InvalidDataException>(() => BundleGuiCommand.PackedFrom(builds, builds[0], 2, BaseId, "1.2.6.30619"));
        Assert.Throws<InvalidDataException>(() => BundleGuiCommand.PackedFrom(builds, builds[0], 99, BaseId, "1.2.6.30619"));
    }

    [Fact]
    public void A_version_no_build_of_has_a_GUI_package_is_left_out()
    {
        List<BuildEntry> builds = [Build(1, "v1.5.0", 100, ["beta"], gui: false, unavailable: true)];
        var log = new List<string>();
        Assert.Empty(BundleGuiCommand.Choose(builds, log.Add));
        Assert.Contains(log, x => x.Contains("Steam no longer serves it") && x.Contains("left out"));
    }

    private static BuildEntry Build(uint buildId, string version, int changeSet, List<string> branches, bool gui = true, bool unavailable = false, DateTimeOffset? date = null)
    {
        var build = new BuildEntry { BuildId = buildId, Version = version, ChangeSet = changeSet, Branches = branches, Date = date ?? DateTimeOffset.UnixEpoch.AddDays(changeSet), ContentUnavailable = unavailable };
        if (gui)
            build.PublishedGuiVersion = build.PackageVersion;
        return build;
    }

    private static string[] Lines(GuiBundleContent content, string table) => new GuiBundleReader(content.Files).Lines(table);

    private static List<GuiBundleSource> Sources()
    {
        var older = Build(38256, "v1.2.9", 38256, ["public"]);
        var newer = Build(44489, "v1.2.10", 44489, ["public"]);
        return
        [
            Source(newer, DlcId, new()
            {
                ["manifest.json"] = Manifest(DlcId, newer, """[{ "folder": "NavalDLC", "dlc": true }]"""),
                ["fonts.json"] = """{ "formatVersion": 3, "fonts": [], "sources": [] }""",
                ["types.json"] = """{ "formatVersion": 3, "widgets": [{ "type": "ShipWidget", "events": [] }], "enums": [] }""",
                ["prefabs.json"] = Prefabs(("Shared", "NavalDLC", "NavalDLC/GUI/Prefabs/Shared.json")),
                ["NavalDLC/GUI/Prefabs/Shared.json"] = SharedTree.Replace("\"module\":\"Native\"", "\"module\":\"NavalDLC\""),
            }),
            Source(older, BaseId, Base(older, buttonEvents: "[]", optionsHeight: "10")),
            Source(newer, BaseId, Base(newer, buttonEvents: """["Click"]""", optionsHeight: "12")),
        ];

        static Dictionary<string, string> Base(BuildEntry build, string buttonEvents, string optionsHeight) => new()
        {
            ["manifest.json"] = Manifest(BaseId, build, """[{ "folder": "Native", "dlc": false }]"""),
            ["fonts.json"] = """{ "formatVersion": 3, "fonts": ["Galahad"], "sources": [] }""",
            ["types.json"] = $$"""
                {
                  "formatVersion": 3,
                  "widgets": [
                    { "type": "TextWidget", "doc": "<Text> & ünïcode", "events": [] },
                    { "type": "ButtonWidget", "events": {{buttonEvents}} }
                  ],
                  "enums": ["Kind"]
                }
                """,
            ["prefabs.json"] = Prefabs(("Shared", "Native", "Native/GUI/Prefabs/Shared.json"), ("Options", "Native", "Native/GUI/Prefabs/Options/Options.json")),
            ["Native/GUI/Prefabs/Shared.json"] = SharedTree,
            ["Native/GUI/Prefabs/Options/Options.json"] = Tree("Options", "Native", $$$"""
                {"n":"Prefab","c":[{"n":"Window","c":[{"n":"ListPanel","c":[{"n":"ButtonWidget","a":{"Height":"{{{optionsHeight}}}"}},{"n":"TextWidget","a":{"Text":"Same"}}]}]}]}
                """),
        };
    }

    private static string Manifest(string packageId, BuildEntry build, string modules) => $$"""
        {
          "formatVersion": 3,
          "package": "{{packageId}}",
          "gameVersion": "{{build.Version}}",
          "changeSet": null,
          "buildId": {{build.BuildId}},
          "modules": {{modules}}
        }
        """;

    private static string Prefabs(params (string Name, string Module, string File)[] prefabs) =>
        new JsonObject
        {
            ["formatVersion"] = GuiPackager.FormatVersion,
            ["prefabs"] = new JsonArray(prefabs.Select(x => (JsonNode?) new JsonObject { ["name"] = x.Name, ["module"] = x.Module, ["file"] = x.File, ["tags"] = new JsonArray() }).ToArray()),
        }.ToJsonString(new() { WriteIndented = true });

    private static string Tree(string name, string module, string root) =>
        $$$"""{"formatVersion":3,"name":"{{{name}}}","module":"{{{module}}}","root":{{{root.Trim()}}}}""";

    private static GuiBundleSource Source(BuildEntry build, string packageId, Dictionary<string, string> files) =>
        new(build, packageId, build.PackageVersion, () => files.ToDictionary(x => x.Key, x => Encoding.UTF8.GetBytes(x.Value), StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, byte[]> With(IReadOnlyDictionary<string, byte[]> files, string path, string content)
    {
        var changed = files.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        changed[path] = Encoding.UTF8.GetBytes(content);
        return changed;
    }

    /// <summary>The sources with one file of the base packages replaced or added.</summary>
    private static IEnumerable<GuiBundleSource> WithInBase(string path, string content) =>
        Sources().Select(x => x.PackageId == BaseId ? x with { ReadFiles = () => With(x.ReadFiles(), path, content) } : x);
}
