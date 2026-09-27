using System.Linq;

using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.TwoDimension;

// Each class loads sprite categories the way some part of the game does. SpriteCategoryScanTests says what each must yield.

namespace Fixtures.SpriteCategories
{
    /// <summary>A category loaded by name, and a subclass that inherits the load.</summary>
    public class OrderScreen
    {
        public void Open() => UIResourceManager.LoadSpriteCategory("ui_order");
    }

    public class NavalOrderScreen : OrderScreen
    {
    }

    /// <summary>A category looked up in one method and loaded in another, as the War Sails character developer does.</summary>
    public class DeveloperScreen
    {
        private SpriteCategory _category;

        public void Initialize() => _category = UIResourceManager.GetSpriteCategory("ui_developer");

        public void Activate() => _category.Load();
    }

    /// <summary>A category looked up and never loaded: it must not be recorded.</summary>
    public class LookupOnly
    {
        public bool Has() => UIResourceManager.GetSpriteCategory("ui_never") != null;
    }

    /// <summary>A category taken straight from the sprite data and loaded with the engine's own Load.</summary>
    public class DirectLoad
    {
        public void Open() => UIResourceManager.SpriteData.SpriteCategories["ui_direct"].Load(null, null);
    }

    /// <summary>A category prepared for a partial load, as the loading window does.</summary>
    public class LoadingWindow
    {
        public void Initialize() => UIResourceManager.GetSpriteCategory("ui_loading").InitializePartialLoad();
    }

    /// <summary>A load at game start: it goes under always.</summary>
    public class FixtureSubModule : MBSubModuleBase
    {
        protected void OnSubModuleLoad() => UIResourceManager.LoadSpriteCategory("ui_startup");
    }

    /// <summary>A name built at runtime: unresolved.</summary>
    public class RuntimeCategory
    {
        public void Open(int index) => UIResourceManager.LoadSpriteCategory("ui_page_" + index.ToString());
    }

    /// <summary>
    /// The categories the sprite data marks AlwaysLoad, loaded from a filtered collection as
    /// GauntletUISubModule.RefreshResources does: known from the sprite data, so not unresolved.
    /// </summary>
    public class AlwaysLoadSubModule : MBSubModuleBase
    {
        protected void RefreshResources()
        {
            var array = UIResourceManager.SpriteData.SpriteCategories.Values.Where(x => x.AlwaysLoad).ToArray();
            for (var i = 0; i < array.Length; i++)
                array[i].Load();
        }
    }

    public class EncyclopediaVM : ViewModel { }

    /// <summary>One class loads the category, the class it creates loads the movie, as the encyclopedia does.</summary>
    public class EncyclopediaView
    {
        private EncyclopediaPages _pages;

        public void Open()
        {
            UIResourceManager.LoadSpriteCategory("ui_encyclopedia");
            _pages = new EncyclopediaPages();
        }
    }

    public class EncyclopediaPages
    {
        public void Show(GauntletLayer layer) => layer.LoadMovie("EncyclopediaHome", new EncyclopediaVM());
    }

    /// <summary>A view every mission gets: its categories go under missions.</summary>
    [DefaultView]
    public class CategoryLoadManager : MissionBehavior
    {
        public void AfterStart() => UIResourceManager.LoadSpriteCategory("ui_mission_backgrounds");
    }
}
