using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>What the scan makes of each way the fixtures load a sprite category.</summary>
public sealed class SpriteCategoryScanTests
{
    private static readonly Lazy<SpriteCategorySchema> Schema = new(() =>
    {
        var fixtures = typeof(Fixtures.SpriteCategories.OrderScreen).Assembly.Location;
        var scanner = new BuildScanner(new GameAssemblies([(fixtures, "Fixtures")]));
        return scanner.ScanSpriteCategories(scanner.ScanMovies().Schema);
    });

    private static ClassCategories Class(string name) => Assert.Single(Schema.Value.Classes, x => x.Class == $"Fixtures.SpriteCategories.{name}");

    [Fact]
    public void A_category_loaded_by_name_is_the_class_own_and_a_subclass_inherits_it()
    {
        Assert.Equal(["ui_order"], Class("OrderScreen").Categories);
        Assert.Equal(["ui_order"], Class("NavalOrderScreen").Categories);
        Assert.Equal("Fixtures", Class("NavalOrderScreen").Module);
    }

    [Fact]
    public void A_category_looked_up_in_one_method_and_loaded_in_another_counts() =>
        Assert.Equal(["ui_developer"], Class("DeveloperScreen").Categories);

    [Fact]
    public void A_category_looked_up_and_never_loaded_does_not_count()
    {
        Assert.DoesNotContain(Schema.Value.Classes, x => x.Class.EndsWith(".LookupOnly", StringComparison.Ordinal));
        Assert.DoesNotContain(Schema.Value.Classes, x => x.Categories.Contains("ui_never"));
    }

    [Fact]
    public void A_category_taken_from_the_sprite_data_and_loaded_directly_counts() =>
        Assert.Equal(["ui_direct"], Class("DirectLoad").Categories);

    [Fact]
    public void A_partial_load_is_recorded_apart()
    {
        var loading = Class("LoadingWindow");
        Assert.Empty(loading.Categories);
        Assert.Equal(["ui_loading"], loading.Partial);
    }

    [Fact]
    public void A_load_in_a_SubModule_goes_under_always()
    {
        var always = Assert.Single(Schema.Value.Always);
        Assert.Equal("ui_startup", always.Category);
        Assert.Equal("Fixtures.SpriteCategories.FixtureSubModule::OnSubModuleLoad", always.Caller);
        Assert.DoesNotContain(Schema.Value.Classes, x => x.Class.EndsWith(".FixtureSubModule", StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_built_at_runtime_is_unresolved()
    {
        var unresolved = Assert.Single(Schema.Value.Unresolved);
        Assert.Equal("Fixtures.SpriteCategories.RuntimeCategory", unresolved.Class);
        Assert.Equal("built from a runtime value", unresolved.Reason);
    }

    [Fact]
    public void Loading_the_categories_the_sprite_data_always_loads_is_not_unresolved()
    {
        Assert.DoesNotContain(Schema.Value.Unresolved, x => x.Caller.Contains("AlwaysLoadSubModule", StringComparison.Ordinal));
        Assert.DoesNotContain(Schema.Value.Always, x => x.Caller?.Contains("AlwaysLoadSubModule", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_class_created_by_one_that_loads_categories_gets_them_when_it_loads_movies()
    {
        var created = Class("EncyclopediaPages");
        Assert.Equal(["ui_encyclopedia"], created.Categories);
        Assert.Equal(["ui_encyclopedia: loaded by Fixtures.SpriteCategories.EncyclopediaView, which creates Fixtures.SpriteCategories.EncyclopediaPages"], created.Via);
        Assert.Equal(["ui_encyclopedia"], Class("EncyclopediaView").Categories);
    }

    [Fact]
    public void A_default_mission_view_loads_its_categories_in_every_mission()
    {
        var mission = Assert.Single(Schema.Value.Missions);
        Assert.Equal("ui_mission_backgrounds", mission.Category);
        Assert.Equal("Fixtures.SpriteCategories.CategoryLoadManager::AfterStart", mission.Caller);
        Assert.DoesNotContain(Schema.Value.Classes, x => x.Class.EndsWith(".CategoryLoadManager", StringComparison.Ordinal));
    }
}
