using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// A cell-painted <see cref="Layout.Node"/> tree is routed by DIR.Lib's <see cref="InputRouter"/> with no
/// hand-written routing: the tree is arranged in cells, painted with <see cref="CellLayout"/> into a
/// <see cref="CellBuffer"/> plus the <see cref="CellInputSurface"/>, and the router reads what the paint
/// declared. Fakes only: no console, no clock.
/// </summary>
public class CellInputRouterTests
{
    private sealed class Host
    {
        private readonly CellBuffer _buffer = new() { ColorMode = ColorMode.TrueColor };
        private readonly CellBufferViewport _viewport;

        public Host(int width = 40, int height = 10)
        {
            Width = width;
            Height = height;
            _buffer.Resize(width, height);
            _viewport = new CellBufferViewport(_buffer, width, height);
            Surface = new CellInputSurface(new WindowUiSettings());
            Router = CellInputRouter.Create(Surface, new BackgroundTaskTracker(), () => { }, evt =>
            {
                Fallback.Add(evt);
                return false;
            });
        }

        public int Width { get; }

        public int Height { get; }

        public CellInputSurface Surface { get; }

        public InputRouter Router { get; }

        public List<InputEvent> Fallback { get; } = [];

        public TextInputFocus Focus => Surface.Ui.Focus;

