using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>
/// What the movie scan makes of each way the game loads a movie, reproduced in the fixtures project. Each
/// call is written as "class: movie -> ViewModel", short names only.
/// </summary>
public sealed class MovieScannerTests
{
    private static readonly Lazy<MovieSchema> Schema = new(() =>
    {
        var fixtures = typeof(Fixtures.Movies.LiteralScreen).Assembly.Location;
        return new BuildScanner(new GameAssemblies([(fixtures, "Fixtures")])).ScanMovies().Schema;
    });

    private static List<string> Calls(params string[] classes) =>
        Schema.Value.Calls
            .Where(x => classes.Contains(Short(x.Class)))
            .Select(x => $"{Short(x.Class)}: {x.Movie} -> {Short(x.ViewModel)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Short(string? type) => type?.Split('.')[^1] ?? "none";

    [Fact]
    public void Every_resolvable_call_is_found_and_paired()
    {
        Assert.Equal(
        [
            "Background: Background -> none",
            "BarHandler: BaseBar -> BarVM",
            "Cycle: CycleMovie -> CycleVM",
            "DeepInner: DeepMovie -> DeepVM",
            "DerivedFactoryScreen: FactoryScreen -> DerivedScreenVM",
            "Encyclopedia: ClanPage -> PageVM",
            "Encyclopedia: HeroPage -> PageVM",
            "EncyclopediaPages: EncyclopediaHome -> EncyclopediaVM",
            "FactoryScreen: FactoryScreen -> BaseScreenVM",
            "LiteralScreen: Literal -> LiteralVM",
            "MapBarLayer: MapBar -> MapBarVM",
            "MultiMenu: MultiMenu -> MultiMenuVM",
            "NavalMapBarLayer: MapBar -> NavalMapBarVM",
            "NotificationBase: BaseNotification -> NotificationVM",
            "OrderUI: OrderBar -> OrderVM",
            "OrderUI: OrderRadial -> OrderVM",
            "OverwritingBarHandler: OverwrittenBar -> BarVM",
            "ReflectedPage: ReflectedPage -> PageVM",
            "ReplacementClanScreen: ClanScreen -> StatusVM",
            "ReplacementStatus: Status -> StatusVM",
            "SharedMovie: Shared -> FirstVM",
            "SharedMovie: Shared -> SecondVM",
            "SingleMenu: SingleMenu -> SingleMenuVM",
            "SometimesBarHandler: BaseBar -> BarVM",
            "SometimesBarHandler: SometimesBar -> BarVM",
            "SpecialNotification: SpecialNotification -> NotificationVM",
            "TooltipView: ItemTooltip -> ItemTooltipVM",
            "TooltipView: TextTooltip -> TextTooltipVM",
        ], Schema.Value.Calls.Select(x => $"{Short(x.Class)}: {x.Movie} -> {Short(x.ViewModel)}").Distinct().Order(StringComparer.Ordinal));
        Assert.All(Schema.Value.Calls.Where(x => x.Movie != "ReflectedPage"), x => Assert.True(x.Paired, $"{x.Class}: {x.Movie} -> {x.ViewModel}"));
    }

    [Fact]
    public void A_class_loads_only_what_its_own_override_of_a_factory_creates() =>
        Assert.Equal(["DerivedFactoryScreen: FactoryScreen -> DerivedScreenVM", "FactoryScreen: FactoryScreen -> BaseScreenVM"], Calls("FactoryScreen", "DerivedFactoryScreen"));

    [Fact]
    public void A_constructor_overwriting_an_initializer_replaces_it_and_a_conditional_store_adds_to_it() =>
        Assert.Equal(
            ["BarHandler: BaseBar -> BarVM", "OverwritingBarHandler: OverwrittenBar -> BarVM", "SometimesBarHandler: BaseBar -> BarVM", "SometimesBarHandler: SometimesBar -> BarVM"],
            Calls("BarHandler", "OverwritingBarHandler", "SometimesBarHandler"));

    [Fact]
    public void A_data_source_handed_in_by_the_creator_is_the_one_its_own_creator_passes() =>
        Assert.Equal(["MapBarLayer: MapBar -> MapBarVM", "NavalMapBarLayer: MapBar -> NavalMapBarVM"], Calls("MapBarLayer", "NavalMapBarLayer"));

    [Fact]
    public void A_movie_loaded_without_a_data_source_records_no_ViewModel()
    {
        var call = Assert.Single(Schema.Value.Calls, x => x.Movie == "Background");
        Assert.Null(call.ViewModel);
        Assert.True(call.Paired);
    }

    [Fact]
    public void A_ViewModel_known_only_by_its_declared_type_is_not_claimed_as_paired()
    {
        var call = Assert.Single(Schema.Value.Calls, x => x.Movie == "ReflectedPage");
        Assert.Equal("Fixtures.Movies.PageVM", call.ViewModel);
        Assert.False(call.Paired);
    }

    [Fact]
    public void A_literal_passed_directly_is_its_own_trace()
    {
        var call = Assert.Single(Schema.Value.Calls, x => x.Movie == "Literal");
        Assert.Equal("\"Literal\"", call.Via);
        Assert.Equal("Fixtures.Movies.LiteralScreen::Open", call.Caller);
        Assert.Equal("Fixtures", call.Module);
    }

    [Fact]
    public void A_virtual_property_resolves_to_the_override_each_class_runs() =>
        Assert.Equal(["NotificationBase: BaseNotification -> NotificationVM", "SpecialNotification: SpecialNotification -> NotificationVM"],
            Calls("NotificationBase", "SpecialNotification"));

    [Fact]
    public void A_base_screen_serving_two_subclasses_pairs_each_movie_with_its_own_ViewModel()
    {
        Assert.Equal(["MultiMenu: MultiMenu -> MultiMenuVM", "SingleMenu: SingleMenu -> SingleMenuVM"], Calls("SingleMenu", "MultiMenu"));

        var single = Assert.Single(Schema.Value.Calls, x => x.Movie == "SingleMenu");
        Assert.Equal("Fixtures.Movies.MenuBase::Toggle", single.Caller);
        Assert.Equal("field _viewFile <- parameter viewFile of MenuBase..ctor <- SingleMenu..ctor: base(...) <- \"SingleMenu\"", single.Via);
    }

    [Fact]
    public void A_local_chosen_between_fields_yields_both_names() =>
        Assert.Equal(["OrderUI: OrderBar -> OrderVM", "OrderUI: OrderRadial -> OrderVM"], Calls("OrderUI"));

    [Fact]
    public void A_registry_pairs_each_registered_name_with_the_ViewModel_registered_beside_it() =>
        Assert.Equal(["TooltipView: ItemTooltip -> ItemTooltipVM", "TooltipView: TextTooltip -> TextTooltipVM"], Calls("TooltipView"));

    [Fact]
    public void A_virtual_method_on_another_object_yields_every_override_but_not_a_base_that_never_runs() =>
        Assert.Equal(["Encyclopedia: ClanPage -> PageVM", "Encyclopedia: HeroPage -> PageVM"], Calls("Encyclopedia"));

    [Fact]
    public void A_literal_is_followed_through_two_constructors_and_a_field() =>
        Assert.Equal(["DeepInner: DeepMovie -> DeepVM"], Calls("DeepInner"));

    [Fact]
    public void A_cycle_between_two_methods_ends_with_the_literal_that_leaves_it() =>
        Assert.Equal(["Cycle: CycleMovie -> CycleVM"], Calls("Cycle"));

    [Fact]
    public void Names_built_at_runtime_or_read_from_a_file_are_unresolved()
    {
        Assert.Empty(Calls("RuntimeNames"));
        Assert.Equal(
            ["FromClock: built from a runtime value", "FromFile: read from a file or config"],
            Schema.Value.Unresolved.Select(x => $"{x.Caller.Split("::")[^1]}: {x.Reason}").Order(StringComparer.Ordinal));
        Assert.All(Schema.Value.Unresolved, x =>
        {
            Assert.Null(x.Movie);
            Assert.Equal("Fixtures.Movies.RuntimeVM", x.ViewModel);
        });
    }

    [Fact]
    public void One_movie_with_two_ViewModels_keeps_both_pairs() =>
        Assert.Equal(["SharedMovie: Shared -> FirstVM", "SharedMovie: Shared -> SecondVM"], Calls("SharedMovie"));

    [Fact]
    public void The_replacement_attributes_of_the_class_are_recorded()
    {
        var status = Assert.Single(Schema.Value.Calls, x => x.Movie == "Status");
        Assert.Equal("Fixtures.Movies.StatusView", status.OverrideView);
        Assert.Null(status.GameStateScreen);

        var clan = Assert.Single(Schema.Value.Calls, x => x.Movie == "ClanScreen");
        Assert.Null(clan.OverrideView);
        Assert.Equal("Fixtures.Movies.ClanState", clan.GameStateScreen);
    }
}
