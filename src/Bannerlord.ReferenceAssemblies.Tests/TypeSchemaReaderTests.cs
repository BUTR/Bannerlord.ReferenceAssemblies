using Xunit;

namespace Bannerlord.ReferenceAssemblies.Tests;

/// <summary>What types.json records of the widget hierarchy and the ViewModels in the fixtures project, and what it leaves out.</summary>
public sealed class TypeSchemaReaderTests
{
    private static readonly Lazy<TypeSchema> Schema = new(() =>
    {
        var fixtures = typeof(Fixtures.Types.ButtonWidget).Assembly.Location;
        var build = new GameAssemblies([(fixtures, "Fixtures")]);
        return TypeSchemaReader.Read(build, new BuildScanner(build), _ => true);
    });

    private static WidgetType Widget(string name) => Assert.Single(Schema.Value.Widgets, x => x.Name == name);

    private static ViewModelType ViewModel(string type) => Assert.Single(Schema.Value.ViewModels, x => x.Type == type);

    [Fact]
    public void A_widget_records_its_own_public_instance_properties_only()
    {
        var button = Widget("ButtonWidget");
        Assert.Equal("Fixtures.Types.ButtonWidget", button.Type);
        Assert.Equal("TaleWorlds.GauntletUI.BaseTypes.Widget", button.BaseType);
        Assert.Equal("Fixtures", button.Module);
        Assert.False(button.Abstract);
        Assert.Equal(
        [
            new WidgetProperty("Alignment", "TaleWorlds.GauntletUI.HorizontalAlignment", true, true),
            new WidgetProperty("IsSelected", "System.Boolean", true, true),
            new WidgetProperty("MaybeAlignment", "System.Nullable<TaleWorlds.GauntletUI.HorizontalAlignment>", true, true),
            new WidgetProperty("PrivateSetter", "System.Int32", true, false),
            new WidgetProperty("ReadOnly", "System.String", true, false),
        ], button.Properties);
    }

    [Fact]
    public void Inherited_members_are_left_to_the_base_type()
    {
        var fancy = Widget("FancyButtonWidget");
        Assert.Equal("Fixtures.Types.ButtonWidget", fancy.BaseType);
        Assert.Equal([new WidgetProperty("Glow", "System.Single", true, true)], fancy.Properties);

        // The root is listed too, so walking baseType ends somewhere.
        Assert.Equal([new WidgetProperty("SuggestedWidth", "System.Single", true, true)], Widget("Widget").Properties);
        Assert.True(Widget("AbstractWidget").Abstract);
    }

    [Fact]
    public void Enums_used_by_widget_properties_are_listed_with_their_members() =>
        Assert.Equal(["Center", "Left", "Right"], Assert.Single(Schema.Value.Enums, x => x.Type == "TaleWorlds.GauntletUI.HorizontalAlignment").Members);

    [Fact]
    public void A_ViewModel_records_instance_properties_of_every_accessibility()
    {
        var members = ViewModel("Fixtures.Types.MembersVM");
        Assert.Equal("TaleWorlds.Library.ViewModel", members.BaseType);
        Assert.Equal(
        [
            new ViewModelProperty("Items", "TaleWorlds.Library.MBBindingList<Fixtures.Types.MembersVM>", "internal", true, true, "internal", "internal"),
            new ViewModelProperty("PrivateProperty", "System.Boolean", "private", true, true, "private", "private"),
            new ViewModelProperty("ProtectedProperty", "System.Int32", "protected", true, true, "protected", "protected"),
            new ViewModelProperty("PublicProperty", "System.String", "public", true, true, "public", "public"),
            new ViewModelProperty("WriteOnly", "System.Int32", "public", false, true, null, "public"),
        ], members.Properties);
    }

    [Fact]
    public void A_ViewModel_records_ordinary_instance_methods_but_not_accessors_statics_or_compiler_generated_ones()
    {
        var methods = ViewModel("Fixtures.Types.MembersVM").Methods;
        Assert.Equal(["Compute", "ExecuteDone", "ExecuteHidden", "WithLocalFunction"], methods.Select(x => x.Name));

        var hidden = Assert.Single(methods, x => x.Name == "ExecuteHidden");
        Assert.Equal("private", hidden.Accessibility);
        Assert.Equal("System.Void", hidden.ReturnType);
        Assert.Equal([new MethodParameter("amount", "System.Int32")], hidden.Parameters);

        var compute = Assert.Single(methods, x => x.Name == "Compute");
        Assert.Equal("protected internal", compute.Accessibility);
        Assert.Equal("System.String", compute.ReturnType);
    }

