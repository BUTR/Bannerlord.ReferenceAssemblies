using System;

using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;

// What TypeSchemaReaderTests expects to be recorded, and left out, of a widget hierarchy and a ViewModel.

namespace Fixtures.Types
{
    public class ButtonWidget : Widget
    {
        public bool IsSelected { get; set; }
        public string ReadOnly { get; }
        public int PrivateSetter { get; private set; }
        public HorizontalAlignment Alignment { get; set; }
        public HorizontalAlignment? MaybeAlignment { get; set; }

        internal int Internal { get; set; }
        protected int Protected { get; set; }
        public static int Static { get; set; }
        public int this[int index] => index;
    }

    public class FancyButtonWidget : ButtonWidget
    {
        public float Glow { get; set; }
    }

    public abstract class AbstractWidget : Widget
    {
    }

    public class MembersVM : ViewModel
    {
        public string PublicProperty { get; set; }
        protected int ProtectedProperty { get; set; }
        private bool PrivateProperty { get; set; }
        internal MBBindingList<MembersVM> Items { get; set; }
        public int WriteOnly { set { } }
        public static int StaticProperty { get; set; }

        public void ExecuteDone()
        {
        }

        private void ExecuteHidden(int amount)
        {
        }

        protected internal string Compute(string input) => input;

        public static void StaticMethod()
        {
        }

        public void WithLocalFunction()
        {
            int Local() => PrivateProperty ? 1 : 0;
            Local();
        }
    }

    public class DerivedVM : MembersVM
    {
        public string Extra { get; set; }
    }

    public class NotAViewModel
    {
        public string Ignored { get; set; }
    }

    /// <summary>A base widget raising a literal and a name from a virtual property; the subclass raises its own, and overrides the name.</summary>
    public class EventBaseWidget : Widget
    {
        protected virtual string ReleaseName => "Release";

        public void Press() => EventFired("Press");

        public void Release() => EventFired(ReleaseName);
    }

    public class EventDerivedWidget : EventBaseWidget
    {
        protected override string ReleaseName => "LetGo";

        public void Hold() => EventFired("Hold");
    }

    /// <summary>Events raised through a helper that takes the name as a parameter.</summary>
    public class HelperEventWidget : Widget
    {
        private void Raise(string name) => EventFired(name, 1);

        public void First() => Raise("Alpha");

        public void Second() => Raise("Beta");
    }

    /// <summary>An event named by a property the prefab XML sets: it cannot be traced.</summary>
    public class XmlEventWidget : Widget
    {
        public string EventName { get; set; }

        public void Fire() => EventFired(EventName);
    }

    public interface IFixtureLayout
    {
    }

    public class ColumnLayout : IFixtureLayout
    {
        public float Spacing { get; set; }
    }

    public class RowLayout : IFixtureLayout
    {
        public float Gap { get; set; }
    }

    public class TintColor
    {
        public float Alpha { get; set; }
    }

    public class TextStyle
    {
        public int FontSize { get; set; }
        public TintColor Tint { get; set; }
        public HorizontalAlignment Alignment { get; set; }
    }

    /// <summary>A property holding a plain class, for dotted attributes, and one of an interface type the constructor fills.</summary>
    public class StyledWidget : Widget
    {
        public StyledWidget()
        {
            Layout = new ColumnLayout();
        }

        public TextStyle Style { get; set; } = new TextStyle();
        public IFixtureLayout Layout { get; set; }
    }

    public class DeepC
    {
        public int Value { get; set; }
    }

    public class DeepB
    {
        public DeepC C { get; set; }
    }

    public class DeepA
    {
        public DeepB B { get; set; }
    }

    public class Holder<T>
    {
        public T Item { get; set; }
        public int Count { get; set; }
    }

    public class HiddenBehind
    {
        public int Value { get; set; }
    }

    public class WriteOnlyStep
    {
        public HiddenBehind Hidden { set { } }
    }

    /// <summary>
    /// Objects three steps deep; a closed generic, recorded as its instance, and an open one, not at all; and
    /// a step through a property with no getter, which reaches nothing beyond it.
    /// </summary>
    public class ObjectPathsWidget<TItem> : Widget
    {
        public DeepA A { get; set; }
        public Holder<DeepC> ClosedHolder { get; set; }
        public Holder<TItem> OpenHolder { get; set; }
        public WriteOnlyStep Step { get; set; }
    }

    /// <summary>A widget-typed property the widget sets to itself: no assignedTypes, a dotted attribute never reaches a widget.</summary>
    public class SelfAreaWidget : Widget
    {
        public SelfAreaWidget()
        {
            Area = this;
        }

        public Widget Area { get; set; }
    }

    public enum Direction
    {
        Up,
        Down,
    }

    /// <summary>One announcement of each shape the loader can be handed a value by.</summary>
    public class AnnouncingWidget : Widget
    {
        private bool _isOn;
        private TextStyle _style;

        /// <summary>A typed overload, named by [CallerMemberName].</summary>
        public bool IsOn
        {
            get => _isOn;
            set
            {
                _isOn = value;
                OnPropertyChanged(value);
            }
        }

        /// <summary>The generic overload, with a literal and with a field of a class type.</summary>
        public HorizontalAlignment Alignment
        {
            set => OnPropertyChanged(value == HorizontalAlignment.Left ? "Left" : "Other");
        }

        public TextStyle Style
        {
            set
            {
                _style = value;
                OnPropertyChanged(_style);
            }
        }

        /// <summary>An enum announced as its number, and one boxed through the object overload.</summary>
        public Direction Heading
        {
            set => OnPropertyChanged((int) value);
        }

        public Direction Boxed
        {
            set => OnPropertyChanged((object) value);
        }

        /// <summary>A name the prefab XML sets: it cannot be traced.</summary>
        public string Target { get; set; }

        public void Announce() => OnPropertyChanged(true, Target);

        /// <summary>A name that is no property, and a literal null, which announces nothing.</summary>
        public void Press() => OnPropertyChanged("MouseDown", "OnPress");

        public void Clear() => OnPropertyChanged<string>(null, "Cleared");

        /// <summary>A property announced by another's setter; its own setter announces nothing.</summary>
        public bool IsVisible { get; set; }

        public bool IsHidden
        {
            set => OnPropertyChanged(!value, "IsVisible");
        }
    }

    /// <summary>A base method announcing a number; the override announces text under the same name.</summary>
    public class AnnouncingBaseWidget : Widget
    {
        public virtual void Refresh() => OnPropertyChanged(1, "Value");
    }

    public class AnnouncingDerivedWidget : AnnouncingBaseWidget
    {
        public override void Refresh() => OnPropertyChanged("one", "Value");
    }

    /// <summary>The generic overload given the class's own type parameter: known per concrete subclass only.</summary>
    public class GenericAnnouncingWidget<TItem> : Widget where TItem : class
    {
        public TItem Item
        {
            set => OnPropertyChanged(value);
        }
    }

    public class ConcreteAnnouncingWidget : GenericAnnouncingWidget<TextStyle>
    {
    }

    public class AccessorsVM : ViewModel
    {
        public string Title { get; private set; }
        public int Count => 0;
    }
}
