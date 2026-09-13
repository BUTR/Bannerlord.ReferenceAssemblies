using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>
/// The rules that decide which of a build's several recorded versions names it. Every case here is
/// taken from a real Bannerlord build, because the disagreements are what make the rules necessary.
/// </summary>
public sealed class VersionReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Bannerlord.VersionTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    // Build 4842596: the assembly never left e1.0.0, the modules followed the patch notes to e1.0.1.
    [InlineData("e1.0.0", "e1.0.1", "e1.0.1")]
    // Build 4892567, eleven patches in, with the assembly still unbumped.
    [InlineData("e1.0.0", "e1.0.11", "e1.0.11")]
    // Build 4911497: the same lag after changesets existed.
    [InlineData("e1.1.0", "e1.1.1", "e1.1.1")]
    // Build 4799936, the pre-launch baseline: its modules were stamped for the coming release already,
    // and a different release prefix must never be taken as a newer version of the same line.
    [InlineData("b0.8.7", "e1.0.0", "b0.8.7")]
    // A module left behind never drags the version backwards.
    [InlineData("v1.3.7", "v1.3.6", "v1.3.7")]
    [InlineData("v1.0.0", "v1.0.0", "v1.0.0")]
    // Nothing to compare against.
    [InlineData("v1.4.8", null, "v1.4.8")]
    public void PreferHigherVersion_picks_the_superseding_version(string assembly, string? module, string expected) =>
        Assert.Equal(expected, VersionReader.PreferHigherVersion(assembly, module));

    [Theory]
    [InlineData("e1.0.2", "e1.0.10")]   // ordered by number, not by text
    [InlineData("v1.2.9", "v1.2.12")]
    [InlineData("v1.3.7", "v1.4.0")]
    [InlineData("v1.3.7", "v1.3.7.1")]  // a trailing changeset still orders
    public void VersionComparer_orders_numerically(string lower, string higher)
    {
        Assert.True(VersionReader.VersionComparer.Compare(lower, higher) < 0);
        Assert.True(VersionReader.VersionComparer.Compare(higher, lower) > 0);
        Assert.Equal(0, VersionReader.VersionComparer.Compare(lower, lower));
    }

    [Fact]
    public void ReadHighestModuleVersion_ignores_the_module_left_behind()
    {
        // Exactly the shape of build 4842596: Native stale, the rest on the real patch.
        WriteModule("Native", "e1.0.0");
        WriteModule("SandBox", "e1.0.1");
        WriteModule("SandBoxCore", "e1.0.1");
        WriteModule("StoryMode", "e1.0.1");
        WriteModule("CustomBattle", "e1.0.1");

        Assert.Equal("e1.0.1", VersionReader.ReadHighestModuleVersion(_root));
    }

    [Fact]
    public void ReadHighestModuleVersion_is_null_without_modules() =>
        Assert.Null(VersionReader.ReadHighestModuleVersion(_root));

    [Fact]
    public void ReadModuleVersion_rejects_a_value_that_is_not_a_version()
    {
        var path = WriteModule("Broken", "not-a-version");
        Assert.Null(VersionReader.ReadModuleVersion(path));
    }

    [Fact]
    public void ReadHighestModuleVersion_takes_the_base_version_a_DLC_declares_not_its_own()
    {
        // Exactly the shape of War Sails in game build 24573425: its own line is v1.2.8, the game is v1.4.8.
        WriteModule("Native", "v1.4.8");
        WriteDlc("NavalDLC", version: "v1.2.8", requiredBaseVersion: "v1.4.8");
        Assert.Equal("v1.4.8", VersionReader.ReadHighestModuleVersion(_root));

        // And the day the DLC's own line overtakes the game's, it still must not name the build.
        WriteDlc("NavalDLC", version: "v2.0.0", requiredBaseVersion: "v1.4.8");
        Assert.Equal("v1.4.8", VersionReader.ReadHighestModuleVersion(_root));
    }

    [Fact]
    public void ReadModuleVersions_keeps_each_module_own_line_keyed_by_folder()
    {
        WriteModule("Native", "v1.4.8");
        WriteModule("SandBox", "v1.4.8");
        WriteDlc("NavalDLC", version: "v1.2.8", requiredBaseVersion: "v1.4.8");
        Assert.Equal(new Dictionary<string, string> { ["Native"] = "v1.4.8", ["SandBox"] = "v1.4.8", ["NavalDLC"] = "v1.2.8" }, VersionReader.ReadModuleVersions(_root));
    }

    [Fact]
    public void ReadModuleVersion_reads_the_spacing_the_manifests_actually_use()
    {
        var path = Path.Combine(_root, "Modules", "Spaced", "SubModule.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Module>\n\t<Name value = \"Spaced\"/>\n\t<Version value = \"v1.4.8\"/>\n</Module>");
        Assert.Equal("v1.4.8", VersionReader.ReadModuleVersion(path));
    }

    [Fact]
    public void Read_is_null_for_a_folder_with_nothing_in_it() => Assert.Null(VersionReader.Read(_root));

    private void WriteDlc(string name, string version, string requiredBaseVersion)
    {
        var path = Path.Combine(_root, "Modules", name, "SubModule.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <Module>
              <Id
                value="{name}" />
              <Version
                value="{version}" />
              <RequiredBaseVersion
                value="{requiredBaseVersion}" />
              <ModuleType
                value="OfficialOptional" />
            </Module>
            """);
    }

    private string WriteModule(string name, string version)
    {
        var path = Path.Combine(_root, "Modules", name, "SubModule.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""<Module><Name value="{name}"/><Version value="{version}"/></Module>""");
        return path;
    }
}
