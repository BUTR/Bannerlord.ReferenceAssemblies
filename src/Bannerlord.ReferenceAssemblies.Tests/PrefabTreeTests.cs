using System.Text;
using System.Text.Json;
using System.Xml;

using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>
/// A prefab tree is the document the game loads, the one UIExtenderEx's XPaths run against: no comments or
/// whitespace-only text, and everything else in order.
/// </summary>
public sealed class PrefabTreeTests : IDisposable
{
    private const string Fixture = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- a comment the game drops -->
        <Prefab>
          <Parameters>
            <Parameter Name="Title" DefaultValue="none" />
            <Parameter Name="Brush" />
          </Parameters>
          <Constants></Constants>
          <Window>
            <OptionsBase Id="Root" WidthSizePolicy="StretchToParent" HeightSizePolicy="CoverChildren" MarginTop="10">
              <!-- inside too -->
              <Children>
                <TextWidget Text="Fish &amp; chips" />
                <RichTextWidget>  two  spaces kept  </RichTextWidget>
                <TextWidget><![CDATA[<b>raw</b>]]></TextWidget>
                <Widget />
                <Widget></Widget>
                <?target data?>
              </Children>
            </OptionsBase>
          </Window>
        </Prefab>
        """;

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"prefab-{Guid.NewGuid():N}.xml");

    public PrefabTreeTests() => File.WriteAllText(_file, Fixture);

    public void Dispose() => File.Delete(_file);

    private JsonElement Tree()
    {
        using var json = JsonDocument.Parse(PrefabTrees.Write(PrefabTrees.Load(_file), "Options", "Native"));
        return json.RootElement.Clone();
    }

    [Fact]
    public void The_tree_has_no_comments_or_whitespace_and_keeps_text_entities_CDATA_and_attribute_order()
    {
        var tree = Tree();
        Assert.Equal(GuiPackager.FormatVersion, tree.GetProperty("formatVersion").GetInt32());
        Assert.Equal("Options", tree.GetProperty("name").GetString());
        Assert.Equal("Native", tree.GetProperty("module").GetString());

        var root = tree.GetProperty("root");
        Assert.Equal(["Parameters", "Constants", "Window"], root.GetProperty("c").EnumerateArray().Select(x => x.GetProperty("n").GetString()));
        var widget = root.GetProperty("c")[2].GetProperty("c")[0];
        Assert.Equal(["Id", "WidthSizePolicy", "HeightSizePolicy", "MarginTop"], widget.GetProperty("a").EnumerateObject().Select(x => x.Name));

        var children = widget.GetProperty("c")[0].GetProperty("c").EnumerateArray().ToList();
        Assert.Equal(5, children.Count);
        Assert.Equal("Fish & chips", children[0].GetProperty("a").GetProperty("Text").GetString());
        Assert.Equal("  two  spaces kept  ", children[1].GetProperty("c")[0].GetString());
        Assert.Equal("<b>raw</b>", children[2].GetProperty("c")[0].GetString());
        // An element written empty either way is the same node, with neither attributes nor children.
        Assert.Equal("""{"n":"Widget"}""", children[3].GetRawText());
        Assert.Equal("""{"n":"Widget"}""", children[4].GetRawText());

        var raw = Encoding.UTF8.GetString(PrefabTrees.Write(PrefabTrees.Load(_file), "Options", "Native"));
        Assert.DoesNotContain("comment", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("target", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rebuilt_document_equals_the_prefab_loaded_the_way_the_game_loads_it()
    {
        var loaded = PrefabTrees.Load(_file);
        var rebuilt = PrefabTrees.Rebuild(PrefabTrees.Write(loaded, "Options", "Native"));
        Assert.Null(PrefabTrees.Difference(loaded, rebuilt));

        // And an XPath of the kind UIExtenderEx patches with finds the same node on both.
        const string xpath = "Prefab/Window/OptionsBase[@Id='Root']/Children/*[3]";
        Assert.Equal(loaded.SelectSingleNode(xpath)!.InnerText, rebuilt.SelectSingleNode(xpath)!.InnerText);
    }

    [Fact]
    public void A_difference_is_found()
    {
        var loaded = PrefabTrees.Load(_file);
        var rebuilt = PrefabTrees.Rebuild(PrefabTrees.Write(loaded, "Options", "Native"));
        ((XmlElement) rebuilt.SelectSingleNode("Prefab/Window/OptionsBase")!).SetAttribute("Id", "Other");
        Assert.Contains("OptionsBase", PrefabTrees.Difference(loaded, rebuilt));
    }

    [Fact]
    public void A_prefab_in_a_namespace_stops_the_packing()
    {
        File.WriteAllText(_file, """<Prefab xmlns:x="urn:x"><Window><x:Widget /></Window></Prefab>""");
        Assert.Throws<InvalidDataException>(() => PrefabTrees.Write(PrefabTrees.Load(_file), "Options", "Native"));
    }

    [Fact]
    public void The_index_takes_the_root_tag_under_Window_even_when_it_is_another_prefab()
    {
        var entry = PrefabTrees.Entry(PrefabTrees.Load(_file), "Options", "Native", "Native/GUI/Prefabs/Options.json");
        Assert.Equal("OptionsBase", entry.RootTag);
        Assert.Equal("Native/GUI/Prefabs/Options.json", entry.File);
    }

    [Fact]
    public void The_index_lists_every_tag_sorted_and_distinct()
    {
        var entry = PrefabTrees.Entry(PrefabTrees.Load(_file), "Options", "Native", "Options.json");
        Assert.Equal(["Children", "Constants", "OptionsBase", "Parameter", "Parameters", "Prefab", "RichTextWidget", "TextWidget", "Widget", "Window"], entry.Tags);
    }

    [Fact]
    public void The_index_lists_the_parameters_in_order_leaving_out_a_missing_default()
    {
        var entry = PrefabTrees.Entry(PrefabTrees.Load(_file), "Options", "Native", "Options.json");
        Assert.Equal([new PrefabParameter("Title", "none"), new PrefabParameter("Brush", null)], entry.Parameters);

        var json = JsonSerializer.Serialize(entry.Parameters, GuiPackager.JsonOptions);
        Assert.DoesNotContain("defaultValue\": null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prefab_whose_root_is_Window_has_its_root_tag_and_no_parameters()
    {
        File.WriteAllText(_file, "<Window><ListPanel /></Window>");
        var entry = PrefabTrees.Entry(PrefabTrees.Load(_file), "Bare", "Native", "Bare.json");
        Assert.Equal("ListPanel", entry.RootTag);
        Assert.Empty(entry.Parameters);
    }
}