    [Fact]
    public void A_derived_ViewModel_records_only_what_it_declares()
    {
        var derived = ViewModel("Fixtures.Types.DerivedVM");
        Assert.Equal("Fixtures.Types.MembersVM", derived.BaseType);
        Assert.Equal(["Extra"], derived.Properties.Select(x => x.Name));
        Assert.Empty(derived.Methods);
    }

    [Fact]
    public void Types_that_are_neither_are_left_out()
    {
        Assert.DoesNotContain(Schema.Value.ViewModels, x => x.Type == "Fixtures.Types.NotAViewModel");
        Assert.DoesNotContain(Schema.Value.Widgets, x => x.Type == "Fixtures.Types.NotAViewModel");
    }

    [Fact]
    public void A_generic_definition_is_written_with_its_parameter_names() =>
        Assert.Equal("TaleWorlds.Library.MBBindingList<T>", TypeNames.Definition(new GameAssemblies([(typeof(Fixtures.Types.MembersVM).Assembly.Location, null)]).FindType("TaleWorlds.Library.MBBindingList`1")!));

    [Fact]
    public void A_widget_records_the_events_it_raises_itself_and_a_subclass_its_own()
    {
        Assert.Equal(["Press", "Release"], Widget("EventBaseWidget").Events);
        // Hold in its own method, LetGo through its override of the name the base raises.
        Assert.Equal(["Hold", "LetGo"], Widget("EventDerivedWidget").Events);
        Assert.Empty(Widget("ButtonWidget").Events);
    }

    [Fact]
    public void An_event_raised_through_a_helper_is_traced_through_its_callers() =>
        Assert.Equal(["Alpha", "Beta"], Widget("HelperEventWidget").Events);

    [Fact]
    public void An_event_named_by_the_prefab_XML_is_unresolved()
    {
        var widget = Widget("XmlEventWidget");
        Assert.Empty(widget.Events);
        var unresolved = Assert.Single(widget.UnresolvedEvents);
        Assert.Equal("set from the prefab XML", unresolved.Reason);
        Assert.Equal("Fixtures.Types.XmlEventWidget::Fire", unresolved.Caller);
    }

    [Fact]
    public void The_classes_widget_properties_hold_are_recorded_with_their_members_to_depth_three()
    {
        var style = Assert.Single(Schema.Value.Objects, x => x.Type == "Fixtures.Types.TextStyle");
        Assert.Equal(
        [
            new WidgetProperty("Alignment", "TaleWorlds.GauntletUI.HorizontalAlignment", true, true),
            new WidgetProperty("FontSize", "System.Int32", true, true),
            new WidgetProperty("Tint", "Fixtures.Types.TintColor", true, true),
        ], style.Properties);
        Assert.Contains(Schema.Value.Objects, x => x.Type == "Fixtures.Types.TintColor");

        // Neither widgets, ViewModels, strings nor interfaces are objects.
        Assert.DoesNotContain(Schema.Value.Objects, x => x.Type is "Fixtures.Types.ButtonWidget" or "System.String" or "Fixtures.Types.IFixtureLayout");
    }

    [Fact]
    public void A_property_of_an_interface_type_records_what_the_constructor_puts_there()
    {
        var layout = Assert.Single(Widget("StyledWidget").Properties, x => x.Name == "Layout");
        Assert.Equal(["Fixtures.Types.ColumnLayout"], layout.AssignedTypes);
        Assert.Contains(Schema.Value.Objects, x => x.Type == "Fixtures.Types.ColumnLayout");
        Assert.DoesNotContain(Schema.Value.Objects, x => x.Type == "Fixtures.Types.RowLayout");

        // A plain class with no subclasses can only hold itself.
        Assert.Null(Assert.Single(Widget("StyledWidget").Properties, x => x.Name == "Style").AssignedTypes);
    }

    [Fact]
    public void Objects_go_three_steps_from_a_widget()
    {
        Assert.Contains(Schema.Value.Objects, x => x.Type == "Fixtures.Types.DeepA");
        Assert.Contains(Schema.Value.Objects, x => x.Type == "Fixtures.Types.DeepB");
        Assert.Contains(Schema.Value.Objects, x => x.Type == "Fixtures.Types.DeepC");
    }

