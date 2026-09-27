using System;
using System.Collections.Generic;
using System.IO;

using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.Screens;

// Each class loads movies the way some part of the game does. MovieScannerTests says what each must yield.

namespace Fixtures.Movies
{
    public class LiteralVM : ViewModel { }

    /// <summary>A literal name with a new ViewModel.</summary>
    public class LiteralScreen
    {
        public void Open(GauntletLayer layer) => layer.LoadMovie("Literal", new LiteralVM());
    }

    public class NotificationVM : ViewModel { }

    /// <summary>A virtual property, overridden in a subclass: each class loads its own name.</summary>
    public class NotificationBase
    {
        protected ViewModel DataSource = new NotificationVM();

        protected virtual string MovieName => "BaseNotification";

        public void Open(GauntletLayer layer) => layer.LoadMovie(MovieName, DataSource);
    }

    public class SpecialNotification : NotificationBase
    {
        protected override string MovieName => "SpecialNotification";
    }

    public class SingleMenuVM : ViewModel { }

    public class MultiMenuVM : ViewModel { }

    /// <summary>A field set from a constructor parameter, given through base(...) by two subclasses with two ViewModels.</summary>
    public abstract class MenuBase
    {
        private readonly string _viewFile;
        protected ViewModel DataSource;

        protected MenuBase(string viewFile)
        {
            _viewFile = viewFile;
        }

        public void Toggle(GauntletLayer layer) => layer.LoadMovie(_viewFile, DataSource);
    }

    public class SingleMenu : MenuBase
    {
        public SingleMenu() : base("SingleMenu")
        {
            DataSource = new SingleMenuVM();
        }
    }

    public class MultiMenu : MenuBase
    {
        public MultiMenu() : base("MultiMenu")
        {
            DataSource = new MultiMenuVM();
        }
    }

    public class OrderVM : ViewModel { }

    /// <summary>A local chosen between two initialized fields: both names are real.</summary>
    public class OrderUI
    {
        private readonly string _barMovie = "OrderBar";
        private readonly string _radialMovie = "OrderRadial";
        public int Kind;

        public void Open(GauntletLayer layer)
        {
            var name = Kind == 0 ? _barMovie : _radialMovie;
            layer.LoadMovie(name, new OrderVM());
        }
    }

    public class TooltipBaseVM : ViewModel { }

    public class ItemTooltipVM : TooltipBaseVM { }

    public class TextTooltipVM : TooltipBaseVM { }

    /// <summary>A registry filled by a generic Register&lt;TObject, TViewModel&gt;(name), read back by the view.</summary>
    public static class TooltipRegistry
    {
        public sealed class Entry
        {
            public Entry(Type viewModelType, string movieName)
            {
                ViewModelType = viewModelType;
                MovieName = movieName;
            }

            public Type ViewModelType { get; }
            public string MovieName { get; }
        }

        public static readonly Dictionary<Type, Entry> Types = new Dictionary<Type, Entry>();

        public static void Register<TObject, TViewModel>(string movieName) where TViewModel : TooltipBaseVM =>
            Types[typeof(TObject)] = new Entry(typeof(TViewModel), movieName);
    }

    public class TooltipSetup
    {
        public void Initialize()
        {
            TooltipRegistry.Register<int, ItemTooltipVM>("ItemTooltip");
            TooltipRegistry.Register<string, TextTooltipVM>("TextTooltip");
        }
    }

    public class TooltipView
    {
        public void Show(GauntletLayer layer, Type type)
        {
            if (TooltipRegistry.Types.TryGetValue(type, out var entry))
            {
                var dataSource = (TooltipBaseVM) Activator.CreateInstance(entry.ViewModelType);
                layer.LoadMovie(entry.MovieName, dataSource);
            }
        }
    }

    public class PageVM : ViewModel { }

    /// <summary>A virtual method on another object, overridden per page type. The base's empty name never loads.</summary>
    public abstract class Page
    {
        public virtual string GetViewName() => "";
    }

    public class HeroPage : Page
    {
        public override string GetViewName() => "HeroPage";
    }

    public class ClanPage : Page
    {
        public override string GetViewName() => "ClanPage";
    }

    public class Encyclopedia
    {
        private readonly Dictionary<string, Page> _pages = new Dictionary<string, Page>();

        public void Open(GauntletLayer layer, string id) => layer.LoadMovie(_pages[id].GetViewName(), new PageVM());
    }

    public class DeepVM : ViewModel { }

    /// <summary>A literal passed through two constructors and a field.</summary>
    public class DeepInner
    {
        private readonly string _movie;

        public DeepInner(string movie)
        {
            _movie = movie;
        }

        public void Open(GauntletLayer layer) => layer.LoadMovie(_movie, new DeepVM());
    }

    public class DeepOuter
    {
        private readonly DeepInner _inner;

        public DeepOuter(string movie)
        {
            _inner = new DeepInner(movie);
        }
    }

    public class DeepCreator
    {
        public object Create() => new DeepOuter("DeepMovie");
    }

    public class CycleVM : ViewModel { }

    /// <summary>Two methods that call each other: the trace must end, with the literal the cycle leaves by.</summary>
    public class Cycle
    {
        private string Ping(int n) => n > 0 ? Pong(n - 1) : "CycleMovie";

        private string Pong(int n) => Ping(n);

        public void Open(GauntletLayer layer) => layer.LoadMovie(Ping(3), new CycleVM());
    }

    public class RuntimeVM : ViewModel { }