        /// <summary>One frame: arrange in cells, paint the cells and declare to the surface.</summary>
        public void Frame(Layout.Node tree)
        {
            var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, Width, Height), CellMeasureContext.CellAuthored);
            CellLayout.Paint(_viewport, arranged, Surface);
        }

        public bool Press(int column, int row)
            => Router.Handle(new InputEvent.MouseDown(column, row, MouseButton.Left, InputModifier.None, 1));

        public bool Key(InputKey key, InputModifier mods = InputModifier.None)
            => Router.Handle(new InputEvent.KeyDown(key, mods));

        /// <summary>What a terminal host does for a printable key: the KeyDown, then its text as a TextInput.</summary>
        public void Typed(InputKey key, string text)
        {
            Key(key);
            Router.Handle(new InputEvent.TextInput(text));
        }
    }

    private static Layout.Node Field(TextInputState state)
        => Layout.Builder.TextInput(state, 1f).RowH(1).WStar();

    [Fact]
    public void ACtrlShortcutFiresWhileAFieldIsFocused_AndABareKeyIsTypedIntoIt()
    {
        var host = new Host();
        var field = new TextInputState { Text = "ab", CursorPos = 2 };
        var fired = 0;
        var tree = Layout.Builder.VStack(
            Field(field),
            Layout.Builder.Text("one", 1f).RowH(1).WStar()
                .Clickable(new HitResult.ButtonHit("one"), _ => fired++)
                .WithShortcut(InputKey.D1, InputModifier.Ctrl));
        host.Frame(tree);
        host.Focus.Focus(field);
        host.Frame(tree);

        host.Key(InputKey.D1, InputModifier.Ctrl).ShouldBeTrue();
        fired.ShouldBe(1);
        field.Text.ShouldBe("ab");

        host.Typed(InputKey.D1, "1");
        field.Text.ShouldBe("ab1");
        fired.ShouldBe(1);
    }

    [Fact]
    public void AClickOnAFieldFocusesIt()
    {
        var host = new Host();
        var first = new TextInputState { Text = "first" };
        var second = new TextInputState { Text = "second" };
        host.Frame(Layout.Builder.VStack(Field(first), Field(second)));

        host.Press(3, 1).ShouldBeTrue();

        host.Focus.Current.ShouldBeSameAs(second);
    }

    [Fact]
    public void TabCyclesFieldsInPaintOrder()
    {
        var host = new Host();
        var a = new TextInputState();
        var b = new TextInputState();
        var c = new TextInputState();
        var tree = Layout.Builder.VStack(Field(a), Field(b), Field(c));
        host.Frame(tree);
        host.Focus.Focus(a);
        host.Frame(tree);

        host.Key(InputKey.Tab);
        host.Focus.Current.ShouldBeSameAs(b);
        host.Key(InputKey.Tab);
        host.Focus.Current.ShouldBeSameAs(c);
        host.Key(InputKey.Tab);
        host.Focus.Current.ShouldBeSameAs(a);
    }

    [Fact]
    public void DownDownEnterWalksAListAndActivatesTheRow()
    {
        var host = new Host();
        var activated = new List<int>();
        var rows = new Layout.Node[3];
        for (var i = 0; i < rows.Length; i++)
        {
            var index = i;
            rows[i] = Layout.Builder.Text($"row {i}", 1f).RowH(1).WStar()
                .Clickable(new HitResult.ListItemHit("rows", index))
                .Activatable(_ => activated.Add(index));
        }

        host.Surface.ListCursor.Open("rows", 0);
        host.Frame(Layout.Builder.VStack(rows));

        host.Key(InputKey.Down).ShouldBeTrue();
        host.Key(InputKey.Down).ShouldBeTrue();
        host.Surface.ListCursor.Index.ShouldBe(2);

        host.Key(InputKey.Enter).ShouldBeTrue();
        activated.ShouldBe([2]);
    }

    private static (Layout.Node Tree, ListScrollController Scroll, List<int> Activated) ScrolledList(int rowCount, int viewportRows)
    {
        var activated = new List<int>();
        var scroll = new ListScrollController { Mode = ScrollBarMode.None };
        var rows = new Layout.Node[rowCount];
        for (var i = 0; i < rows.Length; i++)
        {
            var index = i;
            rows[i] = Layout.Builder.Text($"row {i}", 1f).RowH(1).WStar()
                .Clickable(new HitResult.ListItemHit("rows", index))
                .Activatable(_ => activated.Add(index));
        }

        // Inside a parent stack so the fixed height is honoured: a root fills the whole frame.
        var tree = Layout.Builder.VStack(
            Layout.Builder.VStack(rows).WithScroll(scroll).H(Layout.Sizing.Fixed(viewportRows)).WStar());
        return (tree, scroll, activated);
    }

    [Fact]
    public void DownPastTheViewportScrollsTheRowIntoTheNextFrame_AndEnterActivatesIt()
    {
        var host = new Host();
        var (tree, scroll, activated) = ScrolledList(5, 3);
        host.Surface.ListCursor.Open("rows", 0);
        host.Frame(tree);

        host.Surface.HitTest(2, 4).ShouldBeNull(); // row 4 is clipped: nothing registered below the viewport

        for (var i = 0; i < 4; i++)
        {
            host.Key(InputKey.Down).ShouldBeTrue();
        }

        host.Surface.ListCursor.Index.ShouldBe(4);
        scroll.Offset.ShouldBe(2f);

        host.Frame(tree);
        host.Surface.HitTest(2, 2).ShouldBeOfType<HitResult.ListItemHit>().Index.ShouldBe(4);

        host.Key(InputKey.Enter).ShouldBeTrue();
        activated.ShouldBe([4]);
    }

    [Fact]
    public void EnterOnARowRevealedButNotYetRepaintedStillActivatesIt()
    {
        var host = new Host();
        var (tree, _, activated) = ScrolledList(5, 3);
        host.Surface.ListCursor.Open("rows", 0);
        host.Frame(tree);

        for (var i = 0; i < 4; i++)
        {
            host.Key(InputKey.Down);
        }

        host.Key(InputKey.Enter).ShouldBeTrue();
        activated.ShouldBe([4]);
    }

    [Fact]
    public void TheWheelOverAScrolledViewportMovesItsOffset()
    {
        var host = new Host();
        var (tree, scroll, _) = ScrolledList(5, 3);
        host.Frame(tree);

        host.Router.Handle(new InputEvent.Scroll(-1f, 2f, 1f)).ShouldBeTrue();
        scroll.Offset.ShouldBe(2f);

        host.Frame(tree);
        host.Surface.HitTest(2, 0).ShouldBeOfType<HitResult.ListItemHit>().Index.ShouldBe(2);
    }
}