    [Fact]
    public void A_closed_generic_is_recorded_as_its_instance_and_an_open_one_not_at_all()
    {
        var holder = Assert.Single(Schema.Value.Objects, x => x.Type.StartsWith("Fixtures.Types.Holder", StringComparison.Ordinal));
        Assert.Equal("Fixtures.Types.Holder<Fixtures.Types.DeepC>", holder.Type);
        Assert.Equal("Fixtures.Types.DeepC", Assert.Single(holder.Properties, x => x.Name == "Item").Type);
    }

    [Fact]
    public void A_property_without_a_getter_leads_nowhere()
    {
        Assert.Contains(Schema.Value.Objects, x => x.Type == "Fixtures.Types.WriteOnlyStep");
        Assert.DoesNotContain(Schema.Value.Objects, x => x.Type == "Fixtures.Types.HiddenBehind");
    }

    [Fact]
    public void A_widget_held_by_a_widget_has_no_assigned_types() =>
        Assert.Null(Assert.Single(Widget("SelfAreaWidget").Properties, x => x.Name == "Area").AssignedTypes);

    private static Dictionary<string, List<string>> Announcements(string widget) =>
        Widget(widget).Announcements.ToDictionary(x => x.Name, x => x.Types);

    [Fact]
    public void A_widget_records_what_it_announces_by_name_with_the_type_the_loader_is_handed()
    {
        Assert.Equal(new Dictionary<string, List<string>>
        {
            // Typed overload, [CallerMemberName]; the generic one with literals and with a field; an enum as its number,
            // and boxed through the object overload; a name that is no property; another property's setter.
            ["Alignment"] = ["System.String"],
            ["Boxed"] = ["Fixtures.Types.Direction"],
            ["Heading"] = ["System.Int32"],
            ["IsOn"] = ["System.Boolean"],
            ["IsVisible"] = ["System.Boolean"],
            ["OnPress"] = ["System.String"],
            ["Style"] = ["Fixtures.Types.TextStyle"],
        }, Announcements("AnnouncingWidget"));
        Assert.Equal(["Alignment", "Boxed", "Heading", "IsOn", "IsVisible", "OnPress", "Style"], Widget("AnnouncingWidget").Announcements.Select(x => x.Name));
    }

    [Fact]
    public void A_literal_null_announces_nothing() =>
        Assert.DoesNotContain(Widget("AnnouncingWidget").Announcements, x => x.Name == "Cleared");

    [Fact]
    public void An_announcement_named_by_the_prefab_XML_is_unresolved()
    {
        var unresolved = Assert.Single(Widget("AnnouncingWidget").UnresolvedAnnouncements);
        Assert.Equal("Fixtures.Types.AnnouncingWidget::Announce", unresolved.Caller);
        Assert.Equal("set from the prefab XML", unresolved.Reason);
    }

    [Fact]
    public void An_override_announcing_another_type_announces_it_on_the_subclass_only()
    {
        Assert.Equal(new Dictionary<string, List<string>> { ["Value"] = ["System.Int32"] }, Announcements("AnnouncingBaseWidget"));
        Assert.Equal(new Dictionary<string, List<string>> { ["Value"] = ["System.String"] }, Announcements("AnnouncingDerivedWidget"));
    }

    [Fact]
    public void A_type_parameter_announced_is_resolved_per_concrete_subclass()
    {
        var generic = Assert.Single(Schema.Value.Widgets, x => x.Type == "Fixtures.Types.GenericAnnouncingWidget<TItem>");
        Assert.Empty(generic.Announcements);
        Assert.Equal("generic argument not known", Assert.Single(generic.UnresolvedAnnouncements).Reason);
        Assert.Equal(new Dictionary<string, List<string>> { ["Item"] = ["Fixtures.Types.TextStyle"] }, Announcements("ConcreteAnnouncingWidget"));
    }

    [Fact]
    public void A_widget_that_announces_nothing_has_empty_lists()
    {
        Assert.Empty(Widget("ButtonWidget").Announcements);
        Assert.Empty(Widget("ButtonWidget").UnresolvedAnnouncements);
    }

    [Fact]
    public void A_ViewModel_property_records_the_accessibility_of_each_accessor()
    {
        Assert.Equal(
        [
            new ViewModelProperty("Count", "System.Int32", "public", true, false, "public", null),
            new ViewModelProperty("Title", "System.String", "public", true, true, "public", "private"),
        ], ViewModel("Fixtures.Types.AccessorsVM").Properties);
    }
}
