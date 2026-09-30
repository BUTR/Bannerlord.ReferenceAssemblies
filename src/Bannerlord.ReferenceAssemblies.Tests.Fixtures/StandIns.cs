using System;
using System.Collections.Generic;

// The TaleWorlds types the scanner and the schema reader look for by full name.

namespace TaleWorlds.Library
{
    public class ViewModel
    {
    }

    public class MBBindingList<T> : List<T>
    {
    }
}

namespace TaleWorlds.Engine.GauntletUI
{
    public class GauntletLayer
    {
        public object LoadMovie(string movieName, TaleWorlds.Library.ViewModel dataSource) => null;
    }
}

namespace TaleWorlds.GauntletUI
{
    public enum HorizontalAlignment
    {
        Left,
        Center,
        Right,
    }

    // The overloads a widget announces a change through: a typed one, the generic one of the later builds, and
    // the object one of v1.0.x, which no build has alongside the generic one.
    public class PropertyOwnerObject
    {
        protected void OnPropertyChanged<T>(T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null) where T : class
        {
        }

        protected void OnPropertyChanged(object value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
        }

        protected void OnPropertyChanged(bool value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
        }

        protected void OnPropertyChanged(int value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
        }
    }
}

namespace TaleWorlds.GauntletUI.BaseTypes
{
    public class Widget : PropertyOwnerObject
    {
        public float SuggestedWidth { get; set; }

        protected void EventFired(string eventName, params object[] args)
        {
        }
    }
}

namespace TaleWorlds.TwoDimension
{
    public class SpriteCategory
    {
        public bool AlwaysLoad;

        public void Load(object context, object depot)
        {
        }

        public void InitializePartialLoad()
        {
        }
    }

    public class SpriteData
    {
        public Dictionary<string, SpriteCategory> SpriteCategories { get; } = new Dictionary<string, SpriteCategory>();
    }
}

namespace TaleWorlds.Engine.GauntletUI
{
    // Stand-ins with no loading of their own, so that only the fixtures' calls count.
    public static class UIResourceManager
    {
        public static TaleWorlds.TwoDimension.SpriteData SpriteData { get; } = new TaleWorlds.TwoDimension.SpriteData();

        public static TaleWorlds.TwoDimension.SpriteCategory GetSpriteCategory(string spriteCategoryName) => null;

        public static TaleWorlds.TwoDimension.SpriteCategory LoadSpriteCategory(string spriteCategoryName) => null;
    }

    public static class Extensions
    {
        public static void Load(this TaleWorlds.TwoDimension.SpriteCategory category)
        {
        }
    }
}

namespace TaleWorlds.MountAndBlade
{
    public abstract class MBSubModuleBase
    {
    }

    public abstract class MissionBehavior
    {
    }
}

namespace TaleWorlds.MountAndBlade.View
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DefaultView : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class OverrideView : Attribute
    {
        public OverrideView(Type baseType)
        {
        }
    }
}

namespace TaleWorlds.MountAndBlade.View.Screens
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GameStateScreen : Attribute
    {
        public GameStateScreen(Type gameStateType)
        {
        }
    }
}