    /// <summary>A name built at runtime, and one read from a file: neither can be known.</summary>
    public class RuntimeNames
    {
        public void FromClock(GauntletLayer layer) => layer.LoadMovie(DateTime.Now.ToString(), new RuntimeVM());

        public void FromFile(GauntletLayer layer) => layer.LoadMovie(File.ReadAllText("movie.txt"), new RuntimeVM());
    }

    public class FirstVM : ViewModel { }

    public class SecondVM : ViewModel { }

    /// <summary>One movie loaded with two ViewModels: both pairs stay.</summary>
    public class SharedMovie
    {
        public void First(GauntletLayer layer) => layer.LoadMovie("Shared", new FirstVM());

        public void Second(GauntletLayer layer) => layer.LoadMovie("Shared", new SecondVM());
    }

    public class StatusVM : ViewModel { }

    public class StatusView { }

    public class ClanState { }

    /// <summary>The two ways a DLC stands in for a base screen.</summary>
    [OverrideView(typeof(StatusView))]
    public class ReplacementStatus
    {
        public void Open(GauntletLayer layer) => layer.LoadMovie("Status", new StatusVM());
    }

    [GameStateScreen(typeof(ClanState))]
    public class ReplacementClanScreen
    {
        public void Open(GauntletLayer layer) => layer.LoadMovie("ClanScreen", new StatusVM());
    }

    public class BaseScreenVM : ViewModel { }

    public class DerivedScreenVM : BaseScreenVM { }

    /// <summary>
    /// An auto-property set from a virtual factory just before the load, and cleared on the way out: each
    /// class loads only what its own override creates.
    /// </summary>
    public class FactoryScreen
    {
        public BaseScreenVM DataSource { get; private set; }

        protected virtual BaseScreenVM CreateDataSource() => new BaseScreenVM();

        public void Activate(GauntletLayer layer)
        {
            DataSource = CreateDataSource();
            layer.LoadMovie("FactoryScreen", DataSource);
        }

        public void Close() => DataSource = null;
    }

    public class DerivedFactoryScreen : FactoryScreen
    {
        protected override BaseScreenVM CreateDataSource() => new DerivedScreenVM();
    }

    public class BarVM : ViewModel { }

    /// <summary>
    /// A field initializer a subclass constructor overwrites, and one it overwrites only sometimes: the
    /// last store the constructor chain always makes wins, a conditional one adds to it.
    /// </summary>
    public class BarHandler
    {
        protected string _barMovie = "BaseBar";

        public void Open(GauntletLayer layer) => layer.LoadMovie(_barMovie, new BarVM());
    }

    public class OverwritingBarHandler : BarHandler
    {
        public OverwritingBarHandler()
        {
            _barMovie = "OverwrittenBar";
        }
    }

    public class SometimesBarHandler : BarHandler
    {
        public SometimesBarHandler(bool naval)
        {
            if (naval)
                _barMovie = "SometimesBar";
        }
    }

    /// <summary>A movie loaded with no data source at all.</summary>
    public class Background
    {
        public void Open(GauntletLayer layer) => layer.LoadMovie("Background", null);
    }

    /// <summary>A ViewModel picked by reflection: only the declared type is known, so the pair is not claimed.</summary>
    public class ReflectedPage
    {
        public void Open(GauntletLayer layer, string typeName) => layer.LoadMovie("ReflectedPage", (PageVM) Activator.CreateInstance(Type.GetType(typeName)));
    }

    public class MapBarVM : ViewModel { }

    public class NavalMapBarVM : MapBarVM { }

    /// <summary>A layer handed its data source by whoever creates it, as the map bar is.</summary>
    public class MapBarLayer
    {
        private MapBarVM _dataSource;

        public void Initialize(MapBarVM dataSource)
        {
            _dataSource = dataSource;
            new GauntletLayer().LoadMovie("MapBar", _dataSource);
        }
    }

    public class NavalMapBarLayer : MapBarLayer { }

    public class OverlayVM : ViewModel { }

    public class EncounterOverlayVM : OverlayVM { }

    public class SettlementOverlayVM : OverlayVM { }

    public class PortSettlementOverlayVM : SettlementOverlayVM { }

    public static class OverlayFactory
    {
        public static OverlayVM Get(int kind)
        {
            if (kind == 0)
                return new EncounterOverlayVM();
            if (kind == 1)
                return new SettlementOverlayVM();
            return new PortSettlementOverlayVM();
        }
    }

    /// <summary>
    /// One field that may hold any overlay, and the movie picked by a type check on it, as
    /// GauntletMenuOverlayBaseView does: each movie gets only the ViewModels its check lets through.
    /// </summary>
    public class OverlayView
    {
        private OverlayVM _dataSource;

        public void Initialize(GauntletLayer layer, int kind)
        {
            _dataSource = OverlayFactory.Get(kind);
            if (_dataSource is EncounterOverlayVM)
                layer.LoadMovie("EncounterOverlay", _dataSource);
            else if (_dataSource is SettlementOverlayVM)
                layer.LoadMovie("SettlementOverlay", _dataSource);
            else
                layer.LoadMovie("AnyOverlay", _dataSource);
        }
    }

    public class MapBarView
    {
        protected MapBarLayer _layer;

        public virtual void CreateLayout()
        {
            _layer = new MapBarLayer();
            _layer.Initialize(new MapBarVM());
        }
    }

    public class NavalMapBarView : MapBarView
    {
        public override void CreateLayout()
        {
            _layer = new NavalMapBarLayer();
            _layer.Initialize(new NavalMapBarVM());
        }
    }
}
