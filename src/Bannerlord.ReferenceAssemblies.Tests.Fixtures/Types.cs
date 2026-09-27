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
}
