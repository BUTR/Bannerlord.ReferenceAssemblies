using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>brushes.json and sprites.json record what BrushFactory and SpriteData read, and nothing they do not.</summary>
public sealed class BrushAndSpriteReaderTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), $"brushes-{Guid.NewGuid():N}");

    private static readonly GuiModule Native = new("Native", "Native", "v1.4.8", "Official", [], false);
    private static readonly GuiModule SandBox = new("SandBox", "Sandbox", "v1.4.8", "Official", [], false);

    public void Dispose()
    {
        if (Directory.Exists(_game))
            Directory.Delete(_game, true);
    }

    private void File(string relative, string content)
    {
        var path = Path.Combine(_game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    [Fact]
    public void A_brush_keeps_its_base_layers_styles_animations_and_sounds_but_no_visual_values()
    {
        File("Modules/Native/GUI/Brushes/Buttons.xml", """
            <Brushes>
              <Brush Name="ButtonBrush1" BaseBrush="DefaultButton" Font="Galahad" FontSize="20" GlobalColor="#FF0000FF">
                <Layers>
                  <BrushLayer Name="Default" Sprite="General\Buttons\button_1" Color="#FFFFFFFF" />
                  <BrushLayer Name="Glow" Sprite="General\Buttons\glow" OverlaySprite="General\Buttons\overlay" />
                </Layers>
                <Styles>
                  <Style Name="Pressed" Font="FiraSans" AnimationToPlayOnBegin="Blink" FontColor="#00FF00FF">
                    <BrushLayer Name="Default" Sprite="General\Buttons\button_1_pressed" ColorFactor="1.2" />
                    <BrushLayer Name="Glow" />
                  </Style>
                </Styles>
                <Animations>
                  <Animation Name="Blink" Duration="0.5"><AnimationProperty PropertyName="AlphaFactor"><KeyFrame Time="0" Value="1" /></AnimationProperty></Animation>
                </Animations>
                <SoundProperties>
                  <StateSounds><StateSound StateName="Pressed" Audio="button_press" /></StateSounds>
                  <EventSounds><EventSound EventName="Click" Audio="default" /></EventSounds>
                </SoundProperties>
              </Brush>
            </Brushes>
            """);

        var brush = Assert.Single(BrushReader.Read(_game, [Native]).Brushes);
        Assert.Equal(("ButtonBrush1", "Native", "Brushes/Buttons.xml"), (brush.Name, brush.Module, brush.File));
        Assert.Equal(("DefaultButton", null, "Galahad"), (brush.BaseBrush, brush.OverrideBrush, brush.Font));
        Assert.Equal([new BrushLayerEntry("Default", "General\\Buttons\\button_1", null), new BrushLayerEntry("Glow", "General\\Buttons\\glow", "General\\Buttons\\overlay")], brush.Layers);

        var style = Assert.Single(brush.Styles);
        Assert.Equal(("Pressed", "FiraSans", "Blink"), (style.Name, style.Font, style.AnimationToPlayOnBegin));
        Assert.Equal([new BrushLayerEntry("Default", "General\\Buttons\\button_1_pressed", null), new BrushLayerEntry("Glow", null, null)], style.Layers);

        Assert.Equal(["Blink"], brush.Animations);
        Assert.Equal([new BrushEventSound("Click", "default")], brush.EventSounds);
        Assert.Equal([new BrushStateSound("Pressed", "button_press")], brush.StateSounds);

        var json = System.Text.Json.JsonSerializer.Serialize(brush, GuiPackager.JsonOptions);
        Assert.DoesNotContain("#FF", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0.5", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_what_the_loader_reads_is_recorded()
    {
        File("Modules/Native/GUI/Brushes/Odd.xml", """
            <Brushes>
              <Brush Name="Odd" Sprite="General\never_read">
                <Layers><Layer Name="Default" Sprite="General\read_as_a_layer" /></Layers>
                <BrushLayer Name="Misplaced" Sprite="General\misplaced" />
                <Style Name="Misplaced"><BrushLayer Name="Default" Sprite="General\misplaced_too" /></Style>
                <Styles><Style Name="Hovered"><StyleLayer Name="Default" Sprite="General\read_as_a_style_layer" /></Style></Styles>
                <Styles><Style Name="Second" /></Styles>
              </Brush>
            </Brushes>
            """);

        var brush = Assert.Single(BrushReader.Read(_game, [Native]).Brushes);
        // Every child of Layers is a layer and of a Style a style layer, whatever the element is called.
        Assert.Equal([new BrushLayerEntry("Default", "General\\read_as_a_layer", null)], brush.Layers);
        var style = Assert.Single(brush.Styles);
        Assert.Equal("Hovered", style.Name);
        Assert.Equal([new BrushLayerEntry("Default", "General\\read_as_a_style_layer", null)], style.Layers);

        var json = System.Text.Json.JsonSerializer.Serialize(brush, GuiPackager.JsonOptions);
        Assert.DoesNotContain("misplaced", json, StringComparison.Ordinal);
        Assert.DoesNotContain("never_read", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layer_style_or_sound_named_twice_in_one_brush_is_merged_into_one_as_the_loader_does()
    {
        File("Modules/Native/GUI/Brushes/Twice.xml", """
            <Brushes>
              <Brush Name="Twice">
                <Layers>
                  <BrushLayer Name="Default" Sprite="General\first" />
                  <BrushLayer Name="Glow" />
                  <BrushLayer Name="Default" OverlaySprite="General\overlay" />
                </Layers>
                <Styles>
                  <Style Name="Pressed" Font="Galahad"><BrushLayer Name="Default" Sprite="General\pressed" /></Style>
                  <Style Name="Pressed" AnimationToPlayOnBegin="Blink">
                    <BrushLayer Name="Default" Sprite="General\pressed_again" />
                    <BrushLayer Name="Glow" Sprite="General\glow" />
                    <BrushLayer Name="Glow" OverlaySprite="General\glow_overlay" />
                  </Style>
                </Styles>
                <SoundProperties>
                  <EventSounds><EventSound EventName="Click" Audio="first" /><EventSound EventName="Click" Audio="second" /></EventSounds>
                  <StateSounds><StateSound StateName="Pressed" Audio="first" /><StateSound StateName="Pressed" Audio="second" /></StateSounds>
                </SoundProperties>
              </Brush>
            </Brushes>
            """);

        var brush = Assert.Single(BrushReader.Read(_game, [Native]).Brushes);
        Assert.Equal([new BrushLayerEntry("Default", "General\\first", "General\\overlay"), new BrushLayerEntry("Glow", null, null)], brush.Layers);
        var style = Assert.Single(brush.Styles);
        Assert.Equal(("Pressed", "Galahad", "Blink"), (style.Name, style.Font, style.AnimationToPlayOnBegin));
        Assert.Equal([new BrushLayerEntry("Default", "General\\pressed_again", null), new BrushLayerEntry("Glow", "General\\glow", "General\\glow_overlay")], style.Layers);
        Assert.Equal([new BrushEventSound("Click", "second")], brush.EventSounds);
        Assert.Equal([new BrushStateSound("Pressed", "second")], brush.StateSounds);
    }

    [Fact]
    public void Base_loads_first_a_later_brush_of_a_name_replaces_the_earlier_and_subfolders_are_not_read()
    {
        File("Modules/Native/GUI/Brushes/Base.xml", """<Brushes><Brush Name="Shared" Font="FromBase" /></Brushes>""");
        File("Modules/Native/GUI/Brushes/Another.xml", """<Brushes><Brush Name="Shared" Font="FromAnother" /><Brush Name="Own" /></Brushes>""");
        File("Modules/Native/GUI/Brushes/Nested/Skipped.xml", """<Brushes><Brush Name="Nested" /></Brushes>""");
        File("Modules/SandBox/GUI/Brushes/SandBox.xml", """<Brushes><Brush Name="Shared" Font="FromSandBox" /></Brushes>""");

        var brushes = BrushReader.Read(_game, [Native, SandBox]).Brushes;
        Assert.Equal(["Shared@Native:FromAnother", "Own@Native:", "Shared@SandBox:FromSandBox"], brushes.Select(x => $"{x.Name}@{x.Module}:{x.Font}"));
    }

    [Fact]
    public void Sprites_are_the_generic_and_nine_region_ones_with_the_category_of_their_part()
    {
        File("Modules/Native/GUI/NativeSpriteData.xml", """
            <SpriteData>
              <SpriteCategories>
                <SpriteCategory><Name>ui_fonts</Name><SpriteSheetCount>1</SpriteSheetCount><SpriteSheetSize ID="1" Width="2048" Height="2048" /><AlwaysLoad /></SpriteCategory>
                <SpriteCategory><Name>ui_encyclopedia</Name><SpriteSheetCount>1</SpriteSheetCount></SpriteCategory>
              </SpriteCategories>
              <SpriteParts>
                <SpritePart><Name>Fonts\glyph</Name><Width>8</Width><Height>8</Height><CategoryName>ui_fonts</CategoryName><SheetID>1</SheetID><SheetX>0</SheetX><SheetY>0</SheetY></SpritePart>
                <SpritePart><Name>Encyclopedia\frame</Name><Width>64</Width><Height>64</Height><CategoryName>ui_encyclopedia</CategoryName><SheetID>1</SheetID><SheetX>8</SheetX><SheetY>0</SheetY></SpritePart>
              </SpriteParts>
              <Sprites>
                <GenericSprite><Name>Fonts\glyph</Name><SpritePartName>Fonts\glyph</SpritePartName></GenericSprite>
                <NineRegionSprite><Name>Encyclopedia\frame_9</Name><SpritePartName>Encyclopedia\frame</SpritePartName><LeftWidth>4</LeftWidth><RightWidth>4</RightWidth><TopHeight>4</TopHeight><BottomHeight>4</BottomHeight></NineRegionSprite>
              </Sprites>
            </SpriteData>
            """);

        var sprites = SpriteReader.Read(_game, [Native]);
        Assert.Equal([new SpriteCategoryEntry("ui_fonts", "Native", true), new SpriteCategoryEntry("ui_encyclopedia", "Native", false)], sprites.Categories);
        Assert.Equal([new SpriteEntry("Fonts\\glyph", "ui_fonts", "generic"), new SpriteEntry("Encyclopedia\\frame_9", "ui_encyclopedia", "nineRegion")], sprites.Sprites);

        var json = System.Text.Json.JsonSerializer.Serialize(sprites, GuiPackager.JsonOptions);
        Assert.Contains("\"kind\": \"nineRegion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2048", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SheetX", json, StringComparison.OrdinalIgnoreCase);
    }
}
